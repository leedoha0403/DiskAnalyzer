using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DiskAnalyzer.Core.Interop;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Services;

public sealed class FastDeleteResult
{
    public long FilesDeleted { get; init; }
    public long DirectoriesDeleted { get; init; }

    /// <summary>삭제하지 못한 항목 수(파일 + 폴더).</summary>
    public long Failed { get; init; }

    /// <summary>그중 다른 프로그램이 열고 있어서(공유 위반) 실패한 수.</summary>
    public long InUse { get; init; }

    public int FirstError { get; init; }
    public string? FirstFailedPath { get; init; }
    public bool Cancelled { get; init; }

    /// <summary>시작 시점에 이미 없었다.</summary>
    public bool NotFound { get; init; }

    public bool Success => Failed == 0 && !Cancelled;

    public string FirstErrorText => FirstError == 0 ? string.Empty : new System.ComponentModel.Win32Exception(FirstError).Message;
}

/// <summary>
/// 영구 삭제 전용 고속 삭제기.
///
/// 기존에는 Windows 셸(SHFileOperation)에 맡겼다. 셸은 항목마다 IFileOperation 초기화, 진행 창용 사전 열거,
/// 변경 알림 발행을 하고 한 스레드에서 순서대로 처리해서 파일이 많은 폴더에서 매우 느렸다(6만 개에 50초).
/// 영구 삭제에는 휴지통이 필요 없으므로 Win32 API 를 직접 호출한다.
///
///  - 폴더는 같은 깊이의 디렉터리를 병렬로 열거하며 열거 결과의 파일을 바로 지운다(추가 조회 없음).
///    한 폴더에 파일이 수만 개인 경우 그 안에서도 나눠서 병렬로 지운다.
///  - 비어 버린 폴더는 가장 깊은 곳부터 제거한다(부모는 자식이 사라진 뒤에야 비므로 깊이별 병렬).
///  - Junction / 심볼릭 링크는 따라 들어가지 않고 링크 자체만 지운다(대상 폴더는 건드리지 않는다).
///  - "\\?\" 접두사로 MAX_PATH(260자) 제한을 넘는 경로도 지운다.
///  - 실패 시 단계적으로 다시 시도한다:
///      1. DeleteFile / RemoveDirectory
///      2. 읽기 전용·시스템·숨김 속성을 풀고 재시도
///      3. POSIX 삭제 시맨틱(FileDispositionInfoEx)으로 재시도 - 읽기 전용을 무시하고, 다른 프로세스가
///         FILE_SHARE_DELETE 로 열어 둔 파일도 즉시 이름을 제거한다
///  - 한 항목이 실패해도 나머지는 계속 지우고, 마지막에 실패 수와 첫 원인을 보고한다.
///
/// 보호 등급 판정은 이 클래스의 책임이 아니다. 호출자(DeletionService)가 삭제 직전에 판정을 마쳐야 한다.
/// </summary>
public static class FastDeleter
{
    private const int FilesPerParallelChunk = 512;
    private const int MinFilesForInnerParallel = 1024;

    private sealed class State
    {
        public required int Parallelism { get; init; }
        public required CancellationToken Ct { get; init; }
        public Action<long>? OnFiles { get; init; }

        public long Files, Dirs, Failed, InUse;
        public int FirstError;
        public string? FirstPath;
        public volatile bool Cancelled;

        public ParallelOptions Options => new() { MaxDegreeOfParallelism = Parallelism, CancellationToken = Ct };
    }

    /// <summary>
    /// 이 경로가 있는 드라이브에 맞는 병렬도. SSD/NVMe 는 코어 수만큼(상한 12), HDD 는 헤드 이동이
    /// 잦아지면 오히려 느려지므로 2 로 제한한다.
    /// </summary>
    public static int RecommendedParallelism(string path)
    {
        int cores = Math.Max(2, Environment.ProcessorCount);
        try
        {
            if (path.Length >= 2 && path[1] == ':' &&
                DriveService.DetectKind(path[0]) == DriveKind.Hdd)
                return 2;
        }
        catch { /* 판별 실패 시 SSD 로 간주 */ }
        return Math.Min(cores, 12);
    }

    /// <summary>파일이든 폴더든 <paramref name="path"/> 를 통째로 지운다.</summary>
    /// <param name="onFiles">지금까지 지운 파일 수(누적). 여러 스레드에서 호출되며 자주 호출되지 않는다.</param>
    public static FastDeleteResult Delete(
        string path, int parallelism = 0, CancellationToken ct = default, Action<long>? onFiles = null)
    {
        var s = new State
        {
            Parallelism = parallelism > 0 ? parallelism : RecommendedParallelism(path),
            Ct = ct,
            OnFiles = onFiles,
        };

        string lp;
        try { lp = ToExtended(path); }
        catch (Exception) { Record(s, path, 87 /* ERROR_INVALID_PARAMETER */); return Build(s, notFound: false); }

        if (!TryGetInfo(lp, out uint attr, out uint tag, out int err))
        {
            if (err is Win32.ERROR_FILE_NOT_FOUND or Win32.ERROR_PATH_NOT_FOUND)
                return Build(s, notFound: true);
            Record(s, lp, err);
            return Build(s, notFound: false);
        }

        if ((attr & Win32.FILE_ATTRIBUTE_DIRECTORY) == 0)
        {
            DeleteFileEntry(s, lp);
        }
        else if (IsLink(attr, tag))
        {
            RemoveDirectoryEntry(s, lp);        // 링크만 제거한다. 대상은 그대로.
        }
        else
        {
            DeleteTree(s, lp);
        }

        return Build(s, notFound: false);
    }

    // ------------------------------------------------------------------ 트리

    private static void DeleteTree(State s, string root)
    {
        var levels = new List<List<string>>();
        var current = new List<string> { root };

        // 1) 위에서 아래로: 같은 깊이의 폴더들을 병렬로 열거하고 파일을 지운다.
        while (current.Count > 0 && !s.Cancelled)
        {
            levels.Add(current);
            var next = new ConcurrentBag<string>();

            try
            {
                Parallel.ForEach(current, s.Options, dir => PurgeFiles(s, dir, next));
            }
            catch (OperationCanceledException)
            {
                s.Cancelled = true;
            }

            current = next.ToList();
        }

        if (s.Cancelled) return;

        // 2) 아래에서 위로: 가장 깊은 폴더부터 제거한다. 같은 깊이끼리는 서로 무관하므로 병렬.
        for (int i = levels.Count - 1; i >= 0 && !s.Cancelled; i--)
        {
            try
            {
                Parallel.ForEach(levels[i], s.Options, dir => RemoveDirectoryEntry(s, dir));
            }
            catch (OperationCanceledException)
            {
                s.Cancelled = true;
            }
        }
    }

    /// <summary>폴더 하나를 열거해 파일은 바로 지우고, 하위 폴더는 다음 단계로 넘긴다.</summary>
    private static unsafe void PurgeFiles(State s, string dir, ConcurrentBag<string> next)
    {
        if (s.Ct.IsCancellationRequested) { s.Cancelled = true; return; }

        using var handle = Win32.FindFirstFileEx(
            dir + @"\*", Win32.FindExInfoBasic, out var data,
            Win32.FindExSearchNameMatch, IntPtr.Zero, Win32.FIND_FIRST_EX_LARGE_FETCH);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            if (err is Win32.ERROR_FILE_NOT_FOUND or Win32.ERROR_PATH_NOT_FOUND or Win32.ERROR_NO_MORE_FILES) return;
            Record(s, dir, err);
            return;
        }

        var files = new List<string>(64);

        do
        {
            ReadOnlySpan<char> name = NameOf(data.cFileName);
            if (name.Length == 0) continue;
            if (name[0] == '.' && (name.Length == 1 || (name.Length == 2 && name[1] == '.'))) continue;

            string full = string.Concat(dir, "\\", name);
            uint attr = data.dwFileAttributes;

            if ((attr & Win32.FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                if (IsLink(attr, data.dwReserved0)) RemoveDirectoryEntry(s, full);   // 링크는 따라가지 않는다
                else next.Add(full);
            }
            else
            {
                files.Add(full);
            }
        }
        while (Win32.FindNextFile(handle, out data));

        handle.Dispose();   // 삭제 전에 열거 핸들을 닫는다(열려 있으면 폴더 제거를 방해할 수 있다)

        if (files.Count >= MinFilesForInnerParallel && s.Parallelism > 1)
        {
            try
            {
                Parallel.ForEach(
                    Partitioner.Create(0, files.Count, FilesPerParallelChunk), s.Options,
                    range =>
                    {
                        for (int i = range.Item1; i < range.Item2 && !s.Ct.IsCancellationRequested; i++)
                            DeleteFileEntry(s, files[i]);
                    });
            }
            catch (OperationCanceledException)
            {
                s.Cancelled = true;
            }
        }
        else
        {
            foreach (var f in files)
            {
                if (s.Ct.IsCancellationRequested) { s.Cancelled = true; return; }
                DeleteFileEntry(s, f);
            }
        }
    }

    // ------------------------------------------------------------------ 항목 하나

    private static void DeleteFileEntry(State s, string lp)
    {
        if (DeleteFileRobust(lp, out int err))
        {
            long n = Interlocked.Increment(ref s.Files);
            if (s.OnFiles != null && (n & 0xFF) == 0) s.OnFiles(n);
            return;
        }
        Record(s, lp, err);
    }

    private static void RemoveDirectoryEntry(State s, string lp)
    {
        if (RemoveDirectoryRobust(lp, out int err))
        {
            Interlocked.Increment(ref s.Dirs);
            return;
        }

        // 안에 지우지 못한 파일이 남아 있어서 비지 않은 것이다. 원인은 이미 파일 쪽에서 기록했으므로 중복 집계하지 않는다.
        if (err == Win32.ERROR_DIR_NOT_EMPTY && Interlocked.Read(ref s.Failed) > 0) return;

        Record(s, lp, err);
    }

    private static bool DeleteFileRobust(string lp, out int error)
    {
        error = 0;
        if (Win32.DeleteFile(lp)) return true;

        int err = Marshal.GetLastWin32Error();
        if (IsGone(err)) return true;   // 그사이 사라졌다면 목적은 이미 이루어졌다

        if (err == Win32.ERROR_ACCESS_DENIED)
        {
            // 읽기 전용 / 시스템 / 숨김 속성이 삭제를 막는 가장 흔한 경우.
            Win32.SetFileAttributes(lp, Win32.FILE_ATTRIBUTE_NORMAL);
            if (Win32.DeleteFile(lp)) return true;
            if (IsGone(Marshal.GetLastWin32Error())) return true;
        }

        if (TryPosixDelete(lp)) return true;

        error = err;
        return false;
    }

    private static bool RemoveDirectoryRobust(string lp, out int error)
    {
        error = 0;
        if (Win32.RemoveDirectory(lp)) return true;

        int err = Marshal.GetLastWin32Error();
        if (IsGone(err)) return true;

        if (err == Win32.ERROR_ACCESS_DENIED)
        {
            Win32.SetFileAttributes(lp, Win32.FILE_ATTRIBUTE_NORMAL);
            if (Win32.RemoveDirectory(lp)) return true;
            if (IsGone(Marshal.GetLastWin32Error())) return true;
        }

        if (err != Win32.ERROR_DIR_NOT_EMPTY && TryPosixDelete(lp)) return true;

        error = err;
        return false;
    }

    /// <summary>
    /// 핸들을 열어 POSIX 삭제 시맨틱으로 지운다(Windows 10 1709+).
    /// 읽기 전용 속성을 무시하고, 다른 프로세스가 FILE_SHARE_DELETE 로 열어 둔 파일이라도 이름을 바로 제거한다.
    /// 구형 Windows 에서는 일반 삭제 처분으로 폴백한다.
    /// </summary>
    private static bool TryPosixDelete(string lp)
    {
        var h = Win32.CreateFile(
            lp,
            Win32.DELETE | Win32.FILE_READ_ATTRIBUTES,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE,
            IntPtr.Zero,
            Win32.OPEN_EXISTING,
            Win32.FILE_FLAG_BACKUP_SEMANTICS | Win32.FILE_FLAG_OPEN_REPARSE_POINT,   // 링크는 링크 자체를 연다
            IntPtr.Zero);

        using (h)
        {
            if (h.IsInvalid) return false;

            var ex = new Win32.FILE_DISPOSITION_INFO_EX
            {
                Flags = Win32.FILE_DISPOSITION_FLAG_DELETE
                        | Win32.FILE_DISPOSITION_FLAG_POSIX_SEMANTICS
                        | Win32.FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE,
            };
            if (Win32.SetFileInformationByHandle(h, Win32.FileDispositionInfoEx, ref ex,
                    (uint)Marshal.SizeOf<Win32.FILE_DISPOSITION_INFO_EX>()))
                return true;

            var legacy = new Win32.FILE_DISPOSITION_INFO { DeleteFile = 1 };
            if (!Win32.SetFileInformationByHandle(h, Win32.FileDispositionInfo, ref legacy,
                    (uint)Marshal.SizeOf<Win32.FILE_DISPOSITION_INFO>()))
                return false;
        }

        // 폴백은 핸들을 닫을 때 삭제된다. 정말 사라졌는지 확인한다.
        return Win32.GetFileAttributes(lp) == Win32.INVALID_FILE_ATTRIBUTES;
    }

    // ------------------------------------------------------------------ 도우미

    private static bool IsGone(int err) => err is Win32.ERROR_FILE_NOT_FOUND or Win32.ERROR_PATH_NOT_FOUND;

    /// <summary>Junction / 심볼릭 링크. (클라우드 placeholder 같은 다른 reparse point 는 일반 폴더처럼 다룬다.)</summary>
    private static bool IsLink(uint attributes, uint reparseTag)
        => (attributes & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0
           && (reparseTag == Win32.IO_REPARSE_TAG_MOUNT_POINT || reparseTag == Win32.IO_REPARSE_TAG_SYMLINK);

    private static bool TryGetInfo(string lp, out uint attributes, out uint reparseTag, out int error)
    {
        using var h = Win32.FindFirstFileEx(lp, Win32.FindExInfoBasic, out var data,
            Win32.FindExSearchNameMatch, IntPtr.Zero, 0);

        if (h.IsInvalid)
        {
            attributes = 0;
            reparseTag = 0;
            error = Marshal.GetLastWin32Error();
            return false;
        }

        attributes = data.dwFileAttributes;
        reparseTag = data.dwReserved0;
        error = 0;
        return true;
    }

    /// <summary>"\\?\" 확장 경로로 바꾼다. 마지막 '\' 는 제거한다.</summary>
    internal static string ToExtended(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("경로가 비어 있습니다.", nameof(path));

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path.Length > 7 ? path.TrimEnd('\\') : path;

        string full = Path.GetFullPath(path).TrimEnd('\\');
        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
    }

    private static string Display(string lp)
        => lp.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + lp[8..]
         : lp.StartsWith(@"\\?\", StringComparison.Ordinal) ? lp[4..]
         : lp;

    private static void Record(State s, string lp, int err)
    {
        Interlocked.Increment(ref s.Failed);
        if (err is Win32.ERROR_SHARING_VIOLATION or Win32.ERROR_LOCK_VIOLATION)
            Interlocked.Increment(ref s.InUse);

        if (Interlocked.CompareExchange(ref s.FirstError, err == 0 ? -1 : err, 0) == 0)
            s.FirstPath = Display(lp);
    }

    private static FastDeleteResult Build(State s, bool notFound) => new()
    {
        FilesDeleted = Interlocked.Read(ref s.Files),
        DirectoriesDeleted = Interlocked.Read(ref s.Dirs),
        Failed = Interlocked.Read(ref s.Failed),
        InUse = Interlocked.Read(ref s.InUse),
        FirstError = Math.Max(0, s.FirstError),
        FirstFailedPath = s.FirstPath,
        Cancelled = s.Cancelled || s.Ct.IsCancellationRequested,
        NotFound = notFound,
    };

    private static unsafe ReadOnlySpan<char> NameOf(char* p)
    {
        int len = 0;
        while (len < Win32.MAX_PATH && p[len] != '\0') len++;
        return new ReadOnlySpan<char>(p, len);
    }
}

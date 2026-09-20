using System.ComponentModel;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using DiskAnalyzer.Core.Interop;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.Core.QuickMove;

/// <summary>
/// 파일 / 폴더 이동 엔진.
///
/// <b>이동 방식</b>
///  1. 먼저 MoveFileEx 로 이름 바꾸기를 시도한다. 같은 볼륨이면 데이터를 옮기지 않으므로 용량과 무관하게 즉시 끝난다.
///  2. 다른 볼륨이면 ERROR_NOT_SAME_DEVICE 가 돌아온다 → CopyFileEx(진행률 콜백, 취소 지원)로 복사하고, 성공한 뒤에만 원본을 지운다.
///     복사가 끝나기 전에는 원본을 건드리지 않으므로 중간에 멈춰도 데이터를 잃지 않는다.
///  3. 폴더는 대상에 같은 폴더가 없고 이름 바꾸기가 되면 통째로 옮긴다. 안 되면(대상에 같은 폴더가 있거나 안에 사용 중인 파일이 있으면)
///     안쪽 항목을 하나씩 옮기며 병합한다. 하나가 실패해도 나머지는 계속한다.
///
/// <b>안전 규칙</b>: 원본은 "대상에 온전히 놓인 것을 확인한 뒤"에만 사라진다. 취소는 이미 옮긴 항목을 되돌리지 않는다.
/// Junction / 심볼릭 링크는 따라가지 않는다(링크만 옮기고, 다른 볼륨으로는 옮기지 않는다).
///
/// 인스턴스 하나는 <see cref="Run"/> 한 번에 쓴다. Run 은 호출한 스레드에서 동기로 실행되므로 UI 는 Task.Run 으로 부른다.
/// </summary>
public sealed class MoveEngine
{
    private readonly MoveOptions _opt;
    private readonly ManualResetEventSlim _running = new(true);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private CancellationToken _ct;
    private ConflictChoice? _applyAll;

    private long _bytesDone, _bytesTotal, _filesDone, _filesTotal;
    private int _index, _count;
    private string _curName = string.Empty, _curFrom = string.Empty, _curTo = string.Empty;
    private long _lastReportMs = -1000;

    public MoveEngine(MoveOptions? options = null) => _opt = options ?? new MoveOptions();

    public bool IsPaused => !_running.IsSet;

    /// <summary>다음 파일 경계 / 복사 진행 콜백에서 멈춘다. 이미 진행 중인 파일은 그 콜백이 오는 시점에 멈춘다.</summary>
    public void Pause() => _running.Reset();

    public void Resume() => _running.Set();

    public MoveSummary Run(IReadOnlyList<MoveRequest> requests, CancellationToken ct = default)
    {
        _ct = ct;
        _count = requests.Count;
        _bytesTotal = requests.Sum(r => r.Size);
        _filesTotal = requests.Sum(r => r.FileCount);
        var sw = Stopwatch.StartNew();

        var results = new List<MoveItemResult>(requests.Count);
        bool cancelled = false, driveLost = false;

        for (int i = 0; i < requests.Count; i++)
        {
            var req = requests[i];
            var res = new MoveItemResult { Request = req, FinalPath = req.TargetPath };
            results.Add(res);

            if (cancelled || driveLost)
            {
                res.Status = MoveStatus.NotProcessed;
                res.Error = cancelled ? "이동이 취소되어 처리하지 않았습니다." : "드라이브 연결이 끊겨 처리하지 못했습니다.";
                continue;
            }

            _index = i + 1;
            _curName = req.Name;
            _curFrom = Path.GetDirectoryName(req.SourcePath.TrimEnd('\\')) ?? string.Empty;
            _curTo = req.DestDirectory;
            long b0 = _bytesDone, f0 = _filesDone;
            Report(force: true);

            try
            {
                MoveTop(req, res);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                res.Status = MoveStatus.NotProcessed;
                res.Error = "이동이 취소되었습니다.";
                continue;
            }
            catch (Exception ex)
            {
                res.Status = MoveStatus.Failed;
                res.Error = ex.Message;
            }

            // 실패 / 건너뜀도 "처리한 것"으로 세어 진행 막대가 100% 까지 간다.
            if (_bytesDone < b0 + req.Size) _bytesDone = b0 + req.Size;
            if (_filesDone < f0 + req.FileCount) _filesDone = f0 + req.FileCount;

            if (res.Status == MoveStatus.Failed && IsDriveGone(req))
            {
                driveLost = true;
                res.Error = "드라이브 연결이 끊어졌습니다. " + res.Error;
            }
        }

        Report(force: true);
        return new MoveSummary
        {
            Results = results,
            Elapsed = sw.Elapsed,
            WasCancelled = cancelled,
            DriveLost = driveLost,
        };
    }

    // ------------------------------------------------------------------ 항목 하나

    private void MoveTop(MoveRequest req, MoveItemResult res)
    {
        bool? isDir = PathUtil.IsDirectory(req.SourcePath);
        if (isDir == null)
        {
            res.Status = MoveStatus.Failed;
            res.Error = "원본을 찾을 수 없습니다. 이미 이동되었거나 삭제되었을 수 있습니다.";
            return;
        }
        if (!Directory.Exists(req.DestDirectory))
        {
            res.Status = MoveStatus.Failed;
            res.Error = "대상 폴더를 찾을 수 없습니다.";
            return;
        }

        var o = MoveEntry(req.SourcePath, req.TargetPath, isDir.Value, req.Size, req.FileCount, nested: false);
        res.Status = o.Status;
        res.Error = o.Error;
        res.Note = o.Note;
        res.FinalPath = o.FinalPath ?? req.TargetPath;
    }

    private sealed class Outcome
    {
        public MoveStatus Status;
        public string? Error;
        public string? Note;
        public string? FinalPath;

        public static Outcome Moved() => new() { Status = MoveStatus.Moved };
        public static Outcome Skipped(string? why = null) => new() { Status = MoveStatus.Skipped, Error = why };
        public static Outcome Failed(string why) => new() { Status = MoveStatus.Failed, Error = why };
    }

    /// <param name="size">이 항목의 용량. 모르면 -1(폴더).</param>
    /// <param name="nested">폴더 안쪽 항목이면 true. 같은 이름의 폴더를 만나면 묻지 않고 병합한다(탐색기와 같은 동작).</param>
    private Outcome MoveEntry(string src, string dst, bool isDir, long size, long files, bool nested)
    {
        _ct.ThrowIfCancellationRequested();
        WaitIfPaused();

        if (PathUtil.Equal(src, dst))
            return Outcome.Failed("이미 같은 위치에 있는 항목입니다.");

        string finalPath = dst;
        bool merge = false, replace = false;

        bool dstExists = TryGetAttributes(dst, out uint dstAttr);
        bool dstIsDir = dstExists && (dstAttr & Win32.FILE_ATTRIBUTE_DIRECTORY) != 0;

        if (dstExists)
        {
            if (isDir && dstIsDir && nested)
            {
                merge = true;
            }
            else
            {
                var choice = Decide(BuildInfo(src, dst, isDir, dstIsDir, size));
                switch (choice)
                {
                    case ConflictChoice.Skip:
                        AddBytes(Math.Max(size, 0));
                        AddFiles(files);
                        return Outcome.Skipped("같은 이름이 있어 건너뛰었습니다.");

                    case ConflictChoice.Rename:
                        string dir = Path.GetDirectoryName(dst) ?? string.Empty;
                        string name = Path.GetFileName(dst);
                        dst = isDir ? PathUtil.UniqueNameForDirectory(dir, name) : PathUtil.UniqueName(dir, name);
                        finalPath = dst;
                        dstExists = false;
                        break;

                    default:
                        if (isDir) merge = true; else replace = true;
                        break;
                }
            }
        }

        var outcome = isDir
            ? MoveDirectory(src, dst, dstExists, merge, size, files)
            : MoveFile(src, dst, replace, size);
        outcome.FinalPath ??= finalPath;
        return outcome;
    }

    // ------------------------------------------------------------------ 파일

    private Outcome MoveFile(string src, string dst, bool replace, long size)
    {
        string ls = PathUtil.ToExtended(src), ld = PathUtil.ToExtended(dst);
        if (replace) ClearReadOnly(ld);

        if (!_opt.ForceCopy)
        {
            if (Win32.MoveFileEx(ls, ld, replace ? Win32.MOVEFILE_REPLACE_EXISTING : 0))
            {
                AddBytes(size);
                AddFiles(1);
                return Outcome.Moved();
            }

            int err = Marshal.GetLastWin32Error();
            if (err != Win32.ERROR_NOT_SAME_DEVICE)
                return Outcome.Failed(Describe(err));
        }

        return CopyThenDelete(src, ls, ld, replace);
    }

    /// <summary>다른 볼륨 이동: 복사가 <b>성공한 뒤에만</b> 원본을 지운다.</summary>
    private Outcome CopyThenDelete(string src, string ls, string ld, bool replace)
    {
        long last = 0;
        int cancel = 0;

        Win32.CopyProgressRoutine callback = (_, transferred, _, _, _, _, _, _, _) =>
        {
            long delta = transferred - last;
            last = transferred;
            if (delta != 0) AddBytes(delta);

            if (!_running.IsSet)
            {
                try { _running.Wait(_ct); }
                catch (OperationCanceledException) { return Win32.PROGRESS_CANCEL; }
            }
            return _ct.IsCancellationRequested ? Win32.PROGRESS_CANCEL : Win32.PROGRESS_CONTINUE;
        };

        bool ok = Win32.CopyFileEx(ls, ld, callback, IntPtr.Zero, ref cancel,
            replace ? 0u : Win32.COPY_FILE_FAIL_IF_EXISTS);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        GC.KeepAlive(callback);

        if (!ok)
        {
            // 취소 시 CopyFileEx 가 불완전한 대상 파일을 지운다.
            if (err == Win32.ERROR_REQUEST_ABORTED || _ct.IsCancellationRequested)
                throw new OperationCanceledException(_ct);
            return Outcome.Failed(Describe(err));
        }

        var del = FastDeleter.Delete(src, 1);
        if (!del.Success && !del.NotFound)
            return Outcome.Failed("복사는 끝났지만 원본을 지우지 못했습니다(다른 프로그램이 사용 중일 수 있습니다). 대상에 복사본이 있습니다.");

        AddFiles(1);
        return Outcome.Moved();
    }

    // ------------------------------------------------------------------ 폴더

    private readonly record struct Child(string Name, bool IsDirectory, bool IsReparse, long Size);

    private Outcome MoveDirectory(string src, string dst, bool dstExists, bool merge, long size, long files)
    {
        string ls = PathUtil.ToExtended(src), ld = PathUtil.ToExtended(dst);
        TryGetAttributes(src, out uint srcAttr);
        bool isLink = (srcAttr & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0;

        // 1) 통째로 이름 바꾸기. 같은 볼륨이면 안쪽 파일 수와 무관하게 즉시 끝난다.
        if (!dstExists && !_opt.ForceCopy)
        {
            long sz = size, fc = files;
            if (sz < 0 && !isLink)
            {
                var m = TreeMeasure.Measure(src, _ct);
                sz = m.Bytes;
                fc = m.Files;
            }

            if (Win32.MoveFileEx(ls, ld, 0))
            {
                AddBytes(Math.Max(sz, 0));
                AddFiles(fc);
                return Outcome.Moved();
            }
            // 실패하면 안쪽 항목을 하나씩 옮겨 본다(사용 중인 파일 하나 때문에 폴더 전체가 막히지 않게).
            // 다른 볼륨이면 어차피 복사가 필요하다.
        }

        // 링크(Junction / 심볼릭 링크)는 안으로 들어가지 않는다. 이름 바꾸기가 안 되면 여기서 끝.
        if (isLink)
            return Outcome.Failed("링크(Junction/심볼릭 링크)는 다른 드라이브로 옮길 수 없습니다.");

        // 2) 안쪽 항목을 하나씩 옮긴다(복사 후 삭제 / 병합).
        List<Child> children;
        bool freshDir = !dstExists || !Directory.Exists(dst);
        DateTime createdUtc = default, writtenUtc = default;
        try
        {
            if (freshDir)
            {
                // 폴더를 새로 만드는 경우에만 원본의 시간을 물려준다. 안쪽을 옮기면 원본 시간이 바뀌므로 미리 읽어 둔다.
                createdUtc = Directory.GetCreationTimeUtc(src);
                writtenUtc = Directory.GetLastWriteTimeUtc(src);
                Directory.CreateDirectory(ld);
            }
            children = ListChildren(src);
        }
        catch (UnauthorizedAccessException) { return Outcome.Failed("접근 권한이 없습니다."); }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return Outcome.Failed(ex.Message);
        }

        int moved = 0, skipped = 0, failed = 0;
        string? firstError = null;

        foreach (var c in children)
        {
            _ct.ThrowIfCancellationRequested();
            string cs = Path.Combine(src, c.Name), cd = Path.Combine(dst, c.Name);

            Outcome o = c.IsReparse
                ? MoveLink(cs, cd)
                : MoveEntry(cs, cd, c.IsDirectory, c.IsDirectory ? -1 : c.Size, c.IsDirectory ? 0 : 1, nested: true);

            switch (o.Status)
            {
                case MoveStatus.Moved: moved++; break;
                case MoveStatus.Skipped: skipped++; break;
                default:
                    failed++;
                    firstError ??= $"{c.Name}: {o.Error}";
                    break;
            }
        }

        if (failed > 0)
        {
            return new Outcome
            {
                Status = MoveStatus.Failed,
                Error = $"{failed:N0}개 항목을 옮기지 못했습니다 — {firstError}",
                Note = $"옮김 {moved:N0}개 · 건너뜀 {skipped:N0}개",
            };
        }

        if (skipped > 0)
        {
            // 건너뛴 항목이 원본 폴더에 남아 있으므로 원본 폴더는 지우지 않는다.
            return moved == 0
                ? Outcome.Skipped("같은 이름의 항목이라 모두 건너뛰었습니다.")
                : new Outcome { Status = MoveStatus.Moved, Note = $"{skipped:N0}개 항목은 건너뛰어 원본 폴더에 남아 있습니다." };
        }

        // 전부 옮겼다 → 비어 있는 원본 폴더를 지운다.
        var result = RemoveEmptyDirectory(ls);
        if (result != null) return Outcome.Failed(result);

        if (freshDir) TrySetDirectoryTimes(dst, createdUtc, writtenUtc);
        return Outcome.Moved();
    }

    /// <summary>링크는 이름 바꾸기로만 옮긴다. 대상에 이미 있거나 다른 볼륨이면 건드리지 않고 건너뛴다.</summary>
    private Outcome MoveLink(string src, string dst)
    {
        if (PathUtil.Exists(dst))
            return Outcome.Skipped("대상에 같은 이름이 있어 링크를 옮기지 않았습니다.");

        if (!_opt.ForceCopy && Win32.MoveFileEx(PathUtil.ToExtended(src), PathUtil.ToExtended(dst), 0))
        {
            AddFiles(1);
            return Outcome.Moved();
        }
        return Outcome.Skipped("링크는 다른 드라이브로 옮기지 않습니다.");
    }

    private static string? RemoveEmptyDirectory(string extendedPath)
    {
        Win32.SetFileAttributes(extendedPath, Win32.FILE_ATTRIBUTE_NORMAL | Win32.FILE_ATTRIBUTE_DIRECTORY);
        if (Win32.RemoveDirectory(extendedPath)) return null;

        int err = Marshal.GetLastWin32Error();
        if (err is Win32.ERROR_FILE_NOT_FOUND or Win32.ERROR_PATH_NOT_FOUND) return null;
        return "안쪽 항목은 모두 옮겼지만 원본 폴더를 지우지 못했습니다. " + Describe(err);
    }

    private static void TrySetDirectoryTimes(string dst, DateTime createdUtc, DateTime writtenUtc)
    {
        try
        {
            Directory.SetCreationTimeUtc(dst, createdUtc);
            Directory.SetLastWriteTimeUtc(dst, writtenUtc);
        }
        catch (Exception) { /* 시간 복원은 최선 노력이다. 이동 결과에는 영향이 없다. */ }
    }

    private static List<Child> ListChildren(string path)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,
        };
        var e = new FileSystemEnumerable<Child>(path,
            (ref FileSystemEntry en) => new Child(
                en.FileName.ToString(),
                en.IsDirectory,
                (en.Attributes & FileAttributes.ReparsePoint) != 0,
                en.IsDirectory ? 0 : en.Length),
            options);
        return e.ToList();
    }

    // ------------------------------------------------------------------ 충돌

    private ConflictChoice Decide(ConflictInfo info)
    {
        ConflictChoice choice;

        if (_applyAll is { } all) choice = all;
        else
        {
            switch (_opt.Policy)
            {
                case ConflictPolicy.Overwrite: choice = ConflictChoice.Overwrite; break;
                case ConflictPolicy.Skip: choice = ConflictChoice.Skip; break;
                case ConflictPolicy.Rename: choice = ConflictChoice.Rename; break;
                default:
                    var d = _opt.ResolveConflict?.Invoke(info) ?? new ConflictDecision(ConflictChoice.Skip, false);
                    choice = d.Choice;
                    if (d.ApplyToAll) _applyAll = d.Choice;
                    break;
            }
        }

        // 파일 ↔ 폴더처럼 형식이 다르면 덮어쓸 수 없다.
        if (choice == ConflictChoice.Overwrite && !info.CanOverwrite) choice = ConflictChoice.Skip;
        return choice;
    }

    private static ConflictInfo BuildInfo(string src, string dst, bool srcIsDir, bool dstIsDir, long size)
    {
        long existingSize = -1;
        DateTime srcTime = default, dstTime = default;
        try
        {
            srcTime = srcIsDir ? Directory.GetLastWriteTime(src) : File.GetLastWriteTime(src);
            dstTime = dstIsDir ? Directory.GetLastWriteTime(dst) : File.GetLastWriteTime(dst);
            if (!dstIsDir) existingSize = new FileInfo(dst).Length;
        }
        catch (Exception) { /* 표시용 정보라서 실패해도 무시 */ }

        return new ConflictInfo
        {
            SourcePath = src,
            TargetPath = dst,
            SourceIsDirectory = srcIsDir,
            ExistingIsDirectory = dstIsDir,
            SourceSize = srcIsDir ? -1 : size,
            ExistingSize = existingSize,
            SourceModified = srcTime,
            ExistingModified = dstTime,
            CanOverwrite = srcIsDir == dstIsDir,
        };
    }

    // ------------------------------------------------------------------ 보조

    private void WaitIfPaused()
    {
        if (!_running.IsSet) _running.Wait(_ct);
    }

    private void AddBytes(long delta)
    {
        _bytesDone += delta;
        Report(force: false);
    }

    private void AddFiles(long delta)
    {
        _filesDone += delta;
        Report(force: false);
    }

    private void Report(bool force)
    {
        var progress = _opt.Progress;
        if (progress == null) return;

        long now = _clock.ElapsedMilliseconds;
        if (!force && now - _lastReportMs < 80) return;
        _lastReportMs = now;

        progress.Report(new MoveProgress
        {
            BytesDone = _bytesDone,
            BytesTotal = _bytesTotal,
            FilesDone = _filesDone,
            FilesTotal = _filesTotal,
            ItemIndex = _index,
            ItemCount = _count,
            CurrentName = _curName,
            CurrentFrom = _curFrom,
            CurrentTo = _curTo,
        });
    }

    private static bool TryGetAttributes(string path, out uint attributes)
    {
        attributes = Win32.GetFileAttributes(PathUtil.ToExtendedSafe(path));
        return attributes != 0xFFFFFFFF;
    }

    private static void ClearReadOnly(string extendedPath)
    {
        uint a = Win32.GetFileAttributes(extendedPath);
        if (a != 0xFFFFFFFF && (a & 0x1) != 0)
            Win32.SetFileAttributes(extendedPath, a & ~0x1u);
    }

    private static bool IsDriveGone(MoveRequest r)
    {
        try
        {
            return !Directory.Exists(PathUtil.RootOf(r.DestDirectory))
                   || !Directory.Exists(PathUtil.RootOf(r.SourcePath));
        }
        catch (Exception) { return true; }
    }

    internal static string Describe(int err) => err switch
    {
        Win32.ERROR_ACCESS_DENIED => "접근 권한이 없습니다. (읽기 전용이거나 권한이 필요한 위치일 수 있습니다)",
        Win32.ERROR_SHARING_VIOLATION or Win32.ERROR_LOCK_VIOLATION => "다른 프로그램이 사용 중이라 이동할 수 없습니다.",
        Win32.ERROR_DISK_FULL or Win32.ERROR_HANDLE_DISK_FULL => "대상 디스크의 공간이 부족합니다.",
        Win32.ERROR_FILE_NOT_FOUND or Win32.ERROR_PATH_NOT_FOUND => "원본 또는 대상 경로를 찾을 수 없습니다.",
        206 => "경로가 너무 깁니다.",
        _ => new Win32Exception(err).Message,
    };
}

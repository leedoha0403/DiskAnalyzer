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
/// <b>속도</b>
///  · <b>낙관적 시도</b>: 대상이 없다고 가정하고 곧바로 MoveFileEx / CopyFileEx(FAIL_IF_EXISTS)를 부른다. 이미 있을 때만 충돌 처리로
///    들어가므로 파일마다 "대상이 있는가" 조회 한 번이 사라진다(보통은 충돌이 없다).
///  · <b>병렬</b>: 대기열 항목들과 폴더 안쪽 항목들을 병렬로 처리한다. 동시에 도는 파일 작업 수는 드라이브에 맞춘다
///    (SSD/NVMe 는 코어 수만큼 최대 12, HDD 는 2 — 헤드 이동이 늘면 오히려 느려진다). 큰 파일 복사는 동시에 1~2개만 돌린다.
///  · 큰 파일(64 MB 이상) 복사는 버퍼링 없는 I/O(COPY_FILE_NO_BUFFERING)로 캐시를 오염시키지 않고 디스크 대역폭을 쓴다.
///  · 복사 후 원본 삭제는 DeleteFile 한 번으로 끝내고, 실패할 때만 FastDeleter 의 재시도 사다리를 탄다.
///
/// <b>안전 규칙</b>: 원본은 "대상에 온전히 놓인 것을 확인한 뒤"에만 사라진다. 취소는 이미 옮긴 항목을 되돌리지 않는다.
/// Junction / 심볼릭 링크는 따라가지 않는다(링크만 옮기고, 다른 볼륨으로는 옮기지 않는다).
/// 충돌 대화 상자는 한 번에 하나만 뜬다(락). 병렬이어도 사용자에게 동시에 여러 개를 묻지 않는다.
///
/// 인스턴스 하나는 <see cref="Run"/> 한 번에 쓴다. Run 은 호출한 스레드에서 동기로 실행되므로 UI 는 Task.Run 으로 부른다.
/// </summary>
public sealed class MoveEngine
{
    /// <summary>이 크기 이상의 파일은 "큰 파일"로 본다. 동시 복사 수를 제한하고 버퍼링 없는 I/O 를 쓴다.</summary>
    private const long LargeFile = 64L << 20;

    private const uint CopyNoBuffering = 0x1000;   // COPY_FILE_NO_BUFFERING

    private readonly MoveOptions _opt;
    private readonly ManualResetEventSlim _running = new(true);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _decideLock = new();
    private readonly object _tallyLock = new();

    private CancellationToken _ct;
    private ConflictChoice? _applyAll;

    private int _degree = 1;
    private SemaphoreSlim _ioGate = new(1);      // 작은 파일 작업 동시 실행 수
    private SemaphoreSlim _largeGate = new(1);   // 큰 파일 복사 동시 실행 수
    private volatile bool _cancelled;
    private volatile bool _driveLost;

    private long _bytesDone, _bytesTotal, _filesDone, _filesTotal;
    private int _started, _count;
    private string _curName = string.Empty, _curFrom = string.Empty, _curTo = string.Empty;
    private long _lastReportMs = -1000;

    public MoveEngine(MoveOptions? options = null) => _opt = options ?? new MoveOptions();

    public bool IsPaused => !_running.IsSet;

    /// <summary>다음 파일 경계 / 복사 진행 콜백에서 멈춘다. 이미 진행 중인 파일은 그 콜백이 오는 시점에 멈춘다.</summary>
    public void Pause() => _running.Reset();

    public void Resume() => _running.Set();

    /// <summary>항목 하나(대기열 한 줄)가 지금까지 반영한 진행량. 끝날 때 모자란 만큼 채워서 진행 막대가 100% 까지 가게 한다.</summary>
    private sealed class Ctx
    {
        public long Bytes;
        public long Files;
    }

    public MoveSummary Run(IReadOnlyList<MoveRequest> requests, CancellationToken ct = default)
    {
        _ct = ct;
        _count = requests.Count;
        _bytesTotal = requests.Sum(r => r.Size);
        _filesTotal = requests.Sum(r => r.FileCount);
        _degree = ResolveDegree(requests);
        _ioGate = new SemaphoreSlim(_degree);
        _largeGate = new SemaphoreSlim(_degree <= 2 ? 1 : 2);
        var sw = Stopwatch.StartNew();

        var results = new MoveItemResult[requests.Count];

        void One(int i)
        {
            var req = requests[i];
            var res = new MoveItemResult { Request = req, FinalPath = req.TargetPath };
            results[i] = res;

            if (_ct.IsCancellationRequested) _cancelled = true;
            if (_cancelled || _driveLost)
            {
                res.Status = MoveStatus.NotProcessed;
                res.Error = _cancelled ? "이동이 취소되어 처리하지 않았습니다." : "드라이브 연결이 끊겨 처리하지 못했습니다.";
                return;
            }

            Interlocked.Increment(ref _started);
            _curName = req.Name;
            _curFrom = Path.GetDirectoryName(req.SourcePath.TrimEnd('\\')) ?? string.Empty;
            _curTo = req.DestDirectory;
            Report(force: true);

            var ctx = new Ctx();
            try
            {
                MoveTop(req, res, ctx);
            }
            catch (OperationCanceledException)
            {
                _cancelled = true;
                res.Status = MoveStatus.NotProcessed;
                res.Error = "이동이 취소되었습니다.";
                return;
            }
            catch (Exception ex)
            {
                res.Status = MoveStatus.Failed;
                res.Error = ex.Message;
            }

            // 실패 / 건너뜀도 "처리한 것"으로 세어 진행 막대가 100% 까지 간다.
            long lagBytes = req.Size - Interlocked.Read(ref ctx.Bytes);
            if (lagBytes > 0) Interlocked.Add(ref _bytesDone, lagBytes);
            long lagFiles = req.FileCount - Interlocked.Read(ref ctx.Files);
            if (lagFiles > 0) Interlocked.Add(ref _filesDone, lagFiles);

            if (res.Status == MoveStatus.Failed && IsDriveGone(req))
            {
                _driveLost = true;
                res.Error = "드라이브 연결이 끊어졌습니다. " + res.Error;
            }
        }

        // 작업 단위 구성.
        //  · 같은 볼륨(이름 바꾸기): 같은 대상 폴더로 가는 것끼리 한 묶음으로 <b>순차</b> 처리한다.
        //    한 폴더에 동시에 이름 바꾸기를 하면 NTFS 가 서로 막아서 순차보다 몇 배 느려진다(실측: 동시 2 → 3배, 4 이상 → 15배).
        //    대상 폴더가 서로 다르면 막히지 않으므로 묶음끼리는 병렬로 돌린다.
        //  · 다른 볼륨(복사): 파일마다 독립적이라 전부 병렬.
        var units = new List<List<int>>();
        if (_degree > 1 && requests.Count > 1)
        {
            var renameGroups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < requests.Count; i++)
            {
                if (IsRenameMode(requests[i].SourcePath, requests[i].DestDirectory))
                {
                    if (!renameGroups.TryGetValue(requests[i].DestDirectory, out var g))
                        renameGroups[requests[i].DestDirectory] = g = new List<int>();
                    g.Add(i);
                }
                else units.Add(new List<int> { i });
            }
            units.AddRange(renameGroups.Values);
        }
        else units.Add(Enumerable.Range(0, requests.Count).ToList());

        if (units.Count > 1)
            Parallel.ForEach(units, new ParallelOptions { MaxDegreeOfParallelism = _degree }, unit => { foreach (int i in unit) One(i); });
        else
            foreach (int i in units[0]) One(i);

        Report(force: true);
        return new MoveSummary
        {
            Results = results,
            Elapsed = sw.Elapsed,
            WasCancelled = _cancelled,
            DriveLost = _driveLost,
        };
    }

    /// <summary>이 이동이 이름 바꾸기(같은 볼륨)로 처리될 것으로 보이는가. 추정이며, 틀려도 결과는 같고 속도만 달라진다.</summary>
    private bool IsRenameMode(string source, string destination)
        => !_opt.ForceCopy && PathUtil.SameVolume(source, destination);

    /// <summary>동시에 돌릴 파일 작업 수. 원본·대상 드라이브 중 느린 쪽(HDD 등)에 맞춘다.</summary>
    private int ResolveDegree(IReadOnlyList<MoveRequest> requests)
    {
        if (_opt.Parallelism > 0) return Math.Min(_opt.Parallelism, 32);

        int degree = int.MaxValue;
        foreach (var root in requests.SelectMany(r => new[] { r.SourcePath, r.DestDirectory })
                     .Select(PathUtil.RootOf).Distinct(StringComparer.OrdinalIgnoreCase).Take(6))
        {
            if (root.Length > 0) degree = Math.Min(degree, FastDeleter.RecommendedParallelism(root));
        }
        return degree == int.MaxValue ? Math.Min(Math.Max(2, Environment.ProcessorCount), 12) : degree;
    }

    // ------------------------------------------------------------------ 항목 하나

    private void MoveTop(MoveRequest req, MoveItemResult res, Ctx ctx)
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

        var o = MoveEntry(req.SourcePath, req.TargetPath, isDir.Value, req.Size, req.FileCount, nested: false, ctx);
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
    private Outcome MoveEntry(string src, string dst, bool isDir, long size, long files, bool nested, Ctx ctx)
    {
        _ct.ThrowIfCancellationRequested();
        WaitIfPaused();

        if (PathUtil.Equal(src, dst))
            return Outcome.Failed("이미 같은 위치에 있는 항목입니다.");

        // 1) 낙관적 시도: 대상이 없다고 보고 바로 옮긴다. 대상이 이미 있으면 null → 아래 충돌 처리.
        var quick = isDir ? TryQuickDirectory(src, dst, size, files, ctx) : TryQuickFile(src, dst, size, ctx);
        if (quick != null)
        {
            quick.FinalPath ??= dst;
            return quick;
        }

        // 2) 대상이 이미 있거나 특수한 경우.
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
                        AddBytes(ctx, Math.Max(size, 0));
                        AddFiles(ctx, files);
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
            ? MoveDirectory(src, dst, dstExists, merge, size, files, ctx)
            : MoveFile(src, dst, replace, size, ctx);
        outcome.FinalPath ??= finalPath;
        return outcome;
    }

    // ------------------------------------------------------------------ 파일

    private SemaphoreSlim GateFor(long size) => size >= LargeFile ? _largeGate : _ioGate;

    /// <summary>대상이 없으면 이 한 번의 호출로 끝난다. 대상이 이미 있으면 null.</summary>
    private Outcome? TryQuickFile(string src, string dst, long size, Ctx ctx)
    {
        string ls = PathUtil.ToExtended(src), ld = PathUtil.ToExtended(dst);
        var gate = GateFor(size);
        gate.Wait(_ct);
        try
        {
            if (!_opt.ForceCopy)
            {
                if (Win32.MoveFileEx(ls, ld, 0))
                {
                    AddBytes(ctx, size);
                    AddFiles(ctx, 1);
                    return Outcome.Moved();
                }

                int err = Marshal.GetLastWin32Error();
                if (IsExistsError(err, ld)) return null;
                if (err != Win32.ERROR_NOT_SAME_DEVICE)
                    return Outcome.Failed(Describe(err));
            }

            return CopyThenDelete(src, ls, ld, replace: false, size, ctx);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>"대상에 이미 있다"는 신호인가. 파일이 같은 이름의 폴더 위로 갈 때는 ACCESS_DENIED 가 오므로 실제 존재를 확인한다.</summary>
    private static bool IsExistsError(int err, string extendedDst)
        => err is 183 /* ERROR_ALREADY_EXISTS */ or 80 /* ERROR_FILE_EXISTS */
           || (err == Win32.ERROR_ACCESS_DENIED && Win32.GetFileAttributes(extendedDst) != 0xFFFFFFFF);

    /// <summary>충돌 처리 뒤(덮어쓰기)의 파일 이동. 낙관적 시도가 실패한 경우에만 온다.</summary>
    private Outcome MoveFile(string src, string dst, bool replace, long size, Ctx ctx)
    {
        string ls = PathUtil.ToExtended(src), ld = PathUtil.ToExtended(dst);
        if (replace) ClearReadOnly(ld);

        var gate = GateFor(size);
        gate.Wait(_ct);
        try
        {
            if (!_opt.ForceCopy)
            {
                if (Win32.MoveFileEx(ls, ld, replace ? Win32.MOVEFILE_REPLACE_EXISTING : 0))
                {
                    AddBytes(ctx, size);
                    AddFiles(ctx, 1);
                    return Outcome.Moved();
                }

                int err = Marshal.GetLastWin32Error();
                if (err != Win32.ERROR_NOT_SAME_DEVICE)
                    return Outcome.Failed(Describe(err));
            }

            return CopyThenDelete(src, ls, ld, replace, size, ctx) ?? Outcome.Failed("대상에 같은 이름이 있어 복사하지 못했습니다.");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 다른 볼륨 이동: 복사가 <b>성공한 뒤에만</b> 원본을 지운다.
    /// <paramref name="replace"/> 가 false 이고 대상이 이미 있으면 null(충돌 처리로 넘긴다).
    /// </summary>
    private Outcome? CopyThenDelete(string src, string ls, string ld, bool replace, long size, Ctx ctx)
    {
        long last = 0;
        int cancel = 0;

        Win32.CopyProgressRoutine callback = (_, transferred, _, _, _, _, _, _, _) =>
        {
            long delta = transferred - last;
            last = transferred;
            if (delta != 0) AddBytes(ctx, delta);

            if (!_running.IsSet)
            {
                try { _running.Wait(_ct); }
                catch (OperationCanceledException) { return Win32.PROGRESS_CANCEL; }
            }
            return _ct.IsCancellationRequested ? Win32.PROGRESS_CANCEL : Win32.PROGRESS_CONTINUE;
        };

        uint flags = replace ? 0u : Win32.COPY_FILE_FAIL_IF_EXISTS;
        if (size >= LargeFile && _opt.UnbufferedLargeCopy) flags |= CopyNoBuffering;

        bool ok = Win32.CopyFileEx(ls, ld, callback, IntPtr.Zero, ref cancel, flags);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        GC.KeepAlive(callback);

        if (!ok)
        {
            // 취소 시 CopyFileEx 가 불완전한 대상 파일을 지운다.
            if (err == Win32.ERROR_REQUEST_ABORTED || _ct.IsCancellationRequested)
                throw new OperationCanceledException(_ct);
            if (!replace && IsExistsError(err, ld)) return null;
            return Outcome.Failed(Describe(err));
        }

        if (!DeleteSourceFile(src, PathUtil.ToExtended(src)))
            return Outcome.Failed("복사는 끝났지만 원본을 지우지 못했습니다(다른 프로그램이 사용 중일 수 있습니다). 대상에 복사본이 있습니다.");

        AddFiles(ctx, 1);
        return Outcome.Moved();
    }

    /// <summary>DeleteFile 한 번이 보통 전부다. 읽기 전용 / 사용 중이라 실패할 때만 FastDeleter 의 재시도 사다리를 탄다.</summary>
    private static bool DeleteSourceFile(string src, string extended)
    {
        if (Win32.DeleteFile(extended)) return true;
        int err = Marshal.GetLastWin32Error();
        if (err is Win32.ERROR_FILE_NOT_FOUND or Win32.ERROR_PATH_NOT_FOUND) return true;

        var del = FastDeleter.Delete(src, 1);
        return del.Success || del.NotFound;
    }

    // ------------------------------------------------------------------ 폴더

    private readonly record struct Child(string Name, bool IsDirectory, bool IsReparse, long Size);

    /// <summary>대상이 없으면 폴더 통째로 이름을 바꾼다(같은 볼륨이면 즉시). 안 되면 null → 충돌 / 복사 경로.</summary>
    private Outcome? TryQuickDirectory(string src, string dst, long size, long files, Ctx ctx)
    {
        if (_opt.ForceCopy) return null;

        string ls = PathUtil.ToExtended(src), ld = PathUtil.ToExtended(dst);
        if (!Win32.MoveFileEx(ls, ld, 0)) return null;

        // 원본 경로는 이미 사라졌으므로 옮겨진 위치에서 크기를 센다(진행률용).
        long sz = size, fc = files;
        if (sz < 0)
        {
            var m = TreeMeasure.Measure(dst, _ct);
            sz = m.Bytes;
            fc = m.Files;
        }
        AddBytes(ctx, Math.Max(sz, 0));
        AddFiles(ctx, fc);
        return Outcome.Moved();
    }

    private Outcome MoveDirectory(string src, string dst, bool dstExists, bool merge, long size, long files, Ctx ctx)
    {
        string ls = PathUtil.ToExtended(src), ld = PathUtil.ToExtended(dst);
        TryGetAttributes(src, out uint srcAttr);
        bool isLink = (srcAttr & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0;

        // 1) 통째로 이름 바꾸기(이름 충돌로 "새 이름"을 골라 대상이 바뀐 경우 등). 같은 볼륨이면 즉시 끝난다.
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
                AddBytes(ctx, Math.Max(sz, 0));
                AddFiles(ctx, fc);
                return Outcome.Moved();
            }
            // 실패하면 안쪽 항목을 하나씩 옮겨 본다(사용 중인 파일 하나 때문에 폴더 전체가 막히지 않게).
            // 다른 볼륨이면 어차피 복사가 필요하다.
        }

        // 링크(Junction / 심볼릭 링크)는 안으로 들어가지 않는다. 이름 바꾸기가 안 되면 여기서 끝.
        if (isLink)
            return Outcome.Failed("링크(Junction/심볼릭 링크)는 다른 드라이브로 옮길 수 없습니다.");

        // 2) 안쪽 항목을 옮긴다(복사 후 삭제 / 병합). 항목이 많으면 병렬로.
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

        void Handle(Child c)
        {
            _ct.ThrowIfCancellationRequested();
            string cs = Path.Combine(src, c.Name), cd = Path.Combine(dst, c.Name);

            Outcome o = c.IsReparse
                ? MoveLink(cs, cd, ctx)
                : MoveEntry(cs, cd, c.IsDirectory, c.IsDirectory ? -1 : c.Size, c.IsDirectory ? 0 : 1, nested: true, ctx);

            switch (o.Status)
            {
                case MoveStatus.Moved: Interlocked.Increment(ref moved); break;
                case MoveStatus.Skipped: Interlocked.Increment(ref skipped); break;
                default:
                    Interlocked.Increment(ref failed);
                    lock (_tallyLock) firstError ??= $"{c.Name}: {o.Error}";
                    break;
            }
        }

        var options = new ParallelOptions { MaxDegreeOfParallelism = _degree, CancellationToken = _ct };

        void RunParallel(IEnumerable<Child> items)
        {
            try { Parallel.ForEach(items, options, Handle); }
            catch (AggregateException ae) when (ae.InnerExceptions.Any(e => e is OperationCanceledException))
            {
                throw new OperationCanceledException(_ct);
            }
        }

        if (_degree > 1 && children.Count >= 4)
        {
            if (IsRenameMode(src, dst))
            {
                // 같은 볼륨: 이 폴더(dst) 안에서는 순차로 — 파일 이름 바꾸기와 하위 폴더 통째 이동을 모두 여기서 끝낸다.
                // 통째로 옮겨지지 않는 하위 폴더(대상에 같은 폴더가 있어 병합해야 하는 것)만 남겨 두었다가,
                // 서로 다른 대상 폴더를 다루는 그 재귀들을 병렬로 돌린다.
                var deferred = new List<Child>();
                foreach (var c in children)
                {
                    _ct.ThrowIfCancellationRequested();
                    if (c.IsDirectory && !c.IsReparse)
                    {
                        string cs = Path.Combine(src, c.Name), cd = Path.Combine(dst, c.Name);
                        var quick = TryQuickDirectory(cs, cd, -1, 0, ctx);
                        if (quick == null) { deferred.Add(c); continue; }
                        Interlocked.Increment(ref moved);
                    }
                    else Handle(c);
                }
                if (deferred.Count > 1) RunParallel(deferred);
                else foreach (var c in deferred) Handle(c);
            }
            else
            {
                RunParallel(children);   // 다른 볼륨(복사): 파일마다 독립적이라 전부 병렬
            }
        }
        else
        {
            foreach (var c in children) Handle(c);
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
    private Outcome MoveLink(string src, string dst, Ctx ctx)
    {
        if (PathUtil.Exists(dst))
            return Outcome.Skipped("대상에 같은 이름이 있어 링크를 옮기지 않았습니다.");

        if (!_opt.ForceCopy && Win32.MoveFileEx(PathUtil.ToExtended(src), PathUtil.ToExtended(dst), 0))
        {
            AddFiles(ctx, 1);
            return Outcome.Moved();
        }
        return Outcome.Skipped("링크는 다른 드라이브로 옮기지 않습니다.");
    }

    private static string? RemoveEmptyDirectory(string extendedPath)
    {
        // 대부분 바로 지워진다. 읽기 전용 속성 때문에 실패할 때만 속성을 풀고 다시 시도한다.
        if (Win32.RemoveDirectory(extendedPath)) return null;
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
            BufferSize = 64 * 1024,
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

    /// <summary>
    /// 충돌 결정. 병렬로 돌아도 <b>한 번에 하나만</b> 처리한다 — 사용자에게 대화 상자를 동시에 여러 개 띄우지 않고,
    /// "이후 모두 적용"이 뒤따르는 충돌에 확실히 반영되게 한다.
    /// </summary>
    private ConflictChoice Decide(ConflictInfo info)
    {
        lock (_decideLock)
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

    private void AddBytes(Ctx ctx, long delta)
    {
        Interlocked.Add(ref ctx.Bytes, delta);
        Interlocked.Add(ref _bytesDone, delta);
        Report(force: false);
    }

    private void AddFiles(Ctx ctx, long delta)
    {
        Interlocked.Add(ref ctx.Files, delta);
        Interlocked.Add(ref _filesDone, delta);
        Report(force: false);
    }

    /// <summary>진행 보고. 여러 스레드가 불러도 80ms 에 한 번만 나간다(경쟁에서 진 스레드는 그냥 지나간다).</summary>
    private void Report(bool force)
    {
        var progress = _opt.Progress;
        if (progress == null) return;

        long now = _clock.ElapsedMilliseconds;
        long last = Interlocked.Read(ref _lastReportMs);
        if (!force)
        {
            if (now - last < 80) return;
            if (Interlocked.CompareExchange(ref _lastReportMs, now, last) != last) return;
        }
        else
        {
            Interlocked.Exchange(ref _lastReportMs, now);
        }

        progress.Report(new MoveProgress
        {
            BytesDone = Interlocked.Read(ref _bytesDone),
            BytesTotal = _bytesTotal,
            FilesDone = Interlocked.Read(ref _filesDone),
            FilesTotal = _filesTotal,
            ItemIndex = Volatile.Read(ref _started),
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

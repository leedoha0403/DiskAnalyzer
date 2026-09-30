using DiskAnalyzer.Core.Interop;

namespace DiskAnalyzer.Core.QuickMove;

/// <summary>
/// 빠른 복사. <see cref="MoveRequest.IsCopy"/> 인 항목은 이름 바꾸기를 시도하지 않고 항상 CopyFileEx 로 복사한다.
/// 원본은 어떤 경우에도 지우지 않는다. 충돌 처리(묻기 / 덮어쓰기 / 건너뛰기 / 새 이름)와 진행률 · 일시정지 · 취소는 이동과 같은 경로를 쓴다.
/// Junction / 심볼릭 링크는 따라가지 않고 건너뛴다(링크 대상을 통째로 복사하는 사고를 막는다).
/// </summary>
public sealed partial class MoveEngine
{
    private Outcome CopyEntry(string src, string dst, bool isDir, long size, long files, bool nested, Ctx ctx)
    {
        _ct.ThrowIfCancellationRequested();
        WaitIfPaused();

        if (PathUtil.Equal(src, dst))
            return Outcome.Failed("이미 같은 위치에 있는 항목입니다.");

        string finalPath = dst;
        bool merge = false, replace = false;

        // 낙관적 시도: 파일은 대상이 없다고 보고 곧바로 복사한다(있을 때만 충돌 처리).
        if (!isDir)
        {
            var quick = CopyFileGated(src, dst, replace: false, size, ctx);
            if (quick != null)
            {
                quick.FinalPath ??= dst;
                return quick;
            }
        }

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
                switch (Decide(BuildInfo(src, dst, isDir, dstIsDir, size)))
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
            ? CopyDirectory(src, dst, merge, size, files, ctx)
            : CopyFileGated(src, dst, replace, size, ctx) ?? Outcome.Failed("대상에 같은 이름이 있어 복사하지 못했습니다.");
        outcome.FinalPath ??= finalPath;
        return outcome;
    }

    /// <summary>동시 실행 수를 지키며 파일 하나를 복사한다. 대상이 이미 있고 <paramref name="replace"/> 가 false 면 null.</summary>
    private Outcome? CopyFileGated(string src, string dst, bool replace, long size, Ctx ctx)
    {
        string ls = PathUtil.ToExtended(src), ld = PathUtil.ToExtended(dst);
        if (replace) ClearReadOnly(ld);

        var gate = GateFor(size);
        gate.Wait(_ct);
        try
        {
            var o = CopyFileOnly(ls, ld, replace, size, ctx);
            if (o is { Status: MoveStatus.Moved }) AddFiles(ctx, 1);
            return o;
        }
        finally
        {
            gate.Release();
        }
    }

    private Outcome CopyDirectory(string src, string dst, bool merge, long size, long files, Ctx ctx)
    {
        TryGetAttributes(src, out uint srcAttr);
        if ((srcAttr & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            return Outcome.Skipped("링크(Junction/심볼릭 링크)는 복사하지 않습니다.");

        string ld = PathUtil.ToExtended(dst);
        List<Child> children;
        bool freshDir = !Directory.Exists(dst);
        DateTime createdUtc = default, writtenUtc = default;
        try
        {
            if (freshDir)
            {
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

        int copied = 0, skipped = 0, failed = 0;
        string? firstError = null;

        void Handle(Child c)
        {
            _ct.ThrowIfCancellationRequested();
            string cs = Path.Combine(src, c.Name), cd = Path.Combine(dst, c.Name);

            Outcome o;
            if (c.IsReparse)
            {
                AddFiles(ctx, 1);
                o = Outcome.Skipped("링크는 복사하지 않습니다.");
            }
            else
            {
                o = CopyEntry(cs, cd, c.IsDirectory, c.IsDirectory ? -1 : c.Size, c.IsDirectory ? 0 : 1, nested: true, ctx);
            }

            switch (o.Status)
            {
                case MoveStatus.Moved: Interlocked.Increment(ref copied); break;
                case MoveStatus.Skipped: Interlocked.Increment(ref skipped); break;
                default:
                    Interlocked.Increment(ref failed);
                    lock (_tallyLock) firstError ??= $"{c.Name}: {o.Error}";
                    break;
            }
        }

        if (_degree > 1 && children.Count >= 4)
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = _degree, CancellationToken = _ct };
            try { Parallel.ForEach(children, options, Handle); }
            catch (AggregateException ae) when (ae.InnerExceptions.Any(e => e is OperationCanceledException))
            {
                throw new OperationCanceledException(_ct);
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
                Error = $"{failed:N0}개 항목을 복사하지 못했습니다 — {firstError}",
                Note = $"복사 {copied:N0}개 · 건너뜀 {skipped:N0}개",
            };
        }

        if (freshDir) TrySetDirectoryTimes(dst, createdUtc, writtenUtc);

        if (skipped > 0)
        {
            return copied == 0 && children.Count > 0
                ? Outcome.Skipped("같은 이름의 항목이라 모두 건너뛰었습니다.")
                : new Outcome { Status = MoveStatus.Moved, Note = $"{skipped:N0}개 항목은 건너뛰었습니다." };
        }

        return Outcome.Moved();
    }
}

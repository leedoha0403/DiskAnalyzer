using DiskAnalyzer.Core.Analysis;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Services;

public enum DeletionOutcomeKind
{
    Deleted,
    /// <summary>12. 최초 스캔 이후 이미 사라진 경우 - 건너뛴다.</summary>
    AlreadyGone,
    /// <summary>12. 다른 프로세스가 사용 중.</summary>
    InUse,
    /// <summary>9. 삭제 직전 재검증에서 보호 등급이 P0 로 올라가 차단됨.</summary>
    Protected,
    Failed,
}

public sealed class DeletionRequest
{
    public required RowKind Kind { get; init; }
    public required int Id { get; init; }
    public required string Path { get; init; }
    public required long Size { get; init; }
    public string Name { get; init; } = string.Empty;
    public CategoryFlags Flags { get; init; }
}

public sealed class DeletionOutcome
{
    public required DeletionRequest Request { get; init; }
    public required DeletionOutcomeKind Kind { get; init; }
    public string Message { get; init; } = string.Empty;

    public string StatusText => Kind switch
    {
        DeletionOutcomeKind.Deleted => "삭제됨",
        DeletionOutcomeKind.AlreadyGone => "이미 삭제됨",
        DeletionOutcomeKind.InUse => "사용 중",
        DeletionOutcomeKind.Protected => "보호됨",
        _ => "실패",
    };
}

public readonly record struct DeletionProgress(
    int Done, int Total, long BytesDone, long BytesTotal, string CurrentPath, long EntriesDeleted = 0)
{
    public double Ratio => Total > 0 ? Done / (double)Total : 0d;
}

public sealed class DeletionSummary
{
    public required IReadOnlyList<DeletionOutcome> Outcomes { get; init; }
    public bool Cancelled { get; init; }

    public IEnumerable<DeletionOutcome> Succeeded => Outcomes.Where(o => o.Kind == DeletionOutcomeKind.Deleted);
    public IEnumerable<DeletionOutcome> AlreadyGone => Outcomes.Where(o => o.Kind == DeletionOutcomeKind.AlreadyGone);
    public IEnumerable<DeletionOutcome> Failed =>
        Outcomes.Where(o => o.Kind is DeletionOutcomeKind.Failed
                                   or DeletionOutcomeKind.InUse
                                   or DeletionOutcomeKind.Protected);

    public int SucceededCount => Succeeded.Count();
    public long SucceededBytes => Succeeded.Sum(o => o.Request.Size);
    public int FailedCount => Failed.Count();
    public long FailedBytes => Failed.Sum(o => o.Request.Size);
    public int AlreadyGoneCount => AlreadyGone.Count();
    public long AlreadyGoneBytes => AlreadyGone.Sum(o => o.Request.Size);

    /// <summary>14. UI 에서 즉시 제거해도 되는 항목 = 실제로 사라진 것들.</summary>
    public IReadOnlyList<(RowKind Kind, int Id)> RemovedNodes =>
        Outcomes.Where(o => o.Kind is DeletionOutcomeKind.Deleted or DeletionOutcomeKind.AlreadyGone)
                .Select(o => (o.Request.Kind, o.Request.Id))
                .ToList();
}

/// <summary>
/// 12/13. 다중 삭제 실행기.
///
///  - 삭제 직전에 파일 상태를 다시 확인한다(최초 스캔 이후 바뀌었을 수 있다).
///  - <strong>한 파일이 실패해도 전체 작업을 멈추지 않는다.</strong>
///  - 진행 상황을 보고하고 취소를 지원한다.
///  - 기본은 휴지통. 영구 삭제는 호출자가 명시적으로 요청한 경우에만(60).
///
/// 영구 삭제는 <see cref="FastDeleter"/> 로 병렬 처리한다(셸을 거치지 않는다). 휴지통 이동은 되돌릴 수 있어야 하므로
/// 셸(SHFileOperation)을 그대로 쓰며 순차 처리한다.
/// </summary>
public static class DeletionService
{
    public static async Task<DeletionSummary> DeleteAsync(
        IReadOnlyList<DeletionRequest> items,
        bool permanent,
        IProgress<DeletionProgress>? progress = null,
        CancellationToken ct = default)
    {
        return permanent
            ? await DeletePermanentAsync(items, progress, ct).ConfigureAwait(false)
            : await DeleteToRecycleBinAsync(items, progress, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ 휴지통: 순차

    private static async Task<DeletionSummary> DeleteToRecycleBinAsync(
        IReadOnlyList<DeletionRequest> items, IProgress<DeletionProgress>? progress, CancellationToken ct)
    {
        var outcomes = new List<DeletionOutcome>(items.Count);
        long totalBytes = items.Sum(i => i.Size);
        long doneBytes = 0;
        bool cancelled = false;

        await Task.Run(() =>
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }

                var item = items[i];
                progress?.Report(new DeletionProgress(i, items.Count, doneBytes, totalBytes, item.Path));

                outcomes.Add(Verify(item) ?? MoveToRecycleBin(item));
                doneBytes += item.Size;
            }

            progress?.Report(new DeletionProgress(outcomes.Count, items.Count, doneBytes, totalBytes, string.Empty));
        }, CancellationToken.None).ConfigureAwait(false);

        return new DeletionSummary { Outcomes = outcomes, Cancelled = cancelled };
    }

    // ------------------------------------------------------------------ 영구 삭제: 병렬

    private static async Task<DeletionSummary> DeletePermanentAsync(
        IReadOnlyList<DeletionRequest> items, IProgress<DeletionProgress>? progress, CancellationToken ct)
    {
        var results = new DeletionOutcome?[items.Count];
        long totalBytes = items.Sum(i => i.Size);
        long doneBytes = 0, entries = 0;
        int doneItems = 0;
        bool cancelled = false;

        // 항목 수준 병렬도와 폴더 내부 병렬도를 나눠 쓴다. 폴더 하나만 지울 때는 내부 병렬도를 전부 쓰고,
        // 파일 수천 개를 골랐을 때는 항목끼리 병렬로 돌린다. 곱이 코어 수를 크게 넘지 않게 한다.
        int degree = items.Count > 0 ? FastDeleter.RecommendedParallelism(items[0].Path) : 1;
        int itemDegree = Math.Max(1, Math.Min(degree, items.Count));
        int treeDegree = Math.Max(2, degree / itemDegree);

        // 진행 보고는 초당 수만 번 발생할 수 있다. 그대로 UI 로 보내면 Dispatcher 큐가 넘치므로 50ms 로 솎는다.
        long lastReport = 0;
        long interval = System.Diagnostics.Stopwatch.Frequency / 20;

        void Report(string path, bool force = false)
        {
            if (progress == null) return;
            if (!force)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                long prev = Volatile.Read(ref lastReport);
                if (now - prev < interval || Interlocked.CompareExchange(ref lastReport, now, prev) != prev) return;
            }

            progress.Report(new DeletionProgress(
                Volatile.Read(ref doneItems), items.Count,
                Interlocked.Read(ref doneBytes), totalBytes, path, Interlocked.Read(ref entries)));
        }

        await Task.Run(() =>
        {
            try
            {
                Parallel.For(0, items.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = itemDegree, CancellationToken = ct },
                    i =>
                    {
                        var item = items[i];
                        Report(item.Path);

                        // 폴더 안에서 지운 파일 수를 누적 진행 표시에 더한다(한 항목이 오래 걸려도 진행이 움직이도록).
                        long reported = 0;
                        results[i] = Verify(item) ?? FastDelete(item, treeDegree, ct, n =>
                        {
                            long previous = Interlocked.Exchange(ref reported, n);
                            Interlocked.Add(ref entries, n - previous);
                            Report(item.Path);
                        });

                        Interlocked.Add(ref doneBytes, item.Size);
                        Interlocked.Increment(ref doneItems);
                        Report(item.Path);
                    });
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            Report(string.Empty, force: true);
        }, CancellationToken.None).ConfigureAwait(false);

        var outcomes = results.Where(r => r != null).Select(r => r!).ToList();
        return new DeletionSummary { Outcomes = outcomes, Cancelled = cancelled || ct.IsCancellationRequested };
    }

    // ------------------------------------------------------------------ 한 항목

    /// <summary>
    /// 삭제 직전 검증. 통과하면 null, 삭제하면 안 되거나 할 필요가 없으면 그 결과를 돌려준다.
    /// 스캔 시점의 판정을 그대로 믿지 않고 지금 다시 계산한다(9). 폴더는 하위까지 훑어 가장 높은 등급을 취한다(11).
    /// </summary>
    private static DeletionOutcome? Verify(DeletionRequest item)
    {
        // 12. 삭제 전 최종 검증 - 아직 존재하는가.
        bool exists = item.Kind == RowKind.Directory
            ? Directory.Exists(item.Path)
            : File.Exists(item.Path);

        if (!exists)
        {
            return new DeletionOutcome
            {
                Request = item,
                Kind = DeletionOutcomeKind.AlreadyGone,
                Message = "스캔 이후 이미 삭제되었거나 이동되었습니다.",
            };
        }

        var verdict = item.Kind == RowKind.Directory
            ? ProtectionEvaluator.EvaluateFolderTree(item.Path, item.Flags)
            : ProtectionEvaluator.EvaluateForDeletion(item.Path, false, item.Flags);

        if (verdict.Level == ProtectionLevel.P0)
        {
            return new DeletionOutcome
            {
                Request = item,
                Kind = DeletionOutcomeKind.Protected,
                Message = verdict.Reason,
            };
        }

        return null;
    }

    private static DeletionOutcome FastDelete(
        DeletionRequest item, int parallelism, CancellationToken ct, Action<long> onFiles)
    {
        var r = FastDeleter.Delete(item.Path, parallelism, ct, onFiles);

        if (r.NotFound)
            return new DeletionOutcome { Request = item, Kind = DeletionOutcomeKind.AlreadyGone, Message = "이미 삭제되었습니다." };

        if (r.Success)
            return new DeletionOutcome { Request = item, Kind = DeletionOutcomeKind.Deleted };

        if (r.Cancelled && r.Failed == 0)
        {
            return new DeletionOutcome
            {
                Request = item,
                Kind = DeletionOutcomeKind.Failed,
                Message = $"중지되어 일부만 삭제되었을 수 있습니다 ({r.FilesDeleted:N0}개 삭제됨). 새로고침으로 실제 상태를 확인하세요.",
            };
        }

        // 일부만 실패한 경우에도 지워진 것은 지워졌다. 원인과 개수를 함께 알려 준다.
        string cause = r.FirstErrorText.Length > 0 ? r.FirstErrorText : "알 수 없는 오류";
        string partial = r.FilesDeleted + r.DirectoriesDeleted > 0
            ? $" 나머지 {r.FilesDeleted:N0}개 파일은 삭제되었습니다."
            : string.Empty;
        bool allInUse = r.InUse == r.Failed;

        return new DeletionOutcome
        {
            Request = item,
            Kind = allInUse ? DeletionOutcomeKind.InUse : DeletionOutcomeKind.Failed,
            Message = allInUse
                ? $"다른 프로그램이 사용 중인 항목 {r.Failed:N0}개를 삭제하지 못했습니다 ({r.FirstFailedPath}).{partial}"
                : $"{r.Failed:N0}개 항목을 삭제하지 못했습니다 — {cause} ({r.FirstFailedPath}).{partial}",
        };
    }

    private static DeletionOutcome MoveToRecycleBin(DeletionRequest item)
    {
        bool ok = ShellService.MoveToRecycleBin(item.Path);
        if (ok) return new DeletionOutcome { Request = item, Kind = DeletionOutcomeKind.Deleted };

        // 실패 원인을 구분해 준다. 사라졌으면 성공으로 간주해도 되고,
        // 남아 있으면 잠겨 있는지(사용 중) 확인해서 사용자에게 알린다.
        bool stillThere = item.Kind == RowKind.Directory
            ? Directory.Exists(item.Path)
            : File.Exists(item.Path);

        if (!stillThere)
            return new DeletionOutcome { Request = item, Kind = DeletionOutcomeKind.Deleted };

        if (item.Kind == RowKind.File && IsLocked(item.Path))
        {
            return new DeletionOutcome
            {
                Request = item,
                Kind = DeletionOutcomeKind.InUse,
                Message = "다른 프로그램이 사용 중이라 삭제할 수 없습니다.",
            };
        }

        return new DeletionOutcome
        {
            Request = item,
            Kind = DeletionOutcomeKind.Failed,
            Message = "삭제에 실패했습니다(권한 또는 경로 문제).",
        };
    }

    /// <summary>
    /// 파일이 잠겨 있는지 확인한다.
    /// 공유 모드 없이 열어 보고 실패하면 사용 중이다. 내용은 읽지 않고 즉시 닫는다.
    /// </summary>
    private static bool IsLocked(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }
}

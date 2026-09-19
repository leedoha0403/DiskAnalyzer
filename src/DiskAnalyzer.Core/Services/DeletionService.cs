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
    int Done, int Total, long BytesDone, long BytesTotal, string CurrentPath)
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
/// </summary>
public static class DeletionService
{
    public static async Task<DeletionSummary> DeleteAsync(
        IReadOnlyList<DeletionRequest> items,
        bool permanent,
        IProgress<DeletionProgress>? progress = null,
        CancellationToken ct = default)
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

                outcomes.Add(DeleteOne(item, permanent));
                doneBytes += item.Size;
            }

            progress?.Report(new DeletionProgress(outcomes.Count, items.Count, doneBytes, totalBytes, string.Empty));
        }, CancellationToken.None).ConfigureAwait(false);

        return new DeletionSummary { Outcomes = outcomes, Cancelled = cancelled };
    }

    private static DeletionOutcome DeleteOne(DeletionRequest item, bool permanent)
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

        // 9. 스캔 시점의 판정을 그대로 믿지 않는다. 지금 다시 보호 등급을 계산한다.
        //    폴더는 하위까지 훑어 가장 높은 등급을 취한다(11).
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

        bool ok = permanent
            ? ShellService.DeletePermanently(item.Path)
            : ShellService.MoveToRecycleBin(item.Path);

        if (ok)
        {
            return new DeletionOutcome { Request = item, Kind = DeletionOutcomeKind.Deleted };
        }

        // 실패 원인을 구분해 준다. 사라졌으면 성공으로 간주해도 되고,
        // 남아 있으면 잠겨 있는지(사용 중) 확인해서 사용자에게 알린다.
        bool stillThere = item.Kind == RowKind.Directory
            ? Directory.Exists(item.Path)
            : File.Exists(item.Path);

        if (!stillThere)
        {
            return new DeletionOutcome { Request = item, Kind = DeletionOutcomeKind.Deleted };
        }

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

namespace DiskAnalyzer.Core.Models;

/// <summary>14. 삭제/갱신을 NodeStore 에 반영한 결과 요약.</summary>
public readonly record struct MutationSummary(
    int RemovedFiles,
    int RemovedDirectories,
    long RemovedBytes,
    int ResizedFiles,
    long ResizedDelta)
{
    public bool Any => RemovedFiles > 0 || RemovedDirectories > 0 || ResizedFiles > 0;
    public long NetBytes => RemovedBytes - ResizedDelta;
}

/// <summary>12. 삭제 직전 / 새로고침 시 실제 파일 시스템과 대조한 결과.</summary>
public enum VerifyState
{
    /// <summary>스캔 당시와 동일.</summary>
    Unchanged,
    /// <summary>존재하지만 크기 또는 시각이 달라졌다.</summary>
    Changed,
    /// <summary>이미 사라졌다(외부 삭제 / 이동).</summary>
    Missing,
    /// <summary>존재 여부를 확인할 수 없다(권한 등).</summary>
    Inaccessible,
}

public sealed class VerifyOutcome
{
    public required RowKind Kind { get; init; }
    public required int Id { get; init; }
    public required string Path { get; init; }
    public required VerifyState State { get; init; }
    public long OldSize { get; init; }
    public long NewSize { get; init; }
    public long NewModified { get; init; }
    public long NewAccessed { get; init; }

    public long Delta => NewSize - OldSize;

    public string StateText => State switch
    {
        VerifyState.Missing => "이미 삭제됨",
        VerifyState.Changed => "크기 변경됨",
        VerifyState.Inaccessible => "확인 불가",
        _ => "정상",
    };
}

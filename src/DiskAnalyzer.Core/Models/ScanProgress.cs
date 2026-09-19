namespace DiskAnalyzer.Core.Models;

/// <summary>
/// 5. 스캔 진행 상태 / 42. 상태바 / 43. 성능 모니터용 스냅샷.
/// 불변 record struct 로 만들어 UI 스레드가 lock 없이 통째로 복사해 갈 수 있게 한다.
/// </summary>
public readonly record struct ScanProgress(
    ScanState State,
    ScanMode Mode,
    string Root,
    string CurrentPath,
    long FileCount,
    long DirectoryCount,
    long TotalSize,
    long VolumeUsedBytes,
    double ElapsedSeconds,
    double FilesPerSecond,
    double DirectoriesPerSecond,
    int SkippedFolders,
    int AccessDenied,
    int Errors,
    int WorkerCount,
    int DirectoryQueueSize,
    int ResultQueueSize,
    long ManagedMemoryBytes,
    long PeakMemoryBytes,
    double UiUpdateLatencyMs,
    string? Message)
{
    /// <summary>
    /// 진행률. 파일 시스템 전체 파일 수를 미리 알 수 없으므로
    /// "지금까지 집계한 용량 / 볼륨 사용량" 을 진행률 근사치로 사용한다.
    /// </summary>
    public double Ratio => VolumeUsedBytes > 0
        ? Math.Clamp((double)TotalSize / VolumeUsedBytes, 0d, 1d)
        : 0d;

    public bool IsBusy => State is ScanState.Preparing or ScanState.Running or ScanState.Finalizing;

    public static ScanProgress Empty { get; } = new(
        ScanState.Idle, ScanMode.Auto, string.Empty, string.Empty,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null);
}

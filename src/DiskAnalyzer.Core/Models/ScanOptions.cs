namespace DiskAnalyzer.Core.Models;

/// <summary>47. 기본 설정.</summary>
public sealed class ScanOptions
{
    public ScanMode Mode { get; set; } = ScanMode.Auto;

    /// <summary>0 = Auto (드라이브 종류에 따라 자동 결정).</summary>
    public int WorkerCount { get; set; }

    /// <summary>37. 기본값 false - Reparse Point 를 따라가지 않아 무한 순회를 원천 차단한다.</summary>
    public bool FollowReparsePoints { get; set; }

    public bool ShowHiddenFiles { get; set; } = true;
    public bool ShowSystemFiles { get; set; } = true;
    public bool CacheResults { get; set; } = true;

    /// <summary>Top-K 힙 크기. UI 는 이 안에서 TOP 50/100/500/1000 을 잘라 쓴다.</summary>
    public int TopFileCount { get; set; } = 1000;

    /// <summary>워커가 Aggregator 로 배치를 넘기는 단위. 너무 작으면 채널 오버헤드, 너무 크면 UI 지연.</summary>
    public int BatchSize { get; set; } = 4096;

    public ScanOptions Clone() => (ScanOptions)MemberwiseClone();

    /// <summary>
    /// 22. 스레드 수 제어.
    /// 파일 시스템 스캔은 I/O Bound 이며, HDD 에서는 병렬 요청이 오히려 seek 를 유발해 느려진다.
    /// SSD/NVMe 라도 코어 수만큼 늘리면 Queue Contention 과 커널 디렉터리 캐시 경합이 커지므로 상한을 둔다.
    /// (28코어 머신에서 12 초과로 늘려도 FindNextFile 처리량이 늘지 않는다는 벤치 결과 기준)
    /// </summary>
    public int ResolveWorkerCount(DriveKind kind)
    {
        if (WorkerCount > 0) return Math.Clamp(WorkerCount, 1, 64);
        int cores = Environment.ProcessorCount;
        return kind switch
        {
            DriveKind.Hdd => 2,
            DriveKind.Ssd => Math.Clamp(cores, 4, 12),
            _ => Math.Clamp(cores / 2, 2, 8),
        };
    }
}

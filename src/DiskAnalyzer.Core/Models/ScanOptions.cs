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

    /// <summary>
    /// 크기를 논리로 볼지 물리(디스크 할당)로 볼지.
    ///
    /// <para>기본은 논리다. 탐색기의 "크기" 열과 같아 대조하기 쉽고, 지금까지의 실측 수치도 이 기준이다.
    /// NTFS 압축을 쓰는 볼륨이라면 물리 쪽이 실제 회수 가능한 용량에 가깝다.</para>
    /// </summary>
    public SizeBasis SizeBasis { get; set; } = SizeBasis.Logical;

    /// <summary>
    /// 하드 링크를 한 번만 셀지. 같은 실체가 여러 경로에 걸려 있으면 처음 만난 것만 세고
    /// 나머지는 0 바이트로 둔다(<c>C:\Windows\WinSxS</c> 가 대표적이다).
    ///
    /// <para>켜면 열거를 <c>FindFirstFileEx</c> 대신 파일 id 까지 주는 일괄 API 로 바꾼다.
    /// Fast(MFT) 스캔은 레코드 하나가 곧 파일 하나라 이 설정과 무관하게 언제나 한 번만 센다.</para>
    /// </summary>
    public bool DeduplicateHardLinks { get; set; }

    /// <summary>Top-K 힙 크기. UI 는 이 안에서 TOP 50/100/500/1000 을 잘라 쓴다.</summary>
    public int TopFileCount { get; set; } = 1000;

    /// <summary>워커가 Aggregator 로 배치를 넘기는 단위. 너무 작으면 채널 오버헤드, 너무 크면 UI 지연.</summary>
    public int BatchSize { get; set; } = 4096;

    private IReadOnlyList<string> _exclusionPatterns = Array.Empty<string>();
    private ExclusionRules? _exclusions;

    /// <summary>48. 스캔에서 뺄 패턴(한 줄에 하나). 문법은 <see cref="ExclusionRules"/> 참고.</summary>
    public IReadOnlyList<string> ExclusionPatterns
    {
        get => _exclusionPatterns;
        set
        {
            _exclusionPatterns = value ?? Array.Empty<string>();
            _exclusions = null;      // 다음에 물어볼 때 다시 컴파일한다
        }
    }

    /// <summary>컴파일된 제외 규칙. 스캐너는 이것만 본다.</summary>
    public ExclusionRules Exclusions => _exclusions ??= ExclusionRules.Parse(_exclusionPatterns);

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

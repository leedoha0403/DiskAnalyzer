using System.Threading.Channels;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Scanning;

/// <summary>워커 -> Aggregator 파이프라인의 공유 컨텍스트.</summary>
internal sealed class ScanRuntime
{
    public required string RootPath { get; init; }
    public required ScanOptions Options { get; init; }
    public required ChannelWriter<ScanBatch> BatchWriter { get; init; }
    public required ScanBatchPool Pool { get; init; }
    public required ScanSharedState Stats { get; init; }
    public DriveKind DriveKind { get; init; }

    /// <summary>논리 크기를 디스크 할당 크기로 바꿔 주는 도구. 논리 기준이면 그대로 통과시킨다.</summary>
    public required AllocationSizer Sizer { get; init; }

    /// <summary>하드 링크를 한 번만 세기 위한 필터. 꺼져 있으면 null.</summary>
    public HardLinkFilter? HardLinks { get; init; }

    /// <summary>
    /// 파일 id · 할당 크기가 필요한가. 필요하면 <c>FindFirstFileEx</c> 대신
    /// 그 둘까지 주는 일괄 열거를 쓴다(그 대신 한 항목당 할 일이 조금 늘어난다).
    /// </summary>
    public bool NeedsFileIds => HardLinks != null || Sizer.Basis == SizeBasis.Physical;
}

/// <summary>
/// 워커들이 공유하는 최소한의 상태.
/// 파일 단위로 건드리는 필드는 하나도 없다 - 모두 "디렉터리 단위" 또는 "예외 발생 시"에만 갱신된다(23).
/// </summary>
internal sealed class ScanSharedState
{
    private string _currentPath = string.Empty;

    public int WorkerCount;
    public int DirectoryQueueSize;
    public int ResultQueueSize;
    public int SkippedFolders;
    public int AccessDenied;
    public int Errors;

    /// <summary>48. 제외 규칙으로 빼놓은 수. 폴더는 하위까지 통째로 빠지므로 "가지치기한 폴더 수"다.</summary>
    public int ExcludedFolders;
    public int ExcludedFiles;

    /// <summary>Fast Scan 의 MFT 읽기 단계 진행 상황(레코드 단위).</summary>
    public long MftRecordsRead;
    public long MftRecordsTotal;

    /// <summary>워커가 현재 처리 중인 경로(표시용). 참조 대입은 원자적이라 Lock 이 필요 없다.</summary>
    public string CurrentPath
    {
        get => Volatile.Read(ref _currentPath);
        set => Volatile.Write(ref _currentPath, value);
    }
}

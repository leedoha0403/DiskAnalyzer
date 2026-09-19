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

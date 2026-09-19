using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Scanning;

/// <summary>스캔 1회의 최종 결과. UI 는 이 객체만 붙들고 있으면 된다.</summary>
public sealed class ScanResult
{
    public required NodeStore Store { get; init; }
    public required string RootPath { get; init; }
    public ScanMode Mode { get; init; }
    public DateTime CompletedAt { get; init; }
    public double ElapsedSeconds { get; init; }

    // 14. 삭제 직후 UI 즉시 반영:
    //     고정 스냅샷 값을 들고 있으면 파일을 지운 뒤에도 옛날 숫자가 남는다.
    //     항상 저장소의 현재 상태에서 읽는다.
    public long FileCount => Store.FileCount;
    public long DirectoryCount => Math.Max(0, Store.DirectoryCount - 1);
    public long TotalSize => Store.TotalSize;

    public long VolumeUsedBytes { get; init; }
    public int SkippedFolders { get; init; }
    public int AccessDenied { get; init; }
    public int Errors { get; init; }
    public bool Cancelled { get; init; }

    /// <summary>40. 이전 스캔 결과(캐시)에서 복원된 데이터인지 여부. UI 에 명확히 표시한다.</summary>
    public bool FromCache { get; init; }

    public string ModeText => Mode switch
    {
        ScanMode.Fast => "NTFS Fast Scan",
        ScanMode.Compatibility => "Compatibility Scan",
        _ => "-",
    };
}

/// <summary>
/// 30. Incremental UI.
/// 스캔 도중에도 Aggregator 스레드가 현재 폴더의 행을 만들어 이 스냅샷으로 게시한다.
/// UI 는 참조 하나만 읽어가므로 Lock 도, NodeStore 동시 접근도 없다.
/// </summary>
public sealed class ViewSnapshot
{
    public required int DirectoryId { get; init; }
    public required string Path { get; init; }
    public required IReadOnlyList<EntryRow> Rows { get; init; }
    public required IReadOnlyList<(int Id, string Name)> Breadcrumb { get; init; }
    public long TotalSize { get; init; }
    public bool Partial { get; init; }
    public int Version { get; init; }

    /// <summary>큰 파일 탭이 활성일 때만 채워진다(34. 보지 않는 데이터는 만들지 않는다).</summary>
    public IReadOnlyList<EntryRow>? TopFiles { get; init; }

    /// <summary>파일 유형 탭이 활성일 때만 채워진다.</summary>
    public IReadOnlyList<ExtensionRow>? Extensions { get; init; }
}

/// <summary>스캔 중 Aggregator 가 어떤 스냅샷을 만들지 UI 가 알려 준다.</summary>
/// <summary>값은 화면의 탭 순서와 같다(MainWindow 가 SelectedIndex 를 그대로 캐스팅한다).</summary>
public enum LiveTab
{
    Folder,
    Treemap,
    LargeFiles,
    FileTypes,
    Cleanup,
}

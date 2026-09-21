namespace DiskAnalyzer.Core.Models;

public enum RowKind { Directory, File }

/// <summary>
/// 49. 검색 결과 "경로별 묶기"에서 이 행이 무엇인가.
/// <see cref="None"/> 이 아닌 행은 묶음을 펼치고 접기 위한 머리글이며 실제 항목이 아니다 —
/// 선택 / 삭제 대상에서 반드시 빠져야 한다.
/// </summary>
public enum SearchRowKind { None, Folder, Pattern }

/// <summary>
/// UI 로 넘기는 표시용 행.
/// NodeStore 내부 표현(SoA + 문자열 핸들)과 분리되어 있으며,
/// "현재 화면/탭에 필요한 만큼"만 생성한다(31. 가상화 / 34. Lazy Loading).
/// </summary>
public sealed class EntryRow
{
    public RowKind Kind { get; init; }

    /// <summary>Directory 면 dirId, File 이면 file index.</summary>
    public int Id { get; init; }

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public long Size { get; init; }
    public double Ratio { get; init; }
    public long FileCount { get; init; }
    public long DirectoryCount { get; init; }
    public DateTime Modified { get; init; }
    public DateTime Created { get; init; }
    public DateTime Accessed { get; init; }
    public CategoryFlags Category { get; init; }
    public string Extension { get; init; } = string.Empty;

    // ---- 49. 경로별 묶기 전용 표시 상태. 묶지 않은 목록에서는 모두 기본값이라 아무 영향이 없다.

    /// <summary>들여쓰기 단계. 묶음 안에 있을 때만 0 보다 크다.</summary>
    public int Depth { get; set; }

    /// <summary>묶음 머리글인가. <see cref="SearchRowKind.None"/> 이면 진짜 항목이다.</summary>
    public SearchRowKind SearchKind { get; set; }

    /// <summary>머리글이 품고 있는 결과 수.</summary>
    public int MatchCount { get; set; }

    /// <summary>머리글이 펼쳐져 있는가.</summary>
    public bool IsExpanded { get; set; }

    /// <summary>머리글이면 true. 선택 / 삭제에서 걸러내는 기준이다.</summary>
    public bool IsSearchGroup => SearchKind != SearchRowKind.None;

    /// <summary>머리글 앞의 펼침 표시. 항목 행은 빈 칸이라 이름이 같은 자리에서 시작한다.</summary>
    public string ChevronText => IsSearchGroup ? (IsExpanded ? "▾" : "▸") : string.Empty;

    /// <summary>머리글의 오른쪽에 붙는 건수. 항목 행은 비운다.</summary>
    public string MatchCountText => IsSearchGroup ? $"×{MatchCount:N0}건" : string.Empty;

    public bool IsDirectory => Kind == RowKind.Directory;
    public string CreatedText => Created == default ? "-" : Created.ToString("yyyy-MM-dd");
    public string AccessedText => Accessed == default ? "-" : Accessed.ToString("yyyy-MM-dd");
    public string SizeText => SizeFormatter.Format(Size);
    public string RatioText => SizeFormatter.Percent(Ratio);
    public string CategoryText => Categorizer.ToTagString(Category);
    public string FileCountText => Kind == RowKind.Directory ? SizeFormatter.Count(FileCount) : string.Empty;
    public string DirCountText => Kind == RowKind.Directory ? SizeFormatter.Count(DirectoryCount) : string.Empty;
    public string ModifiedText => Modified == default ? string.Empty : Modified.ToString("yyyy-MM-dd HH:mm");
    public string TypeText => Kind == RowKind.Directory ? "폴더" : (Extension.Length > 0 ? Extension : "파일");
}

/// <summary>검색 결과 + 조건에 맞은 전체 건수(표시 상한을 넘겼는지 알리기 위함).</summary>
public sealed class SearchOutcome
{
    public required List<EntryRow> Rows { get; init; }
    public int TotalMatches { get; init; }
    public bool Truncated => TotalMatches > Rows.Count;
}

public sealed class ExtensionRow
{
    public required string Extension { get; init; }
    public long Size { get; init; }
    public long Count { get; init; }
    public double Ratio { get; init; }
    public CategoryFlags Category { get; init; }

    public string SizeText => SizeFormatter.Format(Size);
    public string CountText => SizeFormatter.Count(Count);
    public string RatioText => SizeFormatter.Percent(Ratio);
    public string CategoryText => Categorizer.ToTagString(Category);
}

public sealed class DriveInfoRow
{
    public required string Name { get; init; }          // "C:"
    public required string RootPath { get; init; }      // "C:\"
    public string Label { get; init; } = string.Empty;
    public string FileSystem { get; init; } = string.Empty;
    public long TotalSize { get; init; }
    public long FreeSpace { get; init; }
    public DriveKind Kind { get; init; }
    public bool IsReady { get; init; }

    public long UsedSpace => TotalSize - FreeSpace;
    public double UsedRatio => TotalSize > 0 ? (double)UsedSpace / TotalSize : 0d;
    public double UsedPercentValue => UsedRatio * 100d;
    public string TotalText => SizeFormatter.Format(TotalSize);
    public string UsedText => SizeFormatter.Format(UsedSpace);
    public string FreeText => SizeFormatter.Format(FreeSpace);
    public string UsedPercentText => SizeFormatter.Percent(UsedRatio);
    public string KindText => Kind switch { DriveKind.Ssd => "SSD", DriveKind.Hdd => "HDD", _ => string.Empty };
    public string DisplayName => string.IsNullOrEmpty(Label) ? Name : $"{Name}  {Label}";
}

/// <summary>13. 필터.</summary>
public sealed class FilterOptions
{
    public long MinSize { get; set; }
    public string ExtensionPattern { get; set; } = string.Empty;   // "*.pdb;*.log" 또는 ".pdb"
    public string NamePattern { get; set; } = string.Empty;

    public bool IsEmpty => MinSize <= 0
        && string.IsNullOrWhiteSpace(ExtensionPattern)
        && string.IsNullOrWhiteSpace(NamePattern);
}

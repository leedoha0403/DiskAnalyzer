namespace DiskAnalyzer.Core.Models;

public enum RowKind { Directory, File }

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

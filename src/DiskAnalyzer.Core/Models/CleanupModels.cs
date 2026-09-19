using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DiskAnalyzer.Core.Models;

/// <summary>54. 정리 추천 등급.</summary>
public enum CleanupGrade
{
    /// <summary>다시 생성 가능한 데이터. [정리 가능성 높음]</summary>
    HighlyCleanable,
    /// <summary>사용자가 직접 만든 데이터일 가능성이 있음. [확인 필요]</summary>
    NeedsReview,
    /// <summary>OS/프로그램 동작에 영향을 줄 수 있음. [삭제 비추천]</summary>
    NotRecommended,
}

public enum CleanupRisk { Low, Medium, High }

/// <summary>57. 정리 추천 화면의 카테고리.</summary>
public enum CleanupCategory
{
    OldLargeFile,
    OldDownload,
    OldLog,
    Cache,
    Temp,
    VisualStudioBuild,
    OldArchive,
    PossibleDuplicate,
    OldInstaller,
    Package,
}

/// <summary>65. 사용자가 고를 수 있는 정렬.</summary>
public enum CleanupSort
{
    Recommended,
    Largest,
    Oldest,
    LeastRecentlyUsed,
    LowestRisk,
}

public static class CleanupText
{
    public static string Of(CleanupCategory c) => c switch
    {
        CleanupCategory.OldLargeFile => "오래된 대용량 파일",
        CleanupCategory.OldDownload => "오래된 다운로드",
        CleanupCategory.OldLog => "오래된 로그",
        CleanupCategory.Cache => "캐시",
        CleanupCategory.Temp => "임시 파일",
        CleanupCategory.VisualStudioBuild => "Visual Studio 빌드 파일",
        CleanupCategory.OldArchive => "오래된 압축 파일",
        CleanupCategory.PossibleDuplicate => "중복 가능 파일",
        CleanupCategory.OldInstaller => "오래된 설치 파일",
        CleanupCategory.Package => "패키지 / 의존성 폴더",
        _ => "기타",
    };

    public static string Of(CleanupGrade g) => g switch
    {
        CleanupGrade.HighlyCleanable => "[정리 가능성 높음]",
        CleanupGrade.NeedsReview => "[확인 필요]",
        _ => "[삭제 비추천]",
    };

    public static string Of(CleanupRisk r) => r switch
    {
        CleanupRisk.Low => "낮음",
        CleanupRisk.Medium => "보통",
        _ => "높음",
    };
}

/// <summary>
/// 53/57/58. 정리 "후보" 한 건.
/// 프로그램은 삭제 안전성을 단정하지 않는다. 등급 / 위험도 / 선정 이유를 함께 제공할 뿐이다.
/// </summary>
public sealed class CleanupCandidate : INotifyPropertyChanged
{
    private bool _isSelected;

    public required RowKind Kind { get; init; }
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required long Size { get; init; }
    public DateTime Modified { get; init; }
    public DateTime Created { get; init; }
    public DateTime Accessed { get; init; }
    public string Extension { get; init; } = string.Empty;
    public long FileCount { get; init; }

    public required CleanupCategory Category { get; init; }
    public required CleanupGrade Grade { get; init; }
    public required CleanupRisk Risk { get; init; }

    /// <summary>58. 설명 없는 추천은 금지. 항상 채워진다.</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// 보호 등급. "삭제해도 되는가"에만 답한다. 정리 추천 점수와는 별개 축이다.
    /// </summary>
    public ProtectionVerdict Protection { get; init; } = ProtectionVerdict.P3Default;

    public ProtectionLevel Level { get; init; } = ProtectionLevel.P3;

    /// <summary>8. 정리 추천 점수(0~100). P0/P1 에는 계산하지 않는다.</summary>
    public int Score { get; init; }

    public RecommendTier Tier { get; init; }

    public string LevelText => ProtectionText.Of(Level);
    public string LevelBadge => ProtectionText.Badge(Level);
    public string TierText => ProtectionText.Of(Tier);
    public string ScoreText => $"{Score}점";

    /// <summary>P0 는 선택 자체가 불가능하다(UI 체크박스 비활성).</summary>
    public bool CanSelect => Level != ProtectionLevel.P0;

    /// <summary>10. P1 은 기본 체크 해제. P2/P3 만 기본 삭제 대상으로 유지한다.</summary>
    public bool DefaultSelectable => Level <= ProtectionLevel.P2;

    internal int AgeDays { get; init; }
    internal int UnusedDays { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            // P0 는 어떤 경로로도 선택되지 않는다(전체 선택 / 그룹 선택 포함).
            if (value && !CanSelect) return;
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, SelectedArgs);
        }
    }

    public bool IsDirectory => Kind == RowKind.Directory;
    public string SizeText => SizeFormatter.Format(Size);
    public string ModifiedText => Modified == default ? "-" : Modified.ToString("yyyy-MM-dd");
    public string AccessedText => Accessed == default ? "-" : Accessed.ToString("yyyy-MM-dd");
    public string CreatedText => Created == default ? "-" : Created.ToString("yyyy-MM-dd");
    public string CategoryText => CleanupText.Of(Category);
    public string GradeText => CleanupText.Of(Grade);
    public string RiskText => CleanupText.Of(Risk);
    public string TypeText => Kind == RowKind.Directory ? "폴더" : (Extension.Length > 0 ? Extension : "파일");

    private static readonly PropertyChangedEventArgs SelectedArgs = new(nameof(IsSelected));
    public event PropertyChangedEventHandler? PropertyChanged;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static CleanupRisk RiskOf(CleanupGrade grade) => grade switch
    {
        CleanupGrade.HighlyCleanable => CleanupRisk.Low,
        CleanupGrade.NeedsReview => CleanupRisk.Medium,
        _ => CleanupRisk.High,
    };
}

/// <summary>55/57. 정리 추천 필터.</summary>
public sealed class CleanupOptions
{
    /// <summary>55. 오래된 파일 기준(일). 30 / 90 / 180 / 365 / 730 / 사용자 지정.</summary>
    public int MinAgeDays { get; set; } = 180;

    public long MinFileSize { get; set; } = 100L * 1024 * 1024;
    public long MinFolderSize { get; set; } = 200L * 1024 * 1024;

    public CleanupSort Sort { get; set; } = CleanupSort.Recommended;
    public CleanupCategory? CategoryFilter { get; set; }

    /// <summary>화면에 올릴 최대 후보 수. 요약 통계는 이 제한과 무관하게 전체를 집계한다.</summary>
    public int MaxResults { get; set; } = 5000;

    public CleanupOptions Clone() => (CleanupOptions)MemberwiseClone();
}

public sealed class CleanupCategorySummary
{
    public required CleanupCategory Category { get; init; }
    public long Size { get; init; }
    public int Count { get; init; }

    public string CategoryText => CleanupText.Of(Category);
    public string SizeText => SizeFormatter.Format(Size);
    public string CountText => SizeFormatter.Count(Count);
}

/// <summary>63. Visual Studio 프로젝트별 빌드 산출물.</summary>
public sealed class ProjectBuildInfo
{
    public required string ProjectName { get; init; }
    public required string ProjectPath { get; init; }
    public required int ProjectDirId { get; init; }
    public long BuildOutputSize { get; init; }
    public DateTime LastModified { get; init; }
    public string Folders { get; init; } = string.Empty;

    public string SizeText => SizeFormatter.Format(BuildOutputSize);
    public string LastModifiedText => LastModified == default
        ? "-"
        : $"{LastModified:yyyy-MM-dd} ({RelativeTime.Describe(LastModified)})";
}

/// <summary>63. Git Repository - 크기만 알려 주고 자동 정리 추천은 하지 않는다.</summary>
public sealed class GitRepoInfo
{
    public required string RepoName { get; init; }
    public required string RepoPath { get; init; }
    public long GitSize { get; init; }
    public long WorkingTreeSize { get; init; }

    public string GitSizeText => SizeFormatter.Format(GitSize);
    public string WorkingTreeSizeText => SizeFormatter.Format(WorkingTreeSize);
}

/// <summary>63. 로그 파일 연령 그룹.</summary>
public sealed class LogAgeBucket
{
    public required string Label { get; init; }
    public long Size { get; init; }
    public int Count { get; init; }

    public string SizeText => SizeFormatter.Format(Size);
    public string CountText => SizeFormatter.Count(Count);
}

public sealed class DeveloperReport
{
    public IReadOnlyList<ProjectBuildInfo> Projects { get; init; } = Array.Empty<ProjectBuildInfo>();
    public IReadOnlyList<GitRepoInfo> GitRepos { get; init; } = Array.Empty<GitRepoInfo>();
    public IReadOnlyList<LogAgeBucket> LogBuckets { get; init; } = Array.Empty<LogAgeBucket>();
    public long TotalBuildOutput { get; init; }
    public long TotalGitSize { get; init; }
    public long TotalLogSize { get; init; }
    public long OldLogSize { get; init; }
}

public sealed class CleanupReport
{
    public IReadOnlyList<CleanupCandidate> Candidates { get; init; } = Array.Empty<CleanupCandidate>();
    public IReadOnlyList<CleanupCategorySummary> Summaries { get; init; } = Array.Empty<CleanupCategorySummary>();
    public DeveloperReport Developer { get; init; } = new();

    public int TotalCandidates { get; init; }
    public long TotalCandidateSize { get; init; }

    /// <summary>61. 보호 경로라서 추천에서 제외한 항목(정보용).</summary>
    public int ProtectedCount { get; init; }
    public long ProtectedSize { get; init; }

    /// <summary>56. Last Access Time 신뢰 여부.</summary>
    public bool AccessTimeReliable { get; init; }

    public string Notice { get; init; } = string.Empty;
    public double ElapsedMs { get; init; }
}

public static class RelativeTime
{
    /// <summary>58. "634일 동안 수정되지 않음" 같이 사람이 바로 이해하는 표현을 만든다.</summary>
    public static string Describe(DateTime when)
        => when == default ? "알 수 없음" : DescribeDays((int)(DateTime.Now - when).TotalDays);

    public static string DescribeDays(int days)
    {
        if (days < 0) days = 0;
        if (days < 1) return "오늘";
        if (days < 30) return $"{days}일 전";
        if (days < 365) return $"약 {days / 30}개월 전";
        int years = days / 365;
        int months = (days % 365) / 30;
        return months == 0 ? $"약 {years}년 전" : $"약 {years}년 {months}개월 전";
    }

    public static string DescribeDuration(int days)
    {
        if (days < 30) return $"{days}일";
        if (days < 365) return $"약 {days / 30}개월";
        int years = days / 365;
        int months = (days % 365) / 30;
        return months == 0 ? $"약 {years}년" : $"약 {years}년 {months}개월";
    }
}

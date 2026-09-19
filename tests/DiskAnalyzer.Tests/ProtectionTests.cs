using DiskAnalyzer.Core.Analysis;
using DiskAnalyzer.Core.Models;
using Xunit;

namespace DiskAnalyzer.Tests;

/// <summary>
/// 보호 등급(P0~P3) 판정 검증.
/// 핵심 성질: <strong>정리 가능 규칙은 보호 규칙을 절대 덮어쓸 수 없다.</strong>
/// </summary>
public class ProtectionTests
{
    private const uint ReparsePoint = 0x400;

    private static ProtectionLevel Level(string path, bool isDir = false,
        uint attributes = 0, CategoryFlags flags = CategoryFlags.None)
        => ProtectionEvaluator.Evaluate(path, isDir, attributes, flags).Level;

    // ---------------------------------------------------------------- P0

    [Theory]
    [InlineData(@"C:\pagefile.sys")]
    [InlineData(@"C:\hiberfil.sys")]
    [InlineData(@"C:\swapfile.sys")]
    [InlineData(@"C:\bootmgr")]
    public void SystemCoreFiles_Are_P0(string path)
        => Assert.Equal(ProtectionLevel.P0, Level(path));

    [Theory]
    [InlineData(@"C:\Windows\System32\kernel32.dll")]
    [InlineData(@"C:\Windows\System32\config\SYSTEM")]
    [InlineData(@"C:\Windows\System32\drivers\disk.sys")]
    [InlineData(@"C:\Windows\WinSxS\something")]
    [InlineData(@"C:\Windows\Boot\EFI\bootmgfw.efi")]
    [InlineData(@"C:\System Volume Information\x.dat")]
    [InlineData(@"C:\Recovery\WindowsRE\winre.wim")]
    public void SystemCoreAreas_Are_P0(string path)
        => Assert.Equal(ProtectionLevel.P0, Level(path));

    [Fact]
    public void P0_Cannot_Be_Selected()
    {
        var verdict = ProtectionEvaluator.Evaluate(@"C:\pagefile.sys", false, 0, CategoryFlags.None);
        Assert.False(verdict.CanDelete);
        Assert.False(verdict.AutoSelectable);
    }

    [Fact]
    public void Invalid_Path_Is_Protected()
    {
        // 1단계. 경로 해석 실패 시 안전하게 P0.
        Assert.Equal(ProtectionLevel.P0, Level(""));
        Assert.Equal(ProtectionLevel.P0, Level("   "));
    }

    [Fact]
    public void UserFile_Named_Like_System_File_Is_Not_P0()
    {
        // 파일명만으로 판단하지 않는다. 드라이브 루트가 아니면 시스템 파일이 아니다.
        Assert.NotEqual(ProtectionLevel.P0, Level(@"D:\Backup\pagefile.sys"));
    }

    // ---------------------------------------------------------------- P1

    [Theory]
    [InlineData(@"C:\Windows\Fonts\arial.ttf")]
    [InlineData(@"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files (x86)\App\lib.dll")]
    [InlineData(@"C:\ProgramData\App\state.db")]
    public void HighRiskPaths_Are_P1(string path)
        => Assert.Equal(ProtectionLevel.P1, Level(path));

    [Fact]
    public void AppData_Is_P1_But_Its_Temp_Is_Not()
    {
        Assert.Equal(ProtectionLevel.P1, Level(@"C:\Users\me\AppData\Roaming\App\config.json"));

        // AppData\Local\Temp 는 알려진 임시 경로라 보호 대상이 아니다.
        var temp = ProtectionEvaluator.Evaluate(
            @"C:\Users\me\AppData\Local\Temp\scratch.dat", false, 0, CategoryFlags.None);
        Assert.Equal(ProtectionLevel.P3, temp.Level);
        Assert.True(temp.RegenerableLocation);
    }

    [Fact]
    public void ReparsePoint_Is_P1_And_Blocks_Recursion()
    {
        var verdict = ProtectionEvaluator.Evaluate(@"D:\Link", true, ReparsePoint, CategoryFlags.None);
        Assert.Equal(ProtectionLevel.P1, verdict.Level);
        Assert.True(verdict.BlockRecursion);
    }

    [Fact]
    public void Extension_Alone_Does_Not_Make_P1()
    {
        // Downloads\old_setup.exe 는 P3 일 수 있다. 경로가 위험할 때만 위험 신호가 의미를 갖는다.
        Assert.Equal(ProtectionLevel.P3, Level(@"C:\Users\me\Downloads\old_setup.exe"));
        Assert.Equal(ProtectionLevel.P1, Level(@"C:\Program Files\App\app.exe"));
    }

    // ---------------------------------------------------------------- P2 / P3

    [Fact]
    public void Known_Temp_Location_Is_Regenerable()
    {
        var verdict = ProtectionEvaluator.Evaluate(@"C:\Windows\Temp\x.tmp", false, 0, CategoryFlags.None);
        // Windows\Temp 는 Windows 하위지만 알려진 임시 경로다.
        Assert.True(verdict.RegenerableLocation || verdict.Level == ProtectionLevel.P1);
    }

    [Fact]
    public void Business_Data_With_Tmp_Extension_Stays_P3()
    {
        // D:\DBBackup\important.tmp 는 확장자가 .tmp 여도 알려진 임시 경로가 아니므로 P2 가 아니다.
        var verdict = ProtectionEvaluator.Evaluate(@"D:\DBBackup\important.tmp", false, 0, CategoryFlags.None);
        Assert.Equal(ProtectionLevel.P3, verdict.Level);
        Assert.False(verdict.RegenerableLocation);
    }

    [Fact]
    public void Ordinary_User_File_Is_P3()
        => Assert.Equal(ProtectionLevel.P3, Level(@"D:\Movies\vacation.mkv"));

    // ---------------------------------------------------------------- 우선순위

    [Fact]
    public void Protection_Always_Beats_Cleanup_Rules()
    {
        // *.log 는 보통 정리 후보지만, System32 안에 있으면 보호 규칙이 이긴다.
        var inSystem = ProtectionEvaluator.Evaluate(
            @"C:\Windows\System32\LogFiles\big.log", false, 0, CategoryFlags.Log);
        Assert.Equal(ProtectionLevel.P0, inSystem.Level);

        var normal = ProtectionEvaluator.Evaluate(@"D:\Logs\big.log", false, 0, CategoryFlags.Log);
        Assert.Equal(ProtectionLevel.P3, normal.Level);
        Assert.True(normal.RegenerableLocation);
    }

    [Fact]
    public void Path_Normalization_Handles_Dot_Segments()
    {
        Assert.Equal(ProtectionLevel.P0, Level(@"C:\Windows\System32\..\System32\kernel32.dll"));
        Assert.Equal(ProtectionLevel.P0, Level(@"C:/Windows/System32/kernel32.dll"));
    }

    // ---------------------------------------------------------------- 점수

    [Fact]
    public void Score_Is_Zero_For_Fresh_Small_File()
    {
        int score = CleanupScorer.Score(new CleanupScorer.Input(
            Size: 1024, AgeDays: 0, UnusedDays: 0,
            AccessReliable: true, IsCacheOrTemp: false, IsRegenerable: false));
        Assert.Equal(RecommendTier.None, CleanupScorer.TierOf(score));
    }

    [Fact]
    public void Old_Large_Cache_Scores_Priority()
    {
        int score = CleanupScorer.Score(new CleanupScorer.Input(
            Size: 20L * 1024 * 1024 * 1024, AgeDays: 800, UnusedDays: 800,
            AccessReliable: true, IsCacheOrTemp: true, IsRegenerable: true));
        Assert.True(score >= 80, $"score={score}");
        Assert.Equal(RecommendTier.Priority, CleanupScorer.TierOf(score));
    }

    [Theory]
    [InlineData(0, RecommendTier.None)]
    [InlineData(29, RecommendTier.None)]
    [InlineData(30, RecommendTier.Reference)]
    [InlineData(59, RecommendTier.Reference)]
    [InlineData(60, RecommendTier.Suggested)]
    [InlineData(79, RecommendTier.Suggested)]
    [InlineData(80, RecommendTier.Priority)]
    [InlineData(100, RecommendTier.Priority)]
    public void Tier_Boundaries(int score, RecommendTier expected)
        => Assert.Equal(expected, CleanupScorer.TierOf(score));

    [Fact]
    public void Score_Never_Exceeds_Bounds()
    {
        int max = CleanupScorer.Score(new CleanupScorer.Input(
            long.MaxValue / 4, 100000, 100000, true, true, true));
        Assert.InRange(max, 0, 100);
    }

    // ---------------------------------------------------------------- 등급 분리

    [Fact]
    public void Protection_And_Recommendation_Are_Separate_Axes()
    {
        // 오래된 ISO: 보호 P3, 추천 높음
        int isoScore = CleanupScorer.Score(new CleanupScorer.Input(
            4L * 1024 * 1024 * 1024, 900, 900, true, false, false));
        Assert.Equal(ProtectionLevel.P3, Level(@"D:\Downloads\old.iso"));
        Assert.True(CleanupScorer.TierOf(isoScore) >= RecommendTier.Reference);

        // Program Files DLL: 보호 P1, 추천 대상 제외
        Assert.Equal(ProtectionLevel.P1, Level(@"C:\Program Files\App\core.dll"));
    }
}

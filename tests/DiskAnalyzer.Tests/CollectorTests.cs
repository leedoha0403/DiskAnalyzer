using DiskAnalyzer.Core.Analysis;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;
using Xunit;

namespace DiskAnalyzer.Tests;

/// <summary>
/// 수집함 — 합계가 언제나 정확해야 한다. 중복이나 이중 계산이 생기면 화면의 "확보 예정 용량"이 거짓말이 된다.
/// </summary>
public class CollectorTests
{
    private const long Mb = 1024L * 1024L;

    private static CollectResult Add(Collector c, string path, long size = Mb,
                                     bool isDir = false, ProtectionLevel level = ProtectionLevel.P3)
        => c.TryAdd(path, Path.GetFileName(path.TrimEnd('\\')), isDir, size, level);

    [Fact]
    public void Adds_And_Sums()
    {
        var c = new Collector();
        Assert.Equal(CollectResult.Added, Add(c, @"D:\a.bin", 10 * Mb));
        Assert.Equal(CollectResult.Added, Add(c, @"D:\b.bin", 5 * Mb));

        Assert.Equal(2, c.Count);
        Assert.Equal(15 * Mb, c.TotalSize);
        Assert.False(c.IsEmpty);
    }

    [Fact]
    public void Same_Path_Is_Not_Added_Twice()
    {
        var c = new Collector();
        Add(c, @"D:\a.bin", 10 * Mb);

        Assert.Equal(CollectResult.Duplicate, Add(c, @"D:\a.bin", 10 * Mb));
        Assert.Equal(CollectResult.Duplicate, Add(c, @"d:\A.BIN", 10 * Mb));   // 대소문자 무시
        Assert.Equal(CollectResult.Duplicate, Add(c, @"D:\sub\..\a.bin", 10 * Mb));   // 정규화

        Assert.Equal(1, c.Count);
        Assert.Equal(10 * Mb, c.TotalSize);
    }

    [Fact]
    public void P0_Never_Enters()
    {
        var c = new Collector();
        Assert.Equal(CollectResult.Protected,
            c.TryAdd(@"C:\Windows\System32", "System32", true, 9999 * Mb, ProtectionLevel.P0));

        Assert.True(c.IsEmpty);
        Assert.Equal(0, c.TotalSize);
    }

    [Fact]
    public void P1_Enters_But_Is_Flagged()
    {
        var c = new Collector();
        Assert.Equal(CollectResult.Added,
            c.TryAdd(@"C:\Program Files\App", "App", true, 200 * Mb, ProtectionLevel.P1));

        Assert.True(c.HasRisky);
        Assert.True(c.Items[0].IsRisky);
    }

    [Fact]
    public void Item_Under_A_Collected_Folder_Is_Refused()
    {
        var c = new Collector();
        Add(c, @"D:\build", 500 * Mb, isDir: true);

        Assert.Equal(CollectResult.CoveredByFolder, Add(c, @"D:\build\obj\app.pdb", 40 * Mb));

        Assert.Equal(1, c.Count);
        Assert.Equal(500 * Mb, c.TotalSize);   // 두 번 세지 않는다
    }

    [Fact]
    public void Collecting_A_Folder_Absorbs_What_Was_Already_Inside()
    {
        var c = new Collector();
        Add(c, @"D:\build\obj\app.pdb", 40 * Mb);
        Add(c, @"D:\build\bin\app.exe", 10 * Mb);
        Add(c, @"D:\keep\other.bin", 7 * Mb);
        Assert.Equal(57 * Mb, c.TotalSize);

        Add(c, @"D:\build", 500 * Mb, isDir: true);

        Assert.Equal(2, c.Count);
        Assert.Equal(507 * Mb, c.TotalSize);
        Assert.Contains(c.Items, i => i.FullPath == @"D:\keep\other.bin");
        Assert.DoesNotContain(c.Items, i => i.FullPath.StartsWith(@"D:\build\"));
    }

    /// <summary>접두사만 같고 형제인 폴더를 자손으로 착각하면 안 된다.</summary>
    [Fact]
    public void Sibling_With_A_Shared_Prefix_Is_Not_A_Descendant()
    {
        var c = new Collector();
        Add(c, @"D:\Data2\x.bin", 3 * Mb);
        Add(c, @"D:\Data", 100 * Mb, isDir: true);

        Assert.Equal(2, c.Count);
        Assert.Equal(103 * Mb, c.TotalSize);
        Assert.False(c.Covers(@"D:\Data2\x.bin") && c.Count == 1);
        Assert.True(c.Covers(@"D:\Data\anything.txt"));
    }

    [Fact]
    public void Covers_Sees_Through_Collected_Folders()
    {
        var c = new Collector();
        Add(c, @"D:\build", 500 * Mb, isDir: true);

        Assert.True(c.Covers(@"D:\build"));
        Assert.True(c.Covers(@"D:\build\deep\down\here.txt"));
        Assert.False(c.Covers(@"D:\other"));
        Assert.False(new Collector().Covers(@"D:\build"));
    }

    [Fact]
    public void Remove_Restores_The_Total()
    {
        var c = new Collector();
        Add(c, @"D:\a.bin", 10 * Mb);
        Add(c, @"D:\b.bin", 5 * Mb);

        Assert.True(c.Remove(@"d:\A.BIN"));
        Assert.False(c.Remove(@"D:\nope.bin"));

        Assert.Equal(1, c.Count);
        Assert.Equal(5 * Mb, c.TotalSize);
    }

    [Fact]
    public void Toggle_Puts_In_And_Takes_Out()
    {
        var c = new Collector();
        Assert.Equal(CollectResult.Added, c.Toggle(@"D:\a.bin", "a.bin", false, Mb, ProtectionLevel.P3));
        Assert.Equal(1, c.Count);

        c.Toggle(@"D:\a.bin", "a.bin", false, Mb, ProtectionLevel.P3);
        Assert.True(c.IsEmpty);
        Assert.Equal(0, c.TotalSize);
    }

    [Fact]
    public void RemoveMany_Keeps_What_Failed()
    {
        var c = new Collector();
        Add(c, @"D:\a.bin", 10 * Mb);
        Add(c, @"D:\b.bin", 5 * Mb);
        Add(c, @"D:\c.bin", 2 * Mb);

        c.RemoveMany(new[] { @"D:\a.bin", @"D:\c.bin", @"D:\never-was.bin" });

        Assert.Equal(1, c.Count);
        Assert.Equal(5 * Mb, c.TotalSize);
    }

    [Fact]
    public void Items_Come_Back_Largest_First()
    {
        var c = new Collector();
        Add(c, @"D:\small.bin", Mb);
        Add(c, @"D:\huge.bin", 900 * Mb);
        Add(c, @"D:\mid.bin", 40 * Mb);

        Assert.Equal(new[] { "huge.bin", "mid.bin", "small.bin" }, c.Items.Select(i => i.Name));
    }

    [Fact]
    public void Clear_Empties_Everything()
    {
        var c = new Collector();
        Add(c, @"D:\a.bin", 10 * Mb);
        c.Clear();

        Assert.True(c.IsEmpty);
        Assert.Equal(0, c.TotalSize);
        Assert.Empty(c.Items);
    }

    [Fact]
    public void Changed_Fires_Only_On_Real_Changes()
    {
        var c = new Collector();
        int fired = 0;
        c.Changed += (_, _) => fired++;

        Add(c, @"D:\a.bin", 10 * Mb);       // 1
        Add(c, @"D:\a.bin", 10 * Mb);       // 중복 - 안 울림
        c.Remove(@"D:\nope.bin");           // 없음 - 안 울림
        c.RemoveMany(new[] { @"D:\nope.bin" });   // 없음 - 안 울림
        c.Remove(@"D:\a.bin");              // 2
        c.Clear();                          // 이미 비었음 - 안 울림

        Assert.Equal(2, fired);
    }

    [Fact]
    public void Garbage_Paths_Are_Refused_Not_Thrown()
    {
        var c = new Collector();
        Assert.Equal(CollectResult.NotCollectable, Add(c, "   "));
        Assert.Equal(CollectResult.NotCollectable, c.TryAdd("a\0b", "x", false, Mb, ProtectionLevel.P3));
        Assert.False(c.Contains("a\0b"));
        Assert.False(c.Covers("a\0b"));
        Assert.True(c.IsEmpty);
    }
}

/// <summary>스캔 합계와 OS 사용량의 차이를 숨기지 않는다.</summary>
public class SpaceLedgerTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void Hidden_Is_The_Difference_Between_Used_And_Scanned()
    {
        var l = SpaceLedger.Create(scanned: 400 * Gb, volumeUsed: 407 * Gb, volumeTotal: 500 * Gb, isVolumeRoot: true);

        Assert.True(l.HasVolumeInfo);
        Assert.True(l.HasHidden);
        Assert.Equal(7 * Gb, l.Hidden);
        Assert.Equal(93 * Gb, l.Free);
    }

    [Fact]
    public void Sub_Folder_Scans_Have_Nothing_To_Compare_Against()
    {
        var l = SpaceLedger.Create(400 * Gb, 407 * Gb, 500 * Gb, isVolumeRoot: false);

        Assert.False(l.HasVolumeInfo);
        Assert.False(l.HasHidden);
        Assert.Equal(400 * Gb, l.Scanned);
    }

    /// <summary>하드 링크 · 압축 · 스파스 파일이면 스캔 합계가 사용량을 넘을 수 있다.</summary>
    [Fact]
    public void Scanned_Larger_Than_Used_Does_Not_Produce_Negative_Hidden()
    {
        var l = SpaceLedger.Create(410 * Gb, 407 * Gb, 500 * Gb, isVolumeRoot: true);

        Assert.Equal(0, l.Hidden);
        Assert.False(l.HasHidden);
    }

    [Fact]
    public void Tiny_Differences_Are_Not_Worth_A_Row()
    {
        var l = SpaceLedger.Create(400 * Gb, 400 * Gb + 8 * 1024 * 1024, 500 * Gb, true);

        Assert.True(l.HasVolumeInfo);
        Assert.False(l.HasHidden);   // 8 MB - 소음이다
    }

    [Fact]
    public void Missing_Volume_Numbers_Degrade_Quietly()
    {
        var l = SpaceLedger.Create(400 * Gb, 0, 0, isVolumeRoot: true);

        Assert.False(l.HasVolumeInfo);
        Assert.Equal(400 * Gb, l.Scanned);
        Assert.Equal(string.Empty, l.ExplainHidden(10, 2, 1));
    }

    [Fact]
    public void Explanation_Names_The_Causes_It_Knows()
    {
        var l = SpaceLedger.Create(400 * Gb, 407 * Gb, 500 * Gb, true);

        Assert.Contains("접근 거부 303건", l.ExplainHidden(303, 0, 0));
        Assert.Contains("건너뜀 359건", l.ExplainHidden(303, 359, 0));
        Assert.Contains("제외 규칙 4폴더", l.ExplainHidden(0, 0, 4));
        Assert.Contains("메타데이터", l.ExplainHidden(0, 0, 0));
    }

    [Fact]
    public void Small_Gap_Is_Explained_As_Metadata()
    {
        var l = SpaceLedger.Create(400 * Gb, 407 * Gb, 500 * Gb, true);   // 1.7%

        Assert.False(l.HiddenIsSuspicious);
        Assert.DoesNotContain("읽지 못했을", l.ExplainHidden(0, 0, 0));
    }

    [Fact]
    public void Huge_Gap_Is_Not_Blamed_On_Metadata()
    {
        // Fast 스캔이 $MFT 절반만 읽었을 때 실제로 나온 숫자다(1.23 TB 스캔 / 3.06 TB 사용).
        const long Tb = 1024L * Gb;
        var l = SpaceLedger.Create((long)(1.23 * Tb), (long)(3.06 * Tb), (long)(3.73 * Tb), true);

        Assert.True(l.HasHidden);
        Assert.True(l.HiddenIsSuspicious);

        string why = l.ExplainHidden(0, 0, 0);
        Assert.Contains("메타데이터로 설명되는 크기가 아닙니다", why);
        Assert.Contains("읽지 못했을", why);
        Assert.Contains("60%", why);
    }

    [Fact]
    public void Suspicious_Line_Is_Drawn_At_Ten_Percent()
    {
        var under = SpaceLedger.Create(910 * Gb, 1000 * Gb, 2000 * Gb, true);   // 9%
        var over = SpaceLedger.Create(890 * Gb, 1000 * Gb, 2000 * Gb, true);    // 11%

        Assert.False(under.HiddenIsSuspicious);
        Assert.True(over.HiddenIsSuspicious);
    }
}

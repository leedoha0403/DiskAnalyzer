using DiskAnalyzer.Core.Analysis;
using DiskAnalyzer.Core.Models;
using Xunit;

namespace DiskAnalyzer.Tests;

public class PatternKeyTests
{
    [Theory]
    [InlineData("log_20240101.txt", "log_20240102.txt", true)]     // 날짜만 다르다
    [InlineData("log_2024-01-01.txt", "log_2025-12-31.txt", true)] // 구분자로 이어진 숫자는 하나로 합친다
    [InlineData("dump (1).dmp", "dump (23).dmp", true)]
    [InlineData("cache_3fa9c21b7d.dat", "cache_a91e0044ff.dat", true)]   // 해시
    [InlineData("a1b2c3d4-0000-1111-2222-333344445555.tmp", "ffffffff-aaaa-bbbb-cccc-000000000001.tmp", true)]
    [InlineData("backup.7z.001", "backup.7z.002", true)]           // 분할 압축
    [InlineData("LOG_1.TXT", "log_2.txt", true)]                   // 대소문자 무시
    [InlineData("song.mp3", "song.mp4", false)]                    // 확장자의 숫자는 구분에 쓴다
    [InlineData("report.txt", "notes.txt", false)]
    [InlineData("log_1.txt", "log_1.log", false)]
    public void Same_Or_Different_Pattern(string a, string b, bool same)
        => Assert.Equal(same, CleanupPatternFolder.PatternKey(a) == CleanupPatternFolder.PatternKey(b));

    [Fact]
    public void Label_Uses_Star_And_Keeps_Case()
        => Assert.Equal("Log_*.txt", CleanupPatternFolder.PatternLabel("Log_20240101.txt"));

    [Fact]
    public void Plain_Words_Are_Not_Treated_As_Hashes()
        => Assert.Equal("deadbeef.txt", CleanupPatternFolder.PatternKey("deadbeef.txt"));

    [Fact]
    public void Directories_Do_Not_Split_Extension()
        => Assert.Equal("v#", CleanupPatternFolder.PatternKey("v1.2", isDirectory: true));
}

public class CleanupFoldingTests
{
    private static int _id;

    private static CleanupCandidate File(string path, long size = 100, ProtectionLevel level = ProtectionLevel.P3,
        CleanupCategory cat = CleanupCategory.OldLog, CleanupGrade grade = CleanupGrade.HighlyCleanable)
        => new()
        {
            Kind = RowKind.File,
            Id = ++_id,
            Name = Path.GetFileName(path),
            FullPath = path,
            Size = size,
            Category = cat,
            Grade = grade,
            Risk = CleanupRisk.Low,
            Reason = "테스트",
            Level = level,
            Extension = Path.GetExtension(path),
        };

    private static CleanupCandidate Folder(string path, long size = 1000, long files = 10)
        => new()
        {
            Kind = RowKind.Directory,
            Id = ++_id,
            Name = Path.GetFileName(path),
            FullPath = path,
            Size = size,
            FileCount = files,
            Category = CleanupCategory.VisualStudioBuild,
            Grade = CleanupGrade.HighlyCleanable,
            Risk = CleanupRisk.Low,
            Reason = "테스트",
        };

    private static IEnumerable<CleanupFoldNode> Walk(IEnumerable<CleanupFoldNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Walk(n.Children)) yield return c;
        }
    }

    [Fact]
    public void Same_Folder_Repeated_Pattern_Is_Folded_Into_One_Node()
    {
        var items = Enumerable.Range(0, 1200)
            .Select(i => File($@"C:\Logs\app\log_{i:D6}.txt", 10))
            .Append(File(@"C:\Logs\app\readme.md", 5))
            .ToList();

        var tree = CleanupPatternFolder.Build(items);

        var patterns = Walk(tree.Roots).Where(n => n.IsPattern).ToList();
        var p = Assert.Single(patterns);
        Assert.Equal("log_*.txt", p.Title);
        Assert.Equal(1200, p.Children.Count);
        Assert.Equal(12_000, p.Size);
        Assert.Equal(1, tree.PatternCount);
        Assert.Equal(1200, tree.FoldedItemCount);
        Assert.Contains(Walk(tree.Roots), n => n.IsItem && n.Title == "readme.md");
    }

    [Fact]
    public void Fewer_Than_MinFold_Stay_As_Individual_Items()
    {
        var items = Enumerable.Range(0, 4).Select(i => File($@"C:\D\log_{i}.txt")).ToList();
        var tree = CleanupPatternFolder.Build(items);

        Assert.DoesNotContain(Walk(tree.Roots), n => n.IsPattern);
        Assert.Equal(4, Walk(tree.Roots).Count(n => n.IsItem));
    }

    [Fact]
    public void Same_Name_In_Different_Folders_Is_Folded_Under_Common_Ancestor()
    {
        var items = new[] { "A", "B", "C", "D", "E", "F" }
            .Select(x => File($@"C:\proj\obj\{x}\out.pdb", 50))
            .ToList();

        var tree = CleanupPatternFolder.Build(items);

        var p = Assert.Single(Walk(tree.Roots), n => n.IsPattern);
        Assert.Equal("out.pdb", p.Title);
        Assert.Equal(@"C:\proj\obj", p.FullPath);                         // 공통 상위 폴더
        Assert.Equal(6, p.Children.Count);
        Assert.Contains(p.Children, c => c.Title == @"A\out.pdb");        // 위치를 알 수 있게 상대 경로
    }

    [Fact]
    public void Same_Name_On_Different_Drives_Is_Not_Merged()
    {
        var items = new List<CleanupCandidate>();
        foreach (var x in new[] { "1", "2", "3" })
        {
            items.Add(File($@"C:\p{x}\a.tmp"));
            items.Add(File($@"D:\p{x}\a.tmp"));
        }
        var tree = CleanupPatternFolder.Build(items);

        // 드라이브당 3건이라 접지 않는다(합치면 6건이 되지만 공통 상위 폴더가 없다).
        Assert.DoesNotContain(Walk(tree.Roots), n => n.IsPattern);
        Assert.Equal(2, tree.Roots.Count);
    }

    [Fact]
    public void Single_Child_Folder_Chains_Are_Compressed()
    {
        var items = new[] { File(@"C:\Users\me\AppData\Local\Temp\a.tmp", 10) };
        var tree = CleanupPatternFolder.Build(items);

        var root = Assert.Single(tree.Roots);
        Assert.Equal(@"C:\Users\me\AppData\Local\Temp", root.Title);
        Assert.Equal(@"C:\Users\me\AppData\Local\Temp", root.FullPath);
        Assert.Equal("a.tmp", Assert.Single(root.Children).Title);
    }

    [Fact]
    public void Sizes_And_Counts_Are_Aggregated()
    {
        var items = new[]
        {
            File(@"C:\X\a.bin", 100), File(@"C:\X\b.bin", 200),
            File(@"C:\Y\c.bin", 400), Folder(@"C:\Y\obj", 1000, files: 7),
        };
        var tree = CleanupPatternFolder.Build(items);

        long total = tree.Roots.Sum(r => r.Size);
        Assert.Equal(1700, total);

        var root = Assert.Single(tree.Roots);              // C:\ (X, Y 두 갈래라 압축되지 않음)
        Assert.Equal(4, root.CandidateCount);
        Assert.Equal(1 + 1 + 1 + 7, root.FileCount);       // 폴더 후보는 안의 파일 수를 센다
    }

    [Fact]
    public void Folder_Grade_Is_The_Most_Conservative_Of_Its_Children()
    {
        var items = new[]
        {
            File(@"C:\X\a.bin", grade: CleanupGrade.HighlyCleanable),
            File(@"C:\X\b.bin", grade: CleanupGrade.NeedsReview),
        };
        var root = Assert.Single(CleanupPatternFolder.Build(items).Roots);
        Assert.Equal(CleanupGrade.NeedsReview, root.Grade);
    }

    [Fact]
    public void Checking_A_Pattern_Selects_All_Members_And_Reports_Tri_State()
    {
        var items = Enumerable.Range(0, 10).Select(i => File($@"C:\L\log_{i}.txt", 10)).ToList();
        var tree = CleanupPatternFolder.Build(items);
        var pattern = Walk(tree.Roots).Single(n => n.IsPattern);

        Assert.False(pattern.IsSelected);

        pattern.IsSelected = true;
        Assert.True(pattern.IsSelected);
        Assert.All(items, c => Assert.True(c.IsSelected));

        items[3].IsSelected = false;                       // 개별 제외 -> 일부 선택
        Assert.Null(pattern.IsSelected);
        Assert.Null(Assert.Single(tree.Roots).IsSelected); // 상위 폴더도 일부 선택

        pattern.IsSelected = false;
        Assert.False(pattern.IsSelected);
        Assert.All(items, c => Assert.False(c.IsSelected));
    }

    [Fact]
    public void Protected_P0_Items_Are_Never_Selected_Through_A_Node()
    {
        var safe = Enumerable.Range(0, 5).Select(i => File($@"C:\W\f_{i}.log")).ToList();
        var blocked = File(@"C:\W\f_9.log", level: ProtectionLevel.P0);
        var tree = CleanupPatternFolder.Build(safe.Append(blocked).ToList());
        var pattern = Walk(tree.Roots).Single(n => n.IsPattern);

        pattern.IsSelected = true;

        Assert.False(blocked.IsSelected);
        Assert.All(safe, c => Assert.True(c.IsSelected));
        Assert.True(pattern.IsSelected);   // 선택 가능한 항목은 모두 선택되었으므로 "전체"로 본다
    }

    [Fact]
    public void Bulk_Selection_Raises_A_Single_Completion_Event()
    {
        var items = Enumerable.Range(0, 50).Select(i => File($@"C:\L\log_{i}.txt")).ToList();
        var tree = CleanupPatternFolder.Build(items);
        var pattern = Walk(tree.Roots).Single(n => n.IsPattern);

        int candidateEvents = 0, completed = 0;
        bool sawBulkFlag = false;
        foreach (var c in items)
            c.PropertyChanged += (_, _) => { candidateEvents++; sawBulkFlag |= tree.IsBulkUpdating; };
        tree.BulkSelectionCompleted += (_, _) => completed++;

        pattern.IsSelected = true;

        Assert.Equal(1, completed);
        Assert.Equal(50, candidateEvents);
        Assert.True(sawBulkFlag);            // 화면 쪽이 이 플래그를 보고 후보별 재집계를 건너뛴다
        Assert.False(tree.IsBulkUpdating);
    }

    [Fact]
    public void Expanded_State_Can_Be_Captured_And_Restored()
    {
        var items = new[] { File(@"C:\X\a.bin"), File(@"C:\Y\b.bin") };
        var tree = CleanupPatternFolder.Build(items);
        var y = Walk(tree.Roots).Single(n => n.IsFolder && n.Title == "Y");
        y.IsExpanded = false;
        var keys = tree.CaptureExpanded();

        var rebuilt = CleanupPatternFolder.Build(items);
        foreach (var n in Walk(rebuilt.Roots).Where(n => !n.IsItem)) n.IsExpanded = true;
        rebuilt.RestoreExpanded(keys);

        Assert.False(Walk(rebuilt.Roots).Single(n => n.IsFolder && n.Title == "Y").IsExpanded);
        Assert.True(Walk(rebuilt.Roots).Single(n => n.IsFolder && n.Title == "X").IsExpanded);
    }

    [Fact]
    public void No_Candidate_Is_Lost_Or_Duplicated()
    {
        var items = new List<CleanupCandidate>();
        for (int i = 0; i < 30; i++) items.Add(File($@"C:\A\b\c_{i}.log"));         // 같은 폴더 패턴
        for (int i = 0; i < 8; i++) items.Add(File($@"C:\Z\d{i}\same.dat"));        // 여러 폴더 같은 이름
        for (int i = 0; i < 3; i++) items.Add(File($@"C:\M\odd_{i}.txt"));          // 접히지 않음
        items.Add(Folder(@"C:\Q\obj"));

        var tree = CleanupPatternFolder.Build(items);

        var leaves = Walk(tree.Roots).Where(n => n.IsItem).Select(n => n.Candidate!).ToList();
        Assert.Equal(items.Count, leaves.Count);
        Assert.Equal(items.Select(c => c.Id).OrderBy(x => x), leaves.Select(c => c.Id).OrderBy(x => x));
        Assert.Equal(items.Sum(c => c.Size), tree.Roots.Sum(r => r.Size));
    }
}

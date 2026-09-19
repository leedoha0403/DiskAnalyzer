using DiskAnalyzer.Core.Models;
using Xunit;

namespace DiskAnalyzer.Tests;

public class NameMatcherTests
{
    [Theory]
    [InlineData("log", "app.log", true)]          // 부분 일치
    [InlineData("LOG", "Catalog.txt", true)]      // 대소문자 무시
    [InlineData("xyz", "app.log", false)]
    [InlineData("*.log", "app.log", true)]        // 와일드카드는 이름 전체 기준
    [InlineData("*.log", "app.log.bak", false)]
    [InlineData("*.LOG", "app.log", true)]
    [InlineData("log*", "logfile.txt", true)]
    [InlineData("log*", "catalog.txt", false)]
    [InlineData("log_??.txt", "log_01.txt", true)]
    [InlineData("log_??.txt", "log_1.txt", false)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("*", "anything", true)]
    [InlineData("*a", "banana", true)]            // '*' 가 되돌아가며 마지막 a 를 맞춰야 한다
    [InlineData("*ab", "aab", true)]
    public void Matches(string term, string name, bool expected)
        => Assert.Equal(expected, new NameMatcher(term).IsMatch(name));

    [Fact]
    public void Blank_Term_Is_Empty()
        => Assert.True(new NameMatcher("   ").IsEmpty);
}

public class SearchTests
{
    /// <summary>
    ///  C:\
    ///   ├ A (report_a.txt 100, other.txt 5)
    ///   │  └ B (report_b.txt 400)
    ///   ├ C (report_c.txt 50)
    ///   └ reports (폴더, 안에 x.dat 10)
    /// </summary>
    private static NodeStore Build()
    {
        var store = new NodeStore("C:\\");
        store.SetDirectory(1, NodeStore.RootId, "A", 0, 0);
        store.SetDirectory(2, 1, "B", 0, 0);
        store.SetDirectory(3, NodeStore.RootId, "C", 0, 0);
        store.SetDirectory(4, NodeStore.RootId, "reports", 0, 0);

        store.AddFile(1, "report_a.txt", 100, 0, 0);
        store.AddFile(1, "other.txt", 5, 0, 0);
        store.AddFile(2, "report_b.txt", 400, 0, 0);
        store.AddFile(3, "report_c.txt", 50, 0, 0);
        store.AddFile(4, "x.dat", 10, 0, 0);
        store.Seal();
        return store;
    }

    [Fact]
    public void Results_Are_Sorted_BySize_Descending()
    {
        var hits = Build().Search("report");
        Assert.Equal(new long[] { 400, 100, 50, 10 }, hits.Select(h => h.Size));
    }

    [Fact]
    public void Truncation_Keeps_The_LargestMatches_Not_The_First()
    {
        // 회귀: 예전에는 앞에서부터 max 건을 채운 뒤 잘라서 큰 항목이 빠졌다.
        var store = new NodeStore("C:\\");
        store.SetDirectory(1, NodeStore.RootId, "d", 0, 0);
        for (int i = 0; i < 100; i++) store.AddFile(1, $"match_{i}.bin", i + 1, 0, 0);
        store.Seal();

        var outcome = store.SearchWithCount("match", max: 5);

        Assert.Equal(100, outcome.TotalMatches);
        Assert.True(outcome.Truncated);
        Assert.Equal(new long[] { 100, 99, 98, 97, 96 }, outcome.Rows.Select(r => r.Size));
    }

    [Fact]
    public void Wildcard_Matches_Whole_Name()
    {
        var hits = Build().Search("report_?.txt");
        Assert.Equal(3, hits.Count);
        Assert.DoesNotContain(hits, h => h.Name == "reports");   // 폴더 이름은 패턴과 다르다
    }

    [Fact]
    public void Scope_Limits_To_Descendants_Of_Folder()
    {
        var store = Build();

        var underA = store.Search("report", scopeDirId: 1);
        Assert.Equal(new[] { "report_b.txt", "report_a.txt" }, underA.Select(h => h.Name));

        var underB = store.Search("report", scopeDirId: 2);
        Assert.Equal("report_b.txt", underB.Single().Name);
    }

    [Fact]
    public void Scope_Excludes_The_Folder_Itself()
    {
        var store = Build();
        Assert.DoesNotContain(store.Search("reports", scopeDirId: 4), h => h.IsDirectory);
        Assert.Contains(store.Search("reports"), h => h.IsDirectory && h.Name == "reports");
    }

    [Fact]
    public void Filter_Applies_To_Search()
    {
        var store = Build();
        var hits = store.Search("report", filter: new FilterOptions { MinSize = 100 });
        Assert.Equal(new[] { "report_b.txt", "report_a.txt" }, hits.Select(h => h.Name));
    }

    [Fact]
    public void Blank_Term_Returns_Nothing()
    {
        var outcome = Build().SearchWithCount("  ");
        Assert.Empty(outcome.Rows);
        Assert.Equal(0, outcome.TotalMatches);
    }
}

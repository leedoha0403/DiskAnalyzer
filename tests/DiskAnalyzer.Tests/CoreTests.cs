using DiskAnalyzer.Core.Models;
using Xunit;

namespace DiskAnalyzer.Tests;

public class StringPoolTests
{
    [Fact]
    public void RoundTrips_Names()
    {
        var pool = new StringPool();
        var handles = new List<long>();
        var names = new List<string>();

        for (int i = 0; i < 5000; i++)
        {
            string name = $"file_{i}_{new string('x', i % 60)}.dat";
            names.Add(name);
            handles.Add(pool.Add(name));
        }

        for (int i = 0; i < names.Count; i++)
            Assert.Equal(names[i], pool.GetString(handles[i]));
    }

    [Fact]
    public void Survives_ChunkBoundary()
    {
        var pool = new StringPool();
        // 청크(1,048,576 chars)를 확실히 넘기도록 충분히 많이 넣는다.
        var big = new string('a', 250);
        long first = pool.Add(big);
        for (int i = 0; i < 20000; i++) pool.Add(big);
        long last = pool.Add("tail");

        Assert.Equal(big, pool.GetString(first));
        Assert.Equal("tail", pool.GetString(last));
    }
}

public class TopKHeapTests
{
    [Fact]
    public void Keeps_LargestK_Only()
    {
        const int k = 10;
        var heap = new TopKHeap(k);
        var rnd = new Random(1234);
        var sizes = new long[5000];

        for (int i = 0; i < sizes.Length; i++)
        {
            sizes[i] = rnd.NextInt64(0, 1_000_000_000);
            heap.Offer(sizes[i], i);
        }

        var expected = sizes.Select((s, i) => (s, i)).OrderByDescending(t => t.s).Take(k).Select(t => t.i).ToArray();
        var actual = heap.ToSortedDescending(k);

        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < k; i++) Assert.Equal(sizes[expected[i]], sizes[actual[i]]);
    }

    [Fact]
    public void Handles_FewerThanK()
    {
        var heap = new TopKHeap(100);
        heap.Offer(5, 0);
        heap.Offer(9, 1);
        heap.Offer(1, 2);

        var top = heap.ToSortedDescending(100);
        Assert.Equal(new[] { 1, 0, 2 }, top);
    }
}

public class SizeFormatterTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(742L * 1024 * 1024, "742 MB")]
    [InlineData(10L, "10 B")]
    public void Formats_Expected(long bytes, string expected)
        => Assert.Equal(expected, SizeFormatter.Format(bytes, SizeUnitMode.Auto));

    [Fact]
    public void Uses_TwoDecimals_Under10()
        => Assert.Equal("1.28 TB", SizeFormatter.Format((long)(1.28 * 1024 * 1024 * 1024 * 1024), SizeUnitMode.Auto));

    [Fact]
    public void Uses_OneDecimal_ForGigabytes()
        => Assert.Equal("12.8 GB", SizeFormatter.Format((long)(12.8 * 1024 * 1024 * 1024), SizeUnitMode.Auto));
}

public class CategorizerTests
{
    [Theory]
    [InlineData("node_modules", CategoryFlags.Package)]
    [InlineData(".git", CategoryFlags.Git)]
    [InlineData("Debug", CategoryFlags.Build)]
    [InlineData("logs", CategoryFlags.Log)]
    [InlineData("tmp", CategoryFlags.Cache)]
    public void Tags_Directories(string name, CategoryFlags expected)
        => Assert.True((Categorizer.ForDirectory(name, false) & expected) != 0);

    [Fact]
    public void Tags_PdbAsVisualStudioBuild()
    {
        var flags = Categorizer.ForExtension(".PDB");
        Assert.True((flags & CategoryFlags.VisualStudio) != 0);
        Assert.True((flags & CategoryFlags.Build) != 0);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32", true)]
    [InlineData(@"C:\Program Files\App", true)]
    [InlineData(@"C:\", true)]
    [InlineData(@"C:\Users\me\Downloads", false)]
    public void Detects_ProtectedPaths(string path, bool expected)
        => Assert.Equal(expected, Categorizer.IsProtectedPath(path));
}

public class NodeStoreTests
{
    /// <summary>
    ///  C:\
    ///   ├ A (file a1 = 100, a2 = 200)
    ///   │  └ B (file b1 = 400)
    ///   └ C (file c1 = 50)
    /// </summary>
    private static NodeStore BuildSample()
    {
        var store = new NodeStore("C:\\");
        store.SetDirectory(1, NodeStore.RootId, "A", 0, 0);
        store.SetDirectory(2, 1, "B", 0, 0);
        store.SetDirectory(3, NodeStore.RootId, "C", 0, 0);

        store.AddFile(1, "a1.txt", 100, 0, 0);
        store.AddFile(1, "a2.log", 200, 0, 0);
        store.AddFile(2, "b1.pdb", 400, 0, 0);
        store.AddFile(3, "c1.dat", 50, 0, 0);

        store.Seal();
        return store;
    }

    [Fact]
    public void Rollup_Accumulates_BottomUp()
    {
        var store = BuildSample();
        Assert.Equal(750, store.TotalSize);
        Assert.Equal(700, store.GetDirectorySize(1));   // A = 300 + B(400)
        Assert.Equal(400, store.GetDirectorySize(2));
        Assert.Equal(50, store.GetDirectorySize(3));
    }

    [Fact]
    public void Counts_Are_Rolled_Up()
    {
        var store = BuildSample();
        var root = store.GetChildren(NodeStore.RootId, includeFiles: false);
        var a = root.First(r => r.Name == "A");

        Assert.Equal(3, a.FileCount);        // a1, a2, b1
        Assert.Equal(1, a.DirectoryCount);   // B
    }

    [Fact]
    public void Paths_Are_Built_From_ParentChain()
    {
        var store = BuildSample();
        Assert.Equal(@"C:\A\B", store.GetDirectoryPath(2));
        Assert.Equal(@"C:\", store.GetDirectoryPath(NodeStore.RootId));

        var files = store.GetChildren(2, includeFiles: true);
        Assert.Equal(@"C:\A\B\b1.pdb", files.Single().FullPath);
    }

    [Fact]
    public void Children_Are_SortedBySize_Descending()
    {
        var store = BuildSample();
        var rows = store.GetChildren(NodeStore.RootId, includeFiles: false);
        Assert.Equal(new[] { "A", "C" }, rows.Select(r => r.Name));
        Assert.True(rows[0].Ratio > rows[1].Ratio);
    }

    [Fact]
    public void ExtensionStats_Are_Built_During_Scan()
    {
        var store = BuildSample();
        var rows = store.GetExtensionRows();

        Assert.Equal(".pdb", rows[0].Extension);
        Assert.Equal(400, rows[0].Size);
        Assert.Equal(4, rows.Sum(r => r.Count));
    }

    [Fact]
    public void FindDirectory_Resolves_Path()
    {
        var store = BuildSample();
        Assert.Equal(2, store.FindDirectory(@"C:\A\B"));
        Assert.Equal(NodeStore.RootId, store.FindDirectory(@"C:\"));
    }

    [Fact]
    public void Search_Finds_Files_And_Directories()
    {
        var store = BuildSample();
        var hits = store.Search("b");
        Assert.Contains(hits, h => h.Name == "B" && h.IsDirectory);
        Assert.Contains(hits, h => h.Name == "b1.pdb" && !h.IsDirectory);
    }

    [Fact]
    public void Filter_ByMinSize_And_Extension()
    {
        var store = BuildSample();
        var filter = new FilterOptions { MinSize = 150, ExtensionPattern = "*.pdb" };
        var rows = store.GetChildren(1, includeFiles: true, filter);

        Assert.Contains(rows, r => r.Name == "B");           // 폴더는 크기 조건만 적용
        Assert.DoesNotContain(rows, r => r.Name == "a1.txt"); // 150 미만
        Assert.DoesNotContain(rows, r => r.Name == "a2.log"); // 확장자 불일치
    }

    [Fact]
    public void FileSizes_Are_Not_DoubleCounted()
    {
        var store = BuildSample();
        long sumOfChildren = store.GetChildren(NodeStore.RootId, includeFiles: true).Sum(r => r.Size);
        Assert.Equal(store.TotalSize, sumOfChildren);
    }
}

public class ExtensionMatchTests
{
    [Theory]
    [InlineData(".pdb", "*.pdb", true)]
    [InlineData(".pdb", "pdb", true)]
    [InlineData(".pdb", ".PDB", true)]
    [InlineData(".log", "*.pdb;*.log", true)]
    [InlineData(".txt", "*.pdb;*.log", false)]
    [InlineData(".txt", "", true)]
    public void Matches(string ext, string pattern, bool expected)
        => Assert.Equal(expected, NodeStore.MatchesExtension(ext, pattern));
}

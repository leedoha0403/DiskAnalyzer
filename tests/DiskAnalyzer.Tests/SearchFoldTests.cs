using DiskAnalyzer.Core.Analysis;
using DiskAnalyzer.Core.Models;
using Xunit;

namespace DiskAnalyzer.Tests;

/// <summary>
/// 49. 검색 결과 폴더 트리 + 이름 접기.
/// 접기는 "보여 주는 방식"만 바꾼다 — 어떤 결과도 만들거나 없애지 않는다는 것을 매번 확인한다.
/// </summary>
public sealed class SearchFoldTests
{
    private static EntryRow File(string path, long size = 100)
        => new()
        {
            Kind = RowKind.File,
            Id = Math.Abs(path.GetHashCode()),
            Name = path[(path.LastIndexOf('\\') + 1)..],
            FullPath = path,
            Size = size,
        };

    private static EntryRow Dir(string path, long size = 100)
        => new()
        {
            Kind = RowKind.Directory,
            Id = Math.Abs(path.GetHashCode()),
            Name = path[(path.LastIndexOf('\\') + 1)..],
            FullPath = path,
            Size = size,
        };

    /// <summary>트리 전체를 훑어 Item 노드의 경로를 모은다(접혔든 펼쳐졌든 결과는 다 살아 있어야 한다).</summary>
    private static List<string> ItemPaths(SearchFoldTree tree)
    {
        var paths = new List<string>();
        foreach (var r in tree.Roots) Walk(r);
        return paths;

        void Walk(SearchNode n)
        {
            if (n.Kind == SearchNodeKind.Item) paths.Add(n.FullPath);
            foreach (var c in n.Children) Walk(c);
        }
    }

    private static IEnumerable<SearchNode> All(SearchFoldTree tree)
    {
        var stack = new Stack<SearchNode>(tree.Roots);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var c in n.Children) stack.Push(c);
        }
    }

    [Fact]
    public void An_empty_result_makes_an_empty_tree()
    {
        var tree = SearchFolder.Build(Array.Empty<EntryRow>());
        Assert.Empty(tree.Roots);
        Assert.Equal(0, tree.PatternCount);
    }

    [Fact]
    public void Few_results_are_not_folded()
    {
        var rows = new[] { File(@"C:\p\a.txt"), File(@"C:\p\b.txt") };
        var tree = SearchFolder.Build(rows);

        Assert.DoesNotContain(All(tree), n => n.Kind == SearchNodeKind.Pattern);
        Assert.Equal(2, ItemPaths(tree).Count);
    }

    [Fact]
    public void The_same_name_in_many_folders_folds_under_the_common_parent()
    {
        // obj\Debug\a.pdb 가 프로젝트 6개에 하나씩 — 검색하면 6줄이 나오는 전형적인 경우
        var rows = Enumerable.Range(0, 6).Select(i => File($@"C:\src\proj{i}\obj\a.pdb")).ToArray();
        var tree = SearchFolder.Build(rows);

        var pattern = Assert.Single(All(tree), n => n.Kind == SearchNodeKind.Pattern);
        Assert.Equal("a.pdb", pattern.Title);
        Assert.Equal("여러 폴더에 있는 같은 이름", pattern.Detail);
        Assert.Equal(6, pattern.MatchCount);
        Assert.Equal(@"C:\src", pattern.FullPath);            // 공통 상위 폴더에 놓인다

        // 멤버는 어디에 있는지 알 수 있게 상대 경로로 보여 준다
        Assert.Contains(pattern.Children, c => c.Title == @"proj0\obj\a.pdb");

        // 접었어도 결과 6건은 그대로 살아 있다
        Assert.Equal(6, ItemPaths(tree).Count);
        Assert.Equal(6, tree.FoldedItemCount);
    }

    [Fact]
    public void Numbered_names_in_one_folder_fold_into_a_wildcard_line()
    {
        var rows = Enumerable.Range(1, 7).Select(i => File($@"C:\logs\log_{i:D4}.txt")).ToArray();
        var tree = SearchFolder.Build(rows);

        var pattern = Assert.Single(All(tree), n => n.Kind == SearchNodeKind.Pattern);
        Assert.Equal("log_*.txt", pattern.Title);
        Assert.Equal(7, pattern.MatchCount);
        Assert.All(pattern.Children, c => Assert.StartsWith("log_", c.Title));   // 같은 폴더 묶음은 이름만
        Assert.Equal(7, ItemPaths(tree).Count);
    }

    [Fact]
    public void Different_drives_are_not_folded_together()
    {
        var rows = Enumerable.Range(0, 3).Select(i => File($@"C:\a{i}\x.dll"))
            .Concat(Enumerable.Range(0, 3).Select(i => File($@"D:\b{i}\x.dll")))
            .ToArray();
        var tree = SearchFolder.Build(rows);

        // 공통 상위 폴더가 없으므로 6건이 한 줄로 합쳐지지 않는다
        Assert.DoesNotContain(All(tree), n => n.Kind == SearchNodeKind.Pattern && n.MatchCount == 6);
        Assert.Equal(6, ItemPaths(tree).Count);
        Assert.Equal(2, tree.Roots.Count);
    }

    [Fact]
    public void A_single_result_folder_chain_is_collapsed_into_one_line()
    {
        var tree = SearchFolder.Build(new[] { File(@"C:\Users\me\AppData\only.txt") });

        var root = Assert.Single(tree.Roots);
        Assert.Contains("AppData", root.Title);              // C:\ → Users → me → AppData 가 한 줄로
        Assert.Equal(@"C:\Users\me\AppData", root.FullPath);
    }

    [Fact]
    public void Sizes_and_counts_roll_up_to_the_folders()
    {
        var rows = new[] { File(@"C:\p\a.txt", 10), File(@"C:\p\q\b.txt", 20), File(@"C:\p\q\c.txt", 30) };
        var tree = SearchFolder.Build(rows);

        var root = Assert.Single(tree.Roots);
        Assert.Equal(60, root.Size);
        Assert.Equal(3, root.MatchCount);
    }

    [Fact]
    public void Children_are_sorted_by_size_descending()
    {
        var rows = new[] { File(@"C:\p\small.txt", 1), File(@"C:\p\big.txt", 1000), File(@"C:\p\mid.txt", 50) };
        var tree = SearchFolder.Build(rows);

        var names = Assert.Single(tree.Roots).Children.Select(c => c.Title).ToList();
        Assert.Equal(new[] { "big.txt", "mid.txt", "small.txt" }, names);
    }

    [Fact]
    public void Folder_results_are_placed_next_to_their_sibling_files()
    {
        var tree = SearchFolder.Build(new[] { Dir(@"C:\p\cache"), File(@"C:\p\cache.txt") });

        var root = Assert.Single(tree.Roots);
        Assert.Equal(@"C:\p", root.FullPath);
        Assert.Equal(2, root.Children.Count);
    }

    [Fact]
    public void Folders_start_expanded_but_folded_groups_start_closed()
    {
        var rows = Enumerable.Range(0, 6).Select(i => File($@"C:\src\proj{i}\a.pdb")).ToArray();
        var tree = SearchFolder.Build(rows);

        Assert.True(Assert.Single(tree.Roots).IsExpanded);
        Assert.All(All(tree).Where(n => n.Kind == SearchNodeKind.Pattern).ToList(), n => Assert.False(n.IsExpanded));
    }

    [Fact]
    public void Every_item_node_keeps_the_original_row()
    {
        var rows = Enumerable.Range(0, 6).Select(i => File($@"C:\src\proj{i}\a.pdb")).ToArray();
        var tree = SearchFolder.Build(rows);

        var items = All(tree).Where(n => n.Kind == SearchNodeKind.Item).ToList();
        Assert.Equal(6, items.Count);
        Assert.All(items, n => Assert.NotNull(n.Row));
        Assert.Equal(rows.Select(r => r.FullPath).OrderBy(p => p),
            items.Select(n => n.Row!.FullPath).OrderBy(p => p));
    }

    [Fact]
    public void MinFold_controls_when_folding_starts()
    {
        var rows = Enumerable.Range(0, 3).Select(i => File($@"C:\src\proj{i}\a.pdb")).ToArray();

        Assert.DoesNotContain(All(SearchFolder.Build(rows)), n => n.Kind == SearchNodeKind.Pattern);
        Assert.Single(All(SearchFolder.Build(rows, minFold: 3)), n => n.Kind == SearchNodeKind.Pattern);
    }
}

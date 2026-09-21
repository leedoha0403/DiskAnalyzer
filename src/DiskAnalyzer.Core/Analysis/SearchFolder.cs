using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Analysis;

/// <summary>
/// 49. 검색 결과를 폴더 트리로 묶고, 이름이 반복되는 결과를 한 줄로 접는다.
///
/// 검색은 이름으로 찾는 기능이라 결과에 같은 이름이 잔뜩 나오는 것이 정상이다
/// (`a.pdb` 가 obj\Debug 와 obj\Release 에 하나씩, 프로젝트 40개면 80줄). 그 80줄을 다 읽게 하지 않고
/// "어느 폴더들에 몇 건이 있는가"를 먼저 보여 준 뒤 필요할 때만 펼치게 한다.
///
/// 접는 규칙은 정리 추천 탭과 같다(<see cref="CleanupPatternFolder"/> 의 이름 정규화를 그대로 쓴다).
///  (a) 같은 폴더 안에서 숫자 / 날짜 / 해시만 다른 이름   log_1.txt, log_2.txt ... -> "log_*.txt ×N건"
///  (b) 서로 다른 폴더에 있는 같은 이름                   obj\Debug\a.pdb, obj\Release\a.pdb -> 공통 상위 폴더 아래 "a.pdb ×N건"
///
/// 접는 것은 보여 주는 방식뿐이다 — 결과를 만들거나 없애지 않고, 모든 Item 노드는 원래 <see cref="EntryRow"/> 를 그대로 들고 있다.
/// </summary>
public static class SearchFolder
{
    /// <summary>
    /// 검색은 2건만 겹쳐도 묶는 것이 이득이다. 정리 추천 탭은 5건인데, 거기서는 "지울 것"을 고르느라
    /// 개별 항목이 보여야 하지만 검색은 "어디 있나"를 먼저 본다. 릴리스 폴더 두 개에 같은 파일이
    /// 하나씩 있는 경우(가장 흔한 중복)가 5건 규칙에서는 전혀 묶이지 않는다.
    /// </summary>
    public const int DefaultMinFold = 2;

    private sealed class Dir
    {
        public required string Path { get; init; }
        public required string Name { get; init; }
        public Dir? Parent { get; init; }
        public int Depth { get; init; }
        public List<Dir> Kids { get; } = new();

        /// <summary>이 폴더 바로 아래에서 찾은 결과.</summary>
        public List<EntryRow> Leaves { get; } = new();

        /// <summary>패턴 판정을 거쳐 이 폴더에 실제로 붙는 노드.</summary>
        public List<SearchNode> Nodes { get; } = new();
    }

    /// <param name="minFold">이 건수 이상일 때만 접는다(<see cref="DefaultMinFold"/> 참고).</param>
    /// <param name="expandDepth">이 깊이까지는 처음부터 펼쳐 둔다.</param>
    public static SearchFoldTree Build(IReadOnlyList<EntryRow> rows, int minFold = DefaultMinFold, int expandDepth = 2)
    {
        if (rows.Count == 0) return SearchFoldTree.Empty;
        minFold = Math.Max(2, minFold);

        var dirs = new Dictionary<string, Dir>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<Dir>();

        foreach (var row in rows)
        {
            // 폴더 결과는 그 폴더 자신이 아니라 "어디에 있는가"로 묶어야 형제 파일과 같은 자리에 선다.
            string parent = CleanupGrouper.ParentOf(row.FullPath);
            if (parent.Length == 0) parent = row.FullPath;
            EnsureDir(parent, dirs, roots).Leaves.Add(row);
        }

        int patternCount = 0, foldedItems = 0;
        var pool = new List<(Dir Dir, EntryRow Row)>();

        // (a) 같은 폴더 안의 반복 패턴
        foreach (var dir in dirs.Values)
        {
            if (dir.Leaves.Count == 0) continue;

            foreach (var g in dir.Leaves.GroupBy(KeyOf))
            {
                var members = g.ToList();
                if (members.Count >= minFold)
                {
                    dir.Nodes.Add(MakePattern(dir, members, crossFolder: false));
                    patternCount++;
                    foldedItems += members.Count;
                }
                else
                {
                    foreach (var m in members) pool.Add((dir, m));
                }
            }
        }

        // (b) 여러 폴더에 흩어진 같은 이름. 드라이브가 다르면 공통 상위 폴더가 없으므로 루트별로 따로 묶는다.
        var leftover = new List<(Dir Dir, EntryRow Row)>();
        foreach (var g in pool.GroupBy(p => (Root: RootOf(p.Dir), Key: KeyOf(p.Row))))
        {
            var members = g.ToList();
            int distinctDirs = members.Select(m => m.Dir).Distinct().Count();

            if (members.Count >= minFold && distinctDirs >= 2)
            {
                var home = CommonAncestor(members.Select(m => m.Dir));
                home.Nodes.Add(MakePattern(home, members.Select(m => m.Row).ToList(), crossFolder: true));
                patternCount++;
                foldedItems += members.Count;
            }
            else
            {
                leftover.AddRange(members);
            }
        }

        foreach (var (dir, row) in leftover)
            dir.Nodes.Add(MakeItem(row, row.Name));

        var tree = new List<SearchNode>();
        foreach (var r in roots)
        {
            var node = ToNode(r);
            if (node == null) continue;
            Compress(node);
            tree.Add(node);
        }

        foreach (var r in tree) Accumulate(r, 0, expandDepth);
        tree.Sort(static (a, b) => b.Size.CompareTo(a.Size));

        return new SearchFoldTree { Roots = tree, PatternCount = patternCount, FoldedItemCount = foldedItems };
    }

    private static string KeyOf(EntryRow row)
        => (row.IsDirectory ? "D:" : "F:") + CleanupPatternFolder.PatternKey(row.Name, row.IsDirectory);

    private static Dir EnsureDir(string path, Dictionary<string, Dir> dirs, List<Dir> roots)
    {
        if (dirs.TryGetValue(path, out var existing)) return existing;

        string parentPath = CleanupGrouper.ParentOf(path);
        bool isRoot = parentPath.Length == 0 || string.Equals(parentPath, path, StringComparison.OrdinalIgnoreCase);
        var parent = isRoot ? null : EnsureDir(parentPath, dirs, roots);

        string name = CleanupGrouper.SegmentName(path);
        var dir = new Dir
        {
            Path = path,
            Name = parent == null && name.EndsWith(':') ? name + "\\" : name,
            Parent = parent,
            Depth = parent == null ? 0 : parent.Depth + 1,
        };
        dirs[path] = dir;

        if (parent == null) roots.Add(dir);
        else parent.Kids.Add(dir);
        return dir;
    }

    private static Dir RootOf(Dir d)
    {
        while (d.Parent != null) d = d.Parent;
        return d;
    }

    /// <summary>같은 루트에 속한 폴더들의 가장 가까운 공통 상위 폴더.</summary>
    private static Dir CommonAncestor(IEnumerable<Dir> members)
    {
        Dir? common = null;
        foreach (var d in members)
        {
            if (common == null) { common = d; continue; }

            var a = common;
            var b = d;
            while (a.Depth > b.Depth) a = a.Parent!;
            while (b.Depth > a.Depth) b = b.Parent!;
            while (!ReferenceEquals(a, b))
            {
                a = a.Parent!;
                b = b.Parent!;
            }
            common = a;
        }
        return common!;
    }

    private static SearchNode MakeItem(EntryRow row, string title) => new()
    {
        Kind = SearchNodeKind.Item,
        Title = title,
        FullPath = row.FullPath,
        Row = row,
    };

    private static SearchNode MakePattern(Dir home, List<EntryRow> members, bool crossFolder)
    {
        var first = members[0];
        string label = CleanupPatternFolder.PatternLabel(first.Name, first.IsDirectory);

        var node = new SearchNode
        {
            Kind = SearchNodeKind.Pattern,
            Title = label,
            FullPath = home.Path,
            Detail = crossFolder
                ? "여러 폴더에 있는 같은 이름"
                : (label.Contains('*') ? "같은 이름 패턴이 반복됨" : "같은 이름"),
        };

        foreach (var m in members)
        {
            // 같은 폴더 안의 묶음은 이름만, 여러 폴더의 묶음은 어디에 있는지 알 수 있게 상대 경로로 보여 준다.
            node.Children.Add(MakeItem(m, crossFolder ? RelativeTo(home.Path, m.FullPath) : m.Name));
        }
        return node;
    }

    private static string RelativeTo(string basePath, string fullPath)
    {
        string prefix = basePath.TrimEnd('\\');
        return fullPath.Length > prefix.Length + 1 &&
               fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? fullPath[(prefix.Length + 1)..]
            : fullPath;
    }

    private static SearchNode? ToNode(Dir dir)
    {
        var node = new SearchNode
        {
            Kind = SearchNodeKind.Folder,
            Title = dir.Name,
            FullPath = dir.Path,
        };

        foreach (var kid in dir.Kids)
        {
            var child = ToNode(kid);
            if (child != null) node.Children.Add(child);
        }
        node.Children.AddRange(dir.Nodes);

        // 결과가 하나도 남지 않은 폴더(묶음이 상위로 올라가 비게 된 경우)는 보여 줄 필요가 없다.
        return node.Children.Count == 0 ? null : node;
    }

    /// <summary>결과가 하나뿐인 폴더 체인(C:\ → Users → me → AppData)은 한 줄로 합친다.</summary>
    private static void Compress(SearchNode node)
    {
        while (node.Kind == SearchNodeKind.Folder
               && node.Children.Count == 1
               && node.Children[0].Kind == SearchNodeKind.Folder)
        {
            var child = node.Children[0];
            node.Title = node.Title.EndsWith('\\') ? node.Title + child.Title : node.Title + "\\" + child.Title;
            node.FullPath = child.FullPath;
            node.Children.Clear();
            node.Children.AddRange(child.Children);
        }

        foreach (var c in node.Children)
            if (c.Kind == SearchNodeKind.Folder) Compress(c);
    }

    private static void Accumulate(SearchNode node, int depth, int expandDepth)
    {
        node.Depth = depth;

        if (node.Kind == SearchNodeKind.Item)
        {
            node.Size = node.Row!.Size;
            node.MatchCount = 1;
            return;
        }

        long size = 0;
        int count = 0;
        foreach (var child in node.Children)
        {
            Accumulate(child, depth + 1, expandDepth);
            size += child.Size;
            count += child.MatchCount;
        }

        node.Size = size;
        node.MatchCount = count;
        node.Children.Sort(static (a, b) => b.Size.CompareTo(a.Size));

        // 묶음 줄은 접어 두는 것이 목적이므로 깊이와 무관하게 닫아 둔다.
        node.IsExpanded = node.Kind == SearchNodeKind.Folder && depth < expandDepth;
    }
}

using System.Text.RegularExpressions;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Analysis;

/// <summary>
/// 정리 후보를 폴더 트리로 묶고, 이름 패턴이 반복되는 후보는 한 줄로 접는다.
///
/// 접는 규칙 (두 가지, 최소 <c>minFold</c> 건 이상일 때만):
///  (a) 같은 폴더 안에서 숫자 / 날짜 / 해시만 다른 이름   log_20240101.txt, log_20240102.txt -> "log_*.txt ×N건"
///  (b) 서로 다른 폴더에 있는 같은 이름(패턴)               obj\Debug\a.pdb, obj\Release\a.pdb -> 공통 상위 폴더 아래 "a.pdb ×N건"
///
/// 접힌 묶음도 펼치면 원래 후보가 그대로 나오고, 선택 상태와 삭제 대상 계산은 후보 자체를 기준으로 한다.
/// 즉 이 클래스는 "보여 주는 방식"만 바꾸며 어떤 후보도 만들거나 없애지 않는다.
/// </summary>
public static class CleanupPatternFolder
{
    public const int DefaultMinFold = 5;
    private const char Slot = '\u0001';

    // 해시/GUID 를 먼저 치환한 뒤 남은 숫자를 치환한다. 순서가 바뀌면 GUID 안의 숫자가 먼저 잘려 나간다.
    private static readonly Regex GuidRx = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    // 8자 이상의 16진수 토큰 중 숫자와 알파벳이 모두 섞인 것(순수 숫자는 아래 숫자 규칙이, 순수 영단어는 그대로 둔다).
    private static readonly Regex HexRx = new(
        @"(?<![0-9A-Za-z])(?=[0-9a-fA-F]*[0-9])(?=[0-9a-fA-F]*[a-fA-F])[0-9a-fA-F]{8,}(?![0-9A-Za-z])",
        RegexOptions.Compiled);

    private static readonly Regex DigitsRx = new(@"[0-9]+", RegexOptions.Compiled);

    // 2024-01-02 -> "#-#-#" -> "#" : 구분자로만 이어진 변수 조각은 하나로 합친다.
    private static readonly Regex MergeRx = new($"{Slot}(?:[-_.: ]?{Slot})+", RegexOptions.Compiled);

    // ------------------------------------------------------------------ 이름 정규화

    /// <summary>같은 패턴이면 같은 값이 나오는 비교용 키. 변수 부분은 '#'.</summary>
    public static string PatternKey(string name, bool isDirectory = false)
        => Normalize(name, isDirectory).Replace(Slot, '#').ToLowerInvariant();

    /// <summary>화면에 보여 줄 이름. 변수 부분은 '*' 이고 원래 대소문자를 유지한다.</summary>
    public static string PatternLabel(string name, bool isDirectory = false)
        => Normalize(name, isDirectory).Replace(Slot, '*');

    private static string Normalize(string name, bool isDirectory)
    {
        string stem = name;
        string ext = string.Empty;

        if (!isDirectory)
        {
            int dot = name.LastIndexOf('.');
            if (dot > 0 && dot < name.Length - 1 && name.Length - dot <= 17)
            {
                stem = name[..dot];
                ext = name[dot..];
            }
        }

        // 분할 압축(.001 .002 ...)처럼 확장자가 숫자뿐이면 그 숫자도 변수다. 그 외 확장자(.mp3 / .7z)는 그대로 둔다.
        if (ext.Length > 1 && ext.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            stem += ext;
            ext = string.Empty;
        }

        stem = GuidRx.Replace(stem, Slot.ToString());
        stem = HexRx.Replace(stem, Slot.ToString());
        stem = DigitsRx.Replace(stem, Slot.ToString());
        stem = MergeRx.Replace(stem, Slot.ToString());
        return stem + ext;
    }

    // ------------------------------------------------------------------ 트리 구성

    private sealed class Dir
    {
        public required string Path { get; init; }
        public required string Name { get; init; }
        public Dir? Parent { get; init; }
        public int Depth { get; init; }
        public List<Dir> Kids { get; } = new();

        /// <summary>이 폴더 바로 아래에 있는 후보.</summary>
        public List<CleanupCandidate> Leaves { get; } = new();

        /// <summary>패턴 판정을 거쳐 이 폴더에 실제로 붙는 노드(후보 + 패턴 묶음).</summary>
        public List<CleanupFoldNode> Nodes { get; } = new();
    }

    public static CleanupFoldTree Build(
        IReadOnlyList<CleanupCandidate> candidates, int minFold = DefaultMinFold, int expandDepth = 3)
    {
        minFold = Math.Max(2, minFold);

        var dirs = new Dictionary<string, Dir>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<Dir>();

        foreach (var c in candidates)
        {
            string parent = CleanupGrouper.ParentOf(c.FullPath);
            if (parent.Length == 0) parent = c.FullPath;
            EnsureDir(parent, dirs, roots).Leaves.Add(c);
        }

        int patternCount = 0, foldedItems = 0;
        var pool = new List<(Dir Dir, CleanupCandidate Cand)>();

        // (a) 같은 폴더 안의 반복 패턴
        foreach (var dir in dirs.Values)
        {
            if (dir.Leaves.Count == 0) continue;

            foreach (var g in dir.Leaves.GroupBy(c => KeyOf(c)))
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

        // (b) 여러 폴더에 흩어진 같은 이름. 공통 상위 폴더에 한 줄로 모은다.
        var leftover = new List<(Dir Dir, CleanupCandidate Cand)>();
        // 드라이브가 다르면 공통 상위 폴더가 없으므로 드라이브별로 따로 묶는다.
        foreach (var g in pool.GroupBy(p => (Root: RootOf(p.Dir), Key: KeyOf(p.Cand))))
        {
            var members = g.ToList();
            int distinctDirs = members.Select(m => m.Dir).Distinct().Count();

            if (members.Count >= minFold && distinctDirs >= 2)
            {
                var home = CommonAncestor(members.Select(m => m.Dir));
                home.Nodes.Add(MakePattern(home, members.Select(m => m.Cand).ToList(), crossFolder: true));
                patternCount++;
                foldedItems += members.Count;
            }
            else
            {
                leftover.AddRange(members);
            }
        }

        foreach (var (dir, cand) in leftover)
            dir.Nodes.Add(MakeItem(cand, cand.Name));

        // Dir 트리 -> 노드 트리
        var tree = new CleanupFoldTree { Roots = new List<CleanupFoldNode>(), PatternCount = patternCount, FoldedItemCount = foldedItems };
        foreach (var r in roots)
        {
            var node = ToNode(r);
            if (node == null) continue;
            Compress(node);
            tree.Roots.Add(node);
        }

        foreach (var r in tree.Roots) Accumulate(r, tree, 0, expandDepth);
        tree.Roots.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        return tree;
    }

    private static string KeyOf(CleanupCandidate c)
        => (c.IsDirectory ? "D:" : "F:") + PatternKey(c.Name, c.IsDirectory);

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
            // 드라이브 루트는 "C:" 보다 "C:\" 가 자연스럽다.
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

    /// <summary>같은 드라이브(같은 루트)에 속한 폴더들의 가장 가까운 공통 상위 폴더.</summary>
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

    private static CleanupFoldNode MakeItem(CleanupCandidate c, string title) => new()
    {
        Kind = CleanupNodeKind.Item,
        Title = title,
        FullPath = c.FullPath,
        Detail = c.Reason,
        Candidate = c,
    };

    private static CleanupFoldNode MakePattern(Dir home, List<CleanupCandidate> members, bool crossFolder)
    {
        var first = members[0];
        string label = PatternLabel(first.Name, first.IsDirectory);

        var node = new CleanupFoldNode
        {
            Kind = CleanupNodeKind.Pattern,
            Title = label,
            FullPath = home.Path,
            Detail = crossFolder
                ? "여러 폴더에 있는 같은 이름"
                : (label.Contains('*') ? "같은 이름 패턴이 반복됨" : "같은 이름"),
        };

        foreach (var m in members)
        {
            // 같은 폴더 안의 묶음은 이름만, 여러 폴더의 묶음은 어디에 있는지 알 수 있게 상대 경로로 보여 준다.
            string title = crossFolder ? RelativeTo(home.Path, m.FullPath) : m.Name;
            node.Children.Add(MakeItem(m, title));
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

    private static CleanupFoldNode? ToNode(Dir dir)
    {
        var node = new CleanupFoldNode
        {
            Kind = CleanupNodeKind.Folder,
            Title = dir.Name,
            FullPath = dir.Path,
        };

        foreach (var kid in dir.Kids)
        {
            var child = ToNode(kid);
            if (child != null) node.Children.Add(child);
        }
        node.Children.AddRange(dir.Nodes);

        // 후보가 하나도 남지 않은 폴더(패턴 묶음이 상위로 올라가 비게 된 경우)는 보여 줄 필요가 없다.
        return node.Children.Count == 0 ? null : node;
    }

    /// <summary>후보가 하나뿐인 폴더 체인(C:\ → Users → me → AppData)은 "C:\Users\me\AppData" 한 줄로 합친다.</summary>
    private static void Compress(CleanupFoldNode node)
    {
        while (node.Kind == CleanupNodeKind.Folder
               && node.Children.Count == 1
               && node.Children[0].Kind == CleanupNodeKind.Folder)
        {
            var child = node.Children[0];
            node.Title = node.Title.EndsWith('\\') ? node.Title + child.Title : node.Title + "\\" + child.Title;
            node.FullPath = child.FullPath;
            node.Children.Clear();
            node.Children.AddRange(child.Children);
        }

        foreach (var c in node.Children)
            if (c.Kind == CleanupNodeKind.Folder) Compress(c);
    }

    private static void Accumulate(CleanupFoldNode node, CleanupFoldTree owner, int depth, int expandDepth)
    {
        node.Owner = owner;
        node.Depth = depth;

        if (node.Kind == CleanupNodeKind.Item)
        {
            var c = node.Candidate!;
            node.Size = c.Size;
            node.FileCount = c.IsDirectory ? c.FileCount : 1;
            node.CandidateCount = 1;
            node.Grade = c.Grade;
            node.Category = c.Category;
            node.SelectableLeaves = c.CanSelect ? new[] { c } : Array.Empty<CleanupCandidate>();
            return;
        }

        long size = 0, files = 0;
        int count = 0;
        var grade = CleanupGrade.HighlyCleanable;
        CleanupCategory? category = null;
        bool firstCategory = true, mixedCategory = false;
        var leaves = new List<CleanupCandidate>();

        foreach (var child in node.Children)
        {
            Accumulate(child, owner, depth + 1, expandDepth);
            size += child.Size;
            files += child.FileCount;
            count += child.CandidateCount;
            if (child.Grade > grade) grade = child.Grade;      // 가장 보수적인 등급
            leaves.AddRange(child.SelectableLeaves);

            if (child.Category is { } cc)
            {
                if (firstCategory) { category = cc; firstCategory = false; }
                else if (category != cc) mixedCategory = true;
            }
            else if (child.Kind != CleanupNodeKind.Item)
            {
                mixedCategory = true;   // 자식이 이미 여러 분류를 품고 있다
            }
        }

        node.Size = size;
        node.FileCount = files;
        node.CandidateCount = count;
        node.Grade = grade;
        node.Category = mixedCategory ? null : category;
        node.SelectableLeaves = leaves.ToArray();
        node.Children.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        node.IsExpanded = node.Kind == CleanupNodeKind.Folder && depth < expandDepth;
    }
}

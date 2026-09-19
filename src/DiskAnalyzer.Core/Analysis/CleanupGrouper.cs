using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Analysis;

/// <summary>
/// 3/4. 정리 후보를 "파일별 / 경로별 / 유형별 / 정리 이유별"로 묶는다.
///
/// 분석(CleanupAnalyzer)과 분리한 이유: 보기 방식을 바꿀 때마다 파일 시스템을 다시 분석할 필요가 없다.
/// 이미 만들어진 후보 목록만 다시 묶으면 되고, 후보 수는 수천 건이라 즉시 끝난다.
/// </summary>
public static class CleanupGrouper
{
    /// <summary>
    /// 3-1. 경로별 - 파일 후보는 부모 폴더, 폴더 후보는 자기 경로를 대표 경로로 삼는다.
    /// 이렇게 하면 요구사항 예시(Temp / .vs / x64\Debug / Downloads)와 같은 단위가 자연스럽게 나온다.
    /// </summary>
    public static List<CleanupGroup> ByPath(IReadOnlyList<CleanupCandidate> candidates, int maxGroups = 500)
    {
        var buckets = new Dictionary<string, List<CleanupCandidate>>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in candidates)
        {
            string key = c.IsDirectory ? c.FullPath : ParentOf(c.FullPath);
            if (key.Length == 0) key = c.FullPath;

            if (!buckets.TryGetValue(key, out var list))
            {
                list = new List<CleanupCandidate>(4);
                buckets[key] = list;
            }
            list.Add(c);
        }

        return buckets
            .Select(kv => Build(kv.Key, PathSubtitle(kv.Value), kv.Value))
            .OrderByDescending(g => g.Size)
            .Take(maxGroups)
            .ToList();
    }

    /// <summary>3. 유형별 - 확장자(파일) / 폴더.</summary>
    public static List<CleanupGroup> ByType(IReadOnlyList<CleanupCandidate> candidates, int maxGroups = 200)
    {
        var buckets = new Dictionary<string, List<CleanupCandidate>>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in candidates)
        {
            string key = c.IsDirectory
                ? "폴더"
                : (c.Extension.Length > 0 ? c.Extension : "(확장자 없음)");

            if (!buckets.TryGetValue(key, out var list))
            {
                list = new List<CleanupCandidate>(8);
                buckets[key] = list;
            }
            list.Add(c);
        }

        return buckets
            .Select(kv => Build(kv.Key, $"{kv.Value.Count:N0}건", kv.Value))
            .OrderByDescending(g => g.Size)
            .Take(maxGroups)
            .ToList();
    }

    /// <summary>
    /// 3. 정리 이유별 - 분류 + 등급 조합.
    /// "왜 후보로 올라왔는가"가 같은 것끼리 묶여서, 한 종류를 통째로 판단할 수 있다.
    /// </summary>
    public static List<CleanupGroup> ByReason(IReadOnlyList<CleanupCandidate> candidates)
    {
        return candidates
            .GroupBy(c => (c.Category, c.Grade))
            .Select(g => Build(
                $"{CleanupText.Of(g.Key.Category)}  {CleanupText.Of(g.Key.Grade)}",
                RepresentativeReason(g),
                g.ToList()))
            .OrderByDescending(g => g.Size)
            .ToList();
    }

    /// <summary>
    /// 4. 경로 계층 트리.
    /// 후보 경로들을 드라이브부터 쪼개 트리를 만들고, 각 노드에 하위 후보 용량을 합산한다.
    /// 의미 없는 1자식 체인(C: -> Users -> me)은 그대로 두되 기본 펼침으로 접근성을 확보한다.
    /// </summary>
    public static List<CleanupPathNode> BuildTree(IReadOnlyList<CleanupCandidate> candidates, int expandDepth = 6)
    {
        var roots = new List<CleanupPathNode>();
        var index = new Dictionary<string, CleanupPathNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in candidates)
        {
            string path = c.IsDirectory ? c.FullPath : ParentOf(c.FullPath);
            if (path.Length == 0) continue;

            var node = EnsureNode(path, roots, index);
            node.Items.Add(c);
        }

        foreach (var r in roots) Accumulate(r, 0, expandDepth);

        roots.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        return roots;
    }

    // ------------------------------------------------------------------ 내부

    private static CleanupPathNode EnsureNode(
        string path, List<CleanupPathNode> roots, Dictionary<string, CleanupPathNode> index)
    {
        if (index.TryGetValue(path, out var existing)) return existing;

        string parentPath = ParentOf(path);
        var node = new CleanupPathNode
        {
            Name = SegmentName(path),
            FullPath = path,
        };
        index[path] = node;

        if (parentPath.Length == 0 || string.Equals(parentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            roots.Add(node);
        }
        else
        {
            var parent = EnsureNode(parentPath, roots, index);
            parent.Children.Add(node);
        }
        return node;
    }

    private static void Accumulate(CleanupPathNode node, int depth, int expandDepth)
    {
        long size = 0, files = 0;
        int count = 0;
        var grade = CleanupGrade.HighlyCleanable;
        CleanupCategory? category = null;

        foreach (var i in node.Items)
        {
            size += i.Size;
            files += i.IsDirectory ? i.FileCount : 1;
            count++;
            if (i.Grade > grade) grade = i.Grade;      // 더 보수적인 등급으로 올린다
            category = category is null || category == i.Category ? i.Category : category;
        }

        foreach (var c in node.Children)
        {
            Accumulate(c, depth + 1, expandDepth);
            size += c.Size;
            files += c.FileCount;
            count += c.CandidateCount;
            if (c.CandidateCount > 0 && c.Grade > grade) grade = c.Grade;
        }

        node.Size = size;
        node.FileCount = files;
        node.CandidateCount = count;
        node.Grade = grade;
        node.Category = node.Items.Count > 0 ? category : null;
        node.IsExpanded = depth < expandDepth;
        node.Children.Sort(static (a, b) => b.Size.CompareTo(a.Size));
    }

    private static CleanupGroup Build(string title, string subtitle, List<CleanupCandidate> items)
    {
        long size = 0, files = 0;
        DateTime oldest = DateTime.MaxValue, newest = DateTime.MinValue;
        var grade = CleanupGrade.HighlyCleanable;
        var risk = CleanupRisk.Low;

        foreach (var i in items)
        {
            size += i.Size;
            files += i.IsDirectory ? i.FileCount : 1;
            if (i.Modified != default)
            {
                if (i.Modified < oldest) oldest = i.Modified;
                if (i.Modified > newest) newest = i.Modified;
            }
            if (i.Grade > grade) grade = i.Grade;   // 그룹 등급은 가장 보수적인 값
            if (i.Risk > risk) risk = i.Risk;
        }

        items.Sort(static (a, b) => b.Size.CompareTo(a.Size));

        return new CleanupGroup
        {
            Title = title,
            Subtitle = subtitle,
            Items = items,
            Size = size,
            FileCount = files,
            Oldest = oldest == DateTime.MaxValue ? default : oldest,
            Newest = newest == DateTime.MinValue ? default : newest,
            Category = items.Count > 0 ? items[0].Category : CleanupCategory.OldLargeFile,
            Grade = grade,
            Risk = risk,
        };
    }

    private static string PathSubtitle(List<CleanupCandidate> items)
    {
        var categories = items.Select(i => i.Category).Distinct().Take(3).Select(CleanupText.Of);
        return string.Join(" · ", categories);
    }

    private static string RepresentativeReason(IEnumerable<CleanupCandidate> items)
    {
        var first = items.FirstOrDefault();
        if (first == null) return string.Empty;
        // 그룹 전체를 대표하는 문장 하나를 보여 준다(58. 이유 없는 추천 금지).
        return first.Reason.Length > 90 ? first.Reason[..90] + "..." : first.Reason;
    }

    internal static string ParentOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        string trimmed = path.TrimEnd('\\');
        int idx = trimmed.LastIndexOf('\\');
        if (idx < 0) return string.Empty;
        if (idx == 2 && trimmed.Length > 2 && trimmed[1] == ':') return trimmed[..3];   // "C:\"
        return trimmed[..idx];
    }

    internal static string SegmentName(string path)
    {
        string trimmed = path.TrimEnd('\\');
        int idx = trimmed.LastIndexOf('\\');
        return idx < 0 ? trimmed : trimmed[(idx + 1)..];
    }
}

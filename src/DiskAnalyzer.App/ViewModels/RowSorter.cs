using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.App.ViewModels;

/// <summary>정렬이 적용되는 목록. 목록마다 정렬 상태를 따로 기억한다.</summary>
public enum SortTarget { Folder, LargeFiles, Extensions, ExtensionFiles }

public readonly record struct SortSpec(string Key, bool Descending);

/// <summary>
/// 컬럼 헤더 정렬.
///
/// 예전에는 헤더를 클릭하면 코드에서 <c>list.ItemsSource = 정렬된 리스트</c> 로 바꿨다.
/// WPF 에서 로컬 값을 지정하면 XAML 의 <c>{Binding Rows}</c> 가 <strong>사라진다</strong>.
/// 그 뒤로는 ViewModel 이 새 목록(폴더 이동 / 검색 / 필터)을 만들어도 화면에 반영되지 않았다.
///
/// 그래서 정렬을 ViewModel 이 맡는다. 목록이 새로 만들어질 때마다 현재 정렬을 적용해서 내보내고,
/// 바인딩은 그대로 유지된다. CollectionView.SortDescriptions 는 리플렉션 비교자라 10만 행에서 느려서 쓰지 않는다.
/// </summary>
public static class RowSorter
{
    public static IReadOnlyList<EntryRow> Sort(IReadOnlyList<EntryRow> rows, SortSpec? spec)
    {
        if (spec is not { } s || rows.Count < 2) return rows;
        var list = rows.ToList();
        list.Sort(CompareEntries(s));
        return list;
    }

    public static IReadOnlyList<ExtensionRow> Sort(IReadOnlyList<ExtensionRow> rows, SortSpec? spec)
    {
        if (spec is not { } s || rows.Count < 2) return rows;
        var list = rows.ToList();
        list.Sort(CompareExtensions(s));
        return list;
    }

    private static Comparison<EntryRow> CompareEntries(SortSpec s)
    {
        int sign = s.Descending ? -1 : 1;
        return (x, y) => sign * s.Key switch
        {
            "Name" => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase),
            "Size" => x.Size.CompareTo(y.Size),
            "FileCount" => x.FileCount.CompareTo(y.FileCount),
            "DirectoryCount" => x.DirectoryCount.CompareTo(y.DirectoryCount),
            "Modified" => x.Modified.CompareTo(y.Modified),
            "Extension" => string.Compare(x.Extension, y.Extension, StringComparison.OrdinalIgnoreCase),
            "Category" => string.Compare(x.CategoryText, y.CategoryText, StringComparison.OrdinalIgnoreCase),
            "FullPath" => string.Compare(x.FullPath, y.FullPath, StringComparison.OrdinalIgnoreCase),
            _ => 0,
        };
    }

    private static Comparison<ExtensionRow> CompareExtensions(SortSpec s)
    {
        int sign = s.Descending ? -1 : 1;
        return (x, y) => sign * s.Key switch
        {
            "Extension" => string.Compare(x.Extension, y.Extension, StringComparison.OrdinalIgnoreCase),
            "Size" => x.Size.CompareTo(y.Size),
            "Count" => x.Count.CompareTo(y.Count),
            "Category" => string.Compare(x.CategoryText, y.CategoryText, StringComparison.OrdinalIgnoreCase),
            _ => 0,
        };
    }

    /// <summary>같은 키를 다시 누르면 방향을 뒤집고, 다른 키면 내림차순으로 시작한다.</summary>
    public static SortSpec Toggle(SortSpec? current, string key)
        => current is { } c && c.Key == key
            ? c with { Descending = !c.Descending }
            : new SortSpec(key, true);
}

namespace DiskAnalyzer.Core.Models;

/// <summary>
/// 36. 확장자 집계.
///
/// 스캔이 끝난 뒤 파일 전체를 다시 순회하지 않는다. Aggregator 가 파일 하나를 받을 때
/// 바로 이 테이블을 갱신하므로 확장자 통계는 스캔 진행 중에도 항상 최신이다.
///
/// .NET 9 의 Dictionary AlternateLookup 을 사용해 ReadOnlySpan&lt;char&gt; 로 조회하므로
/// 파일마다 확장자 string 을 새로 할당하지 않는다(파일 수백만 개 × 24B 할당 제거).
/// Aggregator 단일 스레드 전용이라 Lock 이 없다.
/// </summary>
public sealed class ExtensionTable
{
    private readonly Dictionary<string, int> _map = new(512, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    private string[] _names = new string[512];
    private long[] _sizes = new long[512];
    private long[] _counts = new long[512];
    private CategoryFlags[] _categories = new CategoryFlags[512];
    private int _count;

    public ExtensionTable()
    {
        _lookup = _map.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public int Count => _count;
    public string NameOf(int id) => (uint)id < (uint)_count ? _names[id] : string.Empty;
    public CategoryFlags CategoryOf(int id) => (uint)id < (uint)_count ? _categories[id] : CategoryFlags.None;

    public int Add(ReadOnlySpan<char> extension, long size)
    {
        if (!_lookup.TryGetValue(extension, out int id))
        {
            id = _count;
            if (id >= _names.Length) Grow();

            string name = extension.IsEmpty ? "(없음)" : new string(extension).ToLowerInvariant();
            _names[id] = name;
            _categories[id] = Categorizer.ForExtension(extension);
            _map[name] = id;
            _count++;
        }

        _sizes[id] += size;
        _counts[id]++;
        return id;
    }

    public int Find(string extension)
        => _map.TryGetValue(extension, out int id) ? id : -1;

    /// <summary>
    /// 14. 삭제 직후 UI 즉시 반영.
    /// 파일이 지워지면 확장자 통계에서도 바로 빼 준다(전체 재스캔 없이).
    /// </summary>
    public void Remove(int id, long size)
    {
        if ((uint)id >= (uint)_count) return;
        _sizes[id] -= size;
        _counts[id]--;
        if (_sizes[id] < 0) _sizes[id] = 0;
        if (_counts[id] < 0) _counts[id] = 0;
    }

    /// <summary>파일 크기가 외부에서 바뀐 경우 차이만 반영한다.</summary>
    public void Adjust(int id, long delta)
    {
        if ((uint)id >= (uint)_count) return;
        _sizes[id] += delta;
        if (_sizes[id] < 0) _sizes[id] = 0;
    }

    public IReadOnlyList<ExtensionRow> BuildRows(long totalSize)
    {
        var rows = new List<ExtensionRow>(_count);
        for (int i = 0; i < _count; i++)
        {
            rows.Add(new ExtensionRow
            {
                Extension = _names[i],
                Size = _sizes[i],
                Count = _counts[i],
                Ratio = totalSize > 0 ? (double)_sizes[i] / totalSize : 0d,
                Category = _categories[i],
            });
        }
        rows.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        return rows;
    }

    private void Grow()
    {
        int n = _names.Length * 2;
        Array.Resize(ref _names, n);
        Array.Resize(ref _sizes, n);
        Array.Resize(ref _counts, n);
        Array.Resize(ref _categories, n);
    }
}

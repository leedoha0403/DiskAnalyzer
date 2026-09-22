using DiskAnalyzer.Core.Analysis;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.QuickMove;

namespace DiskAnalyzer.Core.Services;

/// <summary>수집함에 담긴 항목 하나.</summary>
public sealed class CollectedItem
{
    public required string FullPath { get; init; }
    public required string Name { get; init; }
    public required bool IsDirectory { get; init; }
    public required long Size { get; init; }
    public required ProtectionLevel Level { get; init; }

    /// <summary>담을 당시의 저장소 좌표. 삭제 후 모델 갱신에 쓴다. 다시 스캔했으면 맞지 않을 수 있다.</summary>
    public RowKind Kind => IsDirectory ? RowKind.Directory : RowKind.File;
    public int Id { get; init; } = -1;

    public string SizeText => SizeFormatter.Format(Size);
    public string BadgeText => ProtectionText.Badge(Level);

    /// <summary>P1 은 담기되 눈에 띄게 표시한다 — 담는 것과 지우는 것은 다른 행동이다.</summary>
    public bool IsRisky => Level == ProtectionLevel.P1;

    /// <summary>화면 낭독기와 UI 자동화가 읽는 이름. 두지 않으면 타입 이름이 그대로 읽힌다.</summary>
    public override string ToString() => $"{BadgeText} {FullPath} {SizeText}";
}

/// <summary><see cref="Collector.TryAdd"/> 가 왜 그렇게 됐는지.</summary>
public enum CollectResult
{
    Added,

    /// <summary>이미 담겨 있다.</summary>
    Duplicate,

    /// <summary>이미 담긴 폴더 안에 있다 — 따로 담을 필요가 없다.</summary>
    CoveredByFolder,

    /// <summary>P0. 어떤 경로로도 담을 수 없다.</summary>
    Protected,

    /// <summary>묶음(작은 항목 N개)처럼 실체가 없는 항목.</summary>
    NotCollectable,
}

/// <summary>
/// 수집함 — 지울 것을 <b>고르는 행위</b>와 <b>지우는 행위</b>를 분리하는 장바구니.
///
/// <para>기존 선택은 목록(탭)에 묶여 있어서 폴더를 옮기면 사라진다. 그래서 "여기서 하나,
/// 저기서 하나" 를 모아 한 번에 지우는 동선이 불가능했다. 수집함은 탐색과 독립이다 —
/// 어디를 돌아다니든 담긴 목록과 합계가 그대로 남는다.</para>
///
/// <para>규칙 세 가지:</para>
/// <list type="number">
/// <item><b>P0 는 들어오지 않는다.</b> 삭제 확인 창에서 거르는 것이 아니라 애초에 담기지 않는다.</item>
/// <item><b>조상이 담기면 자손은 빠진다.</b> 폴더와 그 안의 파일을 함께 담으면 합계가 두 번 세어진다.</item>
/// <item><b>합계는 항상 정확하다.</b> 중복이 구조적으로 없으므로 단순 합이다.</item>
/// </list>
///
/// <para>UI 타입을 참조하지 않는다. 변경은 <see cref="Changed"/> 하나로만 알린다.</para>
/// </summary>
public sealed class Collector
{
    private readonly Dictionary<string, CollectedItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private long _totalSize;

    /// <summary>담긴 것이 바뀌었다. 무엇이 바뀌었는지는 알리지 않는다 — 목록이 작아 다시 읽으면 된다.</summary>
    public event EventHandler? Changed;

    public int Count => _items.Count;
    public long TotalSize => _totalSize;
    public bool IsEmpty => _items.Count == 0;

    public string TotalSizeText => SizeFormatter.Format(_totalSize);

    /// <summary>P1 이 하나라도 담겨 있는가. 하단 바가 경고 색을 쓸지 결정한다.</summary>
    public bool HasRisky
    {
        get
        {
            foreach (var item in _items.Values)
                if (item.IsRisky) return true;
            return false;
        }
    }

    /// <summary>담긴 순서가 아니라 <b>큰 것부터</b> 준다 — "무엇이 이 합계를 만들었나"가 먼저 보여야 한다.</summary>
    public IReadOnlyList<CollectedItem> Items
    {
        get
        {
            var list = new List<CollectedItem>(_items.Values);
            list.Sort(static (a, b) => b.Size.CompareTo(a.Size));
            return list;
        }
    }

    public bool Contains(string fullPath)
        => PathUtil.TryNormalize(fullPath, out string p) && _items.ContainsKey(p);

    /// <summary>이 경로가 담겨 있거나, 담긴 폴더 안에 있는가. 링에 "담김" 표시를 그릴 때 쓴다.</summary>
    public bool Covers(string fullPath)
    {
        if (_items.Count == 0) return false;
        if (!PathUtil.TryNormalize(fullPath, out string path)) return false;
        if (_items.ContainsKey(path)) return true;

        foreach (var item in _items.Values)
            if (item.IsDirectory && IsUnder(path, item.FullPath)) return true;
        return false;
    }

    public CollectResult TryAdd(string fullPath, string name, bool isDirectory, long size,
                                CategoryFlags category = CategoryFlags.None, int id = -1)
        => TryAdd(fullPath, name, isDirectory, size,
                  ProtectionEvaluator.EvaluateEntry(fullPath, isDirectory, category).Level, id);

    /// <summary>보호 등급을 이미 알고 있을 때(목록이 방금 판정했을 때) 쓰는 쪽.</summary>
    public CollectResult TryAdd(string fullPath, string name, bool isDirectory, long size,
                                ProtectionLevel level, int id = -1)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return CollectResult.NotCollectable;
        if (level == ProtectionLevel.P0) return CollectResult.Protected;

        if (!PathUtil.TryNormalize(fullPath, out string path)) return CollectResult.NotCollectable;
        if (_items.ContainsKey(path)) return CollectResult.Duplicate;

        // 이미 담긴 폴더 안에 있으면 담지 않는다. 지울 때 어차피 함께 사라진다.
        foreach (var item in _items.Values)
            if (item.IsDirectory && IsUnder(path, item.FullPath)) return CollectResult.CoveredByFolder;

        // 폴더를 담으면 그 안에 이미 담겨 있던 것들은 빠진다 - 합계가 두 번 세어지는 것을 막는다.
        if (isDirectory) RemoveDescendants(path);

        _items[path] = new CollectedItem
        {
            FullPath = path,
            Name = string.IsNullOrEmpty(name) ? path : name,
            IsDirectory = isDirectory,
            Size = size,
            Level = level,
            Id = id,
        };
        _totalSize += size;
        Raise();
        return CollectResult.Added;
    }

    public bool Remove(string fullPath)
    {
        if (!PathUtil.TryNormalize(fullPath, out string path)) return false;
        if (!_items.Remove(path, out var removed)) return false;
        _totalSize -= removed.Size;
        Raise();
        return true;
    }

    /// <summary>담겨 있으면 빼고 없으면 담는다. 같은 단축키를 두 번 누르면 원래대로 돌아간다.</summary>
    public CollectResult Toggle(string fullPath, string name, bool isDirectory, long size,
                                ProtectionLevel level, int id = -1)
        => Remove(fullPath)
            ? CollectResult.Duplicate
            : TryAdd(fullPath, name, isDirectory, size, level, id);

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        _totalSize = 0;
        Raise();
    }

    /// <summary>삭제가 끝난 뒤 성공한 것만 걷어낸다. 실패한 것은 남겨 다시 시도할 수 있게 둔다.</summary>
    public void RemoveMany(IEnumerable<string> fullPaths)
    {
        bool changed = false;
        foreach (string raw in fullPaths)
        {
            if (!PathUtil.TryNormalize(raw, out string p)) continue;
            if (!_items.Remove(p, out var removed)) continue;
            _totalSize -= removed.Size;
            changed = true;
        }
        if (changed) Raise();
    }

    private void RemoveDescendants(string folderPath)
    {
        List<string>? doomed = null;
        foreach (var (key, item) in _items)
        {
            if (!IsUnder(key, folderPath)) continue;
            (doomed ??= new List<string>()).Add(key);
            _totalSize -= item.Size;
        }
        if (doomed == null) return;
        foreach (string key in doomed) _items.Remove(key);
    }

    /// <summary>
    /// <paramref name="path"/> 가 <paramref name="folder"/> 아래에 있는가.
    /// 구분자를 붙여 비교하므로 <c>C:\Data2</c> 가 <c>C:\Data</c> 아래로 잡히지 않는다.
    /// </summary>
    private static bool IsUnder(string path, string folder)
    {
        if (path.Length <= folder.Length) return false;
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return false;

        // 폴더가 이미 구분자로 끝나면(드라이브 루트 "C:\") 그 자체가 경계다.
        if (folder.EndsWith(Path.DirectorySeparatorChar)) return true;
        return path[folder.Length] == Path.DirectorySeparatorChar;
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

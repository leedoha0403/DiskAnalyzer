using System.ComponentModel;

namespace DiskAnalyzer.Core.Models;

/// <summary>3. 정리 추천 보기 방식.</summary>
public enum CleanupViewMode
{
    Files,
    Paths,
    Types,
    Reasons,
}

/// <summary>
/// 3-1/5. 정리 후보 그룹.
/// 그룹 자체를 체크하면 내부 후보가 모두 선택되고, 내부에서 개별 파일을 다시 제외할 수 있어야 하므로
/// 선택 상태는 3단계(전체 / 일부 / 없음)다.
/// </summary>
public sealed class CleanupGroup : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _suppress;

    public required string Title { get; init; }
    public string Subtitle { get; init; } = string.Empty;
    public required IReadOnlyList<CleanupCandidate> Items { get; init; }

    public long Size { get; init; }
    public long FileCount { get; init; }
    public DateTime Oldest { get; init; }
    public DateTime Newest { get; init; }
    public CleanupCategory Category { get; init; }
    public CleanupGrade Grade { get; init; }
    public CleanupRisk Risk { get; init; }

    public string SizeText => SizeFormatter.Format(Size);
    public string FileCountText => SizeFormatter.Count(FileCount) + " files";
    public string CategoryText => CleanupText.Of(Category);
    public string GradeText => CleanupText.Of(Grade);
    public string RiskText => CleanupText.Of(Risk);
    public string OldestText => Oldest == default ? "-" : Oldest.ToString("yyyy-MM-dd");
    public string NewestText => Newest == default ? "-" : Newest.ToString("yyyy-MM-dd");
    public string ItemCountText => SizeFormatter.Count(Items.Count) + "건";

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; Raise(nameof(IsExpanded)); }
    }

    /// <summary>null = 일부만 선택(3-state 체크박스).</summary>
    public bool? IsSelected
    {
        get
        {
            int selected = 0;
            foreach (var i in Items) if (i.IsSelected) selected++;
            if (selected == 0) return false;
            return selected == Items.Count ? true : null;
        }
        set
        {
            if (_suppress || value is null) return;
            _suppress = true;
            foreach (var i in Items) i.IsSelected = value.Value;
            _suppress = false;
            Raise(nameof(IsSelected));
        }
    }

    /// <summary>내부 항목이 개별로 바뀌었을 때 그룹 체크박스를 다시 계산하게 한다.</summary>
    public void RefreshSelection() => Raise(nameof(IsSelected));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 4. 경로 계층형 그룹.
/// 각 노드는 자기 하위에 있는 정리 후보 용량의 합을 갖는다.
/// "어느 경로 밑에 정리할 게 몰려 있는가"를 바로 보기 위한 구조.
/// </summary>
public sealed class CleanupPathNode : INotifyPropertyChanged
{
    private bool _isExpanded;

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public List<CleanupPathNode> Children { get; } = new();

    /// <summary>이 노드에 직접 매달린 후보(하위 폴더로 더 내려가지 않는 것).</summary>
    public List<CleanupCandidate> Items { get; } = new();

    public long Size { get; set; }
    public long FileCount { get; set; }
    public int CandidateCount { get; set; }
    public CleanupGrade Grade { get; set; } = CleanupGrade.HighlyCleanable;
    public CleanupCategory? Category { get; set; }

    public bool HasChildren => Children.Count > 0;
    public string SizeText => SizeFormatter.Format(Size);
    public string FileCountText => FileCount > 0 ? SizeFormatter.Count(FileCount) + " files" : string.Empty;
    public string GradeText => CandidateCount > 0 ? CleanupText.Of(Grade) : string.Empty;
    public string CategoryText => Category is { } c ? CleanupText.Of(c) : string.Empty;

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; Raise(nameof(IsExpanded)); }
    }

    /// <summary>하위 전체를 포함한 선택 상태(3-state).</summary>
    public bool? IsSelected
    {
        get
        {
            int total = 0, selected = 0;
            Count(this, ref total, ref selected);
            if (total == 0) return false;
            if (selected == 0) return false;
            return selected == total ? true : null;
        }
        set
        {
            if (value is null) return;
            Apply(this, value.Value);
            Raise(nameof(IsSelected));
        }
    }

    public void RefreshSelection()
    {
        Raise(nameof(IsSelected));
        foreach (var c in Children) c.RefreshSelection();
    }

    private static void Count(CleanupPathNode node, ref int total, ref int selected)
    {
        foreach (var i in node.Items)
        {
            total++;
            if (i.IsSelected) selected++;
        }
        foreach (var c in node.Children) Count(c, ref total, ref selected);
    }

    private static void Apply(CleanupPathNode node, bool value)
    {
        foreach (var i in node.Items) i.IsSelected = value;
        foreach (var c in node.Children) Apply(c, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

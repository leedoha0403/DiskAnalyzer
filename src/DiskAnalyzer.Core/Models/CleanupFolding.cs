using System.ComponentModel;

namespace DiskAnalyzer.Core.Models;

public enum CleanupNodeKind
{
    /// <summary>폴더. 하위 노드를 접고 펼친다.</summary>
    Folder,

    /// <summary>같은 이름 패턴이 반복되는 후보 묶음 ("log_*.txt ×1,200건").</summary>
    Pattern,

    /// <summary>후보 한 건(파일 또는 폴더).</summary>
    Item,
}

/// <summary>
/// 정리 후보를 "폴더 트리 + 반복 패턴 접기"로 보여 주는 트리.
/// 같은 이름 파일이 수백 개 나열되어 한눈에 안 보이는 문제를 줄이려는 구조다.
/// </summary>
public sealed class CleanupFoldTree
{
    public required List<CleanupFoldNode> Roots { get; init; }

    /// <summary>접힌 패턴 묶음 수.</summary>
    public int PatternCount { get; init; }

    /// <summary>패턴 묶음 안으로 들어간 후보 수.</summary>
    public int FoldedItemCount { get; init; }

    /// <summary>
    /// 폴더/패턴 체크박스로 수천 건을 한 번에 켜고 끌 때 true.
    /// 후보마다 "선택 변경 → 전체 재집계"가 돌면 O(N²) 이 되므로, 이 동안 화면 쪽 재집계를 미뤘다가
    /// <see cref="BulkSelectionCompleted"/> 에서 한 번만 한다.
    /// </summary>
    public bool IsBulkUpdating { get; private set; }

    public event EventHandler? BulkSelectionCompleted;

    internal void ApplySelection(IReadOnlyList<CleanupCandidate> items, bool value)
    {
        IsBulkUpdating = true;
        try
        {
            // P0 는 CleanupCandidate.IsSelected 의 setter 가 어떤 경로로도 거부한다.
            foreach (var c in items) c.IsSelected = value;
        }
        finally
        {
            IsBulkUpdating = false;
        }
        BulkSelectionCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>후보의 선택 상태가 바깥에서 바뀌었을 때 모든 노드의 체크박스를 다시 계산하게 한다.</summary>
    public void RefreshSelection()
    {
        foreach (var r in Roots) r.RefreshSelection();
    }

    public void SetAllExpanded(bool expanded)
    {
        foreach (var r in Roots) r.SetExpanded(expanded, keepRootsOpen: !expanded);
    }

    /// <summary>재구성 후에도 사용자가 펼쳐 둔 위치를 유지하기 위한 키.</summary>
    public HashSet<string> CaptureExpanded()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Roots) r.CollectExpanded(keys);
        return keys;
    }

    public void RestoreExpanded(HashSet<string> keys)
    {
        if (keys.Count == 0) return;
        foreach (var r in Roots) r.ApplyExpanded(keys);
    }
}

/// <summary>폴더 / 패턴 / 후보 한 건. 하나의 HierarchicalDataTemplate 로 그릴 수 있게 한 종류의 클래스로 통일했다.</summary>
public sealed class CleanupFoldNode : INotifyPropertyChanged
{
    private bool _isExpanded;

    internal CleanupFoldTree? Owner;

    /// <summary>이 노드 아래(자신 포함)에서 실제로 선택 가능한 후보. 3단계 체크박스 계산 대상.</summary>
    internal CleanupCandidate[] SelectableLeaves = Array.Empty<CleanupCandidate>();

    public required CleanupNodeKind Kind { get; init; }
    public required string Title { get; set; }

    /// <summary>폴더: 폴더 경로 / 패턴: 놓인 폴더 경로 / 후보: 전체 경로.</summary>
    public string FullPath { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;
    public CleanupCandidate? Candidate { get; init; }
    public List<CleanupFoldNode> Children { get; } = new();

    /// <summary>트리에서의 깊이(루트 0). 화면이 행 너비를 들여쓰기만큼 줄여 열을 세로로 맞추는 데 쓴다.</summary>
    public int Depth { get; internal set; }

    public long Size { get; internal set; }
    public long FileCount { get; internal set; }
    public int CandidateCount { get; internal set; }
    public CleanupGrade Grade { get; internal set; }
    public CleanupCategory? Category { get; internal set; }

    public bool IsFolder => Kind == CleanupNodeKind.Folder;
    public bool IsPattern => Kind == CleanupNodeKind.Pattern;
    public bool IsItem => Kind == CleanupNodeKind.Item;
    public bool IsDirectoryItem => Candidate?.IsDirectory ?? false;

    public string SizeText => SizeFormatter.Format(Size);
    public string GradeText => CandidateCount > 0 ? CleanupText.Of(Grade) : string.Empty;
    public string CategoryText => Category is { } c ? CleanupText.Of(c) : string.Empty;

    /// <summary>오른쪽 열: 폴더 "12건 · 340 files" / 패턴 "×1,200 · 1,200 files" / 후보 유형과 수정일.</summary>
    public string CountText => Kind switch
    {
        CleanupNodeKind.Item => Candidate is { } c
            ? $"{c.TypeText}  ·  수정 {c.ModifiedText}"
            : string.Empty,
        CleanupNodeKind.Pattern => $"×{SizeFormatter.Count(CandidateCount)}건",
        _ => $"{SizeFormatter.Count(CandidateCount)}건" +
             (FileCount > CandidateCount ? $"  ·  {SizeFormatter.Count(FileCount)} files" : string.Empty),
    };

    public bool CanSelect => SelectableLeaves.Length > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; Raise(nameof(IsExpanded)); }
    }

    /// <summary>true = 하위 전체 / null = 일부 / false = 없음.</summary>
    public bool? IsSelected
    {
        get
        {
            var leaves = SelectableLeaves;
            if (leaves.Length == 0) return false;

            int selected = 0;
            foreach (var l in leaves) if (l.IsSelected) selected++;
            if (selected == 0) return false;
            return selected == leaves.Length ? true : null;
        }
        set
        {
            // 일부 선택(null) 상태에서 누르면 WPF 는 false 를 넘긴다 = "일부 → 전체 해제".
            bool on = value == true;
            if (Owner != null) Owner.ApplySelection(SelectableLeaves, on);
            else foreach (var l in SelectableLeaves) l.IsSelected = on;
            Raise(nameof(IsSelected));
        }
    }

    public void RefreshSelection()
    {
        Raise(nameof(IsSelected));
        foreach (var c in Children) c.RefreshSelection();
    }

    internal void SetExpanded(bool expanded, bool keepRootsOpen)
    {
        if (Kind != CleanupNodeKind.Item) IsExpanded = expanded || keepRootsOpen;
        foreach (var c in Children) c.SetExpanded(expanded, keepRootsOpen: false);
    }

    internal string ExpandKey => $"{Kind}|{FullPath}|{Title}";

    internal void CollectExpanded(HashSet<string> keys)
    {
        if (Kind == CleanupNodeKind.Item) return;
        if (IsExpanded) keys.Add(ExpandKey);
        foreach (var c in Children) c.CollectExpanded(keys);
    }

    internal void ApplyExpanded(HashSet<string> keys)
    {
        if (Kind == CleanupNodeKind.Item) return;
        IsExpanded = keys.Contains(ExpandKey);
        foreach (var c in Children) c.ApplyExpanded(keys);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

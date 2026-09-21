using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DiskAnalyzer.Core.Models;

public enum SearchNodeKind
{
    /// <summary>폴더. 하위 결과를 품는다.</summary>
    Folder,

    /// <summary>같은 이름(패턴)이 여러 건 묶인 줄. 펼치면 원래 결과가 그대로 나온다.</summary>
    Pattern,

    /// <summary>검색 결과 하나.</summary>
    Item,
}

/// <summary>
/// 49. 검색 결과를 폴더 트리로 묶고 반복되는 이름을 한 줄로 접은 노드.
/// 정리 추천 탭의 <see cref="DiskAnalyzer.Core.Analysis.CleanupPatternFolder"/> 와 같은 규칙을 쓰지만
/// 선택 / 등급 같은 정리 전용 상태는 없다 — 검색은 "찾아서 가는" 화면이기 때문이다.
/// </summary>
public sealed class SearchNode : INotifyPropertyChanged
{
    private bool _isExpanded;

    public required SearchNodeKind Kind { get; init; }

    /// <summary>화면에 보여 줄 이름. 폴더 체인이 합쳐지면 `C:\Users\me\AppData` 처럼 길어질 수 있다.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>폴더면 그 폴더 경로, 항목이면 그 항목의 경로, 묶음이면 묶음이 놓인 폴더.</summary>
    public string FullPath { get; set; } = string.Empty;

    /// <summary>묶음 줄에만 붙는 설명("여러 폴더에 있는 같은 이름" 등).</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Item 노드가 가리키는 검색 결과. 이동 / 우클릭 동작은 이것을 그대로 쓴다.</summary>
    public EntryRow? Row { get; init; }

    public List<SearchNode> Children { get; } = new();

    public long Size { get; set; }

    /// <summary>이 노드 아래의 검색 결과 수(폴더 자신은 세지 않는다).</summary>
    public int MatchCount { get; set; }

    public int Depth { get; set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Raise();
        }
    }

    public bool IsItem => Kind == SearchNodeKind.Item;
    public bool IsFolder => Kind == SearchNodeKind.Folder;

    public string SizeText => SizeFormatter.Format(Size);

    /// <summary>항목 하나면 비우고, 묶음 / 폴더면 몇 건인지 보여 준다.</summary>
    public string CountText => Kind == SearchNodeKind.Item ? string.Empty : $"×{MatchCount:N0}건";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}

/// <summary>검색 결과 접기 한 번의 산출물.</summary>
public sealed class SearchFoldTree
{
    public required IReadOnlyList<SearchNode> Roots { get; init; }

    /// <summary>접힌 묶음 줄의 수.</summary>
    public int PatternCount { get; init; }

    /// <summary>그 묶음들이 품은 결과의 수.</summary>
    public int FoldedItemCount { get; init; }

    public static SearchFoldTree Empty { get; } = new() { Roots = Array.Empty<SearchNode>() };
}

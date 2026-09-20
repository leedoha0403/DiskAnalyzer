using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using DiskAnalyzer.Core.Keymaps;

namespace DiskAnalyzer.App.ViewModels;

/// <summary>단축키 목록의 한 줄. 상태는 모두 <see cref="KeymapDraft"/>(적용 전 임시 키맵)에서 읽는다.</summary>
public sealed class ShortcutRowViewModel : ObservableObject
{
    private readonly KeymapDraft _draft;
    private readonly Func<string, bool> _isConflict;
    private bool _isCapturing;

    public ShortcutRowViewModel(CommandDef def, KeymapDraft draft, Func<string, bool> isConflict)
    {
        Def = def;
        _draft = draft;
        _isConflict = isConflict;
    }

    public CommandDef Def { get; }
    public string Id => Def.Id;
    public string Category => Def.Category;
    public string Name => Def.Name;

    public KeyChord Chord => _draft.Get(Id);
    public bool IsUnassigned => Chord.IsEmpty;
    public string ShortcutText => IsUnassigned ? "미할당" : Chord.ToDisplay();
    public string DefaultText => Def.Default.IsEmpty ? "미할당" : Def.Default.ToDisplay();

    public bool IsModified => _draft.IsModified(Id);
    public bool IsConflict => _isConflict(Id);

    public string StatusText => IsConflict ? "⚠ 충돌" : IsModified ? "변경됨" : string.Empty;

    public string ResetToolTip => $"기본값으로 복원 ({DefaultText})";

    public bool IsCapturing
    {
        get => _isCapturing;
        set => Set(ref _isCapturing, value);
    }

    /// <summary>키·상태가 바뀌었음을 화면에 알린다.</summary>
    public void Refresh()
    {
        Raise(nameof(Chord));
        Raise(nameof(IsUnassigned));
        Raise(nameof(ShortcutText));
        Raise(nameof(IsModified));
        Raise(nameof(IsConflict));
        Raise(nameof(StatusText));
    }
}

public sealed class KeyboardShortcutsViewModel : ObservableObject
{
    public const string AllCategories = "전체";

    private readonly Keymap _keymap;
    private readonly KeymapDraft _draft;
    private IReadOnlySet<string> _conflicts = new HashSet<string>();

    private string _searchText = string.Empty;
    private string _selectedCategory = AllCategories;
    private bool _showModifiedOnly;
    private bool _showConflictsOnly;
    private string _message = string.Empty;
    private bool _messageIsError;
    private ShortcutRowViewModel? _selectedRow;
    private ShortcutRowViewModel? _capturingRow;
    private PendingConflict? _pending;

    /// <summary>키를 눌렀더니 다른 명령과 겹친 상황. 해결(3가지 중 하나)하기 전까지 안내 막대를 띄운다.</summary>
    private sealed record PendingConflict(ShortcutRowViewModel Row, string OtherId, KeyChord Chord, KeyChord Previous);

    public KeyboardShortcutsViewModel(Keymap keymap)
    {
        _keymap = keymap;
        _draft = new KeymapDraft(keymap);

        Rows = new ObservableCollection<ShortcutRowViewModel>(
            CommandCatalog.All.Select(def => new ShortcutRowViewModel(def, _draft, id => _conflicts.Contains(id))));

        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is ShortcutRowViewModel r && Matches(r);

        Categories = new[] { AllCategories }.Concat(CommandCatalog.Categories).ToList();
        RefreshAll();
    }

    public ObservableCollection<ShortcutRowViewModel> Rows { get; }
    public ICollectionView View { get; }
    public IReadOnlyList<string> Categories { get; }

    // ------------------------------------------------------------------ 검색 / 필터 (14, 15, 36, 37)

    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value)) View.Refresh(); }
    }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set { if (Set(ref _selectedCategory, value ?? AllCategories)) View.Refresh(); }
    }

    public bool ShowModifiedOnly
    {
        get => _showModifiedOnly;
        set { if (Set(ref _showModifiedOnly, value)) View.Refresh(); }
    }

    public bool ShowConflictsOnly
    {
        get => _showConflictsOnly;
        set { if (Set(ref _showConflictsOnly, value)) View.Refresh(); }
    }

    /// <summary>기능 이름·분류·명령 ID·단축키 어디에든 들어 있으면 보인다. "ctrl", "Ctrl + 1", "copy", "복사" 모두 통한다.</summary>
    private bool Matches(ShortcutRowViewModel row)
    {
        if (_selectedCategory != AllCategories && row.Category != _selectedCategory) return false;
        if (_showModifiedOnly && !row.IsModified) return false;
        if (_showConflictsOnly && !row.IsConflict) return false;

        string query = _searchText.Trim();
        if (query.Length == 0) return true;

        string compactQuery = query.Replace(" ", string.Empty);
        return Contains(row.Name, query) || Contains(row.Category, query) || Contains(row.Id, query)
            || Contains(row.Chord.ToDisplay(), query)
            || Contains(row.Chord.ToDisplay(spaced: false), compactQuery)
            || Contains(row.Chord.Serialize(), compactQuery);
    }

    private static bool Contains(string text, string query)
        => text.Contains(query, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ 상태 표시

    public ShortcutRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set => Set(ref _selectedRow, value);
    }

    public int ConflictCount => _conflicts.Count;
    public bool HasConflicts => _conflicts.Count > 0;
    public string ConflictText => $"⚠ 충돌 {_conflicts.Count}개";

    public int ModifiedCount => Rows.Count(r => r.IsModified);
    public string ModifiedText => $"변경됨 {ModifiedCount}개";

    public bool IsDirty => _draft.IsDirty;

    /// <summary>충돌이 있으면 적용할 수 없다(11).</summary>
    public bool CanApply => IsDirty && !HasConflicts;

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public bool MessageIsError
    {
        get => _messageIsError;
        private set => Set(ref _messageIsError, value);
    }

    public bool HasMessage => !string.IsNullOrEmpty(_message);

    private void Say(string message, bool isError = false)
    {
        Message = message;
        MessageIsError = isError;
        Raise(nameof(HasMessage));
    }

    // ------------------------------------------------------------------ 키 입력 캡처 (5, 6, 7, 8)

    public ShortcutRowViewModel? CapturingRow => _capturingRow;

    public void BeginCapture(ShortcutRowViewModel row)
    {
        EndCapture();
        _pending = null;
        RaisePending();
        _capturingRow = row;
        row.IsCapturing = true;
        SelectedRow = row;
        Say($"'{row.Name}' 에 사용할 키를 누르세요. Esc 는 취소입니다.");
    }

    /// <summary>입력 대기를 끝낸다. 값은 바꾸지 않는다.</summary>
    public void EndCapture()
    {
        if (_capturingRow != null) _capturingRow.IsCapturing = false;
        _capturingRow = null;
    }

    /// <summary>입력 대기 중 Esc: 변경하지 않고 이전 값으로 돌아간다(8).</summary>
    public void CancelCapture()
    {
        if (_capturingRow == null) return;
        EndCapture();
        Say(string.Empty);
    }

    /// <summary>눌린 키를 받는다. 등록할 수 없는 키면 안내만 하고 계속 입력을 기다린다.</summary>
    public void Capture(KeyChord chord)
    {
        var row = _capturingRow;
        if (row == null) return;

        var previous = row.Chord;
        string? error = _draft.TrySet(row.Id, chord);
        if (error != null)
        {
            Say(error, isError: true);
            return;
        }

        EndCapture();

        string? other = _draft.FindOther(row.Id, chord);
        if (other != null)
        {
            _pending = new PendingConflict(row, other, chord, previous);
            Say(string.Empty);
        }
        else
        {
            Say($"'{row.Name}' → {row.ShortcutText}. 적용을 눌러야 실제로 바뀝니다.");
        }

        RefreshAll();
        RaisePending();
    }

    public void ReportUnsupportedKey() => Say("지원하지 않는 키입니다. 다른 키를 눌러 보세요.", isError: true);

    // ------------------------------------------------------------------ 충돌 해결 (10, 12, 34)

    public bool HasPendingConflict => _pending != null;

    public string PendingConflictText
        => _pending == null
            ? string.Empty
            : $"⚠ {_pending.Chord.ToDisplay()} 는 이미 '{CommandCatalog.Find(_pending.OtherId)?.Name}' 기능에 사용 중입니다.";

    public string UnassignOtherLabel
        => _pending == null ? string.Empty : $"'{CommandCatalog.Find(_pending.OtherId)?.Name}' 의 할당 해제";

    /// <summary>[기존 할당 해제]: 원래 쓰던 명령을 미할당으로 두고 새 키는 그대로 쓴다.</summary>
    public void ResolveByUnassigningOther()
    {
        if (_pending == null) return;
        var p = _pending;
        _pending = null;
        _draft.Unassign(p.OtherId);
        Say($"'{CommandCatalog.Find(p.OtherId)?.Name}' 은(는) 미할당이 되었고 '{p.Row.Name}' 에 {p.Chord.ToDisplay()} 를 지정했습니다.");
        RefreshAll();
        RaisePending();
    }

    /// <summary>[새 단축키 다시 입력]: 겹친 키를 되돌리고 같은 줄에서 다시 입력을 받는다.</summary>
    public void ResolveByRetry()
    {
        if (_pending == null) return;
        var p = _pending;
        _pending = null;
        _draft.TrySet(p.Row.Id, p.Previous);
        RefreshAll();
        RaisePending();
        BeginCapture(p.Row);
    }

    /// <summary>[취소]: 겹친 키 지정을 없던 일로 한다.</summary>
    public void ResolveByCancel()
    {
        if (_pending == null) return;
        var p = _pending;
        _pending = null;
        _draft.TrySet(p.Row.Id, p.Previous);
        Say(string.Empty);
        RefreshAll();
        RaisePending();
    }

    private void RaisePending()
    {
        Raise(nameof(HasPendingConflict));
        Raise(nameof(PendingConflictText));
        Raise(nameof(UnassignOtherLabel));
    }

    // ------------------------------------------------------------------ 해제 / 기본값 복원 (9, 16, 17)

    /// <summary>목록 상태에서 Backspace / Delete: 미할당으로 만든다(9).</summary>
    public void Unassign(ShortcutRowViewModel row)
    {
        ClearTransientState();
        _draft.Unassign(row.Id);
        Say($"'{row.Name}' 의 단축키를 해제했습니다. 적용을 눌러야 실제로 바뀝니다.");
        RefreshAll();
    }

    public void ResetRow(ShortcutRowViewModel row)
    {
        ClearTransientState();
        _draft.Reset(row.Id);
        Say(HasConflicts
            ? $"'{row.Name}' 을(를) 기본값 {row.DefaultText} 로 되돌렸습니다. 다른 기능과 겹쳐 있어 정리가 필요합니다."
            : $"'{row.Name}' 을(를) 기본값 {row.DefaultText} 로 되돌렸습니다.");
        RefreshAll();
    }

    public void ResetAll()
    {
        ClearTransientState();
        _draft.ResetAll();
        Say("모든 단축키를 기본값으로 되돌렸습니다. 적용을 눌러야 실제로 바뀝니다.");
        RefreshAll();
    }

    private void ClearTransientState()
    {
        EndCapture();
        _pending = null;
        RaisePending();
    }

    // ------------------------------------------------------------------ 적용 / 취소 (19, 20)

    public void Apply()
    {
        ClearTransientState();
        if (_draft.HasConflicts)
        {
            Say("충돌하는 단축키가 있어 적용할 수 없습니다.", isError: true);
            return;
        }

        switch (_draft.Commit())
        {
            case KeymapApplyResult.Ok:
                Say("단축키를 적용했습니다.");
                break;
            case KeymapApplyResult.SaveFailed:
                Say("단축키를 적용했지만 설정 파일에 저장하지 못했습니다. 이번 실행에서만 유지됩니다.", isError: true);
                break;
            default:
                Say("적용할 수 없는 단축키가 있습니다.", isError: true);
                break;
        }
        RefreshAll();
    }

    /// <summary>이번 화면에서 바꾼 내용을 모두 버린다.</summary>
    public void Cancel()
    {
        ClearTransientState();
        _draft.Discard();
        Say(string.Empty);
        RefreshAll();
    }

    // ------------------------------------------------------------------ 갱신

    private void RefreshAll()
    {
        _conflicts = _draft.ConflictIds;
        foreach (var row in Rows) row.Refresh();

        // 필터가 상태(변경됨/충돌)에 의존하므로 다시 걸러야 한다.
        View.Refresh();

        // 충돌이 모두 해결되면 "충돌만 보기" 는 의미가 없다.
        if (_showConflictsOnly && !HasConflicts) ShowConflictsOnly = false;

        Raise(nameof(ConflictCount));
        Raise(nameof(HasConflicts));
        Raise(nameof(ConflictText));
        Raise(nameof(ModifiedCount));
        Raise(nameof(ModifiedText));
        Raise(nameof(IsDirty));
        Raise(nameof(CanApply));
    }
}

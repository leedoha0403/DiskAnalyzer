using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.QuickMove;

namespace DiskAnalyzer.App.ViewModels;

/// <summary>즐겨찾기 메뉴의 한 줄. <see cref="InScope"/> 가 false 면 지금 스캔 결과 밖의 폴더다(고르면 스캔 여부를 묻는다).</summary>
public sealed record FavoriteItem(string Name, string Path, bool InScope);

/// <summary>
/// 폴더 / Treemap 탭이 함께 쓰는 이동 막대의 "주소 직접 입력 + 즐겨찾기".
///
/// 이 탭들은 실제 디스크가 아니라 <b>스캔 결과</b>를 탐색한다. 그래서 입력한 경로는 세 가지로 갈린다:
///  · 스캔 결과 안에 있다      → 그 폴더로 이동(파일이면 그 폴더로 가서 파일 선택)
///  · 스캔 이후에 생긴 것      → 가까운 상위 폴더를 새로고침해서 연다
///  · 스캔 범위 밖 / 없음      → 이유를 알리고, 디스크에 있으면 "이 경로 스캔" 을 제안한다
/// 즐겨찾기는 빠른 이동 탭과 같은 목록(<see cref="QuickMove.QuickMoveSettings.Favorites"/>)을 쓴다 — 한 곳에서 추가하면 어디서나 보인다.
/// </summary>
public sealed class PathBarViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    private bool _isEditing;
    private string _text = string.Empty;
    private bool _quiet;
    private IReadOnlyList<string> _suggestions = Array.Empty<string>();
    private int _suggestionIndex = -1;
    private string _notice = string.Empty;
    private bool _noticeIsWarning;
    private string? _offerPath;

    public PathBarViewModel(MainViewModel owner)
    {
        _owner = owner;
        BeginEditCommand = new RelayCommand(BeginEdit, () => _owner.Store != null);
        ToggleFavoriteCommand = new RelayCommand(ToggleFavorite, () => _owner.CurrentFolderPath != null);
        DismissNoticeCommand = new RelayCommand(ClearNotice);
        ScanOfferedCommand = new RelayCommand(ScanOffered, () => _offerPath != null && !_owner.IsScanning);
        OpenOfferedInQuickMoveCommand = new RelayCommand(OpenOfferedInQuickMove, () => _offerPath != null);

        owner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.CurrentPath) or nameof(MainViewModel.IsScanning) or nameof(MainViewModel.IsSearchMode))
                RaiseFavoriteState();
        };
        owner.QuickMove.QuickLocations.CollectionChanged += (_, _) => RaiseFavoriteState();
    }

    public RelayCommand BeginEditCommand { get; }
    public RelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand DismissNoticeCommand { get; }
    public RelayCommand ScanOfferedCommand { get; }
    public RelayCommand OpenOfferedInQuickMoveCommand { get; }

    // ------------------------------------------------------------------ 편집

    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (!Set(ref _isEditing, value)) return;
            if (!value) SetSuggestions(Array.Empty<string>());
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value)) return;
            if (!_quiet && _isEditing) UpdateSuggestions();
        }
    }

    /// <summary>편집을 시작한다. 지금 폴더의 전체 경로를 채우고 전체 선택은 화면이 한다.</summary>
    public void BeginEdit()
    {
        string? path = _owner.CurrentFolderPath;
        if (path == null || _owner.Store == null) return;

        ClearNotice();
        SetTextQuietly(path);
        IsEditing = true;
    }

    public void CancelEdit() => IsEditing = false;

    /// <summary>Enter. 입력한 경로로 간다.</summary>
    public void Commit()
    {
        string text = Text;
        IsEditing = false;
        _ = GoToAsync(text);
    }

    private void SetTextQuietly(string text)
    {
        _quiet = true;
        try { Text = text; }
        finally { _quiet = false; }
    }

    // ------------------------------------------------------------------ 자동 완성(스캔 결과에서만 — 디스크를 읽지 않아 즉시 뜬다)

    public IReadOnlyList<string> Suggestions => _suggestions;
    public bool HasSuggestions => _isEditing && _suggestions.Count > 0;
    public int SuggestionIndex { get => _suggestionIndex; private set => Set(ref _suggestionIndex, value); }

    private void SetSuggestions(IReadOnlyList<string> list)
    {
        _suggestions = list;
        _suggestionIndex = -1;
        Raise(nameof(Suggestions));
        Raise(nameof(SuggestionIndex));
        Raise(nameof(HasSuggestions));
    }

    private void UpdateSuggestions()
    {
        var store = _owner.Store;
        if (store == null) { SetSuggestions(Array.Empty<string>()); return; }

        string text = Environment.ExpandEnvironmentVariables(_text);
        int cut = text.LastIndexOf('\\');
        if (cut < 0) { SetSuggestions(Array.Empty<string>()); return; }

        string parentPath = text[..(cut + 1)];
        string prefix = text[(cut + 1)..];

        var parent = store.ResolvePath(parentPath.Length > 3 ? parentPath.TrimEnd('\\') : parentPath);
        if (parent.Kind != PathKind.Directory) { SetSuggestions(Array.Empty<string>()); return; }

        string baseDir = parentPath.TrimEnd('\\');
        var list = store.ChildDirectoryNames(parent.Id)
            .Where(n => prefix.Length == 0 || n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .Select(n => baseDir + "\\" + n)
            .ToList();

        // 이미 완성된 경로 하나뿐이면 제안할 것이 없다.
        if (list.Count == 1 && PathUtil.Equal(list[0], text)) list.Clear();
        SetSuggestions(list);
    }

    public void MoveSuggestion(int delta)
    {
        if (_suggestions.Count == 0) return;

        int i = _suggestionIndex + delta;
        if (_suggestionIndex < 0) i = delta > 0 ? 0 : _suggestions.Count - 1;
        else if (i >= _suggestions.Count) i = 0;
        else if (i < 0) i = _suggestions.Count - 1;

        SuggestionIndex = i;
        SetTextQuietly(_suggestions[i]);
    }

    /// <summary>Tab: 고른(없으면 첫) 제안으로 완성하고 '\' 를 붙여 다음 단계 제안을 띄운다.</summary>
    public bool AcceptSuggestion()
    {
        if (_suggestions.Count == 0) return false;
        int i = _suggestionIndex >= 0 ? _suggestionIndex : 0;
        Text = _suggestions[i].TrimEnd('\\') + "\\";
        return true;
    }

    public void PickSuggestion(string path)
    {
        SetTextQuietly(path);
        Commit();
    }

    // ------------------------------------------------------------------ 이동

    /// <summary>경로 문자열로 이동한다(입력 / 즐겨찾기 / 최근 등 모든 경로가 여기로 온다).</summary>
    public async Task GoToAsync(string raw)
    {
        ClearNotice();

        string text = Environment.ExpandEnvironmentVariables((raw ?? string.Empty).Trim());
        if (text.Length == 0) return;
        if (!PathUtil.TryNormalize(text, out string path)) { Warn("올바른 경로가 아닙니다."); return; }

        if (_owner.IsScanning) { Warn("스캔이 끝난 뒤에 이동할 수 있습니다."); return; }

        var store = _owner.Store;
        if (store == null)
        {
            await OfferScanAsync(path, "아직 스캔한 결과가 없습니다.");
            return;
        }

        var res = store.ResolvePath(path);
        switch (res.Kind)
        {
            case PathKind.Directory:
                _owner.Navigate(res.Id);
                return;

            case PathKind.File:
                _owner.Navigate(res.NearestDirId);
                _owner.RequestSelectFile(res.Id);
                return;

            case PathKind.OutOfScope:
                await OfferScanAsync(path, $"이 경로는 현재 스캔 범위({store.RootPath}) 밖입니다.");
                return;

            default:
                await OpenNewerThanScanAsync(store, path, res.NearestDirId);
                return;
        }
    }

    /// <summary>스캔 범위 안인데 결과에 없다: 스캔 이후에 생겼을 수 있으니 디스크를 확인하고, 있으면 가까운 상위를 새로고침해서 연다.</summary>
    private async Task OpenNewerThanScanAsync(NodeStore store, string path, int nearestDirId)
    {
        bool? onDisk = await Task.Run(() => PathUtil.IsDirectory(path));

        _owner.Navigate(nearestDirId);

        if (onDisk != null)
        {
            await _owner.RefreshFolderAsync(deep: false);   // 스캔 이후 생긴 항목을 반영한다
            if (!ReferenceEquals(store, _owner.Store)) return;

            var again = store.ResolvePath(path);
            if (again.Kind == PathKind.Directory) { _owner.Navigate(again.Id); return; }
            if (again.Kind == PathKind.File) { _owner.Navigate(again.NearestDirId); _owner.RequestSelectFile(again.Id); return; }
        }

        string where = store.GetDirectoryPath(store.NearestLiveDirectory(nearestDirId));
        Warn($"경로를 찾을 수 없습니다: {path}  ·  가장 가까운 폴더({where})를 열었습니다.");
    }

    private async Task OfferScanAsync(string path, string reason)
    {
        bool? onDisk = await Task.Run(() => PathUtil.IsDirectory(path));
        if (onDisk == null) { Warn($"{reason} 그리고 디스크에서도 찾을 수 없습니다: {path}"); return; }

        // 파일 경로면 그 파일이 든 폴더를 스캔 대상으로 제안한다.
        string folder = onDisk == true ? path : PathUtil.Parent(path) ?? path;
        _offerPath = folder;
        SetNotice($"{reason}  ·  {folder}", warning: false);
        Raise(nameof(HasOffer));
        Raise(nameof(OfferText));
        ScanOfferedCommand.RaiseCanExecuteChanged();
        OpenOfferedInQuickMoveCommand.RaiseCanExecuteChanged();
    }

    public bool HasOffer => _offerPath != null;
    public string OfferText => _offerPath == null ? string.Empty : $"{_offerPath} 스캔";

    private void ScanOffered()
    {
        string? path = _offerPath;
        ClearNotice();
        if (path != null) _owner.StartScan(path);
    }

    private void OpenOfferedInQuickMove()
    {
        string? path = _offerPath;
        ClearNotice();
        if (path == null) return;

        _owner.QuickMove.IsDockVisible = true;   // 지금 탭을 벗어나지 않고 도크에서 연다
        _ = _owner.QuickMove.OpenSourceFolderAsync(path);
    }

    // ------------------------------------------------------------------ 안내

    public string Notice => _notice;
    public bool HasNotice => _notice.Length > 0;
    public bool NoticeIsWarning => _noticeIsWarning;

    private void Warn(string message) => SetNotice(message, warning: true);

    private void SetNotice(string message, bool warning)
    {
        _notice = message;
        _noticeIsWarning = warning;
        Raise(nameof(Notice));
        Raise(nameof(HasNotice));
        Raise(nameof(NoticeIsWarning));
    }

    public void ClearNotice()
    {
        bool had = _notice.Length > 0 || _offerPath != null;
        _notice = string.Empty;
        _offerPath = null;
        if (!had) return;
        Raise(nameof(Notice));
        Raise(nameof(HasNotice));
        Raise(nameof(HasOffer));
        Raise(nameof(OfferText));
        ScanOfferedCommand.RaiseCanExecuteChanged();
        OpenOfferedInQuickMoveCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ 즐겨찾기

    public bool IsCurrentFavorite
    {
        get
        {
            string? path = _owner.CurrentFolderPath;
            return path != null && _owner.QuickMove.Settings.IsFavorite(path);
        }
    }

    public bool IsFavoritePath(string path) => _owner.QuickMove.Settings.IsFavorite(path);

    /// <summary>지금 폴더를 즐겨찾기에 넣거나 뺀다.</summary>
    public void ToggleFavorite()
    {
        string? path = _owner.CurrentFolderPath;
        if (path != null) ToggleFavorite(path);
    }

    public void ToggleFavorite(string path)
    {
        _owner.QuickMove.ToggleFavorite(path);
        RaiseFavoriteState();
        _owner.StatusMessage = _owner.QuickMove.Settings.IsFavorite(path)
            ? $"즐겨찾기에 추가했습니다: {path}"
            : $"즐겨찾기에서 뺐습니다: {path}";
    }

    public IReadOnlyList<FavoriteItem> GetFavorites()
    {
        _owner.QuickMove.CheckFavorites();   // 그 사이 없어진 폴더는 여기서도 빠진다
        string? root = _owner.Store?.RootPath;
        return _owner.QuickMove.Settings.Favorites
            .Select(p => new FavoriteItem(PathUtil.NameOf(p), p, root != null && PathUtil.IsSameOrUnder(root, p)))
            .ToList();
    }

    private void RaiseFavoriteState()
    {
        Raise(nameof(IsCurrentFavorite));
        ToggleFavoriteCommand.RaiseCanExecuteChanged();
        BeginEditCommand.RaiseCanExecuteChanged();
    }
}

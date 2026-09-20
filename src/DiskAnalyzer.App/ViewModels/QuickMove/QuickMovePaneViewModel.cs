using System.Diagnostics;
using System.IO;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.QuickMove;

namespace DiskAnalyzer.App.ViewModels.QuickMove;

/// <summary>
/// 빠른 이동의 패널 하나(출발지 / 목적지). 좌우가 같은 구조라서 같은 클래스를 두 번 쓴다.
/// 스캔 결과(NodeStore)가 아니라 실제 파일 시스템을 읽는다 — 스캔하지 않은 드라이브도 탐색할 수 있다.
/// </summary>
public sealed class QuickMovePaneViewModel : ObservableObject
{
    private readonly QuickMoveViewModel _owner;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _selectionCts;
    private List<FsEntry> _all = new();

    private string _currentPath = string.Empty;
    private IReadOnlyList<CrumbItem> _breadcrumb = Array.Empty<CrumbItem>();
    private IReadOnlyList<FsEntry> _entries = Array.Empty<FsEntry>();
    private bool _isLoading;
    private string _notice = string.Empty;
    private BrowseStatus _status = BrowseStatus.Ok;
    private string _statusMessage = string.Empty;
    private bool _isEditingPath;
    private string _pathText = string.Empty;
    private string _selectionText = string.Empty;
    private IReadOnlyList<FsEntry> _selected = Array.Empty<FsEntry>();
    private string _driveName = string.Empty;
    private string _volumeTag = string.Empty;
    private string _freeText = string.Empty;
    private string _projectionText = string.Empty;
    private bool _projectionWarn;

    private bool _isDropTarget;
    private string _dropTitle = string.Empty;
    private string _dropDetail = string.Empty;
    private string _dropCount = string.Empty;

    public QuickMovePaneViewModel(QuickMoveViewModel owner, string title, string sortKey, bool ascending)
    {
        _owner = owner;
        Title = title;
        SortKey = sortKey;
        SortAscending = ascending;

        BackCommand = new RelayCommand(GoBack, () => _back.Count > 0);
        ForwardCommand = new RelayCommand(GoForward, () => _forward.Count > 0);
        UpCommand = new RelayCommand(GoUp, () => CurrentPath.Length > 0 && PathUtil.Parent(CurrentPath) != null);
        RefreshCommand = new RelayCommand(Refresh);
        ToggleFavoriteCommand = new RelayCommand(() => _owner.ToggleFavorite(CurrentPath), () => CurrentPath.Length > 0);
        BeginEditPathCommand = new RelayCommand(BeginEditPath);
    }

    public string Title { get; }

    /// <summary>"빠른 위치" 버튼들. 좌우 패널이 같은 목록을 쓴다.</summary>
    public System.Collections.ObjectModel.ObservableCollection<QuickLocation> Locations => _owner.QuickLocations;

    /// <summary>반대편 패널.</summary>
    public QuickMovePaneViewModel Other => ReferenceEquals(this, _owner.Left) ? _owner.Right : _owner.Left;

    /// <summary>이 패널의 선택 항목을 반대편 패널의 현재 폴더로 옮길 대기열에 넣는다.</summary>
    public void QueueSelectedToOther() => _owner.AddToQueue(SelectedEntries, Other.CurrentPath);

    /// <summary>"이 폴더를 목적지로 사용": 오른쪽(목적지) 패널을 그 폴더로 연다.</summary>
    public void UseAsDestination(string folder) => _owner.Right.NavigateTo(folder);

    public void ToggleFavorite(string path) => _owner.ToggleFavorite(path);

    public bool IsFavoritePath(string path) => _owner.Settings.IsFavorite(path);

    /// <summary>끌어다 놓은 항목을 <paramref name="destDirectory"/> 로 옮길 대기열에 넣는다(즉시 이동하지 않는다).</summary>
    public void DropOn(string destDirectory, IReadOnlyList<FsEntry> items) => _owner.AddToQueue(items, destDirectory);

    public RelayCommand BackCommand { get; }
    public RelayCommand ForwardCommand { get; }
    public RelayCommand UpCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand BeginEditPathCommand { get; }

    /// <summary>선택해야 할 항목을 알린다(새 폴더를 열고 난 뒤 / 다른 탭에서 보내기). 화면이 목록에 반영한다.</summary>
    public event EventHandler<IReadOnlyCollection<string>>? SelectRequested;

    public event EventHandler? SortChanged;

    // ------------------------------------------------------------------ 위치

    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (!Set(ref _currentPath, value)) return;
            Raise(nameof(IsFavorite));
            Raise(nameof(HasPath));
            UpCommand.RaiseCanExecuteChanged();
            ToggleFavoriteCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasPath => _currentPath.Length > 0;
    public IReadOnlyList<CrumbItem> Breadcrumb { get => _breadcrumb; private set => Set(ref _breadcrumb, value); }
    public bool IsFavorite => CurrentPath.Length > 0 && _owner.Settings.IsFavorite(CurrentPath);

    public bool IsEditingPath
    {
        get => _isEditingPath;
        private set
        {
            if (!Set(ref _isEditingPath, value)) return;
            if (!value)
            {
                _suggestVersion++;   // 진행 중이던 제안 계산 결과는 버린다
                Suggestions = Array.Empty<string>();
                SuggestionIndex = -1;
            }
            Raise(nameof(HasSuggestions));
        }
    }

    public string PathText
    {
        get => _pathText;
        set
        {
            if (!Set(ref _pathText, value)) return;
            if (!_quiet && _isEditingPath) RequestSuggestions();
        }
    }

    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    /// <summary>경로 아래에 잠깐 보여 주는 안내(찾을 수 없는 경로 등). 다음 이동 때 사라진다.</summary>
    public string Notice
    {
        get => _notice;
        set { if (Set(ref _notice, value)) Raise(nameof(HasNotice)); }
    }

    public bool HasNotice => _notice.Length > 0;

    // ------------------------------------------------------------------ 목록

    public IReadOnlyList<FsEntry> Entries { get => _entries; private set => Set(ref _entries, value); }

    public bool ShowEmpty => _status == BrowseStatus.Ok && !_isLoading && _entries.Count == 0;
    public bool ShowAccessDenied => _status == BrowseStatus.AccessDenied;
    public bool ShowError => _status == BrowseStatus.Error;
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    public string SortKey { get; private set; }
    public bool SortAscending { get; private set; }

    public IReadOnlyList<FsEntry> SelectedEntries => _selected;

    public string SelectionText { get => _selectionText; private set => Set(ref _selectionText, value); }

    // ------------------------------------------------------------------ 드라이브 / 이동 예정 표시

    public string DriveName { get => _driveName; private set => Set(ref _driveName, value); }
    public string VolumeTag { get => _volumeTag; private set { if (Set(ref _volumeTag, value)) Raise(nameof(HasVolumeTag)); } }
    public bool HasVolumeTag => _volumeTag.Length > 0;
    public string FreeText { get => _freeText; private set => Set(ref _freeText, value); }

    /// <summary>"이동 예정 42 GB → 이동 후 376 GB 사용 가능". 대기열이 이 드라이브로 향할 때만 채워진다.</summary>
    public string ProjectionText
    {
        get => _projectionText;
        set { if (Set(ref _projectionText, value)) Raise(nameof(HasProjection)); }
    }

    public bool ProjectionWarn { get => _projectionWarn; set => Set(ref _projectionWarn, value); }
    public bool HasProjection => _projectionText.Length > 0;

    // ------------------------------------------------------------------ 끌어놓기 표시

    public bool IsDropTarget { get => _isDropTarget; set => Set(ref _isDropTarget, value); }
    public string DropTitle { get => _dropTitle; set => Set(ref _dropTitle, value); }
    public string DropDetail { get => _dropDetail; set => Set(ref _dropDetail, value); }
    public string DropCount { get => _dropCount; set => Set(ref _dropCount, value); }

    // ------------------------------------------------------------------ 이동

    public void NavigateTo(string path) => _ = NavigateAsync(path);

    /// <summary>
    /// 폴더를 연다. 존재하지 않는 경로는 현재 위치를 유지하고 안내만 띄운다.
    /// 권한이 없는 폴더는 이동은 하되 "접근 권한이 없습니다" 화면을 보여 준다(다른 위치를 고를 수 있게).
    /// </summary>
    public async Task<bool> NavigateAsync(string path, bool record = true, IReadOnlyCollection<string>? select = null)
    {
        if (!PathUtil.TryNormalize(path, out string target))
        {
            Notice = "올바른 경로가 아닙니다.";
            return false;
        }

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _loadCts, cts)?.Cancel();

        IsLoading = true;
        Notice = string.Empty;
        bool showHidden = _owner.Settings.ShowHidden;

        BrowseResult result;
        try
        {
            result = await Task.Run(() => DirectoryBrowser.List(target, showHidden, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return false;   // 더 새로운 이동이 이 요청을 대체했다
        }

        if (cts.IsCancellationRequested) return false;
        IsLoading = false;

        if (result.Status == BrowseStatus.NotFound)
        {
            Notice = $"{result.Message} ({target})";
            RaiseState();
            return false;
        }

        if (record && CurrentPath.Length > 0 && !PathUtil.Equal(CurrentPath, target))
        {
            _back.Push(CurrentPath);
            _forward.Clear();
        }

        Commit(target, result);
        if (select is { Count: > 0 }) SelectRequested?.Invoke(this, select);
        return true;
    }

    /// <summary>새로고침. 현재 선택은 유지하고, 사라진 항목은 자연히 빠진다.</summary>
    public void Refresh()
    {
        if (CurrentPath.Length == 0) return;
        var keep = _selected.Select(e => e.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _owner.ClearMeasureCache();
        _ = NavigateAsync(CurrentPath, record: false, select: keep);
    }

    private void Commit(string path, BrowseResult result)
    {
        CurrentPath = path;
        var segments = PathUtil.Segments(path);
        Breadcrumb = segments
            .Select((seg, i) => new CrumbItem { Name = seg.Name, Path = seg.Path, IsLast = i == segments.Count - 1 })
            .ToList();

        _status = result.Status;
        StatusMessage = result.Message;
        _all = result.Entries.ToList();
        ApplySort();
        SetSelection(Array.Empty<FsEntry>());
        RaiseState();

        BackCommand.RaiseCanExecuteChanged();
        ForwardCommand.RaiseCanExecuteChanged();
        _owner.OnPaneNavigated(this);
        _ = RefreshVolumeInfoAsync();
    }

    private void RaiseState()
    {
        Raise(nameof(ShowEmpty));
        Raise(nameof(ShowAccessDenied));
        Raise(nameof(ShowError));
    }

    public void GoBack()
    {
        if (_back.Count == 0) return;
        string previous = _back.Pop();
        _forward.Push(CurrentPath);
        _ = GoHistoryAsync(previous);
    }

    public void GoForward()
    {
        if (_forward.Count == 0) return;
        string next = _forward.Pop();
        _back.Push(CurrentPath);
        _ = GoHistoryAsync(next);
    }

    private async Task GoHistoryAsync(string path)
    {
        if (!await NavigateAsync(path, record: false))
        {
            // 열리지 않는 위치(사라진 폴더 등)가 기록에 남아 뒤로/앞으로가 막히지 않게 한다.
            BackCommand.RaiseCanExecuteChanged();
            ForwardCommand.RaiseCanExecuteChanged();
            return;
        }
        BackCommand.RaiseCanExecuteChanged();
        ForwardCommand.RaiseCanExecuteChanged();
    }

    public void GoUp()
    {
        string? parent = PathUtil.Parent(CurrentPath);
        if (parent == null) return;
        // 위로 올라가면 방금 있던 폴더를 선택해서 위치를 잃지 않게 한다.
        _ = NavigateAsync(parent, record: true, select: new[] { CurrentPath });
    }

    /// <summary>항목을 더블 클릭 / Enter: 폴더는 열고, 파일은 기본 프로그램으로 연다.</summary>
    public void Activate(FsEntry entry)
    {
        if (entry.IsDirectory)
        {
            NavigateTo(entry.FullPath);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notice = "파일을 열 수 없습니다: " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ 경로 직접 입력

    public void BeginEditPath()
    {
        SetPathTextQuietly(CurrentPath);   // 처음 열 때는 제안 목록을 띄우지 않는다. 글자를 고치기 시작하면 뜬다.
        IsEditingPath = true;
    }

    /// <summary>
    /// 입력한 경로로 간다. 폴더가 아닌 파일 경로를 붙여 넣었다면 그 파일이 있는 폴더를 열고 파일을 선택한다
    /// (탐색기의 "경로로 복사"로 얻은 경로를 그대로 붙여 넣는 경우가 많다).
    /// </summary>
    public void CommitPath()
    {
        IsEditingPath = false;
        string text = PathText.Trim();
        if (text.Length == 0) return;

        if (!PathUtil.TryNormalize(Environment.ExpandEnvironmentVariables(text), out string target))
        {
            Notice = "올바른 경로가 아닙니다.";
            return;
        }

        if (File.Exists(target))
        {
            string? parent = PathUtil.Parent(target);
            if (parent != null) _ = NavigateAsync(parent, record: true, select: new[] { target });
            return;
        }

        if (PathUtil.Equal(target, CurrentPath)) return;
        NavigateTo(target);
    }

    public void CancelEditPath() => IsEditingPath = false;

    // ---- 자동완성: 입력 중인 경로의 아래 폴더를 제안한다 ----

    private IReadOnlyList<string> _suggestions = Array.Empty<string>();
    private int _suggestionIndex = -1;
    private int _suggestVersion;
    private bool _quiet;

    public IReadOnlyList<string> Suggestions
    {
        get => _suggestions;
        private set
        {
            _suggestions = value;
            Raise();
            Raise(nameof(HasSuggestions));
        }
    }

    public bool HasSuggestions => _isEditingPath && _suggestions.Count > 0;

    /// <summary>제안 목록에서 화살표로 고른 위치. 고르지 않았으면 -1.</summary>
    public int SuggestionIndex { get => _suggestionIndex; private set => Set(ref _suggestionIndex, value); }

    private void SetPathTextQuietly(string text)
    {
        _quiet = true;
        try { PathText = text; }
        finally { _quiet = false; }
    }

    /// <summary>글자가 바뀔 때마다 부른다(PathText 세터). 디스크를 읽으므로 백그라운드에서 돌리고 오래된 결과는 버린다.</summary>
    private async void RequestSuggestions()
    {
        int version = ++_suggestVersion;
        string text = PathText;
        bool hidden = _owner.Settings.ShowHidden;

        IReadOnlyList<string> list;
        try { list = await Task.Run(() => PathSuggester.Suggest(text, 12, hidden)); }
        catch (Exception) { return; }

        if (version != _suggestVersion || !_isEditingPath) return;
        SuggestionIndex = -1;
        Suggestions = list;
    }

    /// <summary>↓ / ↑ 로 제안을 고른다. 고른 경로가 입력 칸에 바로 반영된다(Windows 실행 창과 같은 방식).</summary>
    public void MoveSuggestion(int delta)
    {
        if (_suggestions.Count == 0) return;

        int i = _suggestionIndex + delta;
        if (_suggestionIndex < 0) i = delta > 0 ? 0 : _suggestions.Count - 1;
        else if (i >= _suggestions.Count) i = 0;
        else if (i < 0) i = _suggestions.Count - 1;

        SuggestionIndex = i;
        SetPathTextQuietly(_suggestions[i]);
    }

    /// <summary>
    /// Tab: 고른(없으면 첫 번째) 제안으로 완성하고 '\' 를 붙여 바로 다음 단계 제안을 띄운다.
    /// Tab 을 계속 누르면 한 단계씩 깊이 들어간다.
    /// </summary>
    public bool AcceptSuggestion()
    {
        if (_suggestions.Count == 0) return false;
        int i = _suggestionIndex >= 0 ? _suggestionIndex : 0;
        PathText = _suggestions[i].TrimEnd('\\') + "\\";
        return true;
    }

    /// <summary>제안 목록에서 클릭한 폴더로 바로 이동한다.</summary>
    public void PickSuggestion(string path)
    {
        SetPathTextQuietly(path);
        CommitPath();
    }

    // ------------------------------------------------------------------ 정렬

    public void ToggleSort(string key)
    {
        if (key == SortKey) SortAscending = !SortAscending;
        else
        {
            SortKey = key;
            // 크기 / 수정일은 큰 것·최근 것부터 보는 일이 많아서 처음에는 내림차순.
            SortAscending = key is "name" or "type";
        }
        ApplySort();
        SortChanged?.Invoke(this, EventArgs.Empty);
        _owner.SaveSettings();
    }

    public void SetSort(string key, bool ascending)
    {
        SortKey = key;
        SortAscending = ascending;
    }

    private void ApplySort()
    {
        string key = SortKey;
        bool asc = SortAscending;

        _all.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;   // 폴더는 항상 위

            int c = key switch
            {
                "size" => a.IsDirectory ? 0 : a.Size.CompareTo(b.Size),
                "modified" => a.Modified.CompareTo(b.Modified),
                "type" => string.Compare(a.TypeText, b.TypeText, StringComparison.OrdinalIgnoreCase),
                _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            };
            if (!asc) c = -c;
            if (c == 0 && key != "name") c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c;
        });

        Entries = _all.ToArray();
        RaiseState();
    }

    // ------------------------------------------------------------------ 선택

    public void SetSelection(IEnumerable<FsEntry> items)
    {
        _selected = items.ToList();
        _ = UpdateSelectionSummaryAsync();
        _owner.OnSelectionChanged();
    }

    /// <summary>
    /// "3개 선택 · 5.64 GB" / "파일 3개 · 폴더 2개 · 총 18.2 GB".
    /// 폴더 크기는 목록을 열 때 계산하지 않는다(느려진다). 선택한 폴더만 백그라운드에서 계산해 이 문구를 갱신한다.
    /// </summary>
    private async Task UpdateSelectionSummaryAsync()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _selectionCts, cts)?.Cancel();

        var selected = _selected;
        if (selected.Count == 0)
        {
            SelectionText = _entries.Count == 0 ? string.Empty : $"{_entries.Count:N0}개 항목";
            return;
        }

        var files = selected.Where(e => !e.IsDirectory).ToList();
        var folders = selected.Where(e => e.IsDirectory).ToList();
        long bytes = files.Sum(f => Math.Max(0, f.Size));

        var pending = new List<FsEntry>();
        foreach (var d in folders)
        {
            if (_owner.TryGetCachedMeasure(d.FullPath, out var m)) bytes += m.Bytes;
            else pending.Add(d);
        }

        SelectionText = Format(files.Count, folders.Count, bytes, pending.Count > 0);

        foreach (var d in pending)
        {
            TreeMeasure.Result r;
            try
            {
                r = await Task.Run(() => TreeMeasure.Measure(d.FullPath, cts.Token), cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { r = default; }

            if (cts.IsCancellationRequested) return;
            _owner.CacheMeasure(d.FullPath, r);
            bytes += r.Bytes;
            pending.Remove(d);
            SelectionText = Format(files.Count, folders.Count, bytes, pending.Count > 0);
        }
    }

    private static string Format(int files, int folders, long bytes, bool computing)
    {
        string size = computing ? "계산 중…" : SizeFormatter.Format(bytes);
        if (folders == 0) return $"{files:N0}개 선택 · {size}";
        if (files == 0) return $"폴더 {folders:N0}개 · 총 {size}";
        return $"파일 {files:N0}개 · 폴더 {folders:N0}개 · 총 {size}";
    }

    // ------------------------------------------------------------------ 드라이브 정보

    public async Task RefreshVolumeInfoAsync()
    {
        string path = CurrentPath;
        if (path.Length == 0) return;

        var (name, tag, free) = await Task.Run(() => (PathUtil.DriveName(path), PathUtil.VolumeTag(path), PathUtil.GetFreeSpace(path)));
        if (!PathUtil.Equal(path, CurrentPath)) return;

        DriveName = name;
        VolumeTag = tag;
        FreeText = free >= 0 ? $"{SizeFormatter.Format(free)} 사용 가능" : string.Empty;
        _owner.OnPaneVolumeChanged();
    }

    public string RootDriveName => PathUtil.DriveName(CurrentPath);

    /// <summary>즐겨찾기가 바뀌었을 때 별 표시를 갱신한다.</summary>
    public void RaiseFavoriteChanged() => Raise(nameof(IsFavorite));

    public IReadOnlyList<string> GetRecentPaths()
        => _owner.Settings.Recents.Where(r => !PathUtil.Equal(r, CurrentPath)).Take(QuickMoveSettings.MaxRecents).ToList();
}

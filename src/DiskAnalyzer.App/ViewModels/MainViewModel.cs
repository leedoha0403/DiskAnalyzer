using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Scanning;
using DiskAnalyzer.Core.Services;
using DiskAnalyzer.Core.Analysis;
using DiskAnalyzer.App.ViewModels.QuickMove;

namespace DiskAnalyzer.App.ViewModels;

/// <summary>
/// 19. UI ViewModel 계층.
/// 파일 시스템 접근 코드는 단 한 줄도 들어 있지 않다. ScanController 가 게시한 스냅샷을 화면 상태로 옮길 뿐이다.
///
/// 29. UI 업데이트는 DispatcherTimer 150ms 주기의 "pull" 방식이다.
/// 스캔 엔진이 UI 로 이벤트를 밀어 넣으면 파일이 많을수록 Dispatcher 큐가 넘치고 UI 가 멈춘다.
/// 엔진은 최신 스냅샷만 게시하고 UI 가 자기 속도로 읽어가면 UI 는 절대 밀리지 않는다.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly ScanController _controller = new();
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, ScanResult> _results = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<int> _back = new();
    private readonly Stack<int> _forward = new();

    private CancellationTokenSource? _scanCts;
    private Task? _scanTask;
    private int _lastViewVersion = -1;
    private int _currentDirId;
    private ScanResult? _current;

    public MainViewModel()
    {
        Options = new ScanOptions();
        Drives = new ObservableCollection<DriveInfoRow>(DriveService.GetLocalDrives());
        SelectedDrive = Drives.FirstOrDefault();
        IsElevated = DriveService.IsElevated();
        QuickMove = new QuickMoveViewModel(() => CurrentPath);
        QuickMove.SourcesRelocated += OnQuickMoveRelocated;

        ScanSelectedCommand = new RelayCommand(() => StartScan(SelectedDrive?.RootPath), () => !IsScanning);
        ScanAllCommand = new RelayCommand(StartScanAll, () => !IsScanning);
        RefreshCommand = new RelayCommand(() => StartScan(_current?.RootPath ?? SelectedDrive?.RootPath), () => !IsScanning);
        CancelCommand = new RelayCommand(() => _controller.Cancel(), () => IsScanning);
        BackCommand = new RelayCommand(GoBack, () => _back.Count > 0);
        ForwardCommand = new RelayCommand(GoForward, () => _forward.Count > 0);
        UpCommand = new RelayCommand(GoUp, () => _currentDirId > 0);
        RootCommand = new RelayCommand(() => Navigate(NodeStore.RootId), () => _current != null);
        SearchCommand = new RelayCommand(RunSearch);
        ClearSearchCommand = new RelayCommand(ClearSearch);
        ApplyFilterCommand = new RelayCommand(RefreshCurrentView);
        ClearCacheCommand = new RelayCommand(() => { CacheService.Clear(); StatusMessage = "캐시를 삭제했습니다."; });
        RefreshLargeFilesCommand = new RelayCommand(
            () => _ = RefreshLargeFilesAsync(), () => !IsRefreshing && !IsScanning && _current != null);

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        TryLoadCache();
    }

    // ---------------------------------------------------------------- 상태

    public ScanOptions Options { get; }
    public ObservableCollection<DriveInfoRow> Drives { get; }
    public bool IsElevated { get; }

    /// <summary>28. 관리자 권한이 아니면 "관리자 권한으로 다시 실행" 버튼을 띄운다.</summary>
    public bool IsNotElevated => !IsElevated;

    private DriveInfoRow? _selectedDrive;
    public DriveInfoRow? SelectedDrive
    {
        get => _selectedDrive;
        set { if (Set(ref _selectedDrive, value)) ShowResultFor(value?.RootPath); }
    }

    // 목록은 만들어질 때마다 "사용자가 고른 정렬"을 적용해서 내보낸다(RowSorter 주석 참고).
    private readonly Dictionary<SortTarget, SortSpec> _sorts = new();
    private SortSpec? SortOf(SortTarget target) => _sorts.TryGetValue(target, out var s) ? s : null;

    private IReadOnlyList<EntryRow> _rows = Array.Empty<EntryRow>();
    public IReadOnlyList<EntryRow> Rows
    {
        get => _rows;
        private set => Set(ref _rows, RowSorter.Sort(value, SortOf(SortTarget.Folder)));
    }

    private IReadOnlyList<EntryRow> _topFiles = Array.Empty<EntryRow>();
    public IReadOnlyList<EntryRow> TopFiles
    {
        get => _topFiles;
        private set => Set(ref _topFiles, RowSorter.Sort(value, SortOf(SortTarget.LargeFiles)));
    }

    private IReadOnlyList<ExtensionRow> _extensions = Array.Empty<ExtensionRow>();
    public IReadOnlyList<ExtensionRow> Extensions
    {
        get => _extensions;
        private set => Set(ref _extensions, RowSorter.Sort(value, SortOf(SortTarget.Extensions)));
    }

    private IReadOnlyList<EntryRow> _extensionFiles = Array.Empty<EntryRow>();
    public IReadOnlyList<EntryRow> ExtensionFiles
    {
        get => _extensionFiles;
        private set => Set(ref _extensionFiles, RowSorter.Sort(value, SortOf(SortTarget.ExtensionFiles)));
    }

    /// <summary>컬럼 헤더 클릭. 같은 키면 방향을 뒤집고, 새 정렬을 현재 목록에 바로 적용한다.</summary>
    public void ToggleSort(SortTarget target, string key)
    {
        _sorts[target] = RowSorter.Toggle(SortOf(target), key);
        switch (target)
        {
            case SortTarget.Folder: Rows = _rows; break;
            case SortTarget.LargeFiles: TopFiles = _topFiles; break;
            case SortTarget.Extensions: Extensions = _extensions; break;
            case SortTarget.ExtensionFiles: ExtensionFiles = _extensionFiles; break;
        }
    }

    public ObservableCollection<BreadcrumbItem> Breadcrumb { get; } = new();

    private string _currentPath = string.Empty;
    public string CurrentPath { get => _currentPath; private set => Set(ref _currentPath, value); }

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!Set(ref _isScanning, value)) return;
            Raise(nameof(IsIdle));
            ScanSelectedCommand.RaiseCanExecuteChanged();
            ScanAllCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            RefreshLargeFilesCommand.RaiseCanExecuteChanged();
        }
    }
    public bool IsIdle => !_isScanning;

    private ScanProgress _progress = ScanProgress.Empty;
    public ScanProgress Progress { get => _progress; private set { Set(ref _progress, value); RaiseProgressText(); } }

    private string _statusMessage = "드라이브를 선택하고 스캔을 시작하세요.";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    private string _searchText = string.Empty;
    public string SearchText { get => _searchText; set => Set(ref _searchText, value); }

    private bool _isSearchMode;
    public bool IsSearchMode { get => _isSearchMode; private set => Set(ref _isSearchMode, value); }

    /// <summary>검색을 실행한 시점의 검색어. 필터/삭제로 결과를 다시 만들 때는 입력창이 아니라 이 값을 쓴다.</summary>
    private string _activeSearchTerm = string.Empty;

    public IReadOnlyList<string> SearchScopeOptions { get; } = new[] { "전체", "현재 폴더 하위" };

    private string _selectedSearchScope = "전체";
    public string SelectedSearchScope
    {
        get => _selectedSearchScope;
        set
        {
            if (Set(ref _selectedSearchScope, value) && IsSearchMode) ExecuteSearch();
        }
    }

    /// <summary>검색이 실제로 시작될 때. 화면은 폴더 목록이 보이도록 탭을 맞춘다.</summary>
    public event EventHandler? SearchStarted;

    private ExtensionRow? _selectedExtension;
    public ExtensionRow? SelectedExtension
    {
        get => _selectedExtension;
        set { if (Set(ref _selectedExtension, value)) LoadExtensionFiles(value); }
    }

    private EntryRow? _selectedRow;
    public EntryRow? SelectedRow { get => _selectedRow; set => Set(ref _selectedRow, value); }

    private bool _isSplitView;
    /// <summary>폴더 목록과 Treemap 을 나란히 보여 줄지(폴더/Treemap 탭에서만 의미가 있다).</summary>
    public bool IsSplitView { get => _isSplitView; set => Set(ref _isSplitView, value); }

    private bool _showPerfMonitor;
    public bool ShowPerfMonitor { get => _showPerfMonitor; set => Set(ref _showPerfMonitor, value); }

    private bool _showSettings;
    public bool ShowSettings { get => _showSettings; set => Set(ref _showSettings, value); }

    // ---- 13. 필터 ----
    public IReadOnlyList<string> MinSizeOptions { get; } =
        new[] { "전체", "100 MB 이상", "500 MB 이상", "1 GB 이상", "5 GB 이상", "10 GB 이상" };

    private string _selectedMinSize = "전체";
    public string SelectedMinSize
    {
        get => _selectedMinSize;
        set { if (Set(ref _selectedMinSize, value)) RefreshCurrentView(); }
    }

    private string _extensionFilter = string.Empty;
    public string ExtensionFilter { get => _extensionFilter; set => Set(ref _extensionFilter, value); }

    // ---- 9. 큰 파일 TOP N ----
    public IReadOnlyList<int> TopNOptions { get; } = new[] { 50, 100, 500, 1000 };

    private int _selectedTopN = 100;
    public int SelectedTopN
    {
        get => _selectedTopN;
        set
        {
            if (!Set(ref _selectedTopN, value)) return;
            _controller.SetTopFileCount(value);
            RefreshTopFiles();
        }
    }

    private LiveTab _activeTab = LiveTab.Folder;
    public LiveTab ActiveTab
    {
        get => _activeTab;
        set
        {
            if (!Set(ref _activeTab, value)) return;
            _controller.SetLiveTab(value);
            RefreshTabData();
        }
    }

    public ScanResult? CurrentResult => _current;
    public int CurrentDirectoryId => _currentDirId;

    // ---------------------------------------------------------------- 1/15. 큰 파일 새로고침

    private bool _isRefreshing;
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (!Set(ref _isRefreshing, value)) return;
            Raise(nameof(RefreshLargeFilesText));
            RefreshLargeFilesCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>15. 실행 중에는 버튼을 연속으로 누를 수 없고 현재 상태를 표시한다.</summary>
    public string RefreshLargeFilesText => IsRefreshing ? "↻ 확인 중..." : "↻ 새로고침";

    public RelayCommand RefreshLargeFilesCommand { get; private set; } = null!;

    /// <summary>
    /// 1. 큰 파일 탭 새로고침.
    /// ① 현재 목록을 실제 파일 시스템과 즉시 대조해 사라졌거나 크기가 바뀐 항목을 반영하고,
    /// ② 이어서 분석 범위를 다시 스캔해 "새로 생긴 대용량 파일"까지 잡는다.
    /// 둘 다 백그라운드라 UI 는 멈추지 않는다.
    /// </summary>
    public async Task RefreshLargeFilesAsync()
    {
        if (IsRefreshing || IsScanning) return;
        var store = _current?.Store;
        string? root = _current?.RootPath;
        if (store == null || root == null) return;

        IsRefreshing = true;
        try
        {
            var mutation = await VerifyRowsAsync(store, TopFiles).ConfigureAwait(true);

            RefreshTopFiles();
            RefreshCurrentView();
            RaiseStatusText();
            Cleanup.SetResult(_current);

            StatusMessage = mutation.Any
                ? $"실제 상태 반영: 사라짐 {mutation.RemovedFiles:N0}개 · 크기 변경 {mutation.ResizedFiles:N0}개 — 이어서 {root} 재스캔"
                : $"목록은 실제 상태와 같습니다 — 새로 생긴 파일 확인을 위해 {root} 재스캔";
        }
        finally
        {
            IsRefreshing = false;
        }

        StartScan(root);
    }

    /// <summary>표시 중인 행들을 실제 파일 시스템과 대조하고 결과를 저장소에 반영한다.</summary>
    public static async Task<MutationSummary> VerifyRowsAsync(NodeStore store, IEnumerable<EntryRow> rows)
    {
        var targets = rows
            .Select(r => new FileVerifier.Target(r.Kind, r.Id, r.FullPath, r.Size))
            .ToList();
        if (targets.Count == 0) return default;

        var outcomes = await FileVerifier.VerifyAsync(targets).ConfigureAwait(true);
        return store.ApplyVerification(outcomes);
    }

    /// <summary>14. 삭제 성공 항목을 데이터 모델에서 즉시 제거한다(전체 재스캔 없이).</summary>
    public void ApplyDeletion(IReadOnlyList<(RowKind Kind, int Id)> removed)
    {
        // 지운 폴더를 빠른 이동 패널이 열어 두고 있을 수 있다. 스캔 결과가 없어도(다른 드라이브 등) 패널은 갱신한다.
        QuickMove.RefreshPanes();

        RemoveFromStore(removed, moved: false);
    }

    /// <summary>스캔 결과에서 노드를 빼고 모든 탭을 다시 그린다. 화면(패널) 갱신은 호출자 몫이다.</summary>
    private void RemoveFromStore(IReadOnlyList<(RowKind Kind, int Id)> removed, bool moved)
    {
        var store = _current?.Store;
        if (store == null || removed.Count == 0) return;

        var mutation = store.RemoveNodes(removed);

        RefreshCurrentView();
        RefreshTabData();
        RaiseStatusText();
        Cleanup.RemoveDeletedNodes(removed);
        ViewRefreshed?.Invoke(this, EventArgs.Empty);

        StatusMessage = moved
            ? $"이동한 {mutation.RemovedFiles:N0}개 파일 / {mutation.RemovedDirectories:N0}개 폴더를 분석 결과의 원래 위치에서 뺐습니다 — " +
              "옮겨 간 위치의 새 항목은 재스캔해야 나타납니다"
            : $"{mutation.RemovedFiles:N0}개 파일 / {mutation.RemovedDirectories:N0}개 폴더 제거 — " +
              $"{SizeFormatter.Format(mutation.RemovedBytes)} 확보 (화면 즉시 반영, 정확한 동기화는 새로고침)";
    }

    /// <summary>
    /// 빠른 이동으로 원래 자리에서 사라진 항목을 분석 결과(폴더/트리맵/큰 파일/정리 추천 탭)에서 뺀다.
    /// 그러지 않으면 이미 옮긴 파일이 그 탭들에 계속 남아 있고, 그것을 다시 지우려다 "이미 삭제됨" 이 된다.
    /// 스캔 중에는 저장소를 쓰는 스레드가 따로 있으므로 건드리지 않는다.
    /// </summary>
    private void OnQuickMoveRelocated(IReadOnlyList<string> sourcePaths)
    {
        var store = _current?.Store;
        if (store == null || IsScanning) return;

        var nodes = store.FindNodes(sourcePaths);
        if (nodes.Count > 0) RemoveFromStore(nodes, moved: true);
    }

    /// <summary>53~66. 정리 추천 탭.</summary>
    public CleanupViewModel Cleanup { get; } = new();

    /// <summary>빠른 이동 탭. 스캔 결과와 무관하게 실제 파일 시스템을 탐색해 파일을 옮긴다.</summary>
    public QuickMoveViewModel QuickMove { get; }

    // ---------------------------------------------------------------- 47. 설정

    public IReadOnlyList<string> ScanModeOptions { get; } = new[] { "자동 (Auto)", "Fast (NTFS MFT)", "호환 (디렉터리 열거)" };

    private string _selectedScanMode = "자동 (Auto)";
    public string SelectedScanMode
    {
        get => _selectedScanMode;
        set
        {
            if (!Set(ref _selectedScanMode, value)) return;
            Options.Mode = value.StartsWith("Fast", StringComparison.Ordinal) ? ScanMode.Fast
                : value.StartsWith("호환", StringComparison.Ordinal) ? ScanMode.Compatibility
                : ScanMode.Auto;
        }
    }

    public IReadOnlyList<string> WorkerOptions { get; } = new[] { "자동", "1", "2", "4", "8", "12", "16", "24" };

    private string _selectedWorker = "자동";
    public string SelectedWorker
    {
        get => _selectedWorker;
        set
        {
            if (!Set(ref _selectedWorker, value)) return;
            Options.WorkerCount = int.TryParse(value, out int n) ? n : 0;
        }
    }

    public bool FollowLinks
    {
        get => Options.FollowReparsePoints;
        set { Options.FollowReparsePoints = value; Raise(); }
    }

    public bool ShowHiddenFiles
    {
        get => Options.ShowHiddenFiles;
        set { Options.ShowHiddenFiles = value; Raise(); }
    }

    public bool ShowSystemFiles
    {
        get => Options.ShowSystemFiles;
        set { Options.ShowSystemFiles = value; Raise(); }
    }

    public bool CacheResults
    {
        get => Options.CacheResults;
        set { Options.CacheResults = value; Raise(); }
    }

    public IReadOnlyList<string> ThemeOptions { get; } = new[] { "Dark", "Light", "시스템" };

    private string _selectedTheme = "Dark";
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!Set(ref _selectedTheme, value)) return;
            ThemeChangeRequested?.Invoke(this, value);
        }
    }

    public IReadOnlyList<string> SizeUnitOptions { get; } = new[] { "자동", "KB", "MB", "GB", "TB" };

    private string _selectedSizeUnit = "자동";
    public string SelectedSizeUnit
    {
        get => _selectedSizeUnit;
        set
        {
            if (!Set(ref _selectedSizeUnit, value)) return;
            SizeFormatter.DefaultMode = value switch
            {
                "KB" => SizeUnitMode.KB,
                "MB" => SizeUnitMode.MB,
                "GB" => SizeUnitMode.GB,
                "TB" => SizeUnitMode.TB,
                _ => SizeUnitMode.Auto,
            };
            RefreshCurrentView();
            RefreshTabData();
            RaiseStatusText();
        }
    }

    public event EventHandler<string>? ThemeChangeRequested;

    // ---- 42. 상태바 ----
    public string StatusFiles => SizeFormatter.Count(_current?.FileCount ?? Progress.FileCount) + " files";
    public string StatusFolders => SizeFormatter.Count(_current?.DirectoryCount ?? Progress.DirectoryCount) + " folders";
    public string StatusSize => SizeFormatter.Format(_current?.TotalSize ?? Progress.TotalSize) + " analyzed";
    public string StatusElapsed => $"{(_current?.ElapsedSeconds ?? Progress.ElapsedSeconds):F1} sec";
    public string StatusSkipped => $"{(_current?.SkippedFolders ?? Progress.SkippedFolders):N0} skipped";
    public string StatusMode => _current?.ModeText ?? (Progress.Mode == ScanMode.Fast ? "NTFS Fast Scan" : "Compatibility Scan");
    public string StatusCache => _current is { FromCache: true }
        ? $"이전 스캔 결과 ({_current.CompletedAt:yyyy-MM-dd HH:mm})"
        : string.Empty;

    // ---- 5. 진행 상태 텍스트 ----
    public string ProgressPercent => SizeFormatter.Percent(Progress.Ratio);
    public double ProgressValue => Progress.Ratio * 100d;
    public string ProgressFiles => SizeFormatter.Count(Progress.FileCount);
    public string ProgressFolders => SizeFormatter.Count(Progress.DirectoryCount);
    public string ProgressAnalyzed => SizeFormatter.Format(Progress.TotalSize);
    public string ProgressSpeed => SizeFormatter.Rate(Progress.FilesPerSecond) + " files/sec";
    public string ProgressElapsed => $"{Progress.ElapsedSeconds:F1} sec";
    public string ProgressCurrent => Progress.Message ?? Progress.CurrentPath;

    // ---- 43. 성능 모니터 ----
    public string PerfWorkers => Progress.WorkerCount.ToString();
    public string PerfDirQueue => SizeFormatter.Count(Progress.DirectoryQueueSize);
    public string PerfResultQueue => SizeFormatter.Count(Progress.ResultQueueSize);
    public string PerfMemory => SizeFormatter.Format(Progress.ManagedMemoryBytes);
    public string PerfPeakMemory => SizeFormatter.Format(Progress.PeakMemoryBytes);
    public string PerfUiLatency => $"{Progress.UiUpdateLatencyMs:F1} ms";
    public string PerfDirsPerSec => SizeFormatter.Rate(Progress.DirectoriesPerSecond) + " dir/s";
    public string PerfStoreMemory => SizeFormatter.Format(_current?.Store.EstimatedMemoryBytes ?? 0);

    // ---------------------------------------------------------------- 명령

    public RelayCommand ScanSelectedCommand { get; }
    public RelayCommand ScanAllCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand ForwardCommand { get; }
    public RelayCommand UpCommand { get; }
    public RelayCommand RootCommand { get; }
    public RelayCommand SearchCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand ApplyFilterCommand { get; }
    public RelayCommand ClearCacheCommand { get; }

    public event EventHandler? ViewRefreshed;

    // ---------------------------------------------------------------- 스캔

    public void StartScan(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || IsScanning) return;

        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        _lastViewVersion = -1;
        _currentDirId = NodeStore.RootId;
        _back.Clear();
        _forward.Clear();
        ClearSearchState();
        _current = null;
        Rows = Array.Empty<EntryRow>();
        TopFiles = Array.Empty<EntryRow>();
        Extensions = Array.Empty<ExtensionRow>();
        ExtensionFiles = Array.Empty<EntryRow>();

        IsScanning = true;
        StatusMessage = $"{rootPath} 스캔 중...";
        _controller.SetLiveViewDirectory(NodeStore.RootId);
        _controller.SetLiveTab(ActiveTab);
        _controller.SetTopFileCount(SelectedTopN);

        string root = rootPath;
        _scanTask = Task.Run(async () =>
        {
            try
            {
                var result = await _controller.ScanAsync(root, Options.Clone(), _scanCts.Token).ConfigureAwait(false);
                Application.Current?.Dispatcher.Invoke(() => OnScanCompleted(result));
            }
            catch (Exception ex)
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsScanning = false;
                    StatusMessage = "스캔 실패: " + ex.Message;
                });
            }
        });
    }

    private async void StartScanAll()
    {
        foreach (var drive in Drives.Where(d => d.IsReady).ToList())
        {
            StartScan(drive.RootPath);
            while (IsScanning) await Task.Delay(100).ConfigureAwait(true);
        }
    }

    private void OnScanCompleted(ScanResult result)
    {
        IsScanning = false;
        _current = result;
        _results[result.RootPath] = result;

        StatusMessage = result.Cancelled
            ? $"취소됨 - {SizeFormatter.Count(result.FileCount)} files 까지 분석"
            : $"{result.RootPath} 완료 - {SizeFormatter.Count(result.FileCount)} files, {SizeFormatter.Format(result.TotalSize)}, {result.ElapsedSeconds:F1}초 ({result.ModeText})";

        Navigate(NodeStore.RootId, pushHistory: false);
        RefreshTabData();
        RaiseStatusText();
        Cleanup.SetResult(result);
        RefreshLargeFilesCommand.RaiseCanExecuteChanged();
    }

    private void TryLoadCache()
    {
        // 40. 프로그램을 다시 열었을 때 최근 스캔 결과를 즉시 보여 준다.
        var drive = SelectedDrive?.RootPath;
        if (drive == null) return;

        Task.Run(() => CacheService.TryLoad(drive, Options.TopFileCount)).ContinueWith(t =>
        {
            if (t.Result == null || IsScanning) return;
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (IsScanning || _current != null) return;
                _current = t.Result;
                _results[t.Result.RootPath] = t.Result;
                StatusMessage = $"이전 스캔 결과를 불러왔습니다 ({t.Result.CompletedAt:yyyy-MM-dd HH:mm}). 최신 데이터는 새로고침하세요.";
                Navigate(NodeStore.RootId, pushHistory: false);
                RefreshTabData();
                RaiseStatusText();
                Cleanup.SetResult(t.Result);
                RefreshLargeFilesCommand.RaiseCanExecuteChanged();
            });
        }, TaskScheduler.Default);
    }

    private void ShowResultFor(string? rootPath)
    {
        if (rootPath == null || IsScanning) return;
        if (!_results.TryGetValue(ScanController.NormalizeRoot(rootPath), out var r)) return;
        _current = r;
        Navigate(NodeStore.RootId, pushHistory: false);
        RefreshTabData();
        RaiseStatusText();
        Cleanup.SetResult(r);
    }

    // ---------------------------------------------------------------- 타이머

    private void Tick()
    {
        var sw = Stopwatch.StartNew();

        var p = _controller.Progress;
        if (p.State != ScanState.Idle) Progress = p;

        if (IsScanning)
        {
            var view = _controller.LiveView;
            if (view != null && view.Version != _lastViewVersion && view.DirectoryId == _currentDirId)
            {
                _lastViewVersion = view.Version;
                if (!IsSearchMode) Rows = view.Rows;
                CurrentPath = view.Path;
                UpdateBreadcrumb(view.Breadcrumb);
                if (view.TopFiles != null) TopFiles = view.TopFiles;
                if (view.Extensions != null) Extensions = view.Extensions;
                ViewRefreshed?.Invoke(this, EventArgs.Empty);
            }
            RaiseStatusText();
            RaisePerfText();
        }

        _controller.ReportUiLatency(sw.Elapsed.TotalMilliseconds);
    }

    // ---------------------------------------------------------------- 탐색

    public void Navigate(int dirId, bool pushHistory = true)
    {
        if (_current == null && !IsScanning) return;

        if (pushHistory && dirId != _currentDirId)
        {
            _back.Push(_currentDirId);
            _forward.Clear();
        }

        _currentDirId = dirId;
        _controller.SetLiveViewDirectory(dirId);
        ClearSearchState();
        RefreshCurrentView();

        BackCommand.RaiseCanExecuteChanged();
        ForwardCommand.RaiseCanExecuteChanged();
        UpCommand.RaiseCanExecuteChanged();
    }

    public void NavigateToPath(string fullPath)
    {
        if (_current == null) return;
        int id = _current.Store.FindDirectory(fullPath);
        Navigate(id);
    }

    private void GoBack()
    {
        if (_back.Count == 0) return;
        _forward.Push(_currentDirId);
        Navigate(_back.Pop(), pushHistory: false);
    }

    private void GoForward()
    {
        if (_forward.Count == 0) return;
        _back.Push(_currentDirId);
        Navigate(_forward.Pop(), pushHistory: false);
    }

    private void GoUp()
    {
        var store = _current?.Store;
        if (store == null) return;
        int parent = store.GetParent(_currentDirId);
        if (parent >= 0) Navigate(parent);
    }

    // ---------------------------------------------------------------- 뷰 갱신

    private FilterOptions BuildFilter() => new()
    {
        MinSize = SelectedMinSize switch
        {
            "100 MB 이상" => 100L * 1024 * 1024,
            "500 MB 이상" => 500L * 1024 * 1024,
            "1 GB 이상" => 1L * 1024 * 1024 * 1024,
            "5 GB 이상" => 5L * 1024 * 1024 * 1024,
            "10 GB 이상" => 10L * 1024 * 1024 * 1024,
            _ => 0,
        },
        ExtensionPattern = ExtensionFilter,
    };

    private void RefreshCurrentView()
    {
        var store = _current?.Store;
        if (store == null)
        {
            // 스캔 중에는 Aggregator 스냅샷만 사용한다(NodeStore 동시 접근 금지).
            _lastViewVersion = -1;
            return;
        }

        // 검색 결과를 보는 중에 필터를 바꾸거나 항목을 삭제하면 "폴더 목록"이 아니라 검색을 다시 한다.
        // 그렇지 않으면 검색 모드 표시는 그대로인데 목록만 폴더 내용으로 바뀌어 화면이 어긋난다.
        if (IsSearchMode)
        {
            ExecuteSearch();
            return;
        }

        var filter = BuildFilter();
        Rows = store.GetChildren(_currentDirId, includeFiles: true, filter.IsEmpty ? null : filter);
        CurrentPath = store.GetDirectoryPath(_currentDirId);
        UpdateBreadcrumb(store.GetAncestors(_currentDirId));
        ViewRefreshed?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshTabData()
    {
        if (_current?.Store == null) return;
        RefreshTopFiles();
        Extensions = _current.Store.GetExtensionRows();
    }

    private void RefreshTopFiles()
    {
        var store = _current?.Store;
        if (store == null) return;
        var filter = BuildFilter();
        TopFiles = store.GetTopFiles(SelectedTopN, filter.IsEmpty ? null : filter);
    }

    private void LoadExtensionFiles(ExtensionRow? row)
    {
        var store = _current?.Store;
        if (store == null || row == null) { ExtensionFiles = Array.Empty<EntryRow>(); return; }
        ExtensionFiles = store.GetFilesByExtension(row.Extension, 2000);
    }

    private void UpdateBreadcrumb(IReadOnlyList<(int Id, string Name)> parts)
    {
        Breadcrumb.Clear();
        for (int i = 0; i < parts.Count; i++)
        {
            Breadcrumb.Add(new BreadcrumbItem
            {
                Id = parts[i].Id,
                Name = parts[i].Name,
                IsLast = i == parts.Count - 1,
            });
        }
    }

    // ---------------------------------------------------------------- 검색

    private const int MaxSearchResults = 5000;

    private void RunSearch()
    {
        string term = SearchText.Trim();
        if (term.Length == 0)
        {
            ClearSearch();
            return;
        }

        // 스캔 중에는 NodeStore 를 Aggregator 가 쓰고 있어 UI 스레드가 읽을 수 없다.
        // 아무 반응이 없으면 "검색이 안 된다"로 보이므로 이유를 알려 준다.
        if (_current?.Store == null)
        {
            StatusMessage = IsScanning
                ? "스캔이 끝난 뒤에 검색할 수 있습니다."
                : "검색할 스캔 결과가 없습니다. 먼저 드라이브를 스캔하세요.";
            return;
        }

        _activeSearchTerm = term;
        SearchStarted?.Invoke(this, EventArgs.Empty);
        ExecuteSearch();
    }

    private void ExecuteSearch()
    {
        var store = _current?.Store;
        if (store == null || _activeSearchTerm.Length == 0) return;

        var filter = BuildFilter();
        bool scoped = SelectedSearchScope != "전체" && _currentDirId > NodeStore.RootId;

        var sw = Stopwatch.StartNew();
        var outcome = store.SearchWithCount(
            _activeSearchTerm, MaxSearchResults, filter.IsEmpty ? null : filter,
            scoped ? _currentDirId : NodeStore.RootId);
        double ms = sw.Elapsed.TotalMilliseconds;

        IsSearchMode = true;
        Rows = outcome.Rows;
        CurrentPath = scoped
            ? $"검색: \"{_activeSearchTerm}\"  ·  {store.GetDirectoryPath(_currentDirId)} 하위"
            : $"검색: \"{_activeSearchTerm}\"";

        StatusMessage = outcome.Truncated
            ? $"검색 결과 {outcome.TotalMatches:N0}건 중 크기 상위 {outcome.Rows.Count:N0}건 표시 ({ms:F0}ms) — 검색어를 좁혀 보세요"
            : $"검색 결과 {outcome.TotalMatches:N0}건 ({ms:F0}ms)";
        ViewRefreshed?.Invoke(this, EventArgs.Empty);
    }

    private void ClearSearch()
    {
        SearchText = string.Empty;
        ClearSearchState();
        RefreshCurrentView();
    }

    private void ClearSearchState()
    {
        IsSearchMode = false;
        _activeSearchTerm = string.Empty;
    }

    // ---------------------------------------------------------------- 알림

    private void RaiseProgressText()
    {
        Raise(nameof(ProgressPercent));
        Raise(nameof(ProgressValue));
        Raise(nameof(ProgressFiles));
        Raise(nameof(ProgressFolders));
        Raise(nameof(ProgressAnalyzed));
        Raise(nameof(ProgressSpeed));
        Raise(nameof(ProgressElapsed));
        Raise(nameof(ProgressCurrent));
    }

    private void RaiseStatusText()
    {
        Raise(nameof(StatusFiles));
        Raise(nameof(StatusFolders));
        Raise(nameof(StatusSize));
        Raise(nameof(StatusElapsed));
        Raise(nameof(StatusSkipped));
        Raise(nameof(StatusMode));
        Raise(nameof(StatusCache));
    }

    private void RaisePerfText()
    {
        if (!ShowPerfMonitor) return;
        Raise(nameof(PerfWorkers));
        Raise(nameof(PerfDirQueue));
        Raise(nameof(PerfResultQueue));
        Raise(nameof(PerfMemory));
        Raise(nameof(PerfPeakMemory));
        Raise(nameof(PerfUiLatency));
        Raise(nameof(PerfDirsPerSec));
        Raise(nameof(PerfStoreMemory));
    }
}

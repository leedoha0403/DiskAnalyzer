using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Scanning;
using DiskAnalyzer.Core.Services;
using DiskAnalyzer.Core.Analysis;
using DiskAnalyzer.Core.QuickMove;
using DiskAnalyzer.App.ViewModels.QuickMove;
using DiskAnalyzer.App.Controls;

namespace DiskAnalyzer.App.ViewModels;

/// <summary>
/// 19. UI ViewModel 계층.
/// 파일 시스템 접근 코드는 단 한 줄도 들어 있지 않다. ScanController 가 게시한 스냅샷을 화면 상태로 옮길 뿐이다.
///
/// 29. UI 업데이트는 DispatcherTimer 150ms 주기의 "pull" 방식이다.
/// 스캔 엔진이 UI 로 이벤트를 밀어 넣으면 파일이 많을수록 Dispatcher 큐가 넘치고 UI 가 멈춘다.
/// 엔진은 최신 스냅샷만 게시하고 UI 가 자기 속도로 읽어가면 UI 는 절대 밀리지 않는다.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
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
        _useSearchTree = _ui.SearchGroupByPath;      // 기억해 둔 값으로 시작한다(기본 켜짐)
        _sidebarCollapsed = _ui.SidebarCollapsed;
        _sidebarWidth = _ui.SidebarWidth > 0 ? _ui.SidebarWidth : DefaultSidebarWidth;
        _ringTint = Enum.TryParse<SunburstTint>(_ui.RingTint, out var tint) ? tint : SunburstTint.Size;

        // 50. 애니메이션. 한 번도 정한 적이 없으면(첫 실행) Windows 의 '애니메이션 표시' 설정을 따른다.
        // 그 뒤로는 여기서 정한 값이 이긴다 — 그래서 값을 그때 바로 적어 둔다.
        _animations = _ui.Animations ?? Motion.SystemPrefersAnimation;
        if (_ui.Animations == null)
        {
            _ui.Animations = _animations;
            _ui.Save();
        }
        Motion.Apply(_animations);

        Options.SizeBasis = Enum.TryParse<SizeBasis>(_ui.SizeBasis, out var basis) ? basis : SizeBasis.Logical;
        Options.DeduplicateHardLinks = _ui.DeduplicateHardLinks;
        _exclusions = ExclusionSettings.Load();
        Options.ExclusionPatterns = _exclusions.Patterns;
        _exclusionText = _exclusions.ToText();

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
        ApplyExclusionsCommand = new RelayCommand(ApplyExclusions);
        RefreshLargeFilesCommand = new RelayCommand(
            () => _ = RefreshLargeFilesAsync(), () => !IsRefreshing && !IsScanning && _current != null);
        InitAboutCommands();

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
            RefreshFolderCommand.RaiseCanExecuteChanged();
            RefreshFolderDeepCommand.RaiseCanExecuteChanged();
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
    public bool IsSearchMode
    {
        get => _isSearchMode;
        private set { if (Set(ref _isSearchMode, value)) Raise(nameof(ShowSearchTree)); }
    }

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
    /// <summary>
    /// 드라이브 카드의 용량을 다시 읽는다. 생성자에서 한 번 읽고 마는 값이라
    /// 100 GB 를 지워도 "남은 용량"이 그대로였다 — 사이드바에 상시 보이는 지금은 더 눈에 띈다.
    /// </summary>
    public void RefreshDrives()
    {
        string? keep = SelectedDrive?.RootPath;
        var fresh = DriveService.GetLocalDrives();

        Drives.Clear();
        foreach (var d in fresh) Drives.Add(d);

        SelectedDrive = Drives.FirstOrDefault(d => PathUtil.Equal(d.RootPath, keep ?? string.Empty))
                        ?? Drives.FirstOrDefault();
    }

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

    public IReadOnlyList<string> SizeBasisOptions { get; } = new[] { "논리 (파일 크기)", "물리 (디스크 할당)" };

    /// <summary>
    /// 탐색기의 "크기"(논리) 와 "디스크 할당 크기"(물리) 의 차이와 같다.
    /// 물리는 NTFS 압축 폴더 · 스파스 파일이 부풀지 않고 드라이브 사용량과도 덜 어긋난다.
    /// 다음 스캔부터 적용된다.
    /// </summary>
    public string SelectedSizeBasis
    {
        get => Options.SizeBasis == SizeBasis.Physical ? SizeBasisOptions[1] : SizeBasisOptions[0];
        set
        {
            var basis = value == SizeBasisOptions[1] ? SizeBasis.Physical : SizeBasis.Logical;
            if (Options.SizeBasis == basis) return;

            Options.SizeBasis = basis;
            _ui.SizeBasis = basis.ToString();
            _ui.Save();
            Raise();
            StatusMessage = "크기 기준을 바꿨습니다. 다음 스캔부터 적용됩니다.";
        }
    }

    /// <summary>
    /// 같은 실체가 여러 경로에 걸려 있으면(하드 링크) 처음 것만 센다.
    /// <c>C:\Windows</c> 실측으로 44.3 GB → 34.3 GB — 10 GB 가 같은 바이트의 중복이었다.
    /// </summary>
    public bool DeduplicateHardLinks
    {
        get => Options.DeduplicateHardLinks;
        set
        {
            if (Options.DeduplicateHardLinks == value) return;

            Options.DeduplicateHardLinks = value;
            _ui.DeduplicateHardLinks = value;
            _ui.Save();
            Raise();
            StatusMessage = "하드 링크 처리 방식을 바꿨습니다. 다음 스캔부터 적용됩니다.";
        }
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

    // ------------------------------------------------- 48. 스캔 제외 규칙

    private readonly ExclusionSettings _exclusions;
    private string _exclusionText;

    /// <summary>설정 패널의 여러 줄 입력. 편집 중에는 저장하지 않고 <see cref="ApplyExclusionsCommand"/> 에서 한 번에 반영한다.</summary>
    public string ExclusionText
    {
        get => _exclusionText;
        set { if (Set(ref _exclusionText, value)) Raise(nameof(ExclusionSummary)); }
    }

    /// <summary>지금 스캔에 적용되어 있는 규칙 수. 화면에서 "적용했는가"를 바로 알 수 있게 한다.</summary>
    public string ExclusionSummary
    {
        get
        {
            int active = Options.Exclusions.Patterns.Count;
            bool dirty = !ExclusionSettings.SplitLines(_exclusionText).SequenceEqual(Options.Exclusions.Patterns, StringComparer.OrdinalIgnoreCase);
            return active == 0
                ? (dirty ? "적용 안 됨 - [적용] 을 누르세요" : "제외 규칙 없음")
                : dirty ? $"{active}개 적용 중 - 바뀐 내용은 [적용] 후 반영" : $"{active}개 적용 중 (다음 스캔부터)";
        }
    }

    public RelayCommand ApplyExclusionsCommand { get; private set; } = null!;

    private void ApplyExclusions()
    {
        var patterns = ExclusionSettings.SplitLines(_exclusionText);
        Options.ExclusionPatterns = patterns;

        // 알아볼 수 없는 줄은 Parse 가 버린다. 사용자가 무엇이 남았는지 바로 보도록 정규화된 결과를 되돌려 쓴다.
        var kept = Options.Exclusions.Patterns.ToList();
        _exclusions.Patterns = kept;
        _exclusions.Save();

        ExclusionText = string.Join(Environment.NewLine, kept);
        StatusMessage = kept.Count == 0
            ? "스캔 제외 규칙을 지웠습니다. 다음 스캔부터 적용됩니다."
            : $"스캔 제외 규칙 {kept.Count}개를 저장했습니다. 다음 스캔부터 적용됩니다.";
        Raise(nameof(ExclusionSummary));
    }

    public IReadOnlyList<string> ThemeOptions { get; } = new[] { "Mint", "Dark", "Light", "시스템", "Daisy" };

    private string _selectedTheme = "시스템";
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
        ResetFolderChange();
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
        RefreshDrives();          // 스캔 뒤에는 여유 공간이 달라져 있을 수 있다
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

        // 뒤로 / 앞으로로 돌아왔는데 그 폴더가 그 사이 새로고침에서 없어졌다면 가장 가까운 폴더로.
        if (_current?.Store is { } live && !IsScanning && live.IsDirectoryDeleted(dirId))
            dirId = live.NearestLiveDirectory(dirId);

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
        OnFolderOpened();
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
        _searchHits = outcome.Rows;
        Rows = outcome.Rows;
        BuildSearchTree();
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
        _searchHits = Array.Empty<EntryRow>();
        _searchFold = SearchFoldTree.Empty;
        _groupOf.Clear();
        SearchFoldSummary = string.Empty;
        Raise(nameof(HasSearchFoldSummary));
    }

    // -------------------------------------------- 49. 검색 결과 경로별 접기

    private readonly UiSettings _ui = UiSettings.Load();
    private bool _useSearchTree;

    /// <summary>
    /// 검색 결과를 평면 목록 대신 폴더 트리 + 이름 접기로 본다. 검색 중에만 뜻이 있다.
    /// 켜고 끈 상태는 ui.json 에 남아 다음에 앱을 열 때도 그대로다.
    /// </summary>
    public bool UseSearchTree
    {
        get => _useSearchTree;
        set
        {
            if (!Set(ref _useSearchTree, value)) return;
            Raise(nameof(ShowSearchTree));
            if (IsSearchMode) BuildSearchTree();

            _ui.SearchGroupByPath = value;
            _ui.Save();
        }
    }

    private bool _sidebarCollapsed;

    /// <summary>왼쪽 저장소 사이드바가 접혀 있는가. 상태는 ui.json 에 남는다.</summary>
    public bool SidebarCollapsed
    {
        get => _sidebarCollapsed;
        set
        {
            if (!Set(ref _sidebarCollapsed, value)) return;
            _ui.SidebarCollapsed = value;
            _ui.Save();
        }
    }

    public const double DefaultSidebarWidth = 264d;
    public const double MinSidebarWidth = 190d;

    private double _sidebarWidth = DefaultSidebarWidth;

    /// <summary>끌어서 정한 사이드바 폭. 좁은 화면에서는 검색 상자가 줄어드니 줄일 수 있어야 한다.</summary>
    public double SidebarWidth
    {
        get => _sidebarWidth;
        set
        {
            double clamped = Math.Max(MinSidebarWidth, value);
            if (!Set(ref _sidebarWidth, clamped)) return;
            _ui.SidebarWidth = clamped;
            _ui.Save();
        }
    }

    private bool _animations = true;

    /// <summary>
    /// 50. 화면 전환과 강조에 애니메이션을 쓰는가. <b>기본값은 켜짐</b>이고 ui.json 에 남는다.
    ///
    /// <para>이 하나가 앱의 모든 연출을 쥔다 — 선버스트의 줌 · 호버 · 스캔 중 각도 보간,
    /// 패널이 뜨고 지는 방식, 사이드바 접기, 진행률 막대까지 전부 <see cref="Motion"/> 을 지나므로
    /// 여기를 끄면 같은 결과가 움직임 없이 즉시 나타난다(동작이 빠지는 것이 아니라 시간이 빠진다).</para>
    /// </summary>
    public bool AnimationsEnabled
    {
        get => _animations;
        set
        {
            if (!Set(ref _animations, value)) return;
            Motion.Apply(value);
            _ui.Animations = value;
            _ui.Save();
        }
    }

    private SunburstTint _ringTint = SunburstTint.Size;

    /// <summary>선버스트 링을 무엇으로 칠할지. 그림이 답하는 질문이 바뀐다.</summary>
    public SunburstTint RingTint
    {
        get => _ringTint;
        set
        {
            if (!Set(ref _ringTint, value)) return;
            Raise(nameof(RingTintText));
            Raise(nameof(SelectedRingTint));
            _ui.RingTint = value.ToString();
            _ui.Save();
        }
    }

    public IReadOnlyList<string> RingTintOptions { get; } = new[] { "크기", "정리 추천", "이전 대비 증감" };

    /// <summary>화면의 콤보와 묶이는 쪽. 다른 설정들과 같은 방식이라 변환기가 필요 없다.</summary>
    public string SelectedRingTint
    {
        get => RingTintText;
        set => RingTint = value switch
        {
            "정리 추천" => SunburstTint.Cleanup,
            "이전 대비 증감" => SunburstTint.Delta,
            _ => SunburstTint.Size,
        };
    }

    /// <summary>색 기준을 순서대로 돌린다(단축키용).</summary>
    public void CycleRingTint()
        => RingTint = RingTint switch
        {
            SunburstTint.Size => SunburstTint.Cleanup,
            SunburstTint.Cleanup => SunburstTint.Delta,
            _ => SunburstTint.Size,
        };

    public string RingTintText => RingTint switch
    {
        SunburstTint.Cleanup => "정리 추천",
        SunburstTint.Delta => "이전 대비 증감",
        _ => "크기",
    };

    /// <summary>지금 목록이 묶여 있는가(검색 중 + 켜짐). 컬럼 정렬처럼 묶음과 상충하는 기능이 이것을 본다.</summary>
    public bool ShowSearchTree => IsSearchMode && _useSearchTree;

    private string _searchFoldSummary = string.Empty;

    /// <summary>"12묶음으로 340건을 접었습니다" 처럼, 접기가 무엇을 했는지 알려 준다.</summary>
    public string SearchFoldSummary { get => _searchFoldSummary; private set => Set(ref _searchFoldSummary, value); }

    public bool HasSearchFoldSummary => _searchFoldSummary.Length > 0;

    /// <summary>검색이 찾아낸 결과 원본. 묶기를 껐다 켤 때 다시 검색하지 않으려고 들고 있는다.</summary>
    private IReadOnlyList<EntryRow> _searchHits = Array.Empty<EntryRow>();

    private SearchFoldTree _searchFold = SearchFoldTree.Empty;

    /// <summary>머리글 행 -> 그 행이 대표하는 노드. 펼침 상태는 노드가 들고 있다.</summary>
    private readonly Dictionary<EntryRow, SearchNode> _groupOf = new(ReferenceEqualityComparer.Instance);

    private void BuildSearchTree()
    {
        if (!_useSearchTree)
        {
            _searchFold = SearchFoldTree.Empty;
            _groupOf.Clear();
            SearchFoldSummary = string.Empty;
            Raise(nameof(HasSearchFoldSummary));
            Rows = _searchHits;
            return;
        }

        _searchFold = SearchFolder.Build(_searchHits);
        SearchFoldSummary = _searchFold.PatternCount == 0
            ? string.Empty
            : $"{_searchFold.PatternCount:N0}개 묶음으로 {_searchFold.FoldedItemCount:N0}건을 접었습니다";
        Raise(nameof(HasSearchFoldSummary));
        FlattenSearchTree();
    }

    /// <summary>
    /// 트리를 "지금 펼쳐진 만큼만" 한 줄짜리 목록으로 편다.
    /// 트리 컨트롤을 쓰지 않는 이유: 선택 요약 · 삭제 · Treemap 동기화 · 우클릭 메뉴가 모두
    /// 폴더 목록의 SelectedItems 에 걸려 있고, WPF TreeView 는 Ctrl / Shift 다중 선택을 하지 못한다.
    /// 같은 ListView 에 들여쓰기로 그리면 그 배선이 전부 그대로 살아 있다.
    /// </summary>
    private void FlattenSearchTree()
    {
        var flat = new List<EntryRow>(Math.Max(16, _searchHits.Count / 2));
        _groupOf.Clear();

        foreach (var root in _searchFold.Roots) Walk(root, 0);
        Rows = flat;

        void Walk(SearchNode node, int depth)
        {
            if (node.Kind == SearchNodeKind.Item)
            {
                var row = node.Row!;
                row.Depth = depth;
                row.SearchKind = SearchRowKind.None;
                flat.Add(row);
                return;
            }

            var header = new EntryRow
            {
                Kind = RowKind.Directory,
                Id = -1,                       // 실제 노드가 아니다. 선택 / 삭제에서 걸러진다.
                Name = node.Title,
                FullPath = node.FullPath,
                Size = node.Size,
                Depth = depth,
                SearchKind = node.Kind == SearchNodeKind.Folder ? SearchRowKind.Folder : SearchRowKind.Pattern,
                MatchCount = node.MatchCount,
                IsExpanded = node.IsExpanded,
            };
            flat.Add(header);
            _groupOf[header] = node;

            if (!node.IsExpanded) return;
            foreach (var child in node.Children) Walk(child, depth + 1);
        }
    }

    /// <summary>묶음 머리글을 펼치거나 접는다. 접혀 있던 하위는 다시 펼 때까지 목록에 만들지 않는다.</summary>
    public bool ToggleSearchGroup(EntryRow? row)
    {
        if (row == null || !_groupOf.TryGetValue(row, out var node)) return false;

        node.IsExpanded = !node.IsExpanded;
        FlattenSearchTree();
        return true;
    }

    // -------------------------------------------- 49. 검색 결과 -> 그 항목이 있는 폴더로

    /// <summary>검색 결과에서 폴더로 이동했을 때. 화면은 폴더 목록이 보이도록 탭을 맞춘다.</summary>
    public event EventHandler? FolderRevealed;

    /// <summary>
    /// 이 항목이 있는 폴더를 연다. 폴더면 그 폴더로, 파일이면 그 파일이 든 폴더로 가서 파일을 선택한다.
    /// 폴더 위치는 Treemap 과 공유하므로 두 탭이 같은 곳을 보게 된다.
    /// </summary>
    public void RevealRow(EntryRow? row)
    {
        var store = _current?.Store;
        if (row == null || store == null) return;

        // 49. 묶음 머리글은 노드 id 가 없다. 폴더 묶음이면 그 경로로 가고, 이름 묶음이면 갈 곳이 하나가 아니다.
        if (row.IsSearchGroup)
        {
            if (row.SearchKind == SearchRowKind.Folder) NavigateToPath(row.FullPath);
            else StatusMessage = $"{row.Name} 은(는) {row.MatchCount:N0}건을 묶은 줄입니다. 펼쳐서 항목을 고르세요.";
            return;
        }

        if (row.IsDirectory)
        {
            Navigate(row.Id);
        }
        else
        {
            int parent = store.GetFileParent(row.Id);
            if (parent < 0)
            {
                StatusMessage = $"{row.Name} 이(가) 있던 폴더를 찾지 못했습니다. 새로고침 후 다시 시도하세요.";
                return;
            }

            Navigate(parent);
            RequestSelectFile(row.Id);
        }

        FolderRevealed?.Invoke(this, EventArgs.Empty);
        StatusMessage = $"{row.Name} 위치로 이동했습니다.";
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

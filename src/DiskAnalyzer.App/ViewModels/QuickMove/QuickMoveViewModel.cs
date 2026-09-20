using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.QuickMove;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.ViewModels.QuickMove;

/// <summary>화면이 제공하는 대화 상자. ViewModel 이 WPF 창을 직접 알지 않게 한다.</summary>
public interface IQuickMoveUi
{
    /// <summary>이동 엔진 스레드(백그라운드)에서 호출된다. 구현은 UI 스레드에서 창을 띄우고 답을 기다려야 한다.</summary>
    ConflictDecision ResolveConflict(ConflictInfo info);

    /// <summary>UI 스레드에서 호출. 누른 버튼의 번호(0부터), 창을 그냥 닫으면 -1.</summary>
    int Choose(string title, string message, int defaultIndex, params string[] buttons);
}

public enum QuickMovePhase { Idle, Validating, Running, Finished }

public sealed record PolicyOption(string Label, ConflictPolicy Policy)
{
    public override string ToString() => Label;
}

/// <summary>
/// 빠른 이동 탭. 출발지 / 목적지 두 패널 + 이동 대기열 + 진행 / 결과.
///
/// 흐름:  고른다 → 대기열에 넣는다(즉시 이동하지 않는다) → [이동 시작] → 검사(존재·권한·보호 등급·공간) → 실행 → 결과.
/// 실행은 <see cref="MoveEngine"/> 이 한다. 여기는 "무엇을 언제 물어볼지"와 화면 상태만 맡는다.
/// </summary>
public sealed class QuickMoveViewModel : ObservableObject
{
    private static readonly PolicyOption[] Policies =
    {
        new("항목별 확인", ConflictPolicy.Ask),
        new("모두 건너뛰기", ConflictPolicy.Skip),
        new("모두 새 이름으로 저장", ConflictPolicy.Rename),
        new("모두 덮어쓰기", ConflictPolicy.Overwrite),
    };

    private readonly ConcurrentDictionary<string, TreeMeasure.Result> _measureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(long Ms, long Bytes)> _samples = new();
    private readonly Func<string?> _currentFolder;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    /// <summary>
    /// 화면 스레드로 되돌아오는 스케줄러. 생성 시점에 한 번만 잡는다. 동기화 컨텍스트가 없는 환경(테스트 / 도구)에서는
    /// 기본 스케줄러로 대체해 예외 없이 동작한다(그 경우 화면 갱신 스레드 보장은 호출자 책임).
    /// </summary>
    internal TaskScheduler UiScheduler { get; } = SynchronizationContext.Current != null
        ? TaskScheduler.FromCurrentSynchronizationContext()
        : TaskScheduler.Default;
    private long _waitedMs;   // 사용자가 대화 상자에 답하느라 기다린 시간(속도 / 남은 시간 / 소요 시간에서 뺀다)

    private IReadOnlyList<DriveEntry> _drives = Array.Empty<DriveEntry>();
    private IReadOnlyList<SpaceRow> _lastSpace = Array.Empty<SpaceRow>();

    private QuickMovePhase _phase = QuickMovePhase.Idle;
    private CancellationTokenSource? _cts;
    private MoveEngine? _engine;
    private List<QueueItemViewModel> _runItems = new();
    private List<MoveItemResult> _preFailed = new();
    private MoveSummary? _summary;
    private Stopwatch _runClock = new();
    private int _spaceVersion;
    private int _noticeVersion;

    private string _notice = string.Empty;
    private string _queueSummary = "이동 예정 없음";
    private string _routeHint = string.Empty;
    private string _spaceWarning = string.Empty;
    private IReadOnlyList<SpaceLineViewModel> _spaceLines = Array.Empty<SpaceLineViewModel>();
    private bool _isPaused;

    private double _progressFraction;
    private string _progressPercent = "0%";
    private string _progressFiles = string.Empty;
    private string _progressItem = string.Empty;
    private string _progressBytes = string.Empty;
    private string _currentName = string.Empty;
    private string _currentRoute = string.Empty;
    private string _speedText = string.Empty;
    private string _remainingText = string.Empty;

    private string _resultTitle = string.Empty;
    private string _resultDetail = string.Empty;
    private bool _resultHasProblem;
    private bool _showFailedOnly;
    private IReadOnlyList<ResultRowViewModel> _allResults = Array.Empty<ResultRowViewModel>();

    private readonly record struct DriveEntry(string Name, string Path);

    public QuickMoveViewModel(Func<string?>? currentFolder = null)
    {
        _currentFolder = currentFolder ?? (() => null);
        Settings = QuickMoveSettings.Load();

        Left = new QuickMovePaneViewModel(this, "출발지", Settings.LeftSortKey, Settings.LeftSortAscending);
        Right = new QuickMovePaneViewModel(this, "목적지", Settings.RightSortKey, Settings.RightSortAscending);

        MoveRightCommand = new RelayCommand(() => AddToQueue(Left.SelectedEntries, Right.CurrentPath),
            () => CanEditQueue && Left.SelectedEntries.Count > 0 && Right.HasPath);
        MoveLeftCommand = new RelayCommand(() => AddToQueue(Right.SelectedEntries, Left.CurrentPath),
            () => CanEditQueue && Right.SelectedEntries.Count > 0 && Left.HasPath);
        SwapCommand = new RelayCommand(SwapPanes, () => Left.HasPath && Right.HasPath);
        ClearQueueCommand = new RelayCommand(ClearQueue, () => CanEditQueue && Queue.Count > 0);
        StartCommand = new RelayCommand(() => _ = StartAsync(), () => CanStart);
        CancelCommand = new RelayCommand(RequestCancel, () => Phase == QuickMovePhase.Running);
        PauseCommand = new RelayCommand(TogglePause, () => Phase == QuickMovePhase.Running);
        DismissResultCommand = new RelayCommand(DismissResult, () => Phase == QuickMovePhase.Finished);
        RetryCommand = new RelayCommand(() => { DismissResult(); _ = StartAsync(); },
            () => Phase == QuickMovePhase.Finished && CanRetry);
        OpenResultCommand = new RelayCommand(OpenResultLocation, () => Phase == QuickMovePhase.Finished);
        ToggleQueueCollapsedCommand = new RelayCommand(() =>
        {
            if (_isDockMode)
            {
                _dockQueueOpen = !_dockQueueOpen;
                Raise(nameof(QueueBodyOpen));
            }
            else IsQueueCollapsed = !IsQueueCollapsed;
        });

        Queue.CollectionChanged += (_, _) => OnQueueChanged();

        BuildLocations();
        _ = InitializePanesAsync();
    }

    public QuickMoveSettings Settings { get; }
    public QuickMovePaneViewModel Left { get; }
    public QuickMovePaneViewModel Right { get; }
    public ObservableCollection<QueueItemViewModel> Queue { get; } = new();
    public ObservableCollection<QuickLocation> QuickLocations { get; } = new();

    public IQuickMoveUi? Ui { get; set; }

    public RelayCommand MoveRightCommand { get; }
    public RelayCommand MoveLeftCommand { get; }
    public RelayCommand SwapCommand { get; }
    public RelayCommand ClearQueueCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand DismissResultCommand { get; }
    public RelayCommand RetryCommand { get; }
    public RelayCommand OpenResultCommand { get; }
    public RelayCommand ToggleQueueCollapsedCommand { get; }

    public IReadOnlyList<PolicyOption> PolicyOptions => Policies;

    // ------------------------------------------------------------------ 상태

    public QuickMovePhase Phase
    {
        get => _phase;
        private set
        {
            if (!Set(ref _phase, value)) return;
            Raise(nameof(IsIdleView));
            Raise(nameof(IsValidating));
            Raise(nameof(IsRunning));
            Raise(nameof(IsFinished));
            Raise(nameof(CanEditQueue));
            Raise(nameof(HeaderText));
            Raise(nameof(HeaderHint));
            RaiseCommands();
        }
    }

    public bool IsIdleView => _phase is QuickMovePhase.Idle or QuickMovePhase.Validating;
    public bool IsValidating => _phase == QuickMovePhase.Validating;
    public bool IsRunning => _phase == QuickMovePhase.Running;
    public bool IsFinished => _phase == QuickMovePhase.Finished;
    public bool CanEditQueue => _phase == QuickMovePhase.Idle || _phase == QuickMovePhase.Finished;

    public bool CanStart => _phase == QuickMovePhase.Idle && Queue.Count > 0 && _spaceWarning.Length == 0;

    public bool IsQueueCollapsed
    {
        get => Settings.QueueCollapsed;
        set
        {
            if (Settings.QueueCollapsed == value) return;
            Settings.QueueCollapsed = value;
            Raise();
            Raise(nameof(QueueBodyOpen));
            SaveSettings();
        }
    }

    private bool _isDockMode;
    private bool _dockQueueOpen;

    /// <summary>
    /// 같은 화면을 좁은 도크로 쓰는 중인가(화면이 정한다). 도크에서는 대기열 목록을 기본으로 접어 두고
    /// 접힘 상태도 탭과 따로 관리한다 — 도크에서 접어도 탭의 설정이 바뀌지 않게.
    /// </summary>
    public bool IsDockMode
    {
        get => _isDockMode;
        set
        {
            if (!Set(ref _isDockMode, value)) return;
            Raise(nameof(QueueBodyOpen));
        }
    }

    /// <summary>대기열 본문(목록)을 펼쳐 두었는가. 탭은 저장된 설정, 도크는 이번 실행 중의 선택.</summary>
    public bool QueueBodyOpen => _isDockMode ? _dockQueueOpen : !Settings.QueueCollapsed;

    public double QueueHeight
    {
        get => Settings.QueueHeight;
        set
        {
            if (Math.Abs(Settings.QueueHeight - value) < 0.5) return;
            Settings.QueueHeight = value;
            SaveSettings();
        }
    }

    /// <summary>분석 탭(폴더 / Treemap …) 오른쪽의 "빠른 이동 도크" 표시 여부. 좁은 화면 배치로 같은 화면을 옮겨 쓴다.</summary>
    public bool IsDockVisible
    {
        get => Settings.DockVisible;
        set
        {
            if (Settings.DockVisible == value) return;
            Settings.DockVisible = value;
            Raise();
            SaveSettings();
        }
    }

    public double DockWidth
    {
        get => Settings.DockWidth;
        set
        {
            if (Math.Abs(Settings.DockWidth - value) < 0.5) return;
            Settings.DockWidth = value;
            SaveSettings();
        }
    }

    /// <summary>
    /// 머리글 옆 한 줄 안내. 우선순위: 방금 일어난 일(Notice) &gt; 대기열이 비었을 때의 사용법.
    /// 대기열 본문을 접어 둔 동안에도 다음에 무엇을 하면 되는지 보이게 한다.
    /// </summary>
    public string HeaderHint => _notice.Length > 0
        ? _notice
        : Queue.Count == 0 && _phase == QuickMovePhase.Idle ? "출발지에서 옮길 항목을 고르고 →(도크에서는 ↓)를 누르세요. 놓는 즉시 옮기지 않고 대기열에 쌓입니다." : string.Empty;

    public bool ShowHidden
    {
        get => Settings.ShowHidden;
        set
        {
            if (Settings.ShowHidden == value) return;
            Settings.ShowHidden = value;
            Raise();
            SaveSettings();
            Left.Refresh();
            Right.Refresh();
        }
    }

    public PolicyOption SelectedPolicy
    {
        get => Policies.First(p => p.Policy == Settings.ConflictPolicy);
        set
        {
            if (value == null || Settings.ConflictPolicy == value.Policy) return;
            Settings.ConflictPolicy = value.Policy;
            Raise();
            SaveSettings();
        }
    }

    /// <summary>대기열 아래에 잠깐 보여 주는 안내(추가 결과, 차단 사유 등).</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (!Set(ref _notice, value)) return;
            Raise(nameof(HasNotice));
            Raise(nameof(HeaderHint));
        }
    }

    public bool HasNotice => _notice.Length > 0;

    public string QueueSummary { get => _queueSummary; private set { if (Set(ref _queueSummary, value)) Raise(nameof(HeaderText)); } }
    public string RouteHint { get => _routeHint; private set { if (Set(ref _routeHint, value)) Raise(nameof(HasRouteHint)); } }
    public bool HasRouteHint => _routeHint.Length > 0;
    public string SpaceWarning { get => _spaceWarning; private set { if (Set(ref _spaceWarning, value)) { Raise(nameof(HasSpaceWarning)); RaiseCommands(); } } }
    public bool HasSpaceWarning => _spaceWarning.Length > 0;
    public IReadOnlyList<SpaceLineViewModel> SpaceLines { get => _spaceLines; private set => Set(ref _spaceLines, value); }

    /// <summary>대기열 머리글: 대기 중 "이동 예정 3개 · 8.72 GB" / 진행 중 "이동 중 43%" / 끝난 뒤 결과 제목.</summary>
    public string HeaderText => _phase switch
    {
        QuickMovePhase.Running => $"이동 중 · {_progressPercent}",
        QuickMovePhase.Finished => _resultTitle,
        _ => _queueSummary,
    };

    // ------------------------------------------------------------------ 진행

    public bool IsPaused { get => _isPaused; private set { if (Set(ref _isPaused, value)) Raise(nameof(PauseText)); } }
    public string PauseText => _isPaused ? "계속" : "일시정지";

    public double ProgressFraction { get => _progressFraction; private set => Set(ref _progressFraction, value); }
    public string ProgressPercent { get => _progressPercent; private set { if (Set(ref _progressPercent, value)) Raise(nameof(HeaderText)); } }
    public string ProgressFiles { get => _progressFiles; private set => Set(ref _progressFiles, value); }
    public string ProgressItem { get => _progressItem; private set => Set(ref _progressItem, value); }
    public string ProgressBytes { get => _progressBytes; private set => Set(ref _progressBytes, value); }
    public string CurrentName { get => _currentName; private set => Set(ref _currentName, value); }
    public string CurrentRoute { get => _currentRoute; private set => Set(ref _currentRoute, value); }
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }
    public string RemainingText { get => _remainingText; private set => Set(ref _remainingText, value); }

    // ------------------------------------------------------------------ 결과

    public string ResultTitle { get => _resultTitle; private set { if (Set(ref _resultTitle, value)) Raise(nameof(HeaderText)); } }
    public string ResultDetail { get => _resultDetail; private set => Set(ref _resultDetail, value); }
    public bool ResultHasProblem { get => _resultHasProblem; private set => Set(ref _resultHasProblem, value); }
    public bool HasFailures => _allResults.Any(r => r.IsFailed);
    public bool CanRetry => _summary is { } s && (s.DriveLost || s.NotProcessed > 0 || s.Failed > 0) && !s.WasCancelled;
    public bool IsDriveLost => _summary?.DriveLost == true;

    public bool ShowFailedOnly
    {
        get => _showFailedOnly;
        set { if (Set(ref _showFailedOnly, value)) Raise(nameof(Results)); }
    }

    public IReadOnlyList<ResultRowViewModel> Results
        => _showFailedOnly ? _allResults.Where(r => r.IsFailed).ToList() : _allResults;

    // ------------------------------------------------------------------ 초기화

    private async Task InitializePanesAsync()
    {
        string? current = _currentFolder();
        string? left = await Task.Run(() => FirstExisting(Settings.LeftPath, current, @"C:\"));
        string? right = await Task.Run(() => FirstExisting(Settings.RightPath, left, current, @"C:\"));

        if (left != null) await Left.NavigateAsync(left, record: false);
        if (right != null) await Right.NavigateAsync(right, record: false);
    }

    private static string? FirstExisting(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(c) && PathUtil.TryNormalize(c, out string n) && Directory.Exists(n)) return n;
            }
            catch (Exception) { /* 다음 후보 */ }
        }
        return null;
    }

    /// <summary>다른 탭(큰 파일 / 정리 추천 / 폴더)에서 "빠른 이동으로 보내기". 출발지에 그 항목들을 선택한 채로 열어 준다.</summary>
    public async Task SendToSourceAsync(IReadOnlyList<string> paths)
    {
        var valid = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (valid.Count == 0) return;

        string? parent = PathUtil.Parent(valid[0]);
        if (parent == null) return;

        if (!await Left.NavigateAsync(parent, record: true, select: valid)) return;

        // 한 패널은 폴더 하나만 보여 준다. 다른 폴더에 있는 항목(큰 파일 탭 등)은 선택할 수 없으므로 알려 준다.
        int inside = valid.Count(p => PathUtil.Equal(PathUtil.Parent(p) ?? string.Empty, parent));
        ShowNotice(inside == valid.Count
            ? $"{inside:N0}개 항목을 출발지에서 선택했습니다. 목적지를 고른 뒤 옮기기 버튼을 누르세요."
            : $"{inside:N0}개를 출발지에서 선택했습니다. 나머지 {valid.Count - inside:N0}개는 다른 폴더에 있어 선택하지 못했습니다 — '대기열에 추가'를 쓰면 한 번에 넣을 수 있습니다.");
    }

    /// <summary>
    /// "대기열에 추가": 폴더를 열지 않고, 고른 항목을 지금 목적지(오른쪽 패널) 폴더로 옮길 대기열에 바로 넣는다.
    /// 목적지를 한 번 정해 두면 분석 탭에서 여러 항목을 우클릭 한 번씩으로 담을 수 있다.
    /// </summary>
    public async Task AddPathsToQueueAsync(IReadOnlyList<string> paths)
    {
        if (!Right.HasPath)
        {
            ShowNotice("먼저 목적지 폴더를 정하세요 (우클릭 → 이동 (우)).");
            return;
        }

        var entries = await Task.Run(() => paths.Select(DirectoryBrowser.Describe).OfType<FsEntry>().ToList());
        if (entries.Count == 0)
        {
            ShowNotice("선택한 항목을 찾을 수 없습니다.");
            return;
        }
        AddToQueue(entries, Right.CurrentPath);
    }

    /// <summary>"이동 (우)": 그 폴더를 목적지(오른쪽) 패널로 연다. 분석 탭에서 찾은 위치까지 다시 타고 들어가지 않아도 된다.</summary>
    public async Task SendToDestinationAsync(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        if (await Right.NavigateAsync(folder))
            ShowNotice($"목적지를 열었습니다: {folder}  ·  출발지에서 옮길 항목을 골라 → 를 누르세요.");
    }

    /// <summary>선택한 항목이 없을 때: 폴더 자체를 출발지(왼쪽) 패널로 연다.</summary>
    public async Task OpenSourceFolderAsync(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        if (await Left.NavigateAsync(folder))
            ShowNotice($"출발지를 열었습니다: {folder}  ·  옮길 항목을 선택하세요.");
    }

    // ------------------------------------------------------------------ 빠른 위치 / 즐겨찾기 / 최근 위치

    private void BuildLocations()
    {
        RebuildLocations();
        _ = Task.Run(() =>
        {
            var list = new List<DriveEntry>();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType == DriveType.CDRom || !d.IsReady) continue;
                        list.Add(new DriveEntry(d.Name.TrimEnd('\\'), d.Name));
                    }
                    catch (Exception) { /* 준비되지 않은 드라이브는 건너뜀 */ }
                }
            }
            catch (Exception) { }
            return list;
        }).ContinueWith(t =>
        {
            _drives = t.Result;
            RebuildLocations();
        }, UiScheduler);
    }

    private void RebuildLocations()
    {
        var list = new List<QuickLocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fav in Settings.Favorites)
            if (seen.Add(fav)) list.Add(new QuickLocation { Name = PathUtil.NameOf(fav), Path = fav, IsFavorite = true });

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var standard = new (string Name, string Path)[]
        {
            ("바탕화면", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ("다운로드", System.IO.Path.Combine(home, "Downloads")),
            ("문서", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
        };
        foreach (var (name, path) in standard)
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && seen.Add(path))
                list.Add(new QuickLocation { Name = name, Path = path });

        foreach (var d in _drives)
            if (seen.Add(d.Path)) list.Add(new QuickLocation { Name = d.Name, Path = d.Path });

        QuickLocations.Clear();
        foreach (var l in list) QuickLocations.Add(l);
        _ = CheckLocationsAsync();
    }

    // ------------------------------------------------------------------ 없어진 즐겨찾기 정리
    // 누를 때까지 모르고 있다가 알리면 늦다. 확인은 (1) 이 화면을 열 때, (2) 무언가 지우거나 옮긴 직후, (3) 창으로 돌아올 때 한다.
    // 폴더가 정말 없으면 그 자리에서 즐겨찾기를 빼 두고, 이 화면이 보일 때 어떤 경로가 빠졌는지 한 번 알린다.

    private readonly List<string> _removedFavorites = new();
    private bool _pageVisible;
    private bool _showingRemoved;

    /// <summary>빠른 이동 화면(탭 또는 도크)이 지금 보이는가. 보이게 되는 순간 즐겨찾기를 확인하고 밀린 알림을 띄운다.</summary>
    public bool IsPageVisible
    {
        get => _pageVisible;
        set
        {
            if (_pageVisible == value) return;
            _pageVisible = value;
            if (!value) return;
            _ = CheckLocationsAsync();
            ShowRemovedFavorites();
        }
    }

    /// <summary>
    /// 즐겨찾기 폴더를 백그라운드에서 확인한다(네트워크 경로여도 화면이 멈추지 않게).
    /// 드라이브(또는 공유)는 있는데 폴더만 없으면 지워지거나 옮겨진 것이므로 즐겨찾기에서 뺀다.
    /// 드라이브 자체가 없으면(USB 를 뽑음, 네트워크 끊김) 다시 연결할 수 있으니 지우지 않고 흐리게만 표시한다.
    /// </summary>
    private async Task CheckLocationsAsync()
    {
        var paths = Settings.Favorites.ToList();
        if (paths.Count == 0) return;

        var states = await Task.Run(() => paths.Select(p =>
        {
            if (Directory.Exists(p)) return (Path: p, Gone: false, Offline: false);
            string? root = System.IO.Path.GetPathRoot(p);
            bool rootOk = !string.IsNullOrEmpty(root) && Directory.Exists(root);
            return (Path: p, Gone: rootOk, Offline: !rootOk);
        }).ToList());

        bool removed = false;
        foreach (var s in states.Where(s => s.Gone))
        {
            if (Settings.Favorites.RemoveAll(f => PathUtil.Equal(f, s.Path)) == 0) continue;   // 그 사이 다른 확인이 이미 뺐다
            _removedFavorites.Add(s.Path);
            removed = true;
        }

        if (removed)
        {
            SaveSettings();
            RebuildLocations();   // 안에서 다시 확인하지만 이번에는 뺄 것이 없다
            Left.RaiseFavoriteChanged();
            Right.RaiseFavoriteChanged();
            ShowRemovedFavorites();
        }

        var offline = states.Where(s => s.Offline).Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var l in QuickLocations.Where(l => l.IsFavorite)) l.IsMissing = offline.Contains(l.Path);
    }

    /// <summary>
    /// 빠진 즐겨찾기를 알린다(확인 버튼만). 화면이 보이지 않으면 쌓아 두었다가 보일 때 한 번에 알린다.
    /// 탭을 바꾸는 중에 곧바로 대화 상자를 열지 않도록 한 박자 늦춘다.
    /// </summary>
    private void ShowRemovedFavorites()
    {
        if (!_pageVisible || _showingRemoved || _removedFavorites.Count == 0 || Ui == null) return;
        _showingRemoved = true;

        PostToUi(() =>
        {
            try
            {
                while (_removedFavorites.Count > 0 && Ui != null)
                {
                    var paths = _removedFavorites.ToList();
                    _removedFavorites.Clear();
                    Ui.Choose("없어진 즐겨찾기를 목록에서 뺐습니다",
                        $"즐겨찾기 폴더 {paths.Count:N0}개가 삭제되었거나 옮겨져서 더는 없습니다.\n\n" + Bullets(paths),
                        0, "확인");
                }
            }
            finally { _showingRemoved = false; }
        });
    }

    /// <summary>즐겨찾기를 지금 확인한다(다른 탭의 즐겨찾기 메뉴를 열 때). 없어진 폴더는 여기서 빠지고, 알림은 이 화면이 보일 때 뜬다.</summary>
    public void CheckFavorites() => _ = CheckLocationsAsync();

    /// <summary>화면 스레드에서 실행한다(파일 감시 같은 백그라운드 스레드에서 부를 때).</summary>
    internal void PostToUi(Action action)
    {
        if (_ui != null) _ui.Post(_ => action(), null);
        else Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.None, UiScheduler);
    }

    /// <summary>
    /// 두 패널을 실제 상태로 다시 읽는다. 다른 탭에서 지웠거나 탐색기에서 바꾼 결과를 바로 보이게 할 때 쓴다.
    /// </summary>
    public void RefreshPanes(bool clearMeasureCache = true)
    {
        if (Phase == QuickMovePhase.Running) return;   // 실행이 끝나면 어차피 다시 읽는다
        if (clearMeasureCache) ClearMeasureCache();    // 지운 뒤에는 폴더 크기가 달라졌다. 창을 다시 눌렀을 뿐이면 캐시를 유지한다
        Left.Refresh(clearCache: false);
        Right.Refresh(clearCache: false);
        _ = CheckLocationsAsync();
    }

    /// <summary>이동으로 원래 자리에서 사라진 경로들(이동을 마친 뒤). 스캔 결과(폴더/트리맵/큰 파일 탭)에서 빼는 데 쓴다.</summary>
    public event Action<IReadOnlyList<string>>? SourcesRelocated;

    public void ToggleFavorite(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        bool added = Settings.ToggleFavorite(path);
        SaveSettings();
        RebuildLocations();
        Left.RaiseFavoriteChanged();
        Right.RaiseFavoriteChanged();
        ShowNotice(added ? $"즐겨찾기에 추가했습니다: {path}" : $"즐겨찾기에서 뺐습니다: {path}");
    }

    /// <summary>패널이 새 폴더를 열었다. 최근 위치와 마지막 위치를 기록한다.</summary>
    public void OnPaneNavigated(QuickMovePaneViewModel pane)
    {
        Settings.AddRecent(pane.CurrentPath);
        if (ReferenceEquals(pane, Left)) Settings.LeftPath = pane.CurrentPath;
        else Settings.RightPath = pane.CurrentPath;
        SaveSettings();
        RaiseCommands();
        RecomputeSpace();
        _ = CheckLocationsAsync();   // 새로 읽을 때마다(감시 / 새로고침 포함) 사라진 즐겨찾기를 다시 확인한다
    }

    /// <summary>패널이 같은 폴더를 다시 읽었다(위치는 그대로). 최근 위치 / 마지막 위치는 건드리지 않는다.</summary>
    public void OnPaneRefreshed()
    {
        RaiseCommands();
        RecomputeSpace();
        _ = CheckLocationsAsync();
    }

    public void OnSelectionChanged() => RaiseCommands();

    public void OnPaneVolumeChanged() => ApplyProjections(_lastSpace);

    public void SaveSettings()
    {
        Settings.LeftSortKey = Left.SortKey;
        Settings.LeftSortAscending = Left.SortAscending;
        Settings.RightSortKey = Right.SortKey;
        Settings.RightSortAscending = Right.SortAscending;
        Settings.Save();
    }

    // ------------------------------------------------------------------ 폴더 크기 캐시(패널 선택 요약 / 대기열이 함께 쓴다)

    public bool TryGetCachedMeasure(string path, out TreeMeasure.Result result) => _measureCache.TryGetValue(path, out result);
    public void CacheMeasure(string path, TreeMeasure.Result result) => _measureCache[path] = result;
    public void ClearMeasureCache() => _measureCache.Clear();

    // ------------------------------------------------------------------ 대기열

    private void SwapPanes()
    {
        string l = Left.CurrentPath, r = Right.CurrentPath;
        // 선택 상태는 폴더를 새로 열면서 함께 초기화된다.
        Left.NavigateTo(r);
        Right.NavigateTo(l);
    }

    /// <summary>선택한 항목을 <paramref name="destDirectory"/> 로 옮길 대기열에 넣는다. 이 시점에는 아무것도 옮기지 않는다.</summary>
    public void AddToQueue(IReadOnlyList<FsEntry> entries, string destDirectory)
    {
        if (!CanEditQueue)
        {
            ShowNotice("이동하는 동안에는 대기열을 바꿀 수 없습니다.");
            return;
        }
        if (Phase == QuickMovePhase.Finished) DismissResult();

        if (entries.Count == 0)
        {
            ShowNotice("먼저 이동할 파일이나 폴더를 선택하세요.");
            return;
        }
        if (string.IsNullOrEmpty(destDirectory))
        {
            ShowNotice("목적지 폴더를 먼저 여세요.");
            return;
        }

        int added = 0, alreadyThere = 0;
        var problems = new List<string>();

        foreach (var e in entries)
        {
            if (e.IsProtected)
            {
                problems.Add($"{e.Name}: 보호된 시스템 항목이라 이동할 수 없습니다.");
                continue;
            }
            if (!MoveValidator.CheckAdd(e.FullPath, e.IsDirectory, destDirectory, out string? error))
            {
                problems.Add($"{e.Name}: {error}");
                continue;
            }

            var same = Queue.FirstOrDefault(q => PathUtil.Equal(q.SourcePath, e.FullPath));
            if (same != null)
            {
                if (PathUtil.Equal(same.DestDirectory, destDirectory)) { alreadyThere++; continue; }
                same.SetDestination(destDirectory);   // 같은 항목을 다른 목적지로 보내면 목적지만 바꾼다
                added++;
                continue;
            }

            if (Queue.Any(q => q.IsDirectory && PathUtil.IsUnder(q.SourcePath, e.FullPath)))
            {
                problems.Add($"{e.Name}: 이미 대기열에 있는 폴더 안의 항목이라 따로 넣지 않았습니다.");
                continue;
            }

            // 새 항목이 대기열의 항목들을 품고 있으면(폴더를 나중에 골랐다면) 안쪽 항목은 빼서 중복 이동을 막는다.
            foreach (var inner in Queue.Where(q => PathUtil.IsUnder(e.FullPath, q.SourcePath)).ToList())
            {
                inner.Cancel();
                Queue.Remove(inner);
            }

            Queue.Add(new QueueItemViewModel(this, e, destDirectory));
            added++;
        }

        var parts = new List<string>();
        if (added > 0) parts.Add($"{added:N0}개를 이동 대기열에 넣었습니다. [이동 시작]을 누르면 옮겨집니다.");
        if (alreadyThere > 0) parts.Add($"{alreadyThere:N0}개는 이미 대기열에 있습니다.");
        if (problems.Count > 0)
            parts.Add(string.Join("  ", problems.Take(2)) + (problems.Count > 2 ? $"  … 외 {problems.Count - 2:N0}건" : string.Empty));
        ShowNotice(string.Join("  ", parts));
    }

    public void RemoveItem(QueueItemViewModel item)
    {
        if (!CanEditQueue) return;
        item.Cancel();
        Queue.Remove(item);
    }

    public void RemoveItems(IEnumerable<QueueItemViewModel> items)
    {
        if (!CanEditQueue) return;
        foreach (var i in items.ToList())
        {
            i.Cancel();
            Queue.Remove(i);
        }
    }

    private void ClearQueue()
    {
        foreach (var i in Queue) i.Cancel();
        if (Phase == QuickMovePhase.Finished) DismissResult();
        Queue.Clear();
    }

    private void ShowNotice(string text)
    {
        Notice = text;
        int version = ++_noticeVersion;
        _ = Task.Delay(9000).ContinueWith(_ =>
        {
            if (version == _noticeVersion) Notice = string.Empty;
        }, UiScheduler);
    }

    public void OnQueueSizeChanged() => OnQueueChanged();

    private void OnQueueChanged()
    {
        long total = Queue.Sum(q => q.Size);
        int unmeasured = Queue.Count(q => !q.IsMeasured);

        QueueSummary = Queue.Count == 0
            ? "이동 예정 없음"
            : $"이동 예정 {Queue.Count:N0}개 · {SizeFormatter.Format(total)}" + (unmeasured > 0 ? "  (폴더 크기 계산 중…)" : string.Empty);

        RouteHint = BuildRouteHint();
        Raise(nameof(HeaderHint));
        RecomputeSpace();
        RaiseCommands();
    }

    private string BuildRouteHint()
    {
        if (Queue.Count == 0) return string.Empty;

        var cross = Queue.Where(q => !q.IsSameVolume).ToList();
        int same = Queue.Count - cross.Count;
        var lines = new List<string>();

        if (same > 0)
            lines.Add("⚡ 빠른 이동 — 같은 드라이브 내 이동입니다." + (cross.Count > 0 ? $" ({same:N0}개)" : string.Empty));

        if (cross.Count > 0)
        {
            var pairs = cross
                .Select(q => $"{PathUtil.DriveName(q.SourcePath)} → {PathUtil.DriveName(q.DestDirectory)}")
                .Distinct().Take(3);
            lines.Add($"{string.Join(", ", pairs)} — 파일 데이터를 복사한 후 원본 파일이 제거됩니다. 총 이동량 {SizeFormatter.Format(cross.Sum(q => q.Size))}");
        }
        return string.Join("\n", lines);
    }

    // ------------------------------------------------------------------ 공간

    private async void RecomputeSpace()
    {
        int version = ++_spaceVersion;
        var requests = Queue.Select(q => q.Request).ToList();

        if (requests.Count == 0)
        {
            _lastSpace = Array.Empty<SpaceRow>();
            SpaceLines = Array.Empty<SpaceLineViewModel>();
            SpaceWarning = string.Empty;
            ApplyProjections(_lastSpace);
            return;
        }

        IReadOnlyList<SpaceRow> rows;
        try { rows = await Task.Run(() => MoveValidator.CheckSpace(requests)); }
        catch (Exception) { return; }
        if (version != _spaceVersion) return;

        _lastSpace = rows;
        var lines = new List<SpaceLineViewModel>();
        string warning = string.Empty;

        foreach (var r in rows)
        {
            if (r.Required <= 0)
            {
                lines.Add(new SpaceLineViewModel { Drive = r.Drive, Text = $"{r.Drive} 같은 드라이브 이동 — 여유 공간을 쓰지 않습니다" });
                continue;
            }

            if (!r.FreeKnown)
            {
                lines.Add(new SpaceLineViewModel { Drive = r.Drive, Text = $"{r.Drive} 여유 공간을 확인할 수 없습니다 (필요 {SizeFormatter.Format(r.Required)})" });
                continue;
            }

            if (r.Enough)
            {
                lines.Add(new SpaceLineViewModel
                {
                    Drive = r.Drive,
                    Text = $"{r.Drive} 현재 여유 {SizeFormatter.Format(r.Free)} · 이동 예정 {SizeFormatter.Format(r.Required)} · 이동 후 {SizeFormatter.Format(r.After)}",
                });
            }
            else
            {
                string text = $"⚠ 공간 부족 ({r.Drive}) — 필요 {SizeFormatter.Format(r.Required)} / 사용 가능 {SizeFormatter.Format(r.Free)}";
                lines.Add(new SpaceLineViewModel { Drive = r.Drive, Text = text, IsProblem = true });
                if (warning.Length == 0) warning = text;
            }
        }

        SpaceLines = lines;
        SpaceWarning = warning;
        ApplyProjections(rows);
    }

    /// <summary>대기열이 향하는 드라이브의 패널 아래에 "이동 후 여유"를 보여 준다.</summary>
    private void ApplyProjections(IReadOnlyList<SpaceRow> rows)
    {
        foreach (var pane in new[] { Left, Right })
        {
            var row = rows.FirstOrDefault(r => string.Equals(r.Drive, pane.DriveName, StringComparison.OrdinalIgnoreCase));
            if (row == null || row.Required <= 0 || !row.FreeKnown)
            {
                pane.ProjectionText = string.Empty;
                pane.ProjectionWarn = false;
                continue;
            }
            pane.ProjectionText = row.Enough
                ? $"이동 예정 {SizeFormatter.Format(row.Required)} → 이동 후 {SizeFormatter.Format(row.After)} 사용 가능"
                : $"⚠ 공간 부족: 이동 예정 {SizeFormatter.Format(row.Required)} > 사용 가능 {SizeFormatter.Format(row.Free)}";
            pane.ProjectionWarn = !row.Enough;
        }
    }

    // ------------------------------------------------------------------ 이동 시작

    private async Task StartAsync()
    {
        var ui = Ui;
        if (ui == null || !CanStart) return;

        Phase = QuickMovePhase.Validating;
        IsQueueCollapsed = false;
        ShowNotice("이동할 수 있는지 확인하는 중…");

        try
        {
            // 폴더 크기가 아직 계산 중이면 기다린다(공간 검사와 진행률에 필요하다).
            var deadline = Stopwatch.StartNew();
            while (Queue.Any(q => !q.IsMeasured) && deadline.Elapsed < TimeSpan.FromMinutes(10))
                await Task.Delay(100);

            var items = Queue.ToList();
            var requests = items.Select(i => i.Request).ToList();
            var issues = await Task.Run(() => MoveValidator.Validate(requests));

            var errors = issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
            var warnings = issues.Where(i => i.Severity == IssueSeverity.Warning).ToList();
            var blocked = new Dictionary<MoveRequest, string>();
            foreach (var e in errors.Where(e => e.Request != null))
                blocked.TryAdd(e.Request!, e.Message);

            foreach (var item in items)
            {
                if (blocked.TryGetValue(item.Request, out string? why))
                {
                    item.State = QueueItemState.Failed;
                    item.StateText = "이동할 수 없음: " + why;
                }
                else if (item.State == QueueItemState.Failed) item.ResetState();
            }

            if (errors.Count > 0)
            {
                string list = Bullets(errors.Select(e => e.Request != null ? $"{e.Request.Name} — {e.Message}" : e.Message));
                if (blocked.Count >= items.Count)
                {
                    ui.Choose("이동할 수 없습니다", "다음 문제 때문에 이동을 시작하지 않았습니다.\n\n" + list, 0, "확인");
                    return;
                }
                int c = ui.Choose("일부 항목은 이동할 수 없습니다",
                    list + $"\n\n이 항목들을 뺀 나머지 {items.Count - blocked.Count:N0}개만 이동할까요?", 1, "나머지만 이동", "돌아가기");
                if (c != 0) return;
            }

            if (warnings.Count > 0)
            {
                string list = Bullets(warnings.Select(w => w.Request != null ? $"{w.Request.Name} — {w.Message}" : w.Message));
                int c = ui.Choose("⚠ 고위험 항목이 포함되어 있습니다",
                    list + "\n\n옮기면 프로그램이나 Windows 가 정상 동작하지 않을 수 있습니다. 계속할까요?", 1, "그래도 이동", "돌아가기");
                if (c != 0) return;
            }

            var runItems = items.Where(i => !blocked.ContainsKey(i.Request)).ToList();
            var space = await Task.Run(() => MoveValidator.CheckSpace(runItems.Select(i => i.Request)));
            var lack = space.FirstOrDefault(s => !s.Enough);
            if (lack != null)
            {
                ui.Choose("⚠ 공간 부족",
                    $"{lack.Drive} 에 필요한 공간: {SizeFormatter.Format(lack.Required)}\n사용 가능: {SizeFormatter.Format(lack.Free)}\n\n대상 드라이브에 공간을 확보한 뒤 다시 시도하세요.", 0, "확인");
                return;
            }

            _preFailed = blocked
                .Select(kv => new MoveItemResult { Request = kv.Key, Status = MoveStatus.Failed, Error = kv.Value, FinalPath = kv.Key.TargetPath })
                .ToList();

            await RunAsync(runItems, ui);
        }
        catch (Exception ex)
        {
            ShowNotice("이동을 시작하지 못했습니다: " + ex.Message);
        }
        finally
        {
            if (Phase == QuickMovePhase.Validating) Phase = QuickMovePhase.Idle;
        }
    }

    private static string Bullets(IEnumerable<string> lines)
    {
        var all = lines.ToList();
        var shown = all.Take(10).Select(l => "• " + l).ToList();
        if (all.Count > 10) shown.Add($"… 외 {all.Count - 10:N0}건");
        return string.Join("\n", shown);
    }

    private async Task RunAsync(List<QueueItemViewModel> runItems, IQuickMoveUi ui)
    {
        _runItems = runItems;
        foreach (var i in runItems) { i.State = QueueItemState.Waiting; i.StateText = string.Empty; }

        var options = new MoveOptions
        {
            Policy = Settings.ConflictPolicy,
            ResolveConflict = info => WaitForUser(ui, info),
            Progress = new Progress<MoveProgress>(OnProgress),
        };
        _engine = new MoveEngine(options);
        _cts = new CancellationTokenSource();

        var token = _cts.Token;
        var engine = _engine;
        var requests = runItems.Select(i => i.Request).ToList();

        ProgressFraction = 0;
        ProgressPercent = "0%";
        ProgressFiles = string.Empty;
        ProgressItem = string.Empty;
        ProgressBytes = $"0 B / {SizeFormatter.Format(requests.Sum(r => r.Size))}";
        CurrentName = string.Empty;
        CurrentRoute = string.Empty;
        SpeedText = string.Empty;
        RemainingText = "계산 중…";
        IsPaused = false;
        _samples.Clear();
        Interlocked.Exchange(ref _waitedMs, 0);
        _runClock = Stopwatch.StartNew();
        Notice = string.Empty;
        Left.SuspendWatching();
        Right.SuspendWatching();
        Phase = QuickMovePhase.Running;

        MoveSummary summary;
        try
        {
            summary = await Task.Run(() => engine.Run(requests, token));
        }
        catch (Exception ex)
        {
            summary = new MoveSummary
            {
                Results = requests.Select(r => new MoveItemResult
                {
                    Request = r, Status = MoveStatus.Failed, Error = ex.Message, FinalPath = r.TargetPath,
                }).ToList(),
                Elapsed = _runClock.Elapsed,
            };
        }

        Finish(summary);
    }

    private void OnProgress(MoveProgress p)
    {
        if (Phase != QuickMovePhase.Running) return;

        ProgressFraction = p.Fraction;
        ProgressPercent = p.Fraction.ToString("P0");
        ProgressFiles = $"{p.FilesDone:N0} / {p.FilesTotal:N0}개 파일";
        ProgressItem = $"항목 {Math.Min(p.ItemIndex, p.ItemCount):N0} / {p.ItemCount:N0}";
        ProgressBytes = $"{SizeFormatter.Format(p.BytesDone)} / {SizeFormatter.Format(p.BytesTotal)}";
        CurrentName = p.CurrentName;
        CurrentRoute = $"{p.CurrentFrom}\n→ {p.CurrentTo}";

        for (int i = 0; i < _runItems.Count; i++)
        {
            var item = _runItems[i];
            if (i < p.ItemIndex - 1) { if (item.State is QueueItemState.Waiting or QueueItemState.Moving) item.State = QueueItemState.Done; }
            else if (i == p.ItemIndex - 1) item.State = QueueItemState.Moving;
        }

        UpdateSpeed(p);
    }

    /// <summary>최근 3초 구간의 처리량으로 속도와 남은 시간을 계산한다. 초기 구간은 정확하지 않아서 "계산 중…".</summary>
    private void UpdateSpeed(MoveProgress p)
    {
        if (IsPaused) { SpeedText = "일시정지됨"; RemainingText = string.Empty; return; }

        long now = _runClock.ElapsedMilliseconds - Interlocked.Read(ref _waitedMs);
        _samples.Enqueue((now, p.BytesDone));
        while (_samples.Count > 2 && now - _samples.Peek().Ms > 3000) _samples.Dequeue();

        var first = _samples.Peek();
        long dt = now - first.Ms;
        if (dt < 500 || p.BytesDone <= first.Bytes)
        {
            if (now > 2000) SpeedText = string.Empty;
            RemainingText = "계산 중…";
            return;
        }

        double perSecond = (p.BytesDone - first.Bytes) * 1000d / dt;
        SpeedText = SizeFormatter.Format((long)perSecond) + "/s";

        RemainingText = now < 2000 || perSecond <= 0
            ? "계산 중…"
            : "약 " + FormatDuration(TimeSpan.FromSeconds(Math.Max(0, (p.BytesTotal - p.BytesDone) / perSecond))) + " 남음";
    }

    /// <summary>
    /// 충돌 대화 상자는 엔진 스레드를 멈춰 세운다. 사용자가 고민한 시간은 "이동에 걸린 시간"이 아니므로 따로 재서 뺀다.
    /// 그렇지 않으면 대화 상자를 열어 둔 만큼 속도가 떨어지고 남은 시간이 부풀려진다.
    /// </summary>
    private ConflictDecision WaitForUser(IQuickMoveUi ui, ConflictInfo info)
    {
        _ui?.Post(_ =>
        {
            SpeedText = "확인 대기 중";
            RemainingText = string.Empty;
        }, null);

        long start = Stopwatch.GetTimestamp();
        try { return ui.ResolveConflict(info); }
        finally { Interlocked.Add(ref _waitedMs, (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
    }

    public static string FormatDuration(TimeSpan t)
    {
        if (t.TotalSeconds < 1) return "1초 미만";
        if (t.TotalMinutes < 1) return $"{(int)Math.Ceiling(t.TotalSeconds)}초";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}분 {t.Seconds}초";
        return $"{(int)t.TotalHours}시간 {t.Minutes}분";
    }

    // ------------------------------------------------------------------ 일시정지 / 취소

    private void TogglePause()
    {
        if (_engine == null || Phase != QuickMovePhase.Running) return;
        if (IsPaused) { _engine.Resume(); IsPaused = false; }
        else { _engine.Pause(); IsPaused = true; SpeedText = "일시정지됨"; RemainingText = string.Empty; }
    }

    private void RequestCancel()
    {
        if (Phase != QuickMovePhase.Running || Ui == null) return;

        // 이미 옮긴 것은 되돌리지 않는다. 그 점을 확인받는다. 기본 선택은 "계속 이동".
        int c = Ui.Choose("이동을 취소하시겠습니까?",
            "이미 이동이 완료된 항목은\n원래 위치로 자동 복원되지 않습니다.", 0, "계속 이동", "이동 취소");
        if (c != 1) return;

        _cts?.Cancel();
        if (_engine?.IsPaused == true) _engine.Resume();
    }

    // ------------------------------------------------------------------ 결과

    private void Finish(MoveSummary summary)
    {
        _summary = summary;

        var rows = new List<ResultRowViewModel>();
        for (int i = 0; i < _runItems.Count && i < summary.Results.Count; i++)
        {
            var item = _runItems[i];
            var res = summary.Results[i];
            switch (res.Status)
            {
                case MoveStatus.Moved:
                    item.State = QueueItemState.Done;
                    item.StateText = res.Note ?? string.Empty;
                    break;
                case MoveStatus.Skipped:
                    item.State = QueueItemState.Skipped;
                    item.StateText = res.Error ?? "건너뜀";
                    break;
                case MoveStatus.Failed:
                    item.State = QueueItemState.Failed;
                    item.StateText = res.Error ?? "실패";
                    break;
                default:
                    item.State = QueueItemState.Waiting;
                    item.StateText = res.Error ?? string.Empty;
                    break;
            }
            rows.Add(ToRow(res));
        }
        foreach (var pre in _preFailed) rows.Add(ToRow(pre));
        _preFailed = new List<MoveItemResult>();

        _allResults = rows
            .OrderBy(r => r.Status switch { MoveStatus.Failed => 0, MoveStatus.NotProcessed => 1, MoveStatus.Skipped => 2, _ => 3 })
            .ToList();
        _showFailedOnly = false;

        int failed = rows.Count(r => r.Status == MoveStatus.Failed);   // 검사에서 미리 걸러낸 항목 포함
        int moved = summary.Moved;

        if (summary.DriveLost)
        {
            ResultTitle = "⚠ 대상 드라이브 연결이 끊어졌습니다.";
            ResultDetail = $"현재 작업을 중단했습니다. 완료 {moved:N0}개 · 처리하지 못함 {summary.NotProcessed + summary.Failed:N0}개";
            ResultHasProblem = true;
        }
        else if (summary.WasCancelled)
        {
            ResultTitle = "이동을 취소했습니다.";
            ResultDetail = $"완료 {moved:N0}개 · 처리하지 않음 {summary.NotProcessed:N0}개 — 이미 옮긴 항목은 원래 위치로 복원되지 않았습니다.";
            ResultHasProblem = true;
        }
        else if (failed > 0)
        {
            ResultTitle = "⚠ 일부 항목을 이동하지 못했습니다.";
            ResultDetail = $"성공 {moved:N0} · 실패 {failed:N0}" + (summary.Skipped > 0 ? $" · 건너뜀 {summary.Skipped:N0}" : string.Empty);
            ResultHasProblem = true;
        }
        else
        {
            ResultTitle = "✓ 이동 완료";
            ResultDetail = $"{summary.FilesMoved:N0}개 파일 · {SizeFormatter.Format(summary.BytesMoved)} · {FormatDuration(ActiveElapsed(summary))}"
                           + (summary.Skipped > 0 ? $" · 건너뜀 {summary.Skipped:N0}개" : string.Empty);
            ResultHasProblem = false;
        }

        Raise(nameof(Results));
        Raise(nameof(HasFailures));
        Raise(nameof(CanRetry));
        Raise(nameof(IsDriveLost));
        Raise(nameof(ShowFailedOnly));

        Phase = QuickMovePhase.Finished;

        // 옮긴 결과가 패널에 바로 보이게 한다. 폴더 크기 캐시는 더 이상 맞지 않는다.
        ClearMeasureCache();
        Left.Refresh();
        Right.Refresh();
        Left.ResumeWatching();
        Right.ResumeWatching();
        _ = Left.RefreshVolumeInfoAsync();
        _ = Right.RefreshVolumeInfoAsync();
        RecomputeSpace();
        _ = CheckLocationsAsync();

        // 옮겨진 항목은 스캔 결과에도 남아 있으면 안 된다(폴더/트리맵/큰 파일/정리 추천 탭이 그대로 보여 준다).
        var relocated = summary.Results.Where(r => r.Status == MoveStatus.Moved).Select(r => r.Request.SourcePath).ToList();
        if (relocated.Count > 0) SourcesRelocated?.Invoke(relocated);
    }

    private TimeSpan ActiveElapsed(MoveSummary summary)
    {
        var active = summary.Elapsed - TimeSpan.FromMilliseconds(Interlocked.Read(ref _waitedMs));
        return active < TimeSpan.Zero ? TimeSpan.Zero : active;
    }

    private static ResultRowViewModel ToRow(MoveItemResult r) => new()
    {
        Name = r.Request.Name,
        Status = r.Status,
        Icon = r.Status switch { MoveStatus.Moved => "✓", MoveStatus.Skipped => "↷", MoveStatus.Failed => "⚠", _ => "–" },
        Text = r.Status switch
        {
            MoveStatus.Moved => r.Note ?? "이동함",
            MoveStatus.Skipped => r.Error ?? "건너뜀",
            MoveStatus.Failed => r.Error ?? "실패",
            _ => r.Error ?? "처리하지 않음",
        },
        Detail = r.Request.SourcePath + "  →  " + r.FinalPath,
        SizeText = SizeFormatter.Format(r.Request.Size),
    };

    /// <summary>[완료]: 정상 처리된 항목(옮김 / 건너뜀)은 대기열에서 치우고, 실패 / 미처리 항목은 남겨 다시 시도할 수 있게 한다.</summary>
    public void DismissResult()
    {
        foreach (var item in Queue.Where(q => q.State is QueueItemState.Done or QueueItemState.Skipped).ToList())
            Queue.Remove(item);

        _allResults = Array.Empty<ResultRowViewModel>();
        _summary = null;
        Raise(nameof(Results));
        Raise(nameof(HasFailures));
        Phase = QuickMovePhase.Idle;
        OnQueueChanged();
    }

    private void OpenResultLocation()
    {
        var moved = _summary?.Results.FirstOrDefault(r => r.Status == MoveStatus.Moved);
        if (moved != null && PathUtil.Exists(moved.FinalPath))
        {
            ShellService.OpenInExplorer(moved.FinalPath, isDirectory: false);   // 탐색기에서 그 항목을 선택해 보여 준다
            return;
        }
        if (Right.HasPath) ShellService.OpenInExplorer(Right.CurrentPath, isDirectory: true);
    }

    private void RaiseCommands()
    {
        MoveRightCommand.RaiseCanExecuteChanged();
        MoveLeftCommand.RaiseCanExecuteChanged();
        SwapCommand.RaiseCanExecuteChanged();
        ClearQueueCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        DismissResultCommand.RaiseCanExecuteChanged();
        RetryCommand.RaiseCanExecuteChanged();
        OpenResultCommand.RaiseCanExecuteChanged();
    }
}

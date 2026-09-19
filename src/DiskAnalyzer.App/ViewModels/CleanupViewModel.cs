using System.Collections.ObjectModel;
using System.ComponentModel;
using DiskAnalyzer.Core.Analysis;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Scanning;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.ViewModels;

/// <summary>
/// 53~66. 정리 추천 탭의 화면 상태.
///
/// 분석 자체는 CleanupAnalyzer(Core)가 담당하고 여기서는
///  - 사용자가 고른 기준을 옵션으로 옮기고
///  - 백그라운드에서 분석을 돌리고(스캔이 끝난 NodeStore 는 읽기 전용이라 안전하다)
///  - 선택 항목의 예상 확보 용량(59)과 시뮬레이션 결과(64)를 계산한다.
/// 실제 삭제는 사용자의 명시적인 확인을 거쳐 MainWindow 에서 수행한다(60).
/// </summary>
public sealed class CleanupViewModel : ObservableObject
{
    private readonly CleanupOptions _options = new();
    private CancellationTokenSource? _cts;
    private ScanResult? _result;

    public CleanupViewModel()
    {
        AnalyzeCommand = new RelayCommand(() => Analyze(), () => _result != null && !IsBusy && !IsRefreshing);
        RefreshCommand = new RelayCommand(
            () => _ = RefreshAsync(), () => _result != null && !IsBusy && !IsRefreshing);
        SelectRecommendedCommand = new RelayCommand(SelectRecommended, () => Candidates.Count > 0);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => SelectedCount > 0);
        SimulateCommand = new RelayCommand(Simulate, () => SelectedCount > 0);
        ExpandAllCommand = new RelayCommand(() => _foldTree?.SetAllExpanded(true), () => _foldTree != null);
        CollapseAllCommand = new RelayCommand(() => _foldTree?.SetAllExpanded(false), () => _foldTree != null);
    }

    // ---------------------------------------------------------------- 옵션 (55/65)

    public IReadOnlyList<string> AgeOptions { get; } =
        new[] { "30일 이상", "90일 이상", "180일 이상", "1년 이상", "2년 이상", "기간 무관" };

    private string _selectedAge = "180일 이상";
    public string SelectedAge
    {
        get => _selectedAge;
        set { if (Set(ref _selectedAge, value)) Analyze(); }
    }

    public IReadOnlyList<string> SizeOptions { get; } =
        new[] { "10 MB 이상", "100 MB 이상", "500 MB 이상", "1 GB 이상", "5 GB 이상", "10 GB 이상" };

    private string _selectedSize = "100 MB 이상";
    public string SelectedSize
    {
        get => _selectedSize;
        set { if (Set(ref _selectedSize, value)) Analyze(); }
    }

    public IReadOnlyList<string> SortOptions { get; } =
        new[] { "정리 추천순", "가장 큰 파일순", "가장 오래된 순", "가장 최근 사용 안 한 순", "위험도 낮은 순" };

    private string _selectedSort = "정리 추천순";
    public string SelectedSort
    {
        get => _selectedSort;
        set { if (Set(ref _selectedSort, value)) Analyze(); }
    }

    public IReadOnlyList<string> CategoryOptions { get; } = BuildCategoryOptions();

    private string _selectedCategory = "전체";
    public string SelectedCategory
    {
        get => _selectedCategory;
        set { if (Set(ref _selectedCategory, value)) Analyze(); }
    }

    // ---------------------------------------------------------------- 결과

    public ObservableCollection<CleanupCandidate> Candidates { get; } = new();

    // ---------------------------------------------------------------- 3/4. 보기 방식

    /// <summary>"폴더 트리" 가 기본이다: 폴더별로 묶고 이름 패턴이 반복되는 후보는 한 줄로 접는다.</summary>
    public const string FoldTreeMode = "폴더 트리";

    public IReadOnlyList<string> ViewModeOptions { get; } =
        new[] { FoldTreeMode, "파일별", "경로별", "유형별", "정리 이유별" };

    private string _selectedViewMode = FoldTreeMode;
    public string SelectedViewMode
    {
        get => _selectedViewMode;
        set
        {
            if (!Set(ref _selectedViewMode, value)) return;
            Raise(nameof(ShowFileList));
            Raise(nameof(ShowGroupList));
            Raise(nameof(ShowPathModeOptions));
            Raise(nameof(ShowTree));
            Raise(nameof(ShowFoldTree));
            BuildViews();
        }
    }

    private bool _useTreeView;
    /// <summary>4. 경로별 보기에서 계층 트리로 전환한다.</summary>
    public bool UseTreeView
    {
        get => _useTreeView;
        set
        {
            if (!Set(ref _useTreeView, value)) return;
            Raise(nameof(ShowGroupList));
            Raise(nameof(ShowTree));
        }
    }

    public bool ShowFileList => SelectedViewMode == "파일별";
    public bool ShowPathModeOptions => SelectedViewMode == "경로별";
    public bool ShowTree => ShowPathModeOptions && UseTreeView;
    public bool ShowFoldTree => SelectedViewMode == FoldTreeMode;
    public bool ShowGroupList => !ShowFileList && !ShowTree && !ShowFoldTree;

    public ObservableCollection<CleanupGroup> Groups { get; } = new();
    public ObservableCollection<CleanupPathNode> PathTree { get; } = new();

    /// <summary>폴더 트리 보기의 루트들(패턴 접기 포함).</summary>
    public ObservableCollection<CleanupFoldNode> FoldTree { get; } = new();

    private CleanupFoldTree? _foldTree;

    private string _foldSummary = string.Empty;
    public string FoldSummary { get => _foldSummary; private set => Set(ref _foldSummary, value); }

    public RelayCommand ExpandAllCommand { get; private set; } = null!;
    public RelayCommand CollapseAllCommand { get; private set; } = null!;

    // ---------------------------------------------------------------- 2/15. 새로고침

    private bool _isRefreshing;
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (!Set(ref _isRefreshing, value)) return;
            Raise(nameof(RefreshText));
            RefreshCommand.RaiseCanExecuteChanged();
            AnalyzeCommand.RaiseCanExecuteChanged();
        }
    }

    public string RefreshText => IsRefreshing ? "↻ 분석 중..." : "↻ 정리 후보 다시 분석";

    public RelayCommand RefreshCommand { get; private set; } = null!;

    private IReadOnlyList<CleanupCategorySummary> _summaries = Array.Empty<CleanupCategorySummary>();
    public IReadOnlyList<CleanupCategorySummary> Summaries { get => _summaries; private set => Set(ref _summaries, value); }

    private IReadOnlyList<ProjectBuildInfo> _projects = Array.Empty<ProjectBuildInfo>();
    public IReadOnlyList<ProjectBuildInfo> Projects
    {
        get => _projects;
        private set { if (Set(ref _projects, value)) Raise(nameof(ProjectsEmptyText)); }
    }

    private IReadOnlyList<GitRepoInfo> _gitRepos = Array.Empty<GitRepoInfo>();
    public IReadOnlyList<GitRepoInfo> GitRepos
    {
        get => _gitRepos;
        private set { if (Set(ref _gitRepos, value)) Raise(nameof(GitEmptyText)); }
    }

    public string ProjectsEmptyText => Projects.Count == 0 ? "빌드 산출물 폴더를 찾지 못했습니다." : string.Empty;
    public string GitEmptyText => GitRepos.Count == 0 ? "스캔 범위에 .git 저장소가 없습니다." : string.Empty;

    private IReadOnlyList<LogAgeBucket> _logBuckets = Array.Empty<LogAgeBucket>();
    public IReadOnlyList<LogAgeBucket> LogBuckets { get => _logBuckets; private set => Set(ref _logBuckets, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            AnalyzeCommand.RaiseCanExecuteChanged();
        }
    }

    private string _notice = "스캔을 완료하면 정리 후보를 분석합니다.";
    public string Notice { get => _notice; private set => Set(ref _notice, value); }

    private string _headline = string.Empty;
    public string Headline { get => _headline; private set => Set(ref _headline, value); }

    private string _protectedInfo = string.Empty;
    public string ProtectedInfo { get => _protectedInfo; private set => Set(ref _protectedInfo, value); }

    private string _developerSummary = string.Empty;
    public string DeveloperSummary { get => _developerSummary; private set => Set(ref _developerSummary, value); }

    private string _simulation = string.Empty;
    public string Simulation { get => _simulation; private set => Set(ref _simulation, value); }

    // ---------------------------------------------------------------- 59. 선택 집계

    private int _selectedCount;
    public int SelectedCount
    {
        get => _selectedCount;
        private set
        {
            if (!Set(ref _selectedCount, value)) return;
            Raise(nameof(SelectedCountText));
            ClearSelectionCommand.RaiseCanExecuteChanged();
            SimulateCommand.RaiseCanExecuteChanged();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private long _selectedSizeTotal;
    public long SelectedSizeTotal
    {
        get => _selectedSizeTotal;
        private set { if (Set(ref _selectedSizeTotal, value)) Raise(nameof(ExpectedFreeSpaceText)); }
    }

    public string SelectedCountText => $"{SizeFormatter.Count(SelectedCount)} 항목 선택됨";
    public string ExpectedFreeSpaceText => SizeFormatter.Format(SelectedSizeTotal);

    public event EventHandler? SelectionChanged;

    public IReadOnlyList<CleanupCandidate> SelectedItems
        => Candidates.Where(c => c.IsSelected).ToList();

    public RelayCommand AnalyzeCommand { get; }
    public RelayCommand SelectRecommendedCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand SimulateCommand { get; }

    // ---------------------------------------------------------------- 실행

    public void SetResult(ScanResult? result)
    {
        _result = result;
        AnalyzeCommand.RaiseCanExecuteChanged();
        if (result != null) Analyze();
    }

    public async void Analyze()
    {
        var result = _result;
        if (result == null || IsBusy) return;

        ApplyOptions();

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsBusy = true;
        Notice = "정리 후보를 분석하는 중...";

        try
        {
            // 스캔이 끝난 NodeStore 는 쓰는 쪽이 없으므로 백그라운드에서 읽어도 안전하다.
            var report = await Task.Run(
                () => new CleanupAnalyzer().Analyze(result.Store, _options.Clone(), token), token)
                .ConfigureAwait(true);

            if (token.IsCancellationRequested) return;
            ApplyReport(report);
        }
        catch (OperationCanceledException)
        {
            // 옵션을 연달아 바꾼 경우 - 마지막 분석 결과만 반영된다.
        }
        catch (Exception ex)
        {
            Notice = "정리 분석 실패: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyOptions()
    {
        _options.MinAgeDays = SelectedAge switch
        {
            "30일 이상" => 30,
            "90일 이상" => 90,
            "180일 이상" => 180,
            "1년 이상" => 365,
            "2년 이상" => 730,
            _ => 0,
        };

        _options.MinFileSize = SelectedSize switch
        {
            "10 MB 이상" => 10L * 1024 * 1024,
            "100 MB 이상" => 100L * 1024 * 1024,
            "500 MB 이상" => 500L * 1024 * 1024,
            "1 GB 이상" => 1024L * 1024 * 1024,
            "5 GB 이상" => 5L * 1024 * 1024 * 1024,
            _ => 10L * 1024 * 1024 * 1024,
        };
        _options.MinFolderSize = Math.Max(50L * 1024 * 1024, _options.MinFileSize);

        _options.Sort = SelectedSort switch
        {
            "가장 큰 파일순" => CleanupSort.Largest,
            "가장 오래된 순" => CleanupSort.Oldest,
            "가장 최근 사용 안 한 순" => CleanupSort.LeastRecentlyUsed,
            "위험도 낮은 순" => CleanupSort.LowestRisk,
            _ => CleanupSort.Recommended,
        };

        _options.CategoryFilter = SelectedCategory == "전체"
            ? null
            : Enum.GetValues<CleanupCategory>().FirstOrDefault(c => CleanupText.Of(c) == SelectedCategory);
    }

    private void ApplyReport(CleanupReport report)
    {
        DetachSelectionHandlers();
        Candidates.Clear();
        foreach (var c in report.Candidates)
        {
            c.PropertyChanged += OnCandidateChanged;
            Candidates.Add(c);
        }

        BuildViews();
        Summaries = report.Summaries;
        Projects = report.Developer.Projects;
        GitRepos = report.Developer.GitRepos;
        LogBuckets = report.Developer.LogBuckets;

        SelectedCount = 0;
        SelectedSizeTotal = 0;
        Simulation = string.Empty;

        Headline = report.TotalCandidates == 0
            ? "조건에 맞는 정리 후보가 없습니다."
            : $"정리 후보 {SizeFormatter.Count(report.TotalCandidates)}건 · 합계 {SizeFormatter.Format(report.TotalCandidateSize)}" +
              (report.Candidates.Count < report.TotalCandidates
                  ? $" (상위 {SizeFormatter.Count(report.Candidates.Count)}건 표시)"
                  : string.Empty);

        ProtectedInfo = report.ProtectedCount > 0
            ? $"시스템 보호 경로에서 {SizeFormatter.Count(report.ProtectedCount)}개 / {SizeFormatter.Format(report.ProtectedSize)} 를 찾았지만 정리 대상으로 추천하지 않습니다(정보용)."
            : string.Empty;

        DeveloperSummary =
            $"빌드 산출물 {SizeFormatter.Format(report.Developer.TotalBuildOutput)} · " +
            $".git {SizeFormatter.Format(report.Developer.TotalGitSize)} · " +
            $"로그 {SizeFormatter.Format(report.Developer.TotalLogSize)}" +
            (report.Developer.OldLogSize > 0
                ? $" (91일 이상 {SizeFormatter.Format(report.Developer.OldLogSize)})"
                : string.Empty);

        Notice = report.Notice.Length > 0
            ? report.Notice
            : $"분석 {report.ElapsedMs:F0}ms · 모든 후보에는 선정 이유가 붙어 있습니다. 삭제 여부는 직접 확인하세요.";

        SelectRecommendedCommand.RaiseCanExecuteChanged();
    }

    private void OnCandidateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CleanupCandidate.IsSelected)) return;

        // 폴더/패턴 체크박스가 수천 건을 한 번에 바꾸는 중이면 여기서 재집계하지 않는다.
        // 후보마다 O(N) 집계가 돌면 O(N²) 이 되어 화면이 멈춘다. 끝난 뒤 OnBulkSelectionCompleted 가 한 번만 한다.
        if (_foldTree is { IsBulkUpdating: true }) return;

        RecalculateSelection();
        RefreshGroupSelection();
    }

    private void RecalculateSelection()
    {
        int count = 0;
        long size = 0;
        foreach (var c in Candidates)
        {
            if (!c.IsSelected) continue;
            count++;
            size += c.Size;
        }
        SelectedSizeTotal = size;
        SelectedCount = count;
    }

    /// <summary>"정리 가능성 높음" 만 한 번에 선택한다. 확인 필요 / 비추천 항목은 건드리지 않는다.</summary>
    private void SelectRecommended()
    {
        foreach (var c in Candidates)
            c.IsSelected = c.Grade == CleanupGrade.HighlyCleanable && c.DefaultSelectable;
        RecalculateSelection();
        RefreshGroupSelection();
    }

    private void ClearSelection()
    {
        foreach (var c in Candidates) c.IsSelected = false;
        RecalculateSelection();
        RefreshGroupSelection();
    }

    /// <summary>64. 정리 시뮬레이션 - 실제 파일은 전혀 건드리지 않는다.</summary>
    private void Simulate()
    {
        var selected = SelectedItems;
        if (selected.Count == 0) { Simulation = string.Empty; return; }

        var byCategory = selected
            .GroupBy(c => c.Category)
            .Select(g => (Name: CleanupText.Of(g.Key), Size: g.Sum(c => c.Size), Count: g.Count()))
            .OrderByDescending(g => g.Size)
            .ToList();

        var lines = new List<string>
        {
            $"Clean Simulation — 선택 {SizeFormatter.Count(selected.Count)}개 항목 / 예상 확보 {SizeFormatter.Format(selected.Sum(c => c.Size))}",
        };
        lines.AddRange(byCategory.Select(g =>
            $"  · {g.Name}  {SizeFormatter.Format(g.Size)}  ({SizeFormatter.Count(g.Count)}개)"));

        int review = selected.Count(c => c.Grade != CleanupGrade.HighlyCleanable);
        if (review > 0)
            lines.Add($"  ! 확인이 필요한 항목이 {SizeFormatter.Count(review)}개 포함되어 있습니다.");
        lines.Add("  (시뮬레이션이므로 실제 파일은 변경되지 않았습니다)");

        Simulation = string.Join(Environment.NewLine, lines);
    }

    public void RemoveDeleted(IEnumerable<CleanupCandidate> deleted)
    {
        foreach (var c in deleted.ToList())
        {
            c.PropertyChanged -= OnCandidateChanged;
            Candidates.Remove(c);
        }
        BuildViews();
        RecalculateSelection();

        // 삭제 후에도 "정리 후보 34건 · 7.96 GB" 가 그대로 남아 있으면 화면이 거짓말을 한다.
        Headline = Candidates.Count == 0
            ? "조건에 맞는 정리 후보가 없습니다."
            : $"정리 후보 {SizeFormatter.Count(Candidates.Count)}건 · 합계 {SizeFormatter.Format(Candidates.Sum(c => c.Size))}";
    }

    /// <summary>
    /// 14. 프로그램에서 삭제한 항목을 전체 스캔 없이 목록에서 즉시 제거하고 예상 확보 용량을 갱신한다.
    /// </summary>
    public void RemoveDeletedNodes(IReadOnlyList<(RowKind Kind, int Id)> removed)
    {
        if (removed.Count == 0) return;
        var keys = removed.Select(r => (r.Kind, r.Id)).ToHashSet();
        RemoveDeleted(Candidates.Where(c => keys.Contains((c.Kind, c.Id))).ToList());
    }

    /// <summary>
    /// 2. 정리 추천 새로고침.
    /// ① 현재 후보를 실제 파일 시스템과 대조해 사라졌거나 크기가 바뀐 항목을 저장소에 반영하고,
    /// ② 같은 저장소로 다시 분석해 후보 여부 / 분류 / 예상 확보 용량을 새로 계산한다.
    /// 전체 디스크 재스캔은 하지 않는다(그건 상단 "새로고침"이 담당).
    /// </summary>
    public async Task RefreshAsync()
    {
        var result = _result;
        if (result == null || IsRefreshing || IsBusy) return;

        IsRefreshing = true;
        try
        {
            var targets = Candidates
                .Select(c => new FileVerifier.Target(c.Kind, c.Id, c.FullPath, c.Size))
                .ToList();

            MutationSummary mutation = default;
            if (targets.Count > 0)
            {
                var outcomes = await FileVerifier.VerifyAsync(targets).ConfigureAwait(true);
                mutation = result.Store.ApplyVerification(outcomes);
            }

            Notice = mutation.Any
                ? $"실제 상태 반영: 사라짐 {mutation.RemovedFiles:N0}개 · 크기 변경 {mutation.ResizedFiles:N0}개 — 다시 분석합니다."
                : "변경된 항목이 없습니다 — 다시 분석합니다.";
        }
        finally
        {
            IsRefreshing = false;
        }

        Analyze();
    }

    // ---------------------------------------------------------------- 3/4. 그룹 / 트리 구성

    private void BuildViews()
    {
        var items = Candidates.ToList();

        // 다시 만들어도 사용자가 펼쳐 둔 위치는 유지한다(삭제할 때마다 트리가 접히면 불편하다).
        var expanded = _foldTree?.CaptureExpanded();
        if (_foldTree != null) _foldTree.BulkSelectionCompleted -= OnBulkSelectionCompleted;
        _foldTree = null;

        Groups.Clear();
        PathTree.Clear();
        FoldTree.Clear();
        FoldSummary = string.Empty;
        ExpandAllCommand.RaiseCanExecuteChanged();
        CollapseAllCommand.RaiseCanExecuteChanged();
        if (items.Count == 0) return;

        switch (SelectedViewMode)
        {
            case FoldTreeMode:
                _foldTree = CleanupPatternFolder.Build(items);
                _foldTree.BulkSelectionCompleted += OnBulkSelectionCompleted;
                if (expanded is { Count: > 0 }) _foldTree.RestoreExpanded(expanded);
                foreach (var n in _foldTree.Roots) FoldTree.Add(n);

                FoldSummary = _foldTree.PatternCount > 0
                    ? $"이름 패턴이 반복되는 {SizeFormatter.Count(_foldTree.FoldedItemCount)}건을 " +
                      $"{SizeFormatter.Count(_foldTree.PatternCount)}개 묶음으로 접었습니다 — 묶음을 펼치면 개별 파일이 나옵니다."
                    : string.Empty;
                ExpandAllCommand.RaiseCanExecuteChanged();
                CollapseAllCommand.RaiseCanExecuteChanged();
                break;

            case "경로별":
                foreach (var g in CleanupGrouper.ByPath(items)) Groups.Add(g);
                foreach (var n in CleanupGrouper.BuildTree(items)) PathTree.Add(n);
                break;

            case "유형별":
                foreach (var g in CleanupGrouper.ByType(items)) Groups.Add(g);
                break;

            case "정리 이유별":
                foreach (var g in CleanupGrouper.ByReason(items)) Groups.Add(g);
                break;
        }
    }

    /// <summary>5. 개별 파일을 제외하면 그룹 체크박스가 "일부 선택" 상태로 바뀌어야 한다.</summary>
    private void RefreshGroupSelection()
    {
        foreach (var g in Groups) g.RefreshSelection();
        foreach (var n in PathTree) n.RefreshSelection();
        _foldTree?.RefreshSelection();
    }

    private void OnBulkSelectionCompleted(object? sender, EventArgs e)
    {
        RecalculateSelection();
        RefreshGroupSelection();
    }

    private void DetachSelectionHandlers()
    {
        foreach (var c in Candidates) c.PropertyChanged -= OnCandidateChanged;
    }

    private static string[] BuildCategoryOptions()
    {
        var list = new List<string> { "전체" };
        list.AddRange(Enum.GetValues<CleanupCategory>().Select(CleanupText.Of));
        return list.ToArray();
    }
}

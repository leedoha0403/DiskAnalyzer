using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.ViewModels;

/// <summary>목록의 한 줄. 종료 중/실패 상태를 줄 자체가 들고 있어야 여러 줄을 동시에 눌러도 서로 섞이지 않는다.</summary>
public sealed class ProcessRowViewModel : ObservableObject
{
    private ProcessInfo _info;
    private bool _isBusy;
    private bool _isSelected;
    private string _status = string.Empty;
    private bool _statusIsError;

    public ProcessRowViewModel(ProcessInfo info)
    {
        _info = info;
        IsProtected = ProcessCleanerService.IsProtected(info.Name);
    }

    public ProcessInfo Info => _info;

    /// <summary>새로고침마다 같은 Pid 의 줄 객체를 재사용할 때 숫자만 갈아 끼운다 - 선택 상태·진행중 표시는 그대로 둔다.</summary>
    public void UpdateInfo(ProcessInfo info)
    {
        _info = info;
        Raise(nameof(WindowTitle));
        Raise(nameof(Responding));
        Raise(nameof(RespondingText));
        Raise(nameof(MemoryBytes));
        Raise(nameof(MemoryText));
        Raise(nameof(CpuPercent));
        Raise(nameof(CpuText));
        Raise(nameof(DiskText));
        Raise(nameof(RootApp));
        Raise(nameof(NeedsAttention));
    }

    private int _depth;
    private int _descendantCount;
    private bool _expanded = true;
    private bool _treeMode;

    /// <summary>트리 보기에서 이 줄의 들여쓰기·접기 상태 - ApplyView 가 줄을 배치할 때마다 갱신한다.</summary>
    public void SetTree(bool treeMode, int depth, int descendantCount, bool expanded)
    {
        _treeMode = treeMode;
        _depth = depth;
        _descendantCount = descendantCount;
        _expanded = expanded;
        Raise(nameof(IndentMargin));
        Raise(nameof(ToggleGlyph));
        Raise(nameof(ToggleVisibility));
        Raise(nameof(DisplayName));
    }

    public System.Windows.Thickness IndentMargin => new(_treeMode ? _depth * 16 : 0, 0, 0, 0);
    public string ToggleGlyph => _descendantCount == 0 ? "" : _expanded ? "\u25BE" : "\u25B8";
    public System.Windows.Visibility ToggleVisibility => !_treeMode ? System.Windows.Visibility.Collapsed
        : _descendantCount == 0 ? System.Windows.Visibility.Hidden : System.Windows.Visibility.Visible;
    public string DisplayName => _treeMode && _descendantCount > 0 ? $"{Name} ({_descendantCount})" : Name;

    public int Pid => Info.Pid;
    public string Name => Info.Name;
    public string WindowTitle => Info.WindowTitle;
    public bool Responding => Info.Responding;
    public bool NeedsAttention => Info.NeedsAttention;
    public string RootApp => Info.RootApp;
    public string RespondingText => !Responding ? "응답 없음"
        : Info.Suspicious ? $"멈춤 의심 ({Math.Max(1, (int)(Info.IdleSeconds / 60))}분)"
        : "정상";
    public long MemoryBytes => Info.MemoryBytes;
    public string MemoryText => $"{Info.MemoryBytes / 1024.0 / 1024.0:N0} MB";
    public double CpuPercent => Info.CpuPercent;
    public string CpuText => $"{Info.CpuPercent:N1}%";
    public string DiskText => FormatRate(Info.DiskBytesPerSecond);
    public string StartedText => Info.StartTime?.ToString("HH:mm:ss") ?? "-";

    private static string FormatRate(double bytesPerSecond) => bytesPerSecond switch
    {
        <= 0 => "0 KB/s",
        < 1024 * 1024 => $"{bytesPerSecond / 1024.0:N0} KB/s",
        _ => $"{bytesPerSecond / 1024.0 / 1024.0:N1} MB/s",
    };

    /// <summary>explorer, csrss 같은 핵심 시스템 프로세스 - 여기서는 선택도, 종료도 할 수 없다.</summary>
    public bool IsProtected { get; }

    public string KillTooltip => IsProtected
        ? "보호된 시스템 프로세스라 여기서 종료할 수 없습니다."
        : "이 프로세스를 강제 종료합니다.";

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => Set(ref _isBusy, value);
    }

    public bool CanKill => !IsProtected && !IsBusy;

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        set => Set(ref _statusIsError, value);
    }
}

/// <summary>
/// 응답 없는(좀비) 프로세스를 찾아 종료한다. 작업 관리자처럼 창이 열려 있는 동안은 주기적으로
/// 다시 조회해 CPU%·디스크 속도를 갱신한다(View 의 타이머가 RefreshAsync 를 반복 호출한다).
/// 검색·정렬·"선택 항목만 보기"는 최근 조회 결과(<see cref="_snapshot"/>) 위에서만 다시 계산한다.
/// </summary>
public sealed class ProcessCleanerViewModel : ObservableObject
{
    private IReadOnlyList<ProcessInfo> _snapshot = Array.Empty<ProcessInfo>();
    private (string Key, bool Descending)? _sort = ("Memory", true);

    private bool _showAll;
    private bool _showSelectedOnly;
    private bool _isLoading;
    private string _message = string.Empty;
    private string _searchText = string.Empty;

    public ObservableCollection<ProcessRowViewModel> Rows { get; } = new();

    public bool ShowAll
    {
        get => _showAll;
        set
        {
            if (Set(ref _showAll, value)) ApplyView();
        }
    }

    public bool ShowSelectedOnly
    {
        get => _showSelectedOnly;
        set
        {
            if (Set(ref _showSelectedOnly, value)) ApplyView();
        }
    }

    private bool _treeMode = true;
    private readonly HashSet<int> _collapsed = new();

    /// <summary>작업 관리자처럼 부모 아래로 자식을 묶어 보여준다(Claude ▸ pwsh ▸ conhost).</summary>
    public bool TreeMode
    {
        get => _treeMode;
        set
        {
            if (Set(ref _treeMode, value)) ApplyView();
        }
    }

    public void ToggleCollapse(ProcessRowViewModel row)
    {
        if (!_collapsed.Remove(row.Pid)) _collapsed.Add(row.Pid);
        ApplyView();
    }

    /// <summary>멈춤 의심 프로세스를 전부 선택한다(창이 없어 응답 없음으로는 안 잡히는 고아·정체 프로세스 일괄 정리용).</summary>
    public int SelectSuspicious()
    {
        int n = 0;
        foreach (var r in _snapshot.Where(p => p.Suspicious && !ProcessCleanerService.IsProtected(p.Name)))
        {
            _selectedPids.Add(r.Pid);
            n++;
        }
        foreach (var row in Rows) row.IsSelected = _selectedPids.Contains(row.Pid);
        RaiseSelectionChanged();
        return n;
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => Set(ref _isLoading, value);
    }

    private bool _isInitialLoading;

    /// <summary>맨 처음 목록을 불러올 때만 true - 작업 관리자처럼 2초마다 조용히 다시 조회하는 동안은
    /// "불러오는 중..." 문구가 깜빡이지 않아야 한다.</summary>
    public bool IsInitialLoading
    {
        get => _isInitialLoading;
        private set => Set(ref _isInitialLoading, value);
    }

    public string Message
    {
        get => _message;
        set => Set(ref _message, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value)) ApplyView();
        }
    }

    // ---------------------------------------------------------------- 선택 요약

    private readonly HashSet<int> _selectedPids = new();

    public int SelectedCount => _selectedPids.Count;
    public bool HasSelection => _selectedPids.Count > 0;

    public string SelectionSummaryText
    {
        get
        {
            var selected = Rows.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0) return string.Empty;

            double totalMemoryMb = selected.Sum(r => r.MemoryBytes) / 1024.0 / 1024.0;
            double totalCpu = selected.Sum(r => r.CpuPercent);
            string names = string.Join(", ", selected.Take(3).Select(r => r.Name));
            if (selected.Count > 3) names += $" 외 {selected.Count - 3}개";

            return $"{selected.Count}개 선택됨 ({names}) · 합계 CPU {totalCpu:N1}%, 메모리 {totalMemoryMb:N0} MB";
        }
    }

    public ICommand RefreshCommand { get; }

    public ProcessCleanerViewModel()
    {
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsLoading);
    }

    /// <summary>
    /// 새로 만든 줄에만 한 번 건다 - 재사용되는 줄은 이미 구독돼 있다(Rows 는 계속 Clear+다시 채우지만,
    /// 그건 ObservableCollection 에 안 보일 뿐 같은 ProcessRowViewModel 객체다). 여기서 매번 다시 구독하면
    /// 새로고침마다 중복 구독이 쌓여서 체크박스 하나 누른 게 여러 번 처리된다.
    /// </summary>
    private void SubscribeRow(ProcessRowViewModel row) => row.PropertyChanged += OnRowPropertyChanged;

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ProcessRowViewModel row) return;
        if (e.PropertyName is nameof(ProcessRowViewModel.IsSelected))
        {
            if (row.IsSelected) _selectedPids.Add(row.Pid); else _selectedPids.Remove(row.Pid);
            RaiseSelectionChanged();

            // 지금 이 호출 자체가 그 줄의 체크박스 바인딩이 갱신되는 도중이다 - 여기서 바로 ApplyView() 로
            // Rows 를 Clear/Add 하면 "컬렉션이 수정됨" 예외로 앱이 죽는다. 이번 디스패처 작업이 끝난 뒤로 미룬다.
            if (ShowSelectedOnly)
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(ApplyView));
        }
        else if (e.PropertyName is nameof(ProcessRowViewModel.MemoryText) or nameof(ProcessRowViewModel.CpuText))
        {
            if (row.IsSelected) Raise(nameof(SelectionSummaryText));
        }
    }

    private void RaiseSelectionChanged()
    {
        Raise(nameof(SelectedCount));
        Raise(nameof(HasSelection));
        Raise(nameof(SelectionSummaryText));
    }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        if (Rows.Count == 0) IsInitialLoading = true;
        try
        {
            _snapshot = await Task.Run(() => ProcessCleanerService.ListProcesses());
            ApplyView();   // Rows 를 지우지 않는다 - 같은 Pid 줄은 재사용해서 선택 상태를 그대로 둔다
        }
        finally
        {
            IsLoading = false;
            IsInitialLoading = false;
        }
    }

    /// <summary>같은 헤더를 다시 누르면 방향을 뒤집고, 다른 헤더면 내림차순으로 시작한다.</summary>
    public void ToggleSort(string key)
    {
        _sort = _sort is { } s && s.Key == key ? (s.Key, !s.Descending) : (key, true);
        ApplyView();
    }

    /// <summary>_snapshot 에 검색어·정렬·선택 필터만 다시 적용해 Rows 를 새로 만든다. 기존 줄 객체는 그대로 재사용한다.</summary>
    private void ApplyView()
    {
        IEnumerable<ProcessInfo> query = _snapshot;

        // 검색어가 있으면 "응답 없음/멈춤 의심만" 필터는 무시하고 전체에서 찾는다 - 창 없는 자식(pwsh 등)이
        // 기본 필터에 가려 "claude" 로 검색해도 0건이던 문제.
        string term = SearchText.Trim();
        if (term.Length > 0)
        {
            query = query.Where(p =>
                p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.WindowTitle.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.RootApp.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Ancestors.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Pid.ToString().Contains(term, StringComparison.Ordinal));
        }
        else if (!ShowAll)
        {
            query = query.Where(p => p.NeedsAttention);
        }

        if (ShowSelectedOnly)
            query = query.Where(p => _selectedPids.Contains(p.Pid));

        var visible = query.ToList();

        var placed = new List<(ProcessInfo Info, int Depth, int Count, bool Expanded)>();
        if (TreeMode)
            PlaceTree(visible, term.Length > 0, placed);
        else
            foreach (var info in Sort(visible)) placed.Add((info, 0, 0, true));

        var existing = Rows.ToDictionary(r => r.Pid);

        Rows.Clear();
        foreach (var (info, depth, count, expanded) in placed)
        {
            if (!existing.TryGetValue(info.Pid, out var row))
            {
                row = new ProcessRowViewModel(info);
                SubscribeRow(row);
            }
            else
            {
                row.UpdateInfo(info);
            }

            row.SetTree(TreeMode, depth, count, expanded);
            Rows.Add(row);
        }

        Message = placed.Count > 0 ? string.Empty
            : ShowSelectedOnly ? "선택한 프로세스가 없습니다."
            : term.Length > 0 ? "검색 결과가 없습니다."
            : !ShowAll ? "응답 없거나 멈춤 의심되는 프로세스가 없습니다."
            : "표시할 프로세스가 없습니다.";

        RaiseSelectionChanged();
    }

    private IEnumerable<ProcessInfo> Sort(IEnumerable<ProcessInfo> items)
    {
        if (_sort is { } s)
        {
            Func<ProcessInfo, object> keySelector = s.Key switch
            {
                "Pid" => p => p.Pid,
                "Name" => p => p.Name,
                "Responding" => p => p.NeedsAttention,
                "Cpu" => p => p.CpuPercent,
                "Disk" => p => p.DiskBytesPerSecond,
                _ => p => p.MemoryBytes,
            };
            items = s.Descending ? items.OrderByDescending(keySelector) : items.OrderBy(keySelector);
        }

        // 선택도 종료도 안 되는 보호된(흐린) 프로세스가 목록 중간에 섞이면 헷갈린다 - 정렬 기준과 무관하게 맨 아래로 내린다.
        return items.OrderBy(p => ProcessCleanerService.IsProtected(p.Name));
    }

    /// <summary>
    /// 보이는 프로세스들을 부모 아래로 묶어 배치한다. 부모가 필터에 걸려 안 보이면 보이는 가장 가까운 조상 밑으로,
    /// 그런 조상도 없으면 최상위로 올린다(그래서 기본 필터에서도 자식이 사라지지 않는다).
    /// </summary>
    private void PlaceTree(List<ProcessInfo> visible, bool expandAll,
        List<(ProcessInfo Info, int Depth, int Count, bool Expanded)> output)
    {
        var all = _snapshot.ToDictionary(p => p.Pid);
        var visiblePids = visible.Select(p => p.Pid).ToHashSet();

        var children = new Dictionary<int, List<ProcessInfo>>();
        var roots = new List<ProcessInfo>();
        foreach (var p in visible)
        {
            int up = p.ParentPid;
            for (int guard = 0; up != 0 && !visiblePids.Contains(up) && guard < 32; guard++)
                up = all.TryGetValue(up, out var anc) ? anc.ParentPid : 0;

            if (up == 0 || up == p.Pid || !visiblePids.Contains(up)) { roots.Add(p); continue; }

            if (!children.TryGetValue(up, out var list)) children[up] = list = new List<ProcessInfo>();
            list.Add(p);
        }

        int CountDescendants(int pid, int depth = 0)
            => depth > 40 || !children.TryGetValue(pid, out var kids) ? 0
                : kids.Count + kids.Sum(k => CountDescendants(k.Pid, depth + 1));

        void Emit(ProcessInfo p, int depth)
        {
            int count = CountDescendants(p.Pid);
            bool expanded = expandAll || !_collapsed.Contains(p.Pid);
            output.Add((p, depth, count, expanded));
            if (!expanded || depth > 40 || !children.TryGetValue(p.Pid, out var kids)) return;
            foreach (var k in Sort(kids)) Emit(k, depth + 1);
        }

        foreach (var r in Sort(roots)) Emit(r, 0);
    }

    private void RemoveFromSnapshot(int pid) => _snapshot = _snapshot.Where(p => p.Pid != pid).ToList();

    /// <summary>
    /// 그냥 종료를 시도한다. 거부(AccessDenied)가 나오면 결과만 돌려주고, "관리자 권한으로 재시도"
    /// 확인 대화상자를 띄울지는 호출자(View)가 정한다 - ViewModel 은 UI 대화상자를 모른다.
    /// </summary>
    public async Task<KillOutcome> KillAsync(ProcessRowViewModel row)
    {
        row.IsBusy = true;
        row.Status = "종료 중...";
        row.StatusIsError = false;
        try
        {
            var outcome = await Task.Run(() => ProcessCleanerService.TryKill(row.Pid));
            ApplyOutcome(row, outcome);
            return outcome;
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    /// <summary>여러 줄을 순서대로 종료 시도하고, 거부된 줄만 돌려준다(호출자가 관리자 권한 재시도를 물어보게).</summary>
    public async Task<IReadOnlyList<ProcessRowViewModel>> KillManyAsync(IReadOnlyList<ProcessRowViewModel> rows)
    {
        var denied = new List<ProcessRowViewModel>();
        foreach (var row in rows)
        {
            var outcome = await KillAsync(row);
            if (outcome == KillOutcome.AccessDenied) denied.Add(row);
        }
        return denied;
    }

    /// <summary>
    /// UAC 상승 재실행 1건. 몇 개가 실제로 죽었는지는 재실행된 프로세스의 종료 코드로만 알 수 있어서,
    /// 결과를 반영한 뒤에는 화면을 다시 새로고침해 실제 상태와 맞춘다(줄 하나하나를 추측해서 지우지 않는다).
    /// </summary>
    public async Task<bool> KillElevatedAsync(ProcessRowViewModel row)
    {
        row.IsBusy = true;
        try
        {
            int failures = await Task.Run(() => ProcessCleanerService.TryKillElevated(new[] { row.Pid }));
            Message = failures switch
            {
                < 0 => "관리자 권한 요청이 취소되었거나 실행할 수 없었습니다.",
                0 => $"'{row.Name}' (PID {row.Pid}) 을(를) 관리자 권한으로 종료했습니다.",
                _ => $"'{row.Name}' (PID {row.Pid}) 을(를) 관리자 권한으로도 종료하지 못했습니다.",
            };
            await RefreshAsync();
            return failures == 0;
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    public async Task KillManyElevatedAsync(IReadOnlyList<ProcessRowViewModel> rows)
    {
        foreach (var r in rows) r.IsBusy = true;
        try
        {
            var pids = rows.Select(r => r.Pid).ToList();
            int failures = await Task.Run(() => ProcessCleanerService.TryKillElevated(pids));
            Message = failures switch
            {
                < 0 => "관리자 권한 요청이 취소되었거나 실행할 수 없었습니다.",
                0 => $"{pids.Count}개 프로세스를 관리자 권한으로 종료했습니다.",
                _ => $"{pids.Count}개 중 {failures}개는 관리자 권한으로도 종료하지 못했습니다.",
            };
            await RefreshAsync();
        }
        finally
        {
            foreach (var r in rows) r.IsBusy = false;
        }
    }

    private void ApplyOutcome(ProcessRowViewModel row, KillOutcome outcome)
    {
        switch (outcome)
        {
            case KillOutcome.Success:
                row.Status = "종료됨";
                Rows.Remove(row);
                RemoveFromSnapshot(row.Pid);
                break;
            case KillOutcome.NotFound:
                row.Status = "이미 종료됨";
                Rows.Remove(row);
                RemoveFromSnapshot(row.Pid);
                break;
            case KillOutcome.AccessDenied:
                row.Status = "거부됨 (권한 부족)";
                row.StatusIsError = true;
                break;
            default:
                row.Status = "종료하지 못했습니다.";
                row.StatusIsError = true;
                break;
        }
    }
}

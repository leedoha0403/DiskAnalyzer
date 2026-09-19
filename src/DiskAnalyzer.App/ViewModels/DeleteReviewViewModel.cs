using System.Collections.ObjectModel;
using System.ComponentModel;
using DiskAnalyzer.Core.Analysis;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.ViewModels;

/// <summary>8/10. 삭제 확인창의 한 줄.</summary>
public sealed class DeleteReviewRow : ObservableObject
{
    private bool _isSelected;
    private string _status = string.Empty;

    public required RowKind Kind { get; init; }
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required long Size { get; init; }
    public DateTime Modified { get; init; }
    public string Extension { get; init; } = string.Empty;
    public string CategoryText { get; init; } = string.Empty;
    public CategoryFlags Flags { get; init; }

    public required ProtectionLevel Level { get; init; }
    public required string ProtectionReason { get; init; }
    public string Warnings { get; init; } = string.Empty;

    /// <summary>P0 는 선택 자체가 불가능하다.</summary>
    public bool CanSelect => Level != ProtectionLevel.P0;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !CanSelect) return;
            if (_isSelected == value) return;
            _isSelected = value;
            Raise();
        }
    }

    /// <summary>삭제 실행 후 결과(삭제됨 / 이미 삭제됨 / 사용 중 / 보호됨 / 실패).</summary>
    public string Status { get => _status; set => Set(ref _status, value); }

    public bool IsDirectory => Kind == RowKind.Directory;
    public string SizeText => SizeFormatter.Format(Size);
    public string ModifiedText => Modified == default ? "-" : Modified.ToString("yyyy-MM-dd");
    public string TypeText => IsDirectory ? "폴더" : (Extension.Length > 0 ? Extension : "파일");
    public string LevelText => ProtectionText.Of(Level);
    public string LevelBadge => ProtectionText.Badge(Level);
    public bool HasWarning => Warnings.Length > 0 || Level >= ProtectionLevel.P1;

    public string WarningText => Level == ProtectionLevel.P0
        ? "🔒 " + ProtectionReason
        : Warnings.Length > 0
            ? "⚠ " + Warnings
            : Level == ProtectionLevel.P1 ? "⚠ " + ProtectionReason : string.Empty;

    /// <summary>
    /// 폴더는 하위까지 훑어 등급을 정하므로 <strong>백그라운드에서 호출</strong>해야 한다.
    /// </summary>
    public static DeleteReviewRow From(EntryRow row, CategoryFlags flags)
    {
        var verdict = ProtectionEvaluator.EvaluateEntry(row.FullPath, row.IsDirectory, flags);
        return new DeleteReviewRow
        {
            Kind = row.Kind,
            Id = row.Id,
            Name = row.Name,
            FullPath = row.FullPath,
            Size = row.Size,
            Modified = row.Modified,
            Extension = row.Extension,
            CategoryText = row.CategoryText,
            Flags = flags,
            Level = verdict.Level,
            ProtectionReason = verdict.Reason,
            Warnings = string.Join(" · ", Categorizer.DeleteWarnings(row.FullPath, flags)),
        };
    }

    public static DeleteReviewRow From(CleanupCandidate c)
        => new()
        {
            Kind = c.Kind,
            Id = c.Id,
            Name = c.Name,
            FullPath = c.FullPath,
            Size = c.Size,
            Modified = c.Modified,
            Extension = c.Extension,
            CategoryText = c.CategoryText,
            Flags = CategoryFlags.None,
            Level = c.Level,
            ProtectionReason = c.Protection.Reason,
            Warnings = string.Join(" · ", Categorizer.DeleteWarnings(c.FullPath, CategoryFlags.None)),
        };

    public DeletionRequest ToRequest() => new()
    {
        Kind = Kind,
        Id = Id,
        Path = FullPath,
        Size = Size,
        Name = Name,
        Flags = Flags,
    };
}

public enum DeleteReviewStage { Review, Running, Result }

/// <summary>
/// 7~13. 삭제 대상 확인 → 진행 → 결과.
/// 삭제 버튼을 누르는 즉시 지우지 않고 반드시 이 단계를 거친다.
/// </summary>
public sealed class DeleteReviewViewModel : ObservableObject
{
    private readonly List<DeleteReviewRow> _all;
    private CancellationTokenSource? _cts;

    public DeleteReviewViewModel(IEnumerable<DeleteReviewRow> rows)
    {
        _all = rows.ToList();

        // 10. P0 는 자동 제외, P1 은 기본 체크 해제, P2/P3 만 기본 삭제 대상.
        foreach (var r in _all)
        {
            r.IsSelected = r.Level <= ProtectionLevel.P2;
            r.PropertyChanged += OnRowChanged;
        }

        Rows = new ObservableCollection<DeleteReviewRow>(_all);
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        ClearAllCommand = new RelayCommand(() => SetAll(false));
        Recalculate();
    }

    public ObservableCollection<DeleteReviewRow> Rows { get; }

    public IReadOnlyList<string> SortOptions { get; } =
        new[] { "가장 큰 순", "경로순", "파일명순", "오래된 순", "위험도순" };

    private string _selectedSort = "가장 큰 순";
    public string SelectedSort
    {
        get => _selectedSort;
        set { if (Set(ref _selectedSort, value)) ApplyView(); }
    }

    private string _search = string.Empty;
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) ApplyView(); }
    }

    private DeleteReviewStage _stage = DeleteReviewStage.Review;
    public DeleteReviewStage Stage
    {
        get => _stage;
        private set
        {
            if (!Set(ref _stage, value)) return;
            Raise(nameof(IsReview));
            Raise(nameof(IsRunning));
            Raise(nameof(IsResult));
            Raise(nameof(ShowToolbar));
        }
    }

    public bool IsReview => Stage == DeleteReviewStage.Review;
    public bool IsRunning => Stage == DeleteReviewStage.Running;
    public bool IsResult => Stage == DeleteReviewStage.Result;
    public bool ShowToolbar => Stage != DeleteReviewStage.Running;

    // ---- 요약 (9/10) ----

    private int _selectedCount;
    public int SelectedCount { get => _selectedCount; private set { if (Set(ref _selectedCount, value)) Raise(nameof(SummaryText)); } }

    private long _selectedBytes;
    public long SelectedBytes { get => _selectedBytes; private set { if (Set(ref _selectedBytes, value)) Raise(nameof(FreeSpaceText)); } }

    public string FreeSpaceText => SizeFormatter.Format(SelectedBytes);
    public string SummaryText => $"선택됨 {SizeFormatter.Count(SelectedCount)}개";

    public string VerdictText
    {
        get
        {
            int deletable = _all.Count(r => r.Level <= ProtectionLevel.P2);
            int caution = _all.Count(r => r.Level == ProtectionLevel.P1);
            int blocked = _all.Count(r => r.Level == ProtectionLevel.P0);
            return $"선택 {_all.Count:N0}개 · 삭제 가능 {deletable:N0} · 주의 필요 {caution:N0} · 보호됨 {blocked:N0}";
        }
    }

    public string TotalText => $"총 {_all.Count:N0}개 항목";

    // ---- 진행 (13) ----

    private double _progressValue;
    public double ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }

    private string _progressText = string.Empty;
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    private string _currentPath = string.Empty;
    public string CurrentPath { get => _currentPath; private set => Set(ref _currentPath, value); }

    // ---- 결과 (13) ----

    private string _resultText = string.Empty;
    public string ResultText { get => _resultText; private set => Set(ref _resultText, value); }

    private bool _showFailuresOnly;
    public bool ShowFailuresOnly
    {
        get => _showFailuresOnly;
        set { if (Set(ref _showFailuresOnly, value)) ApplyView(); }
    }

    public DeletionSummary? Summary { get; private set; }
    public bool Permanent { get; private set; }

    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearAllCommand { get; }

    public IReadOnlyList<DeleteReviewRow> SelectedRows => _all.Where(r => r.IsSelected).ToList();

    // ---------------------------------------------------------------- 실행

    public async Task<DeletionSummary> ExecuteAsync(bool permanent)
    {
        Permanent = permanent;
        var targets = SelectedRows;
        var requests = targets.Select(r => r.ToRequest()).ToList();

        Stage = DeleteReviewStage.Running;
        _cts = new CancellationTokenSource();

        var progress = new Progress<DeletionProgress>(p =>
        {
            ProgressValue = p.Ratio * 100d;
            ProgressText = $"{p.Done:N0} / {p.Total:N0} 항목    " +
                           $"{SizeFormatter.Format(p.BytesDone)} / {SizeFormatter.Format(p.BytesTotal)}" +
                           (p.EntriesDeleted > 0 ? $"    ·    삭제된 파일 {p.EntriesDeleted:N0}개" : string.Empty);
            CurrentPath = p.CurrentPath;
        });

        var summary = await DeletionService
            .DeleteAsync(requests, permanent, progress, _cts.Token)
            .ConfigureAwait(true);

        Summary = summary;
        ApplyOutcomes(summary);
        Stage = DeleteReviewStage.Result;
        return summary;
    }

    public void CancelRun() => _cts?.Cancel();

    private void ApplyOutcomes(DeletionSummary summary)
    {
        // 같은 경로가 두 번 들어올 수 있으므로 ToDictionary(중복 키 예외) 대신 그룹으로 만든다.
        var byPath = new Dictionary<string, List<DeleteReviewRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _all)
        {
            if (!byPath.TryGetValue(r.FullPath, out var list))
            {
                list = new List<DeleteReviewRow>(1);
                byPath[r.FullPath] = list;
            }
            list.Add(r);
        }

        foreach (var o in summary.Outcomes)
        {
            if (!byPath.TryGetValue(o.Request.Path, out var rows)) continue;
            foreach (var row in rows)
                row.Status = o.Message.Length > 0 ? $"{o.StatusText} - {o.Message}" : o.StatusText;
        }

        ResultText =
            $"성공 {summary.SucceededCount:N0}개 / {SizeFormatter.Format(summary.SucceededBytes)}    ·    " +
            $"실패 {summary.FailedCount:N0}개 / {SizeFormatter.Format(summary.FailedBytes)}    ·    " +
            $"이미 존재하지 않음 {summary.AlreadyGoneCount:N0}개" +
            (summary.Cancelled ? "    (사용자가 취소함)" : string.Empty);

        ApplyView();
    }

    // ---------------------------------------------------------------- 목록

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeleteReviewRow.IsSelected)) Recalculate();
    }

    private void Recalculate()
    {
        int count = 0;
        long bytes = 0;
        foreach (var r in _all)
        {
            if (!r.IsSelected) continue;
            count++;
            bytes += r.Size;
        }
        SelectedBytes = bytes;
        SelectedCount = count;
    }

    private void SetAll(bool value)
    {
        foreach (var r in Rows) r.IsSelected = value;   // P0 는 setter 에서 무시된다
        Recalculate();
    }

    /// <summary>11. 정렬 + 검색. 항목이 수백 개여도 확인창을 쓸 수 있어야 한다.</summary>
    private void ApplyView()
    {
        IEnumerable<DeleteReviewRow> view = _all;

        if (ShowFailuresOnly)
            view = view.Where(r => r.Status.Length > 0 && !r.Status.StartsWith("삭제됨", StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(Search))
        {
            string term = Search.Trim();
            view = view.Where(r =>
                r.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                r.FullPath.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        view = SelectedSort switch
        {
            "경로순" => view.OrderBy(r => r.FullPath, StringComparer.OrdinalIgnoreCase),
            "파일명순" => view.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            "오래된 순" => view.OrderBy(r => r.Modified),
            "위험도순" => view.OrderByDescending(r => r.Level).ThenByDescending(r => r.Size),
            _ => view.OrderByDescending(r => r.Size),
        };

        Rows.Clear();
        foreach (var r in view) Rows.Add(r);
    }
}

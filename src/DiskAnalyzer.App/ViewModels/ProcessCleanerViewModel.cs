using System.Collections.ObjectModel;
using System.Windows.Input;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.ViewModels;

/// <summary>목록의 한 줄. 종료 중/실패 상태를 줄 자체가 들고 있어야 여러 줄을 동시에 눌러도 서로 섞이지 않는다.</summary>
public sealed class ProcessRowViewModel : ObservableObject
{
    private bool _isBusy;
    private string _status = string.Empty;
    private bool _statusIsError;

    public ProcessRowViewModel(ProcessInfo info) => Info = info;

    public ProcessInfo Info { get; }

    public int Pid => Info.Pid;
    public string Name => Info.Name;
    public string WindowTitle => Info.WindowTitle;
    public bool Responding => Info.Responding;
    public string RespondingText => Responding ? "정상" : "응답 없음";
    public string MemoryText => $"{Info.MemoryBytes / 1024.0 / 1024.0:N0} MB";
    public string StartedText => Info.StartTime?.ToString("HH:mm:ss") ?? "-";

    public bool IsBusy
    {
        get => _isBusy;
        set => Set(ref _isBusy, value);
    }

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
/// 응답 없는(좀비) 프로세스를 찾아 종료한다. 목록은 스냅샷이라 [새로고침]을 눌러야 갱신된다 —
/// 게임처럼 창이 사라진 채 죽어 있는 프로세스는 자동으로 계속 갱신할 필요가 없다.
/// </summary>
public sealed class ProcessCleanerViewModel : ObservableObject
{
    private bool _showAll;
    private bool _isLoading;
    private string _message = string.Empty;

    public ObservableCollection<ProcessRowViewModel> Rows { get; } = new();

    public bool ShowAll
    {
        get => _showAll;
        set
        {
            if (Set(ref _showAll, value)) _ = RefreshAsync();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => Set(ref _isLoading, value);
    }

    public string Message
    {
        get => _message;
        set => Set(ref _message, value);
    }

    public ICommand RefreshCommand { get; }

    public ProcessCleanerViewModel()
    {
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsLoading);
    }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        Message = string.Empty;
        bool notRespondingOnly = !ShowAll;
        try
        {
            var list = await Task.Run(() => ProcessCleanerService.ListProcesses(notRespondingOnly));
            Rows.Clear();
            foreach (var info in list) Rows.Add(new ProcessRowViewModel(info));

            if (Rows.Count == 0)
            {
                Message = notRespondingOnly
                    ? "응답 없는 프로세스가 없습니다."
                    : "표시할 프로세스가 없습니다.";
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

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

    public async Task KillElevatedAsync(ProcessRowViewModel row)
    {
        row.IsBusy = true;
        row.Status = "관리자 권한으로 재시도 중...";
        row.StatusIsError = false;
        try
        {
            bool ok = await Task.Run(() => ProcessCleanerService.TryKillElevated(row.Pid));
            if (ok)
            {
                row.Status = "종료됨 (관리자 권한)";
                Rows.Remove(row);
            }
            else
            {
                row.Status = "관리자 권한으로도 종료하지 못했습니다.";
                row.StatusIsError = true;
            }
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private void ApplyOutcome(ProcessRowViewModel row, KillOutcome outcome)
    {
        switch (outcome)
        {
            case KillOutcome.Success:
                row.Status = "종료됨";
                Rows.Remove(row);
                break;
            case KillOutcome.NotFound:
                row.Status = "이미 종료됨";
                Rows.Remove(row);
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

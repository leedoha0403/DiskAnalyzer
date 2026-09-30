using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.ViewModels;

/// <summary>삭제하지 못한 파일을 붙들고 있는 프로세스 한 줄(삭제 결과 화면).</summary>
public sealed class LockerRow : ObservableObject
{
    private string _status = string.Empty;
    private bool _isBusy;
    private bool _isDone;

    public LockerRow(LockingProcess process)
    {
        Pid = process.Pid;
        Name = process.Name;
        AppName = process.AppName;
        IsService = process.IsService;
        IsProtected = ProcessCleanerService.IsProtected(process.Name);
    }

    public int Pid { get; }
    public string Name { get; }
    public string AppName { get; }
    public bool IsService { get; }
    public bool IsProtected { get; }

    public string Title => string.Equals(AppName, Name, StringComparison.OrdinalIgnoreCase)
        ? $"{Name} (PID {Pid})"
        : $"{AppName} - {Name} (PID {Pid})";

    public string Hint => IsProtected ? "시스템 프로세스라 여기서 종료할 수 없습니다."
        : IsService ? "Windows 서비스입니다. 종료하면 해당 기능이 멈출 수 있습니다."
        : string.Empty;

    public string Status { get => _status; set => Set(ref _status, value); }

    public bool IsBusy
    {
        get => _isBusy;
        set { if (Set(ref _isBusy, value)) Raise(nameof(CanKill)); }
    }

    public bool IsDone
    {
        get => _isDone;
        set { if (Set(ref _isDone, value)) Raise(nameof(CanKill)); }
    }

    public bool CanKill => !IsProtected && !IsBusy && !IsDone;
}

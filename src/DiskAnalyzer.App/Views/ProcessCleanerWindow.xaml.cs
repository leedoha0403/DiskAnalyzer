using System.Windows;
using DiskAnalyzer.App.ViewModels;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.Views;

/// <summary>
/// 응답 없는(좀비) 프로세스를 찾아 종료하는 창. 그냥 종료가 거부(AccessDenied)되면
/// 관리자 권한 재시도 여부를 물어보고, 승인하면 이 exe 를 --kill-pid 로 상승 재실행한다
/// (ProcessCleanerService.TryKillElevated / App.OnStartup). 이 창 자체는 승격되지 않는다.
/// </summary>
public partial class ProcessCleanerWindow : Window
{
    private readonly ProcessCleanerViewModel _vm = new();

    public ProcessCleanerWindow()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await _vm.RefreshAsync();

    private async void OnKillClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ProcessRowViewModel row) return;

        var outcome = await _vm.KillAsync(row);
        if (outcome != KillOutcome.AccessDenied) return;

        var answer = MessageBox.Show(this,
            $"'{row.Name}' (PID {row.Pid}) 을(를) 지금 권한으로는 종료할 수 없습니다.\n\n" +
            "관리자 권한으로 다시 시도할까요? UAC 승인 창이 뜹니다.",
            "관리자 권한으로 재시도",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

        if (answer == MessageBoxResult.Yes)
            await _vm.KillElevatedAsync(row);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

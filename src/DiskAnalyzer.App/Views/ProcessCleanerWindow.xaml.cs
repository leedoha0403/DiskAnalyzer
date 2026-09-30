using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskAnalyzer.App.ViewModels;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.Views;

/// <summary>
/// 응답 없는(좀비) 프로세스를 찾아 종료하는 창. 작업 관리자처럼 열려 있는 동안 주기적으로 새로고침해서
/// CPU%·디스크 속도가 계속 갱신된다(<see cref="_refreshTimer"/>). 그냥 종료가 거부(AccessDenied)되면
/// 관리자 권한 재시도 여부를 물어보고, 승인하면 이 exe 를 --kill-pid 로 상승 재실행한다
/// (ProcessCleanerService.TryKillElevated / App.OnStartup). 이 창 자체는 승격되지 않는다.
/// </summary>
public partial class ProcessCleanerWindow : Window
{
    private readonly ProcessCleanerViewModel _vm = new();
    private readonly DispatcherTimer _refreshTimer;

    public ProcessCleanerWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += async (_, _) => { if (!_vm.IsLoading) await _vm.RefreshAsync(); };
        Closed += (_, _) => { _refreshTimer.Stop(); _vm.Dispose(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _vm.RefreshAsync();
        _refreshTimer.Start();
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Tag: string key }) _vm.ToggleSort(key);
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _vm.Rows.Where(r => !r.IsProtected)) row.IsSelected = true;
    }

    private void OnToggleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ProcessRowViewModel row) _vm.ToggleCollapse(row);
        e.Handled = true;
    }

    private void OnIgnoreMenuClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ProcessRowViewModel row) return;
        if (row.IsIgnored) _vm.UnignoreName(row.Name); else _vm.IgnoreName(row.Name);
    }

    private void OnIgnoreListClick(object sender, RoutedEventArgs e) => ProcessIgnoreMenu.Show((FrameworkElement)sender);

    private void OnSelectSuspiciousClick(object sender, RoutedEventArgs e)
    {
        int n = _vm.SelectSuspicious();
        _vm.Message = n == 0
            ? "멈춤 의심 프로세스가 없습니다. (창이 없고 5분 넘게 CPU·디스크 변화가 없어야 표시됩니다 - 창을 연 지 얼마 안 됐다면 잠시 뒤 다시 눌러보세요.)"
            : $"멈춤 의심 {n}개를 선택했습니다. 목록을 확인한 뒤 [선택 종료]를 누르세요.";
    }

    private void OnDeselectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _vm.Rows) row.IsSelected = false;
    }

    private async void OnKillClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ProcessRowViewModel row) return;

        var outcome = await _vm.KillAsync(row);
        if (outcome != KillOutcome.AccessDenied) return;

        if (ConfirmElevate($"'{row.Name}' (PID {row.Pid})"))
            await _vm.KillElevatedAsync(row);
    }

    /// <summary>[선택 종료]: 고른 줄을 모두 시도하고, 거부된 줄이 있으면 한 번만 물어봐서 관리자 권한으로 다시 시도한다.</summary>
    private async void OnKillSelectedClick(object sender, RoutedEventArgs e)
    {
        var selected = _vm.Rows.Where(r => r.IsSelected && !r.IsProtected).ToList();
        if (selected.Count == 0)
        {
            _vm.Message = "먼저 종료할 프로세스를 선택하세요 (목록의 체크박스).";
            return;
        }

        var denied = await _vm.KillManyAsync(selected);
        if (denied.Count == 0) return;

        if (ConfirmElevate($"{denied.Count}개"))
            await _vm.KillManyElevatedAsync(denied);
    }

    private bool ConfirmElevate(string subject)
        => MessageBox.Show(this,
            $"{subject}을(를) 지금 권한으로는 종료할 수 없습니다.\n\n" +
            "관리자 권한으로 다시 시도할까요? UAC 승인 창이 뜹니다.",
            "관리자 권한으로 재시도",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

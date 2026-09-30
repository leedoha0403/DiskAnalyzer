using System.Windows;
using System.Windows.Controls.Primitives;
using DiskAnalyzer.App.ViewModels;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.Views;

/// <summary>
/// 메인 창 왼쪽 사이드바 아래에 붙는 작은 프로세스 정리기. 전체 창(<see cref="ProcessCleanerWindow"/>)과 같은
/// 서비스를 쓰되 좁은 폭에 맞게 한 줄 목록만 보여준다. 조회는 앱 전체가 함께 쓰는
/// <see cref="ProcessCleanerViewModel.Shared"/> 가 돌린다 - 펼쳐서 보이는 동안은 2초, 접혀 있으면 느리게
/// (상태바 배지가 살아 있으려면 계속 재야 한다). 접힘 상태와 목록 높이는 ui.json 에 남는다.
/// </summary>
public partial class ProcessCleanerPanel : System.Windows.Controls.UserControl
{
    private const double DefaultListHeight = 170, MinListHeight = 80, MaxListHeight = 480;

    private readonly ProcessCleanerViewModel _vm = ProcessCleanerViewModel.Shared;
    private bool _expanded;

    public ProcessCleanerPanel()
    {
        InitializeComponent();
        DataContext = _vm;

        var s = UiSettings.Load();
        _expanded = s.ProcessPanelExpanded;
        ListScroll.Height = s.ProcessPanelListHeight > 0
            ? Math.Clamp(s.ProcessPanelListHeight, MinListHeight, MaxListHeight)
            : DefaultListHeight;
        UpdateExpandState();
    }

    private Window? Owner => Window.GetWindow(this);

    private void UpdateExpandState()
    {
        Body.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        ExpandButton.Content = (_expanded ? "▾" : "▸") + " 프로세스 정리";
        UpdateFastPolling();
    }

    private void UpdateFastPolling()
    {
        bool fast = _expanded && IsVisible && IsLoaded;
        bool becameFast = fast && !_vm.FastPolling;
        _vm.FastPolling = fast;
        if (becameFast && !_vm.IsLoading) _ = _vm.RefreshAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm.RevealRequested -= OnRevealRequested;
        _vm.RevealRequested += OnRevealRequested;
        UpdateFastPolling();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e) { _vm.FastPolling = false; _vm.RevealRequested -= OnRevealRequested; }
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateFastPolling();

    private void OnRevealRequested()
    {
        if (_expanded) return;
        _expanded = true;
        SaveExpanded();
        UpdateExpandState();
    }

    private void SaveExpanded()
    {
        var s = UiSettings.Load();
        s.ProcessPanelExpanded = _expanded;
        s.Save();
    }

    private void OnExpandClick(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        SaveExpanded();
        UpdateExpandState();
    }

    private void OnGripDelta(object sender, DragDeltaEventArgs e)
        => ListScroll.Height = Math.Clamp(ListScroll.Height + e.VerticalChange, MinListHeight, MaxListHeight);

    private void OnGripCompleted(object sender, DragCompletedEventArgs e)
    {
        var s = UiSettings.Load();
        s.ProcessPanelListHeight = ListScroll.Height;
        s.Save();
    }

    private void OnOpenFullClick(object sender, RoutedEventArgs e)
    {
        var window = new ProcessCleanerWindow { Owner = Owner };
        window.Show();
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
            ? "멈춤 의심 프로세스가 없습니다. (창 없이 5분 넘게 변화가 없어야 합니다)"
            : $"멈춤 의심 {n}개를 선택했습니다. 확인 후 [선택 종료].";
    }

    private void OnDeselectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _vm.Rows) row.IsSelected = false;
    }

    private async void OnKillClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ProcessRowViewModel row) return;

        // 그룹(트리 루트)은 자식까지 함께 끝나므로 실수 방지로 한 번 확인한다.
        if (row.Info.ParentPid == 0 && HasChildren(row) &&
            MessageBox.Show(Owner, $"'{row.Name}' 과(와) 그 아래 자식 프로세스를 모두 종료할까요?", "프로세스 종료",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var outcome = await _vm.KillAsync(row);
        if (outcome == KillOutcome.AccessDenied && ConfirmElevate($"'{row.Name}' (PID {row.Pid})"))
            await _vm.KillElevatedAsync(row);
    }

    private static bool HasChildren(ProcessRowViewModel row) => row.ToggleVisibility == Visibility.Visible;

    private async void OnKillSelectedClick(object sender, RoutedEventArgs e)
    {
        var selected = _vm.Rows.Where(r => r.IsSelected && !r.IsProtected).ToList();
        if (selected.Count == 0)
        {
            _vm.Message = "먼저 종료할 프로세스를 체크하세요.";
            return;
        }

        var denied = await _vm.KillManyAsync(selected);
        if (denied.Count > 0 && ConfirmElevate($"{denied.Count}개"))
            await _vm.KillManyElevatedAsync(denied);
    }

    private bool ConfirmElevate(string subject)
        => MessageBox.Show(Owner,
            $"{subject}을(를) 지금 권한으로는 종료할 수 없습니다.\n\n관리자 권한으로 다시 시도할까요? UAC 승인 창이 뜹니다.",
            "관리자 권한으로 재시도",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
}

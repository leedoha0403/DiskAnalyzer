using System.Windows;
using System.Windows.Controls;
using DiskAnalyzer.App.ViewModels;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.Views;

/// <summary>
/// 응답 없는(좀비) 프로세스를 찾아 종료하는 큰 화면. 메인 창의 "프로세스" 탭으로 들어가서 다른 탭과 자유롭게 오갈 수 있고,
/// 사이드바 패널과 같은 <see cref="ProcessCleanerViewModel.Shared"/> 를 쓴다 - 검색어·선택·무시 목록이 패널과 이어진다.
/// 열려 있는 동안은 2초마다 갱신해서 CPU%·디스크 속도가 계속 움직인다. 종료가 거부(AccessDenied)되면
/// 관리자 권한 재시도 여부를 물어보고, 승인하면 이 exe 를 --kill-pid 로 상승 재실행한다
/// (ProcessCleanerService.TryKillElevated / App.OnStartup). 이 화면 자체는 승격되지 않는다.
/// </summary>
public partial class ProcessCleanerView : System.Windows.Controls.UserControl
{
    private readonly ProcessCleanerViewModel _vm = ProcessCleanerViewModel.Shared;

    public ProcessCleanerView()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private Window? Owner => Window.GetWindow(this);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 열 머리글은 시각 트리 밖이라 DataContext 를 못 물려받는다 - 합계를 보여주는 머리글에만 직접 걸어준다.
        if (ProcessList.View is GridView gv)
            foreach (var col in gv.Columns)
                if (col.Header is GridViewColumnHeader { Tag: "Cpu" or "Memory" or "Disk" } h) h.DataContext = _vm;
        UpdateFast();
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateFast();

    /// <summary>보이는 동안만 2초 갱신을 요청한다(안 보이는 큰 화면이 몰래 조회를 재촉하지 않게).</summary>
    private void UpdateFast()
    {
        bool fast = IsVisible && IsLoaded;
        bool wasFast = _vm.FastPolling;
        _vm.SetFast(this, fast);
        if (fast && !wasFast && !_vm.IsLoading) _ = _vm.RefreshAsync();
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

    /// <summary>
    /// 행 우클릭 메뉴는 XAML 스타일 안에 두면 이벤트 연결(Connect)에서 캐스트 오류로 창이 안 열려서,
    /// 눌린 행을 찾아 코드에서 그때그때 만든다.
    /// </summary>
    private void OnListContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        var item = (e.OriginalSource as DependencyObject) is { } d ? FindListViewItem(d) : null;
        if (item?.DataContext is not ProcessRowViewModel row)
        {
            e.Handled = true;   // 빈 곳에서는 메뉴를 띄우지 않는다
            return;
        }

        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = item };
        var entry = new System.Windows.Controls.MenuItem { Header = row.IgnoreMenuText };
        entry.Click += (_, _) => { if (row.IsIgnored) _vm.UnignoreName(row.Name); else _vm.IgnoreName(row.Name); };
        menu.Items.Add(entry);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static System.Windows.Controls.ListViewItem? FindListViewItem(DependencyObject d)
    {
        while (d != null && d is not System.Windows.Controls.ListViewItem)
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as System.Windows.Controls.ListViewItem;
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
        _vm.DeselectAll();
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
        var selected = _vm.GetSelectedRows();
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
        => MessageBox.Show(Owner,
            $"{subject}을(를) 지금 권한으로는 종료할 수 없습니다.\n\n" +
            "관리자 권한으로 다시 시도할까요? UAC 승인 창이 뜹니다.",
            "관리자 권한으로 재시도",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
}

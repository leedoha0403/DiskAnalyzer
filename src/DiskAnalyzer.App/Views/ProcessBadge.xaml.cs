using System.Windows;
using DiskAnalyzer.App.ViewModels;

namespace DiskAnalyzer.App.Views;

/// <summary>
/// 상태바의 프로세스 알림. 이 컨트롤이 뜰 때 앱 전체 조회(<see cref="ProcessCleanerViewModel.StartMonitor"/>)를 시작한다 -
/// 패널이 접혀 있거나 사이드바가 숨겨져 있어도 "멈춤 의심"이 쌓이도록.
/// </summary>
public partial class ProcessBadge : System.Windows.Controls.UserControl
{
    public ProcessBadge()
    {
        InitializeComponent();
        DataContext = ProcessCleanerViewModel.Shared;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ProcessCleanerViewModel.Shared.StartMonitor();

    private void OnClick(object sender, RoutedEventArgs e)
    {
        // 사이드바가 접혀 있으면 먼저 펼쳐야 패널이 보인다.
        if (Window.GetWindow(this)?.DataContext is MainViewModel main && main.SidebarCollapsed)
            main.SidebarCollapsed = false;

        ProcessCleanerViewModel.Shared.RequestReveal();
    }
}

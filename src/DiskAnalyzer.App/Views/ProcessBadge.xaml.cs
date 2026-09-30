using System.Windows;
using DiskAnalyzer.App.ViewModels;

namespace DiskAnalyzer.App.Views;

/// <summary>
/// 상태바의 프로세스 알림. 이 컨트롤이 뜰 때 앱 전체 조회(<see cref="ProcessCleanerViewModel.StartMonitor"/>)를 시작한다 -
/// 프로세스 탭을 안 열어 봐도 "멈춤 의심"이 쌓이도록.
/// </summary>
public partial class ProcessBadge : System.Windows.Controls.UserControl
{
    public ProcessBadge()
    {
        InitializeComponent();
        DataContext = ProcessCleanerViewModel.Shared;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ProcessCleanerViewModel.Shared.StartMonitor();

    /// <summary>"프로세스" 탭으로 이동한다.</summary>
    private void OnClick(object sender, RoutedEventArgs e) => ProcessCleanerViewModel.Shared.OpenFullView();
}

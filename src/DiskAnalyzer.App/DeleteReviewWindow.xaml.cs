using System.Windows;
using DiskAnalyzer.App.ViewModels;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App;

/// <summary>
/// 7~13. 삭제 대상 확인 창.
/// 삭제 버튼을 누르는 즉시 지우지 않는다: 확인 → (사용자 검토/제외) → 최종 삭제 → 결과.
/// </summary>
public partial class DeleteReviewWindow : Window
{
    private readonly DeleteReviewViewModel _vm;

    public DeleteReviewWindow(IEnumerable<DeleteReviewRow> rows)
    {
        InitializeComponent();
        _vm = new DeleteReviewViewModel(rows);
        DataContext = _vm;
    }

    /// <summary>실행 후 실제로 사라진 노드. 호출자가 데이터 모델에서 즉시 제거한다(14).</summary>
    public DeletionSummary? Summary => _vm.Summary;

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_vm.IsRunning) { _vm.CancelRun(); return; }
        DialogResult = false;
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = _vm.Summary is { SucceededCount: > 0 };
        Close();
    }

    private void OnRecycle(object sender, RoutedEventArgs e) => Run(permanent: false);

    private void OnPermanent(object sender, RoutedEventArgs e) => Run(permanent: true);

    private async void Run(bool permanent)
    {
        var selected = _vm.SelectedRows;
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "삭제할 항목을 선택하세요.", "선택 없음",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string action = permanent ? "영구 삭제" : "휴지통으로 이동";
        int highRisk = selected.Count(r => r.Level == ProtectionLevel.P1);

        var message = new System.Text.StringBuilder();
        message.AppendLine($"{selected.Count:N0}개 항목을 {action} 합니다.");
        message.AppendLine($"예상 확보 공간: {SizeFormatter.Format(selected.Sum(r => r.Size))}");
        if (highRisk > 0)
            message.AppendLine($"\n⚠ 고위험(P1) 항목이 {highRisk:N0}개 포함되어 있습니다.");
        if (permanent)
            message.AppendLine("\n영구 삭제한 파일은 휴지통을 거치지 않아 복구할 수 없습니다.");

        var answer = MessageBox.Show(this, message.ToString(), $"{action} 확인",
            MessageBoxButton.OKCancel,
            permanent || highRisk > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        // 60. 영구 삭제는 한 번 더 확인한다.
        if (permanent)
        {
            var second = MessageBox.Show(this,
                $"정말 {selected.Count:N0}개 항목을 영구 삭제하시겠습니까?",
                "영구 삭제 - 최종 확인", MessageBoxButton.YesNo, MessageBoxImage.Stop, MessageBoxResult.No);
            if (second != MessageBoxResult.Yes) return;
        }

        CancelButton.Content = "중지";
        try
        {
            await _vm.ExecuteAsync(permanent);
        }
        catch (Exception ex)
        {
            // 삭제 도중 예외가 나도 창을 닫지 않는다. 사용자가 무엇이 지워졌는지 볼 수 있어야 한다.
            MessageBox.Show(this, "삭제 처리 중 오류가 발생했습니다.\n\n" + ex.Message,
                "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CancelButton.Visibility = Visibility.Collapsed;
        }
    }
}

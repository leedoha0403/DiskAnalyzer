using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.App;

/// <summary>
/// 49. 검색 결과 화면.
///  · 우클릭 / 더블 클릭으로 "이 항목이 있는 폴더"로 이동 (폴더 탭 + Treemap 이 같은 위치로 간다)
///  · [경로별 묶기] 를 켜면 같은 목록이 폴더별로 묶이고 반복되는 이름이 한 줄로 접힌다
///
/// 묶기를 별도 트리 컨트롤이 아니라 폴더 목록 안에서 들여쓰기로 그리는 이유:
/// 선택 요약 · 삭제 · Treemap 동기화 · 우클릭 메뉴가 모두 이 ListView 의 SelectedItems 에 걸려 있고,
/// WPF TreeView 는 Ctrl / Shift 다중 선택을 지원하지 않는다. 같은 목록에 그리면 그 기능이 전부 그대로 산다.
/// </summary>
public partial class MainWindow
{
    private void OnRevealRow(object sender, RoutedEventArgs e) => _vm.RevealRow(RowOf(sender));

    /// <summary>
    /// 묶음 머리글을 누르면 선택이 아니라 펼치기 / 접기다.
    /// 머리글은 진짜 항목이 아니라서 선택되면 삭제 대상 계산이 어긋난다 — 선택 자체를 만들지 않는다.
    /// </summary>
    private void OnFolderListPreviewClick(object sender, MouseButtonEventArgs e)
    {
        if (RowUnder(e.OriginalSource as DependencyObject) is not { IsSearchGroup: true } header) return;

        _vm.ToggleSearchGroup(header);
        e.Handled = true;
    }

    /// <summary>클릭 지점이 속한 행. 셀 안의 TextBlock 을 눌러도 행을 찾아낸다.</summary>
    private static EntryRow? RowUnder(DependencyObject? source)
    {
        while (source != null && source is not ListViewItem)
            source = VisualTreeHelper.GetParent(source);
        return (source as ListViewItem)?.DataContext as EntryRow;
    }

    /// <summary>
    /// 묶음 머리글을 걸러낸 선택 항목. Ctrl+A 는 머리글까지 고르므로
    /// 선택 요약 · 삭제 · Treemap 동기화는 모두 이것을 거쳐야 한다.
    /// </summary>
    private static List<EntryRow> RealSelection(System.Collections.IList? items)
    {
        var rows = new List<EntryRow>();
        if (items == null) return rows;

        foreach (var item in items)
            if (item is EntryRow { IsSearchGroup: false } row) rows.Add(row);
        return rows;
    }
}

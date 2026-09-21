using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App;

/// <summary>
/// 49. 검색 결과 화면.
///  · 우클릭 / 더블 클릭으로 "이 항목이 있는 폴더"로 이동 (폴더 탭 + Treemap 이 같은 위치로 간다)
///  · [경로별 묶기] 를 켜면 같은 목록이 폴더 트리 + 이름 접기로 바뀐다
/// </summary>
public partial class MainWindow
{
    // ---------------------------------------------------------------- 목록(평면)에서 이동

    private void OnRevealRow(object sender, RoutedEventArgs e) => _vm.RevealRow(RowOf(sender));

    // ---------------------------------------------------------------- 트리에서 이동

    /// <summary>트리에서 실제로 누른 줄. SelectedItem 은 중첩 트리에서 상위 행으로 잘못 잡힐 수 있다.</summary>
    private static SearchNode? NodeOf(RoutedEventArgs e)
        => (e.OriginalSource as FrameworkElement)?.DataContext as SearchNode;

    private SearchNode? MenuNode => SearchTreeView.SelectedItem as SearchNode;

    /// <summary>파일 / 폴더 줄은 이동, 폴더·묶음 줄은 기본 동작(펼치기·접기)에 맡긴다.</summary>
    private void OnSearchTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (NodeOf(e) is { Row: { } row }) _vm.RevealRow(row);
    }

    private void OnSearchTreeMenuOpened(object sender, RoutedEventArgs e)
    {
        var node = MenuNode;

        // 묶음 줄은 여러 건을 대표하므로 갈 곳이 하나로 정해지지 않는다. 폴더 줄은 그 폴더로 갈 수 있다.
        bool canReveal = node is { Kind: SearchNodeKind.Item } or { Kind: SearchNodeKind.Folder };
        StReveal.IsEnabled = canReveal;
        StOpenExplorer.IsEnabled = node != null;
        StCopyPath.IsEnabled = node != null;
    }

    private void OnSearchTreeReveal(object sender, RoutedEventArgs e)
    {
        var node = MenuNode;
        if (node == null) return;

        if (node.Row != null) _vm.RevealRow(node.Row);
        else if (node.Kind == SearchNodeKind.Folder) _vm.NavigateToPath(node.FullPath);
    }

    private void OnSearchTreeOpenExplorer(object sender, RoutedEventArgs e)
    {
        if (MenuNode is { } node)
            ShellService.OpenInExplorer(node.FullPath, node.Row?.IsDirectory ?? true);
    }

    private void OnSearchTreeCopyPath(object sender, RoutedEventArgs e)
    {
        if (MenuNode is { } node) TrySetClipboard(node.FullPath);
    }

    private void OnSearchTreeExpandAll(object sender, RoutedEventArgs e) => SetSearchTreeExpanded(true);

    private void OnSearchTreeCollapseAll(object sender, RoutedEventArgs e) => SetSearchTreeExpanded(false);

    private void SetSearchTreeExpanded(bool expanded)
    {
        foreach (var root in _vm.SearchTree) Apply(root);

        void Apply(SearchNode node)
        {
            if (node.Children.Count > 0) node.IsExpanded = expanded;
            foreach (var child in node.Children) Apply(child);
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiskAnalyzer.App.ViewModels;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.App;

/// <summary>
/// 폴더 / Treemap 이동 막대의 경로 직접 입력 · 즐겨찾기 · 변경 감지에 딸린 화면 동작.
/// 무엇을 할지는 <see cref="PathBarViewModel"/> / MainViewModel 이 정하고, 여기서는 포커스 · 스크롤 · 메뉴처럼 화면에만 속한 일을 한다.
/// </summary>
public partial class MainWindow
{
    private void InitializePathBar()
    {
        // 입력 칸이 열리면 곧바로 커서를 두고 전체 선택한다(붙여 넣기 / 덮어쓰기가 바로 되게).
        _vm.PathBar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PathBarViewModel.IsEditing) && _vm.PathBar.IsEditing) FocusPathEdit();
        };

        // 깊은 경로는 막대보다 길다. 항상 지금 폴더(오른쪽 끝)가 보이게 한다.
        _vm.Breadcrumb.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(() => CrumbScroller.ScrollToRightEnd(), DispatcherPriority.Loaded);

        _vm.SelectFileRequested += SelectFileRow;

        // 탐색기 등에서 파일을 바꾸고 돌아오면 지금 폴더가 스캔 이후 달라졌는지 다시 본다(2초 안의 반복은 무시된다).
        Activated += (_, _) => _vm.CheckFolderChanges();
    }

    private void FocusPathEdit()
        => Dispatcher.BeginInvoke(() =>
        {
            PathEdit.Focus();
            Keyboard.Focus(PathEdit);
            PathEdit.SelectAll();
        }, DispatcherPriority.Loaded);

    /// <summary>경로 조각 버튼이 아닌 빈 곳을 누르면 직접 입력 모드로 바뀐다(버튼은 자기 클릭을 처리하므로 여기까지 오지 않는다).</summary>
    private void OnPathAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _vm.PathBar.BeginEdit();
        e.Handled = _vm.PathBar.IsEditing;
    }

    private void OnPathEditKeyDown(object sender, KeyEventArgs e)
    {
        var bar = _vm.PathBar;
        switch (e.Key)
        {
            case Key.Enter:
                bar.Commit();
                e.Handled = true;
                break;
            case Key.Escape:
                bar.CancelEdit();
                FolderList.Focus();
                e.Handled = true;
                break;
            case Key.Down:
                bar.MoveSuggestion(+1);
                e.Handled = true;
                break;
            case Key.Up:
                bar.MoveSuggestion(-1);
                e.Handled = true;
                break;
            case Key.Tab:
                if (bar.AcceptSuggestion())
                {
                    PathEdit.CaretIndex = PathEdit.Text.Length;
                    e.Handled = true;
                }
                break;
        }
    }

    private void OnPathEditLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 제안 목록은 포커스를 받지 않으므로, 여기로 온다는 것은 정말 다른 곳을 눌렀다는 뜻이다.
        if (_vm.PathBar.IsEditing) _vm.PathBar.CancelEdit();
    }

    private void OnPathSuggestionClick(object sender, MouseButtonEventArgs e)
    {
        var node = e.OriginalSource as DependencyObject;
        while (node != null && node is not ListBoxItem) node = VisualTreeHelper.GetParent(node);
        if (node is not ListBoxItem { DataContext: string path }) return;

        _vm.PathBar.PickSuggestion(path);
        e.Handled = true;
    }

    /// <summary>경로를 입력해 파일을 열었다면 그 파일을 목록에서 골라 보여 준다.</summary>
    private void SelectFileRow(int fileId)
        => Dispatcher.BeginInvoke(() =>
        {
            var row = _vm.Rows.FirstOrDefault(r => !r.IsDirectory && r.Id == fileId);
            if (row == null) return;

            EnsureFolderListVisible();
            FolderList.SelectedItems.Clear();
            FolderList.SelectedItem = row;
            FolderList.ScrollIntoView(row);
            FolderList.Focus();
        }, DispatcherPriority.Loaded);

    // ---------------------------------------------------------------- 즐겨찾기 목록

    private void OnFavoritesClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        var bar = _vm.PathBar;

        string? current = _vm.CurrentFolderPath;
        var toggle = new MenuItem
        {
            Header = current == null ? "지금 폴더를 즐겨찾기에 추가"
                : bar.IsCurrentFavorite ? "지금 폴더를 즐겨찾기에서 제거" : "지금 폴더를 즐겨찾기에 추가",
            IsEnabled = current != null,
            InputGestureText = DiskAnalyzer.Core.Keymaps.Keymap.Current.GetText(DiskAnalyzer.Core.Keymaps.CommandIds.ToggleFavorite),
        };
        toggle.Click += (_, _) => bar.ToggleFavorite();
        menu.Items.Add(toggle);
        menu.Items.Add(new Separator());

        var favorites = bar.GetFavorites();
        if (favorites.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "즐겨찾기가 없습니다 — ☆ 로 지금 폴더를 추가하세요", IsEnabled = false });
        }
        else
        {
            foreach (var fav in favorites)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Opacity = fav.InScope ? 1 : 0.55 };
                row.Children.Add(new TextBlock { Text = fav.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 12, 0) });
                row.Children.Add(new TextBlock { Text = fav.Path, Opacity = 0.65 });

                var item = new MenuItem
                {
                    Header = row,
                    ToolTip = fav.InScope
                        ? "클릭: 이동  ·  우클릭: 즐겨찾기에서 빼기"
                        : "현재 스캔 범위 밖입니다. 누르면 스캔할지 물어봅니다  ·  우클릭: 즐겨찾기에서 빼기",
                };
                string path = fav.Path;
                item.Click += (_, _) => _ = bar.GoToAsync(path);
                item.MouseRightButtonUp += (_, args) =>
                {
                    bar.ToggleFavorite(path);
                    menu.IsOpen = false;
                    args.Handled = true;
                };
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "우클릭하면 즐겨찾기에서 뺍니다", IsEnabled = false, FontSize = 11 });
        }

        menu.IsOpen = true;
    }

    // ---------------------------------------------------------------- 우클릭 메뉴의 즐겨찾기 항목

    private void OnRowMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        var rows = (menu.PlacementTarget as ListView)?.SelectedItems.OfType<EntryRow>().ToList();
        var folder = rows is { Count: 1 } && rows[0].IsDirectory ? rows[0] : null;

        foreach (var item in menu.Items.OfType<MenuItem>().Where(i => i.Tag is "fav"))
        {
            item.Visibility = folder != null ? Visibility.Visible : Visibility.Collapsed;
            if (folder != null)
                item.Header = _vm.PathBar.IsFavoritePath(folder.FullPath) ? "즐겨찾기에서 제거" : "즐겨찾기에 추가";
        }
    }

    private void OnRowFavorite(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { IsDirectory: true } row) _vm.PathBar.ToggleFavorite(row.FullPath);
    }

    /// <summary>Treemap: 폴더 사각형 위면 그 폴더, 빈 곳이면 지금 폴더. 파일 사각형이면 항목을 숨긴다.</summary>
    private string? TreemapFavoriteTarget()
        => Treemap.ContextItem is { } item ? (item.IsDirectory ? item.FullPath : null) : _vm.CurrentFolderPath;

    private void UpdateTreemapFavoriteItem()
    {
        string? target = TreemapFavoriteTarget();
        TmFavorite.Visibility = target != null ? Visibility.Visible : Visibility.Collapsed;
        if (target != null)
            TmFavorite.Header = _vm.PathBar.IsFavoritePath(target) ? "즐겨찾기에서 제거" : "즐겨찾기에 추가";
    }

    private void OnTreemapFavorite(object sender, RoutedEventArgs e)
    {
        if (TreemapFavoriteTarget() is { } target) _vm.PathBar.ToggleFavorite(target);
    }
}

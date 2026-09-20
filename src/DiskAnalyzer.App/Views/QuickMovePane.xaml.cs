using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskAnalyzer.App.Keymaps;
using DiskAnalyzer.App.ViewModels.QuickMove;
using DiskAnalyzer.Core.Keymaps;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.QuickMove;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.Views;

/// <summary>패널 사이(그리고 빠른 위치 버튼으로) 끌어다 놓을 때 주고받는 정보. 앱 안에서만 쓰므로 정적 필드로 전달한다.</summary>
public sealed record QuickMoveDragData(QuickMovePaneViewModel Source, IReadOnlyList<FsEntry> Items)
{
    /// <summary>DataObject 에 넣는 표식. 실제 항목은 <see cref="Current"/> 에 있다(OLE 직렬화를 거치지 않는다).</summary>
    public const string Format = "DiskAnalyzer.QuickMove.Items";

    public static QuickMoveDragData? Current { get; set; }
}

/// <summary>
/// 패널 하나의 화면 동작: Breadcrumb / 경로 입력 / 목록 선택·정렬 / 끌어놓기 / 우클릭 메뉴.
/// 파일 시스템 접근과 판단은 ViewModel 에 있고 여기서는 입력을 옮겨 전달한다.
/// </summary>
public partial class QuickMovePane : UserControl, IShortcutTarget
{
    private static readonly Dictionary<string, string> HeaderNames = new()
    {
        ["name"] = "이름", ["size"] = "크기", ["modified"] = "수정일", ["type"] = "유형",
    };

    private QuickMovePaneViewModel? _vm;
    private Point _dragStart;
    private bool _dragArmed;
    private ListViewItem? _deferSelect;

    public QuickMovePane()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // ---- 좁은 화면(도크) ----

    private const double SizeWidth = 88, DateWidth = 128, TypeWidth = 90;
    private bool _compact;

    /// <summary>
    /// 도크처럼 좁은 곳에서는 꼭 필요한 것만 보여 준다: 빠른 위치 줄 / 앞으로 / 새로고침 / 수정일 / 유형 / 여유 공간을 감춘다.
    /// 폴더 이름 열은 남는 너비를 모두 쓴다(<see cref="FitColumns"/>).
    /// </summary>
    public bool IsCompact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;

            var v = value ? Visibility.Collapsed : Visibility.Visible;
            ChipsPanel.Visibility = v;
            ForwardButton.Visibility = v;
            RefreshButton.Visibility = v;
            FreeTextBlock.Visibility = v;

            // 좁고 낮은 도크에서는 여백을 줄여 폴더 목록이 최대한 많이 보이게 한다.
            TitleRow.Margin = value ? new Thickness(10, 6, 10, 0) : new Thickness(14, 10, 14, 0);
            NavRow.Margin = value ? new Thickness(6, 4, 6, 0) : new Thickness(10, 8, 10, 0);
            ListArea.Margin = value ? new Thickness(0, 4, 0, 0) : new Thickness(0, 8, 0, 0);
            FooterBorder.Padding = value ? new Thickness(10, 4, 10, 4) : new Thickness(14, 8, 14, 8);

            ApplyHeaderVisibility();
            FitColumns();
        }
    }

    private void OnListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyHeaderVisibility();
        FitColumns();
    }

    /// <summary>
    /// 도크에서는 열 머리글 줄(30px)을 접어 목록 줄을 더 보여 준다. 정렬은 넓은 화면(탭)에서 바꾼다.
    /// 머리글 줄은 ListView 템플릿 안에 있어서 화면 트리에서 찾아 감춘다.
    /// </summary>
    private void ApplyHeaderVisibility()
    {
        var presenter = FindDescendant<GridViewHeaderRowPresenter>(ItemList);
        if (presenter != null) presenter.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>
    /// 이름 열이 목록 너비에 맞춰 늘어나고 줄어든다. 창을 키우면 폴더 이름이 더 길게 보이고,
    /// 도크처럼 좁으면 수정일·유형 열을 접어서 이름과 크기만 남긴다. 가로 스크롤이 생기지 않게 한다.
    /// </summary>
    private void FitColumns()
    {
        if (ItemList.ActualWidth <= 0) return;

        double others = SizeWidth + (_compact ? 0 : DateWidth + TypeWidth);
        SizeColumn.Width = SizeWidth;
        DateColumn.Width = _compact ? 0 : DateWidth;
        TypeColumn.Width = _compact ? 0 : TypeWidth;

        double scrollbar = SystemParameters.VerticalScrollBarWidth + 6;
        NameColumn.Width = Math.Max(120, ItemList.ActualWidth - others - scrollbar);
    }

    public QuickMovePaneViewModel? Vm => _vm;

    public ListView List => ItemList;

    /// <summary>이 패널의 경로 입력 칸을 연다(Ctrl+L).</summary>
    public void FocusPathBox() => _vm?.BeginEditPath();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.SelectRequested -= OnSelectRequested;
            _vm.SortChanged -= OnSortChanged;
        }

        _vm = e.NewValue as QuickMovePaneViewModel;
        if (_vm == null) return;

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.SelectRequested += OnSelectRequested;
        _vm.SortChanged += OnSortChanged;
        UpdateHeaders();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(QuickMovePaneViewModel.IsEditingPath) when _vm?.IsEditingPath == true:
                Dispatcher.BeginInvoke(() =>
                {
                    PathBox.Focus();
                    PathBox.SelectAll();
                }, System.Windows.Threading.DispatcherPriority.Input);
                break;

            case nameof(QuickMovePaneViewModel.Breadcrumb):
                // 경로가 길면 오른쪽 끝(현재 폴더)이 보이게 한다.
                Dispatcher.BeginInvoke(() => CrumbScroller.ScrollToRightEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
                break;
        }
    }

    // ------------------------------------------------------------------ 경로

    private void OnCrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path }) _vm?.NavigateTo(path);
    }

    private void OnPathHostClick(object sender, MouseButtonEventArgs e) => _vm?.BeginEditPath();

    private void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;

        switch (e.Key)
        {
            case Key.Enter:
                _vm.CommitPath();
                e.Handled = true;
                ItemList.Focus();
                break;

            case Key.Escape:
                _vm.CancelEditPath();
                e.Handled = true;
                ItemList.Focus();
                break;

            case Key.Down:
            case Key.Up:
                _vm.MoveSuggestion(e.Key == Key.Down ? 1 : -1);
                MoveCaretToEnd();
                e.Handled = true;
                break;

            case Key.Tab:
                // Tab 은 포커스를 옮기지 않고 자동완성에 쓴다. 후보가 없어도 입력 모드는 유지한다.
                _vm.AcceptSuggestion();
                MoveCaretToEnd();
                e.Handled = true;
                break;
        }
    }

    /// <summary>바인딩으로 글자가 바뀌면 캐럿이 맨 앞으로 돌아간다. 이어서 칠 수 있게 끝으로 옮긴다.</summary>
    private void MoveCaretToEnd()
        => Dispatcher.BeginInvoke(() => PathBox.CaretIndex = PathBox.Text.Length, System.Windows.Threading.DispatcherPriority.Input);

    private void OnPathLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 제안 목록을 누르는 중에는 입력 모드를 닫지 않는다.
        if (SuggestPopup.IsMouseOver) return;
        if (_vm?.IsEditingPath == true) _vm.CancelEditPath();
    }

    private void OnSuggestionClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source is not ListBoxItem)
            source = VisualTreeHelper.GetParent(source);

        if (source is ListBoxItem { DataContext: string path })
        {
            _vm.PickSuggestion(path);
            e.Handled = true;
        }
    }

    private void OnRecentClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not Button button) return;

        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "최근 위치", IsEnabled = false, FontWeight = FontWeights.SemiBold });

        var recents = _vm.GetRecentPaths();
        if (recents.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "최근 위치가 없습니다", IsEnabled = false });
        }
        else
        {
            menu.Items.Add(new Separator());
            foreach (var path in recents)
            {
                var item = new MenuItem { Header = path };
                string target = path;
                item.Click += (_, _) => _vm.NavigateTo(target);
                menu.Items.Add(item);
            }
        }
        menu.IsOpen = true;
    }

    private void OnPickOther(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        if (_vm.BackCommand.CanExecute(null)) _vm.GoBack();
        else _vm.GoUp();
    }

    // ------------------------------------------------------------------ 빠른 위치

    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path }) _vm?.NavigateTo(path);
    }

    private void OnChipRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null || sender is not Button { DataContext: QuickLocation loc } button) return;

        var menu = new ContextMenu { PlacementTarget = button };
        bool fav = _vm.IsFavoritePath(loc.Path);
        var item = new MenuItem { Header = fav ? "즐겨찾기에서 제거" : "즐겨찾기에 추가" };
        item.Click += (_, _) => _vm.ToggleFavorite(loc.Path);
        menu.Items.Add(item);
        var copy = new MenuItem { Header = "경로 복사" };
        copy.Click += (_, _) => TryCopy(loc.Path);
        menu.Items.Add(copy);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnChipDragOver(object sender, DragEventArgs e)
    {
        if (QuickMoveDragData.Current == null || !e.Data.GetDataPresent(QuickMoveDragData.Format))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (_vm != null) _vm.IsDropTarget = false;   // 패널 전체 안내는 끄고, 버튼 하나만 강조한다
        if (sender is Button b && b.Tag is string path)
        {
            b.SetResourceReference(BackgroundProperty, "AccentSoftBrush");
            b.SetResourceReference(BorderBrushProperty, "AccentBrush");
            b.ToolTip = $"{path} 으로 이동";
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnChipDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button b) ResetChip(b);
        e.Handled = true;
    }

    private void OnChipDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: string path } b) return;
        ResetChip(b);

        var data = QuickMoveDragData.Current;
        if (data == null || _vm == null) return;
        _vm.DropOn(path, data.Items);
    }

    private static void ResetChip(Button b)
    {
        b.ClearValue(BackgroundProperty);
        b.ClearValue(BorderBrushProperty);
        if (b.DataContext is QuickLocation loc) b.ToolTip = loc.ToolTip;
    }

    // ------------------------------------------------------------------ 목록: 선택 / 정렬 / 열기

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        => _vm?.SetSelection(ItemList.SelectedItems.Cast<FsEntry>());

    private void OnSelectRequested(object? sender, IReadOnlyCollection<string> paths)
    {
        if (_vm == null) return;
        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);

        ItemList.SelectedItems.Clear();
        FsEntry? first = null;
        foreach (var entry in _vm.Entries)
        {
            if (!wanted.Contains(entry.FullPath)) continue;
            ItemList.SelectedItems.Add(entry);
            first ??= entry;
        }
        if (first != null) ItemList.ScrollIntoView(first);
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Tag: string key }) _vm?.ToggleSort(key);
    }

    private void OnSortChanged(object? sender, EventArgs e) => UpdateHeaders();

    /// <summary>현재 정렬 기준 열 머리글에 ▲ / ▼ 를 붙인다.</summary>
    private void UpdateHeaders()
    {
        if (_vm == null || ItemList.View is not GridView view) return;
        foreach (var col in view.Columns)
        {
            if (col.Header is not GridViewColumnHeader { Tag: string key } header) continue;
            string name = HeaderNames.GetValueOrDefault(key, key);
            header.Content = key == _vm.SortKey ? name + (_vm.SortAscending ? " ▲" : " ▼") : name;
        }
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        if (ItemAt(e.OriginalSource as DependencyObject)?.DataContext is FsEntry entry) _vm.Activate(entry);
    }

    /// <summary>목록에 포커스가 있을 때의 명령: 열기 / 폴더 진입, 상위 폴더. 키는 키맵이 정한다.</summary>
    public bool TryExecuteShortcut(string commandId)
    {
        if (_vm == null || !ItemList.IsKeyboardFocusWithin) return false;

        switch (commandId)
        {
            case CommandIds.Open when ItemList.SelectedItems.Count == 1 && ItemList.SelectedItem is FsEntry entry:
                _vm.Activate(entry);
                return true;
            case CommandIds.GoParent:
                _vm.GoUp();
                return true;
            default:
                return false;
        }
    }

    private static ListViewItem? ItemAt(DependencyObject? source)
    {
        while (source != null && source is not ListViewItem)
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        return source as ListViewItem;
    }

    // ------------------------------------------------------------------ 끌기 시작

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = false;
        _deferSelect = null;

        var item = ItemAt(e.OriginalSource as DependencyObject);
        if (item == null) return;

        _dragStart = e.GetPosition(null);
        _dragArmed = true;

        // 여러 개를 선택해 둔 상태에서 그중 하나를 눌러 끌면 선택이 하나로 줄어들면 안 된다(탐색기와 같은 동작).
        // 버튼을 놓을 때까지 선택 변경을 미루고, 끌지 않고 놓았다면 그때 그 항목만 선택한다.
        if (e.ClickCount == 1 && item.IsSelected && ItemList.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _deferSelect = item;
            e.Handled = true;
            ItemList.Focus();
        }
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || _vm == null || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragArmed = false;
        _deferSelect = null;

        var items = ItemList.SelectedItems.Cast<FsEntry>().ToList();
        if (items.Count == 0) return;

        var data = new DataObject();
        data.SetData(QuickMoveDragData.Format, "items");
        QuickMoveDragData.Current = new QuickMoveDragData(_vm, items);
        try
        {
            DragDrop.DoDragDrop(ItemList, data, DragDropEffects.Move);
        }
        finally
        {
            QuickMoveDragData.Current = null;
            _vm.Other.IsDropTarget = false;
            _vm.IsDropTarget = false;
        }
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferSelect != null)
        {
            ItemList.SelectedItems.Clear();
            _deferSelect.IsSelected = true;
        }
        _dragArmed = false;
        _deferSelect = null;
    }

    private void OnListRightDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemAt(e.OriginalSource as DependencyObject);
        if (item == null || item.IsSelected) return;
        ItemList.SelectedItems.Clear();
        item.IsSelected = true;
    }

    // ------------------------------------------------------------------ 놓는 쪽(패널 전체)

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var data = QuickMoveDragData.Current;
        if (_vm == null || data == null || !e.Data.GetDataPresent(QuickMoveDragData.Format) || ReferenceEquals(data.Source, _vm))
        {
            // 같은 패널 안에서는 이동이 아니다. 놓을 수 없다는 표시만 한다.
            e.Effects = DragDropEffects.None;
            if (_vm != null) _vm.IsDropTarget = false;
            e.Handled = true;
            return;
        }

        if (!_vm.IsDropTarget)
        {
            _vm.DropTitle = "여기에 놓기";
            _vm.DropDetail = $"{_vm.CurrentPath} 으로 이동";
            _vm.DropCount = Describe(data.Items);
            _vm.IsDropTarget = true;
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (_vm != null) _vm.IsDropTarget = false;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_vm == null) return;
        _vm.IsDropTarget = false;

        var data = QuickMoveDragData.Current;
        if (data == null || ReferenceEquals(data.Source, _vm)) return;

        // 놓는 즉시 이동하지 않는다. 대기열에 넣고, 사용자가 검토한 뒤 [이동 시작]을 누른다.
        _vm.DropOn(_vm.CurrentPath, data.Items);
    }

    private static string Describe(IReadOnlyList<FsEntry> items)
    {
        int folders = items.Count(i => i.IsDirectory);
        long bytes = items.Where(i => !i.IsDirectory).Sum(i => Math.Max(0, i.Size));
        return folders == 0
            ? $"{items.Count:N0}개 · {SizeFormatter.Format(bytes)}"
            : $"{items.Count:N0}개 · 폴더 {folders:N0}개 포함";
    }

    // ------------------------------------------------------------------ 우클릭 메뉴

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not ContextMenu menu) return;

        var selected = _vm.SelectedEntries;
        if (selected.Count == 0)
        {
            menu.IsOpen = false;   // 빈 곳에서는 메뉴를 띄우지 않는다
            return;
        }

        bool single = selected.Count == 1;
        bool singleFolder = single && selected[0].IsDirectory;

        MenuOpen.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        MenuReveal.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        MenuUseDest.Visibility = singleFolder ? Visibility.Visible : Visibility.Collapsed;
        MenuFavorite.Visibility = singleFolder ? Visibility.Visible : Visibility.Collapsed;
        MenuFolderSeparator.Visibility = singleFolder ? Visibility.Visible : Visibility.Collapsed;

        if (singleFolder)
            MenuFavorite.Header = _vm.IsFavoritePath(selected[0].FullPath) ? "즐겨찾기에서 제거" : "즐겨찾기에 추가";

        MenuQueue.Header = $"빠른 이동 대기열에 추가 ({selected.Count:N0}개)";
        MenuName.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMenuOpen(object sender, RoutedEventArgs e)
    {
        if (_vm is { SelectedEntries: [var entry] }) _vm.Activate(entry);
    }

    private void OnMenuReveal(object sender, RoutedEventArgs e)
    {
        if (_vm is { SelectedEntries: [var entry] })
            ShellService.OpenInExplorer(entry.FullPath, isDirectory: false);   // 탐색기에서 그 항목을 선택해 보여 준다
    }

    private void OnMenuQueue(object sender, RoutedEventArgs e) => _vm?.QueueSelectedToOther();

    private void OnMenuUseDest(object sender, RoutedEventArgs e)
    {
        if (_vm is { SelectedEntries: [{ IsDirectory: true } folder] }) _vm.UseAsDestination(folder.FullPath);
    }

    private void OnMenuFavorite(object sender, RoutedEventArgs e)
    {
        if (_vm is { SelectedEntries: [{ IsDirectory: true } folder] }) _vm.ToggleFavorite(folder.FullPath);
    }

    private void OnMenuCopyPath(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        TryCopy(string.Join(Environment.NewLine, _vm.SelectedEntries.Select(s => s.FullPath)));
    }

    private void OnMenuCopyName(object sender, RoutedEventArgs e)
    {
        if (_vm is { SelectedEntries: [var entry] }) TryCopy(entry.Name);
    }

    private void TryCopy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception ex) { if (_vm != null) _vm.Notice = "클립보드 복사 실패: " + ex.Message; }
    }
}

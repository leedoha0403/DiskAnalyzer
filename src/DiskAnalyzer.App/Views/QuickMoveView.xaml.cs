using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiskAnalyzer.App.Keymaps;
using DiskAnalyzer.App.ViewModels.QuickMove;
using DiskAnalyzer.Core.Keymaps;
using DiskAnalyzer.Core.QuickMove;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.Views;

/// <summary>
/// 빠른 이동 화면. 두 가지 배치를 한 화면(한 인스턴스)이 오간다:
///
///  · 넓은 화면(탭):   출발지 | 이동 컨트롤 | 목적지  /  대기열       — <see cref="IsCompact"/> = false
///  · 좁은 화면(도크): 출발지(위) / 이동 컨트롤 / 목적지(아래) / 대기열 — <see cref="IsCompact"/> = true
///
/// 인스턴스가 하나라서 어느 쪽에서 고르든 선택 · 대기열 · 진행 상태가 같다. 폴더 / Treemap 탭에서 우클릭으로 보낸 항목을
/// 탭을 벗어나지 않고 이 도크에서 이어서 처리할 수 있다.
///
/// 파일을 실제로 옮기는 코드는 여기에 없다 — Core 의 MoveEngine 을 ViewModel 이 부른다.
/// 이 클래스는 (1) 배치 전환 (2) ViewModel 이 요청하는 대화 상자 (3) 단축키와 대기열 조작 전달만 한다.
/// </summary>
public partial class QuickMoveView : UserControl, IQuickMoveUi, IShortcutTarget
{
    private const double DefaultQueueHeight = 200;
    private const double DockQueueMaxHeight = 150;

    private QuickMoveViewModel? _vm;
    private QuickMovePane? _active;
    private bool _compact;

    /// <summary>도크의 ✕ 를 눌렀다. 호스트(MainWindow)가 도크를 닫는다.</summary>
    public event EventHandler? DockCloseRequested;

    /// <summary>도크의 ↗ 를 눌렀다. 호스트가 "빠른 이동" 탭으로 전환한다.</summary>
    public event EventHandler? OpenAsTabRequested;

    public QuickMoveView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // 마지막으로 포커스를 가진 패널이 단축키(Ctrl+L, Alt+←, F5 …)의 대상이다.
        LeftPane.GotKeyboardFocus += (_, _) => _active = LeftPane;
        RightPane.GotKeyboardFocus += (_, _) => _active = RightPane;
        LeftPane.PreviewMouseDown += (_, _) => _active = LeftPane;
        RightPane.PreviewMouseDown += (_, _) => _active = RightPane;

        ApplyLayoutMode();
    }

    /// <summary>좁은 화면(도크) 배치인가.</summary>
    public bool IsCompact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            if (_vm != null) _vm.IsDockMode = value;
            ApplyLayoutMode();
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Queue.CollectionChanged -= OnQueueCollectionChanged;
            _vm.Ui = null;
        }

        _vm = e.NewValue as QuickMoveViewModel;
        if (_vm == null) return;

        _vm.Ui = this;
        _vm.IsDockMode = _compact;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Queue.CollectionChanged += OnQueueCollectionChanged;
        UpdateQueueLayout();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QuickMoveViewModel.QueueBodyOpen) or nameof(QuickMoveViewModel.Phase))
            UpdateQueueLayout();
    }

    private void OnQueueCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateQueueLayout();

    // ------------------------------------------------------------------ 배치 전환

    /// <summary>행 / 열 정의와 각 조각의 위치를 현재 배치에 맞게 다시 잡는다.</summary>
    private void ApplyLayoutMode()
    {
        var g = LayoutGrid;
        g.RowDefinitions.Clear();
        g.ColumnDefinitions.Clear();

        if (!_compact)
        {
            g.Margin = new Thickness(12);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });

            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 200 });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            QueueRow = new RowDefinition { Height = GridLength.Auto };
            g.RowDefinitions.Add(QueueRow);

            Place(DockBar, 0, 0, 3, Visibility.Collapsed);
            Place(LeftPane, 0, 0, 1);
            Place(ControlsPanel, 0, 1, 1);
            Place(RightPane, 0, 2, 1);
            Place(QueueSplitter, 1, 0, 3);
            Place(QueueBorder, 2, 0, 3);

            ControlsPanel.Orientation = Orientation.Vertical;
            SetMoveButton(MoveRightButton, "→", vertical: true);
            SetMoveButton(MoveLeftButton, "←", vertical: true);
            SetMoveButton(SwapButton, "⇄", vertical: true);
        }
        else
        {
            g.Margin = new Thickness(6);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });

            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                      // 제목 줄
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 110 }); // 출발지(위)
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                      // 이동 버튼
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 110 }); // 목적지(아래)
            QueueRow = new RowDefinition { Height = GridLength.Auto };
            g.RowDefinitions.Add(QueueRow);                                                                            // 대기열

            Place(DockBar, 0, 0, 3, Visibility.Visible);
            Place(LeftPane, 1, 0, 3);
            Place(ControlsPanel, 2, 0, 3);
            Place(RightPane, 3, 0, 3);
            Place(QueueBorder, 4, 0, 3);
            QueueSplitter.Visibility = Visibility.Collapsed;

            ControlsPanel.Orientation = Orientation.Horizontal;
            SetMoveButton(MoveRightButton, "↓ 대기열에 담기", vertical: false);
            SetMoveButton(MoveLeftButton, "↑ 담기", vertical: false);
            SetMoveButton(SwapButton, "⇄ 바꾸기", vertical: false);
        }

        LeftPane.IsCompact = _compact;
        RightPane.IsCompact = _compact;
        ApplyQueueChrome();
        UpdateQueueLayout();
    }

    private static void Place(FrameworkElement element, int row, int column, int columnSpan, Visibility? visibility = null)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
        if (visibility is { } v) element.Visibility = v;
    }

    /// <summary>세로 배치의 이동 버튼은 화살표만, 가로 배치(도크)는 무슨 일을 하는지 글자로 보여 준다.</summary>
    private void SetMoveButton(Button button, string text, bool vertical)
    {
        button.Content = text;
        button.Width = vertical ? 34 : double.NaN;
        button.MinWidth = 34;
        button.Height = vertical ? 34 : 30;
        button.Padding = vertical ? new Thickness(0) : new Thickness(12, 0, 12, 0);
        button.FontSize = vertical ? 17 : 12.5;
        button.Margin = vertical ? new Thickness(0, 5, 0, 5) : new Thickness(3, 4, 3, 4);
    }

    /// <summary>대기열 패널 안의 머리글 / 푸터 / 진행 표시를 좁은 폭에 맞게 다시 배치한다.</summary>
    private void ApplyQueueChrome()
    {
        // 머리글: 좁으면 안내 문구를 아랫줄로 내리고, 숨김 표시는 감추고, 대기열이 접혀 있어도 눌러야 하니 [이동 시작]을 머리글에 둔다.
        Grid.SetRow(HintText, _compact ? 1 : 0);
        Grid.SetColumn(HintText, _compact ? 0 : 1);
        Grid.SetColumnSpan(HintText, _compact ? 5 : 1);
        HintText.Margin = _compact ? new Thickness(0, 3, 0, 0) : new Thickness(16, 0, 10, 0);
        HintText.TextWrapping = _compact ? TextWrapping.Wrap : TextWrapping.NoWrap;
        HintText.TextTrimming = _compact ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        HiddenCheck.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        CompactStartButton.Visibility = _compact ? Visibility.Visible : Visibility.Collapsed;

        // 푸터: 좁으면 버튼 줄을 정보 아래로 내린다.
        Grid.SetRow(FooterInfo, 0);
        Grid.SetColumn(FooterInfo, 0);
        Grid.SetColumnSpan(FooterInfo, _compact ? 2 : 1);
        Grid.SetRow(FooterButtons, _compact ? 1 : 0);
        Grid.SetColumn(FooterButtons, _compact ? 0 : 1);
        Grid.SetColumnSpan(FooterButtons, _compact ? 2 : 1);
        FooterButtons.HorizontalAlignment = _compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        FooterInfo.Margin = _compact ? new Thickness(0, 0, 0, 6) : new Thickness(0, 0, 12, 0);

        // 도크: 머리글에 [이동 시작]이 있으므로 푸터의 것은 접고, 경로 안내 문구도 접는다(공간 경고는 남긴다).
        FooterStartButton.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        RouteHintText.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;

        ProgressStats.Columns = _compact ? 2 : 5;
    }

    // ------------------------------------------------------------------ 대기열 영역: 접기 / 자동 접기 / 높이

    private RowDefinition QueueRow { get; set; } = new();

    /// <summary>
    /// 대기열 본문을 보일지 정한다. 목록 영역을 넓게 쓰려고 <b>대기열이 비어 있으면 자동으로 접는다</b>(머리글 한 줄만 남고
    /// 사용법 안내가 뜬다). 항목이 생기거나 이동이 시작되면 다시 펼쳐진다. 도크에서는 진행 / 결과는 항상 보여 주고,
    /// 대기열 목록은 펼쳤을 때만 보여 준다(도크의 공간이 좁다).
    /// </summary>
    private void UpdateQueueLayout()
    {
        if (_vm == null) return;

        bool active = _vm.Phase is QuickMovePhase.Running or QuickMovePhase.Finished;
        bool hasContent = _vm.Queue.Count > 0 || _vm.Phase != QuickMovePhase.Idle;
        bool showBody = (_vm.QueueBodyOpen && hasContent) || (_compact && active);

        QueueBody.Visibility = showBody ? Visibility.Visible : Visibility.Collapsed;
        QueueBody.MaxHeight = _compact ? DockQueueMaxHeight : double.PositiveInfinity;

        if (_compact)
        {
            QueueRow.MinHeight = 0;
            QueueRow.Height = GridLength.Auto;
            QueueSplitter.Visibility = Visibility.Collapsed;
        }
        else if (showBody)
        {
            double h = _vm.QueueHeight > 0 ? _vm.QueueHeight : DefaultQueueHeight;
            QueueRow.MinHeight = 130;
            QueueRow.Height = new GridLength(h);
            QueueSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            QueueRow.MinHeight = 0;
            QueueRow.Height = GridLength.Auto;
            QueueSplitter.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (_vm != null && !_compact && QueueBody.Visibility == Visibility.Visible)
            _vm.QueueHeight = QueueRow.ActualHeight;
    }

    private void OnDockClose(object sender, RoutedEventArgs e) => DockCloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnDockOpenAsTab(object sender, RoutedEventArgs e) => OpenAsTabRequested?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------ IQuickMoveUi

    public ConflictDecision ResolveConflict(ConflictInfo info)
    {
        // 이동 엔진은 백그라운드 스레드에서 돈다. 답이 올 때까지 엔진이 기다려야 하므로 동기로 UI 스레드에 넘긴다.
        return Dispatcher.Invoke(() =>
        {
            var window = new MoveConflictWindow(info) { Owner = Window.GetWindow(this) };
            window.ShowDialog();
            return window.Decision;
        });
    }

    public int Choose(string title, string message, int defaultIndex, params string[] buttons)
    {
        var window = new ConfirmWindow(title, message, defaultIndex, buttons) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
        return window.Result;
    }

    // ------------------------------------------------------------------ 키보드

    private QuickMovePane ActivePane => _active ?? LeftPane;

    /// <summary>
    /// 단축키 라우터(ShortcutRouter)가 부른다. 키를 여기서 직접 해석하지 않는다 — 어떤 키가 어떤 명령인지는 현재 키맵이 정하고,
    /// 이 화면은 "명령이 왔을 때 무엇을 할지" 만 안다. 그래서 사용자가 키를 바꿔도 여기는 바뀔 것이 없다.
    /// </summary>
    public bool TryExecuteShortcut(string commandId)
    {
        if (_vm == null) return false;
        var pane = ActivePane.Vm;

        switch (commandId)
        {
            case CommandIds.FocusPath when pane != null:
                pane.BeginEditPath();
                return true;
            case CommandIds.GoBack when pane != null:
                pane.GoBack();
                return true;
            case CommandIds.GoForward when pane != null:
                pane.GoForward();
                return true;
            case CommandIds.GoParentAlt when pane != null:
                pane.GoUp();
                return true;
            case CommandIds.Refresh when pane != null:
                pane.Refresh();
                return true;
            case CommandIds.StartMove:
                if (_vm.StartCommand.CanExecute(null)) _vm.StartCommand.Execute(null);
                return true;
            case CommandIds.QueueSelected when pane != null:
                // 활성 패널의 선택 항목을 반대편 폴더로 보낼 대기열에 넣는다.
                pane.QueueSelectedToOther();
                return true;
            case CommandIds.RemoveFromQueue when QueueList.IsKeyboardFocusWithin:
                RemoveSelected();
                return true;
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------ 대기열 조작

    private void OnQueueRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QueueItemViewModel item }) _vm?.RemoveItem(item);
    }

    private void RemoveSelected()
    {
        var items = QueueList.SelectedItems.OfType<QueueItemViewModel>().ToList();
        if (items.Count > 0) _vm?.RemoveItems(items);
    }

    private void OnQueueMenuRemove(object sender, RoutedEventArgs e) => RemoveSelected();

    private void OnQueueMenuOpenTarget(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is QueueItemViewModel item)
            ShellService.OpenInExplorer(item.DestDirectory, isDirectory: true);
    }

    private void OnQueueMenuOpenSource(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is QueueItemViewModel item)
            ShellService.OpenInExplorer(item.SourcePath, isDirectory: false);   // 원본을 선택한 상태로 탐색기를 연다
    }
}

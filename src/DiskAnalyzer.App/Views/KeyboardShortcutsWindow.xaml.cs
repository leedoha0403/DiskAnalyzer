using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskAnalyzer.App.Keymaps;
using DiskAnalyzer.App.ViewModels;
using DiskAnalyzer.Core.Keymaps;

namespace DiskAnalyzer.App.Views;

/// <summary>
/// 단축키 설정 화면. 여기서 바꾸는 값은 [적용] 을 누르기 전까지 임시 키맵(KeymapDraft)에만 있고,
/// 프로그램 단축키에는 반영되지 않는다.
/// </summary>
public partial class KeyboardShortcutsWindow : Window
{
    private readonly KeyboardShortcutsViewModel _vm;
    private bool _forceClose;

    public KeyboardShortcutsWindow(Keymap keymap)
    {
        InitializeComponent();
        _vm = new KeyboardShortcutsViewModel(keymap);
        DataContext = _vm;
    }

    // ------------------------------------------------------------------ 키 입력 캡처

    /// <summary>
    /// 입력 대기 중에는 창 전체에서 키를 가로챈다. 문자열을 직접 치는 것이 아니라 실제로 누른 조합을 등록한다.
    /// Esc 는 취소, 그 밖의 등록할 수 없는 키는 이유를 안내하고 계속 기다린다.
    /// </summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.CapturingRow == null) return;

        e.Handled = true;
        if (e.IsRepeat) return;

        if (KeyInput.TryGetChord(e, out var chord, out bool unsupported))
        {
            // 누르고 있는 Esc 단독은 취소. (Ctrl+Esc 같은 조합은 등록 규칙이 거절한다.)
            if (chord == new KeyChord("Esc")) _vm.CancelCapture();
            else _vm.Capture(chord);
        }
        else if (unsupported)
        {
            _vm.ReportUnsupportedKey();
        }
        // Modifier 만 눌린 상태: 조합이 끝나길 기다린다.
    }

    /// <summary>입력 대기 중 다른 곳을 누르면 대기를 취소한다(값은 그대로).</summary>
    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var capturing = _vm.CapturingRow;
        if (capturing == null) return;

        for (var node = e.OriginalSource as DependencyObject; node != null; node = ParentOf(node))
            if (node is Button b && ReferenceEquals(b.Tag, capturing)) return;

        _vm.CancelCapture();
    }

    private void OnDeactivated(object? sender, EventArgs e) => _vm.CancelCapture();

    private static DependencyObject? ParentOf(DependencyObject node)
        => node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    // ------------------------------------------------------------------ 목록 조작

    private void OnKeyCellClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ShortcutRowViewModel row }) _vm.BeginCapture(row);
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.CapturingRow != null) return;
        if (ItemAt(e.OriginalSource as DependencyObject)?.DataContext is ShortcutRowViewModel row) _vm.BeginCapture(row);
    }

    /// <summary>입력 대기 상태가 아닐 때의 목록 키: Backspace / Delete 로 해제(9), Enter / F2 로 변경 시작.</summary>
    private void OnListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.CapturingRow != null || _vm.SelectedRow is not { } row) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        switch (e.Key)
        {
            case Key.Back:
            case Key.Delete:
                _vm.Unassign(row);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.F2:
                _vm.BeginCapture(row);
                e.Handled = true;
                break;
        }
    }

    private static ListViewItem? ItemAt(DependencyObject? source)
    {
        while (source != null && source is not ListViewItem) source = ParentOf(source);
        return source as ListViewItem;
    }

    private void OnResetRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ShortcutRowViewModel row }) _vm.ResetRow(row);
    }

    /// <summary>우클릭한 줄을 선택해 두어, 메뉴가 그 줄을 대상으로 한다. 빈 곳이면 메뉴를 띄우지 않는다.</summary>
    private void OnListRightDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.CapturingRow != null) return;
        if (ItemAt(e.OriginalSource as DependencyObject)?.DataContext is ShortcutRowViewModel row) _vm.SelectedRow = row;
        else e.Handled = true;
    }

    private ShortcutRowViewModel? MenuRow(object sender) => _vm.SelectedRow;

    private void OnMenuChange(object sender, RoutedEventArgs e)
    {
        if (MenuRow(sender) is { } row) _vm.BeginCapture(row);
    }

    private void OnMenuUnassign(object sender, RoutedEventArgs e)
    {
        if (MenuRow(sender) is { } row) _vm.Unassign(row);
    }

    private void OnMenuReset(object sender, RoutedEventArgs e)
    {
        if (MenuRow(sender) is { } row) _vm.ResetRow(row);
    }

    // ------------------------------------------------------------------ 충돌 해결

    private void OnResolveUnassign(object sender, RoutedEventArgs e) => _vm.ResolveByUnassigningOther();
    private void OnResolveRetry(object sender, RoutedEventArgs e) => _vm.ResolveByRetry();
    private void OnResolveCancel(object sender, RoutedEventArgs e) => _vm.ResolveByCancel();

    // ------------------------------------------------------------------ 기본값 복원 / 적용 / 취소

    private void OnResetAllClick(object sender, RoutedEventArgs e)
    {
        int choice = Confirm("모든 단축키를 기본값으로 복원하시겠습니까?",
            "사용자가 변경한 단축키 설정이 모두 초기화됩니다.\n\n복원한 뒤 적용을 눌러야 실제로 바뀝니다.",
            defaultIndex: 0, "취소", "기본값 복원");
        if (choice == 1) _vm.ResetAll();
    }

    private void OnApplyClick(object sender, RoutedEventArgs e) => _vm.Apply();

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _vm.Cancel();
        _forceClose = true;
        Close();
    }

    /// <summary>바꾼 내용이 남아 있는 채로 창을 닫으려 하면 물어본다. 실수로 닫아 설정을 잃지 않게 한다.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_forceClose || !_vm.IsDirty) return;

        if (_vm.CanApply)
        {
            int choice = Confirm("적용하지 않은 변경 사항이 있습니다", "변경한 단축키를 적용하시겠습니까?",
                defaultIndex: 0, "적용", "버리기", "계속 편집");
            if (choice == 0) _vm.Apply();
            else if (choice != 1) e.Cancel = true;
        }
        else
        {
            int choice = Confirm("충돌하는 단축키가 있습니다",
                "충돌이 있어 적용할 수 없습니다. 변경한 내용을 버리고 닫으시겠습니까?",
                defaultIndex: 1, "버리고 닫기", "계속 편집");
            if (choice != 0) e.Cancel = true;
        }
    }

    private int Confirm(string title, string message, int defaultIndex, params string[] buttons)
    {
        var window = new ConfirmWindow(title, message, defaultIndex, buttons) { Owner = this };
        window.ShowDialog();
        return window.Result;
    }
}

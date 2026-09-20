using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DiskAnalyzer.Core.Keymaps;

namespace DiskAnalyzer.App.Keymaps;

/// <summary>
/// 자기 안에서 일어나는 단축키(명령)를 직접 처리하는 화면. 예: 빠른 이동 탭은 활성 패널 기준으로 "이전 위치" 를 처리한다.
/// 같은 명령이 화면마다 다른 일을 하지만 키는 하나다 — 그래서 사용자가 키를 바꿔도 모든 화면에 함께 적용된다.
/// </summary>
public interface IShortcutTarget
{
    /// <summary>이 화면이 명령을 처리했으면 true. 지금 상황에서 할 일이 없으면 false 를 돌려 바깥 처리기에 넘긴다.</summary>
    bool TryExecuteShortcut(string commandId);
}

/// <summary>
/// 창 전체의 단축키 입구. 화면마다 KeyDown 을 따로 달지 않고 이 라우터 하나가
///   키 입력 → 현재 키맵에서 명령 찾기 → Context(포커스 위치) 검사 → 실행
/// 순서로 처리한다.
/// </summary>
public sealed class ShortcutRouter
{
    private readonly Keymap _keymap;
    private readonly Func<string, bool> _fallback;

    /// <param name="fallback">포커스 아래 어느 <see cref="IShortcutTarget"/> 도 처리하지 않았을 때 부르는 창 수준 처리기.</param>
    public ShortcutRouter(Keymap keymap, Func<string, bool> fallback)
    {
        _keymap = keymap;
        _fallback = fallback;
    }

    public void Attach(Window window) => window.PreviewKeyDown += OnPreviewKeyDown;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (!KeyInput.TryGetChord(e, out var chord, out _)) return;

        string? commandId = _keymap.Find(chord);
        if (commandId == null) return;

        var focused = Keyboard.FocusedElement as DependencyObject;

        // 기존 Focus 우선 규칙(25): 입력창이 포커스를 가졌으면 글자 입력·캐럿 이동·복사/붙여넣기는 입력창 몫이다.
        if (focused is TextBoxBase && ChordRules.BelongsToTextInput(chord)) return;

        if (Dispatch(focused, commandId)) e.Handled = true;
    }

    private bool Dispatch(DependencyObject? focused, string commandId)
    {
        for (var node = focused; node != null; node = Parent(node))
            if (node is IShortcutTarget target && target.TryExecuteShortcut(commandId)) return true;

        return _fallback(commandId);
    }

    private static DependencyObject? Parent(DependencyObject node)
    {
        if (node is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            var visual = VisualTreeHelper.GetParent(node);
            if (visual != null) return visual;
        }
        return node is FrameworkElement fe ? fe.Parent ?? fe.TemplatedParent : LogicalTreeHelper.GetParent(node);
    }
}

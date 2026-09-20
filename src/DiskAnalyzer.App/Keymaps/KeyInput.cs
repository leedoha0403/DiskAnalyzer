using System.Windows.Input;
using DiskAnalyzer.Core.Keymaps;

namespace DiskAnalyzer.App.Keymaps;

/// <summary>WPF 키 이벤트를 Core 의 <see cref="KeyChord"/> 로 바꾼다.</summary>
public static class KeyInput
{
    /// <summary>
    /// 눌린 키 조합. Modifier 만 눌렀거나(아직 조합이 끝나지 않음) 등록할 수 없는 키면 false.
    /// unsupported 는 "글자 키인데 표에 없다" 를 구분하려는 것으로, Modifier 단독 입력에서는 false 이다.
    /// </summary>
    public static bool TryGetChord(KeyEventArgs e, out KeyChord chord, out bool unsupported)
    {
        chord = default;
        unsupported = false;

        // Alt 조합은 Key.System, 한글 IME 가 켜져 있으면 Key.ImeProcessed 로 온다. 실제 키는 별도 속성에 들어 있다.
        Key key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };

        if (IsModifierKey(key) || key == Key.None) return false;

        var token = ToToken(key);
        if (token == null)
        {
            unsupported = true;
            return false;
        }

        var mods = e.KeyboardDevice.Modifiers;
        chord = new KeyChord(
            token,
            Ctrl: (mods & ModifierKeys.Control) != 0,
            Alt: (mods & ModifierKeys.Alt) != 0,
            Shift: (mods & ModifierKeys.Shift) != 0,
            Win: (mods & ModifierKeys.Windows) != 0);
        return true;
    }

    private static bool IsModifierKey(Key key)
        => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.CapsLock or Key.NumLock or Key.Scroll;

    public static string? ToToken(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
        if (key >= Key.D0 && key <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key >= Key.F1 && key <= Key.F12) return "F" + (1 + (key - Key.F1));

        return key switch
        {
            Key.Enter => "Enter",
            Key.Space => "Space",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Escape => "Esc",
            Key.Tab => "Tab",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.OemComma => "Comma",
            Key.OemPeriod => "Period",
            Key.OemMinus => "Minus",
            Key.OemPlus => "Plus",
            Key.Oem2 => "Slash",
            Key.Oem5 => "Backslash",
            Key.Oem1 => "Semicolon",
            Key.Oem7 => "Quote",
            Key.Oem3 => "Backtick",
            Key.Oem4 => "OpenBracket",
            Key.Oem6 => "CloseBracket",
            _ => null,
        };
    }
}

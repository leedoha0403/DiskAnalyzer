using System.Text;

namespace DiskAnalyzer.Core.Keymaps;

/// <summary>
/// 단축키 하나(키 + Modifier). WPF 의 Key 열거형에 묶이지 않도록 키를 문자열 토큰("F", "F5", "Left", "Backspace")으로 들고 있다.
/// 설정 파일에는 <see cref="Serialize"/> 결과("Ctrl+Shift+F")가 그대로 저장된다.
/// default(KeyChord) 가 "미할당" 이다.
/// </summary>
public readonly record struct KeyChord(string? Key, bool Ctrl = false, bool Alt = false, bool Shift = false, bool Win = false)
{
    public static KeyChord None => default;

    public bool IsEmpty => string.IsNullOrEmpty(Key);

    public bool HasModifier => Ctrl || Alt || Shift || Win;

    /// <summary>화면 표시용. "Ctrl + Shift + F", 방향키는 ←↑→↓. 미할당이면 빈 문자열.</summary>
    public string ToDisplay(bool spaced = true) => Format(display: true, spaced);

    /// <summary>설정 파일용. "Ctrl+Shift+F", 방향키는 Left/Up/Right/Down. 미할당이면 빈 문자열.</summary>
    public string Serialize() => Format(display: false, spaced: false);

    public override string ToString() => IsEmpty ? "(미할당)" : ToDisplay();

    private string Format(bool display, bool spaced)
    {
        if (IsEmpty) return string.Empty;
        var sb = new StringBuilder();
        string sep = spaced ? " + " : "+";
        if (Ctrl) sb.Append("Ctrl").Append(sep);
        if (Alt) sb.Append("Alt").Append(sep);
        if (Shift) sb.Append("Shift").Append(sep);
        if (Win) sb.Append("Win").Append(sep);
        sb.Append(display ? KeyTokens.DisplayOf(Key!) : Key);
        return sb.ToString();
    }

    /// <summary>
    /// "Ctrl+Shift+F" 형태의 문자열을 읽는다. 대소문자와 별칭(Esc/Escape, Del/Delete, Return/Enter, Back/Backspace)을 허용하고
    /// 공백은 무시한다. 빈 문자열은 "미할당" 으로 성공 처리한다. 알 수 없는 키/Modifier 가 있으면 false.
    /// </summary>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return true;

        bool ctrl = false, alt = false, shift = false, win = false;
        string? key = null;

        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0)
            {
                // "Ctrl++" 처럼 + 키 자체를 쓴 경우는 Plus 토큰으로만 허용한다(모호함 방지).
                return false;
            }

            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; continue;
                case "alt": alt = true; continue;
                case "shift": shift = true; continue;
                case "win" or "windows": win = true; continue;
            }

            if (key != null) return false;
            key = KeyTokens.Normalize(raw);
            if (key == null) return false;
        }

        if (key == null) return false;
        chord = new KeyChord(key, ctrl, alt, shift, win);
        return true;
    }
}

/// <summary>등록 가능한 키 토큰과 별칭 표.</summary>
public static class KeyTokens
{
    // 토큰 -> 화면에 보이는 글자. 표에 없는 키는 등록할 수 없다.
    private static readonly Dictionary<string, string> Display = BuildDisplay();

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Escape"] = "Esc",
        ["Del"] = "Delete",
        ["Return"] = "Enter",
        ["Back"] = "Backspace",
        ["Prior"] = "PageUp",
        ["Next"] = "PageDown",
        ["Ins"] = "Insert",
    };

    private static Dictionary<string, string> BuildDisplay()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (char c = 'A'; c <= 'Z'; c++) d[c.ToString()] = c.ToString();
        for (char c = '0'; c <= '9'; c++) d[c.ToString()] = c.ToString();
        for (int i = 1; i <= 12; i++) d["F" + i] = "F" + i;

        foreach (var name in new[] { "Enter", "Space", "Backspace", "Delete", "Esc", "Tab", "Insert", "Home", "End", "PageUp", "PageDown" })
            d[name] = name;

        d["Left"] = "←";
        d["Right"] = "→";
        d["Up"] = "↑";
        d["Down"] = "↓";

        d["Comma"] = ",";
        d["Period"] = ".";
        d["Minus"] = "-";
        d["Plus"] = "+";
        d["Slash"] = "/";
        d["Backslash"] = "\\";
        d["Semicolon"] = ";";
        d["Quote"] = "'";
        d["Backtick"] = "`";
        d["OpenBracket"] = "[";
        d["CloseBracket"] = "]";
        return d;
    }

    /// <summary>별칭/대소문자를 정리한 표준 토큰. 등록할 수 없는 키면 null.</summary>
    public static string? Normalize(string token)
    {
        if (Aliases.TryGetValue(token, out var alias)) token = alias;
        foreach (var known in Display.Keys)
            if (string.Equals(known, token, StringComparison.OrdinalIgnoreCase)) return known;
        return null;
    }

    public static bool IsKnown(string token) => Display.ContainsKey(token);

    public static string DisplayOf(string token) => Display.TryGetValue(token, out var d) ? d : token;

    public static bool IsLetterOrDigit(string token) => token.Length == 1 && char.IsAsciiLetterOrDigit(token[0]);

    public static bool IsFunctionKey(string token)
        => token.Length >= 2 && token[0] == 'F' && int.TryParse(token.AsSpan(1), out int n) && n is >= 1 and <= 12;

    /// <summary>글자·숫자·기호처럼 입력창에 글자를 만드는 키.</summary>
    public static bool IsCharacter(string token)
        => IsLetterOrDigit(token) || token is "Comma" or "Period" or "Minus" or "Plus" or "Slash" or "Backslash"
            or "Semicolon" or "Quote" or "Backtick" or "OpenBracket" or "CloseBracket";
}

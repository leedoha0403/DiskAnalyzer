namespace DiskAnalyzer.Core.Keymaps;

/// <summary>어떤 키 조합을 단축키로 등록할 수 있는가(22~24), 그리고 입력창이 포커스를 가졌을 때 누구 몫인가(25).</summary>
public static class ChordRules
{
    // 운영체제가 먼저 가져가거나(작업 관리자, 창 전환) 창 자체의 동작(닫기, 시스템 메뉴)과 겹치는 조합.
    private static readonly HashSet<KeyChord> Reserved = new()
    {
        new("F4", Alt: true),
        new("Delete", Ctrl: true, Alt: true),
        new("Tab", Alt: true),
        new("Tab", Alt: true, Shift: true),
        new("Esc", Alt: true),
        new("Esc", Ctrl: true),
        new("Esc", Ctrl: true, Shift: true),
        new("Space", Alt: true),
    };

    /// <summary>등록할 수 없으면 사용자에게 보여 줄 이유를, 등록할 수 있으면(미할당 포함) null 을 돌려준다.</summary>
    public static string? Validate(KeyChord chord)
    {
        if (chord.IsEmpty) return null;

        string key = chord.Key!;
        if (!KeyTokens.IsKnown(key)) return "지원하지 않는 키입니다.";

        if (chord.Win) return "Windows 키가 들어간 조합은 운영체제 단축키와 겹칠 수 있어 등록할 수 없습니다.";

        if (Reserved.Contains(chord))
            return $"{chord.ToDisplay()} 는 운영체제 또는 창 기본 동작에 예약되어 있어 등록할 수 없습니다.";

        // 기능키와 Enter/Space/Backspace/Delete/Esc 는 단독으로도 쓸 수 있다.
        bool standalone = KeyTokens.IsFunctionKey(key) || key is "Enter" or "Space" or "Backspace" or "Delete" or "Esc";
        if (standalone) return null;

        // 그 밖의 키(글자·숫자·기호·Tab·방향키·Home/End…)는 입력창 타이핑이나 목록 이동과 겹치므로 Ctrl 또는 Alt 가 있어야 한다.
        // Shift 만으로는 대문자 입력·범위 선택과 구분되지 않는다.
        if (!chord.HasModifier)
            return "글자·숫자 키는 Ctrl 또는 Alt 와 함께 눌러야 합니다.";
        if (!chord.Ctrl && !chord.Alt)
            return "Shift 만으로는 등록할 수 없습니다. Ctrl 또는 Alt 를 함께 누르세요.";

        return null;
    }

    /// <summary>
    /// 입력창(TextBox 등)에 포커스가 있을 때 이 조합을 전역 단축키로 가로채지 않고 입력창에 맡길지.
    /// 글자 입력, 캐럿 이동, 복사/붙여넣기/실행 취소 같은 편집 동작은 항상 입력창이 우선이다. 기능키(F1~F12)와 Alt 조합은 전역 단축키가 처리한다.
    /// </summary>
    public static bool BelongsToTextInput(KeyChord chord)
    {
        if (chord.IsEmpty) return false;
        string key = chord.Key!;

        if (KeyTokens.IsFunctionKey(key)) return false;

        // Ctrl 도 Alt 도 없으면 타이핑/편집(Backspace, Delete, Enter, Space, 화살표, 글자...)이다.
        if (!chord.Ctrl && !chord.Alt) return true;

        // Ctrl+Alt+글자 는 AltGr 조합으로 글자를 만드는 자판이 있다.
        if (chord.Ctrl && chord.Alt && KeyTokens.IsCharacter(key)) return true;

        if (chord.Ctrl && !chord.Alt)
        {
            if (!chord.Shift && key is "A" or "C" or "X" or "V" or "Y" or "Z") return true;
            if (key is "Backspace" or "Delete" or "Left" or "Right" or "Home" or "End") return true;
        }

        return false;
    }
}

using System.Windows;
using System.Windows.Controls;
using DiskAnalyzer.Core.Keymaps;

namespace DiskAnalyzer.App.Keymaps;

/// <summary>
/// 툴팁/메뉴에 현재 단축키를 붙인다. 글자를 XAML 에 박아 두지 않는다(26, 27).
///   &lt;Button km:ShortcutHint.Command="Search.Focus" km:ShortcutHint.Text="검색" /&gt;
///   -> 툴팁 "검색 (Ctrl+F)".  사용자가 키를 바꾸면 "검색 (Ctrl+Shift+F)". 미할당이면 "검색".
/// MenuItem 은 오른쪽 단축키 자리(InputGestureText)에 "Ctrl+F" 가 표시된다.
/// </summary>
public static class ShortcutHint
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(string), typeof(ShortcutHint), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(ShortcutHint), new PropertyMetadata(null, OnChanged));

    public static string? GetCommand(DependencyObject d) => (string?)d.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject d, string? value) => d.SetValue(CommandProperty, value);
    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    private const string Placeholder = "{0}";

    /// <summary>
    /// 코드에서 툴팁 글자를 만들 때: "검색 (Ctrl+F)". 미할당이면 설명만.
    /// 글자 안에 {0} 이 있으면 끝이 아니라 그 자리에 "(Ctrl+F)" 를 넣는다(여러 줄 툴팁용).
    /// </summary>
    public static string Format(string text, string commandId)
    {
        string keys = Keymap.Current.GetText(commandId);
        string hint = keys.Length == 0 ? string.Empty : $"({keys})";

        if (text.Contains(Placeholder, StringComparison.Ordinal))
            return text.Replace(Placeholder, hint).Replace(" \n", "\n").TrimEnd();

        return hint.Length == 0 ? text : $"{text} {hint}";
    }

    // 화면에 떠 있는 요소만 약한 참조로 들고 있다가 키맵이 바뀌면 다시 그린다.
    private static readonly List<WeakReference<FrameworkElement>> Live = new();
    private static bool _subscribed;

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;

        if (!_subscribed)
        {
            _subscribed = true;
            Keymap.Current.Changed += (_, _) => RefreshAll();
        }

        fe.Loaded -= OnLoaded;
        fe.Unloaded -= OnUnloaded;
        fe.Loaded += OnLoaded;
        fe.Unloaded += OnUnloaded;
        if (fe.IsLoaded) Track(fe);

        Refresh(fe);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        Track(fe);
        Refresh(fe);      // 꺼져 있는 동안 키맵이 바뀌었을 수 있다.
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e) => Untrack((FrameworkElement)sender);

    private static void Track(FrameworkElement fe)
    {
        Live.RemoveAll(w => !w.TryGetTarget(out var t) || ReferenceEquals(t, fe));
        Live.Add(new WeakReference<FrameworkElement>(fe));
    }

    private static void Untrack(FrameworkElement fe)
        => Live.RemoveAll(w => !w.TryGetTarget(out var t) || ReferenceEquals(t, fe));

    private static void RefreshAll()
    {
        foreach (var weak in Live.ToList())
            if (weak.TryGetTarget(out var fe)) Refresh(fe);
    }

    private static void Refresh(FrameworkElement fe)
    {
        string? id = GetCommand(fe);
        if (string.IsNullOrEmpty(id)) return;

        if (fe is MenuItem menuItem)
        {
            menuItem.InputGestureText = Keymap.Current.GetText(id);
            return;
        }

        string? text = GetText(fe);
        if (!string.IsNullOrEmpty(text)) fe.ToolTip = Format(text, id);
    }
}

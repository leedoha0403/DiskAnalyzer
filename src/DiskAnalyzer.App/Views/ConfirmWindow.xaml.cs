using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DiskAnalyzer.App.Views;

/// <summary>
/// 버튼 문구를 직접 정하는 확인 창. 누른 버튼의 번호가 <see cref="Result"/>, Esc / 닫기는 -1.
/// 기본 선택 버튼(Enter)은 호출자가 정한다 — 위험한 동작은 안전한 쪽을 기본으로 둔다.
/// </summary>
public partial class ConfirmWindow : Window
{
    public int Result { get; private set; } = -1;

    public ConfirmWindow(string title, string message, int defaultIndex, string[] buttons)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;

        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var b = new Button
            {
                Content = buttons[i],
                MinWidth = 96,
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = i == defaultIndex,
                Padding = new Thickness(16, 7, 16, 7),
            };
            if (i == defaultIndex) b.Style = (Style)FindResource("AccentButton");
            b.Click += (_, _) => { Result = index; Close(); };
            ButtonRow.Children.Add(b);
        }

        Loaded += (_, _) =>
        {
            if (defaultIndex >= 0 && defaultIndex < ButtonRow.Children.Count)
                ((Button)ButtonRow.Children[defaultIndex]).Focus();
        };
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Result = -1; Close(); }
    }
}

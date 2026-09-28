using System.Windows;

namespace DiskAnalyzer.App.Views;

public partial class LegalTextWindow : Window
{
    public LegalTextWindow(string title, string subtitle, string body)
    {
        InitializeComponent();
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        BodyText.Text = body;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

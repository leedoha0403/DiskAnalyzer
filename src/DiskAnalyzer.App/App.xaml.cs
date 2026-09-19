using System.Windows;
using Microsoft.Win32;

namespace DiskAnalyzer.App;

public partial class App : Application
{
    public enum AppTheme { Dark, Light, System }

    private static AppTheme _theme = AppTheme.Dark;

    public static AppTheme CurrentTheme => _theme;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyTheme(AppTheme.Dark);
    }

    /// <summary>
    /// 17. Dark / Light / System.
    /// 팔레트 ResourceDictionary 만 교체하고 모든 스타일은 DynamicResource 로 참조하므로
    /// 창을 다시 만들지 않고 즉시 반영된다.
    /// </summary>
    public static void ApplyTheme(AppTheme theme)
    {
        _theme = theme;
        bool dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark(),
        };

        var dict = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative),
        };

        var merged = Current.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = dict;
        else merged.Add(dict);
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return true;
        }
    }
}

using System.Windows;
using Microsoft.Win32;

namespace DiskAnalyzer.App;

public partial class App : Application
{
    public enum AppTheme { Dark, Light, System, Daisy, Mint }

    private static AppTheme _theme = AppTheme.Dark;

    public static AppTheme CurrentTheme => _theme;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyTheme(AppTheme.System);
    }

    /// <summary>
    /// 17. Dark / Light / System / Daisy / Mint.
    /// 팔레트 ResourceDictionary 만 교체하고 모든 스타일은 DynamicResource 로 참조하므로
    /// 창을 다시 만들지 않고 즉시 반영된다.
    /// </summary>
    public static void ApplyTheme(AppTheme theme)
    {
        _theme = theme;
        bool systemDark = theme == AppTheme.System && IsSystemDark();
        string source = theme switch
        {
            AppTheme.Daisy => "Themes/Daisy.xaml",
            AppTheme.Mint => "Themes/Mint.xaml",
            AppTheme.Dark => "Themes/Dark.xaml",
            AppTheme.Light => "Themes/Light.xaml",
            // 밝은 쪽은 예전 Light.xaml 대신 새로 다듬은 Mint 팔레트를 쓴다.
            _ => systemDark ? "Themes/Dark.xaml" : "Themes/Mint.xaml",
        };

        var dict = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };

        var merged = Current.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = dict;
        else merged.Add(dict);

        // 선버스트의 File · Free 색은 DaisyDisk 의 짙은 캔버스를 기준으로 잡았다 - 흰 배경 테마에서는
        // 뒤집어 옅게 그려야 같은 "물러나 보인다"는 인상을 준다.
        Core.Models.SunburstPalette.LightCanvas = theme is AppTheme.Light or AppTheme.Mint
            || (theme == AppTheme.System && !systemDark);
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

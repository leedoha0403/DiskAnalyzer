using System.Windows;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;
using Microsoft.Win32;

namespace DiskAnalyzer.App;

public partial class App : Application
{
    public enum AppTheme { Dark, Light, System, Navy, Mint }

    private static AppTheme _theme = AppTheme.Dark;

    public static AppTheme CurrentTheme => _theme;

    /// <summary>
    /// 프로세스 정리기에서 "관리자 권한으로 재시도"를 누르면 이 exe 를 --kill-pid=&lt;pid[,pid...]&gt; 로
    /// UAC 상승 재실행한다(ProcessCleanerService.TryKillElevated). 그 인스턴스는 창을 띄우지 않고
    /// 그 프로세스(들)만 죽인 뒤 실패한 개수를 종료 코드로 돌려주고 바로 빠진다.
    /// </summary>
    private const string ElevatedKillArgPrefix = "--kill-pid=";

    protected override void OnStartup(StartupEventArgs e)
    {
        if (TryRunElevatedKillAndExit(e.Args)) return;

        base.OnStartup(e);
        ApplyTheme(ParseTheme(UiSettings.Load().Theme));
    }

    /// <summary>설정 화면의 테마 이름(문자열로 저장)을 <see cref="AppTheme"/> 로 바꾼다.
    /// "Daisy" 는 이전 버전에서 저장된 값이라 그대로 <see cref="AppTheme.Navy"/> 로 매핑한다.</summary>
    public static AppTheme ParseTheme(string name) => name switch
    {
        "Light" => AppTheme.Light,
        "Dark" => AppTheme.Dark,
        "Navy" => AppTheme.Navy,
        "Daisy" => AppTheme.Navy,
        "Mint" => AppTheme.Mint,
        _ => AppTheme.System,
    };

    private static bool TryRunElevatedKillAndExit(string[] args)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith(ElevatedKillArgPrefix, StringComparison.Ordinal));
        if (arg == null) return false;

        var pids = arg.AsSpan(ElevatedKillArgPrefix.Length).ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => int.TryParse(s, out _))
            .Select(int.Parse);

        int failures = pids.Count(pid => ProcessCleanerService.TryKill(pid) != KillOutcome.Success);
        Environment.Exit(Math.Clamp(failures, 0, 255));
        return true;
    }

    /// <summary>
    /// 17. Dark / Light / System / Navy / Mint.
    /// 팔레트 ResourceDictionary 만 교체하고 모든 스타일은 DynamicResource 로 참조하므로
    /// 창을 다시 만들지 않고 즉시 반영된다.
    /// </summary>
    public static void ApplyTheme(AppTheme theme)
    {
        _theme = theme;
        bool systemDark = theme == AppTheme.System && IsSystemDark();
        string source = theme switch
        {
            AppTheme.Navy => "Themes/Navy.xaml",
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

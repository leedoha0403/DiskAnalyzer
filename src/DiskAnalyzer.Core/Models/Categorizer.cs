namespace DiskAnalyzer.Core.Models;

/// <summary>
/// 11. 개발 환경 관련 자동 분류.
/// 스캔 중 파일/폴더마다 호출되므로 할당 없이(ReadOnlySpan) 동작해야 한다.
/// 대소문자 무시 비교는 OrdinalIgnoreCase 스팬 비교로 처리한다.
/// </summary>
public static class Categorizer
{
    private static readonly (string Name, CategoryFlags Flag)[] DirRules =
    {
        (".git",          CategoryFlags.Git),
        (".vs",           CategoryFlags.VisualStudio),
        ("debug",         CategoryFlags.Build),
        ("release",       CategoryFlags.Build),
        ("x64",           CategoryFlags.Build),
        ("x86",           CategoryFlags.Build),
        ("bin",           CategoryFlags.Build),
        ("obj",           CategoryFlags.Build),
        ("build",         CategoryFlags.Build),
        ("node_modules",  CategoryFlags.Package),
        ("packages",      CategoryFlags.Package),
        ("nuget",         CategoryFlags.Package),
        ("log",           CategoryFlags.Log),
        ("logs",          CategoryFlags.Log),
        ("cache",         CategoryFlags.Cache),
        ("caches",        CategoryFlags.Cache),
        ("cache2",        CategoryFlags.Cache),
        ("codecache",     CategoryFlags.Cache),
        ("temp",          CategoryFlags.Temp | CategoryFlags.Cache),
        ("tmp",           CategoryFlags.Temp | CategoryFlags.Cache),
        ("docker",        CategoryFlags.Container),
        ("dockerdesktop", CategoryFlags.Container),
        ("wsl",           CategoryFlags.Container),
        ("downloads",     CategoryFlags.Download),
        ("documents",     CategoryFlags.UserData),
        ("pictures",      CategoryFlags.UserData),
        ("videos",        CategoryFlags.UserData),
        ("music",         CategoryFlags.UserData),
        ("desktop",       CategoryFlags.UserData),
        ("src",           CategoryFlags.SourceCode),
        ("source",        CategoryFlags.SourceCode),
        ("sources",       CategoryFlags.SourceCode),
    };

    /// <summary>
    /// 61. 시스템 보호 경로. 드라이브 루트 직속이 아니어도 이 이름을 만나면 보호 대상으로 본다.
    /// 상속 규칙(하위 전체가 보호 대상) 은 CleanupAnalyzer 에서 부모 체인으로 전파한다.
    /// </summary>
    private static readonly string[] ProtectedAnywhere =
    {
        "winsxs", "windowsapps", "system32", "syswow64", "driverstore",
        "system volume information", "$recycle.bin", "servicing", "assembly",
    };

    /// <summary>
    /// P0(절대 보호) 1차 분류용 폴더 이름.
    /// 정밀 판정은 ProtectionEvaluator 가 전체 경로로 다시 하지만,
    /// 수천만 개의 파일을 경로 문자열 없이 걸러내려면 이 단계가 필요하다.
    /// </summary>
    private static readonly string[] SystemCoreDirs =
    {
        "system32", "syswow64", "winsxs", "servicing", "driverstore",
        "system volume information", "boot", "recovery", "config",
    };

    private static readonly (string Ext, CategoryFlags Flag)[] ExtRules =
    {
        (".pdb",   CategoryFlags.VisualStudio | CategoryFlags.Build),
        (".obj",   CategoryFlags.VisualStudio | CategoryFlags.Build),
        (".ipch",  CategoryFlags.VisualStudio | CategoryFlags.Cache),
        (".ilk",   CategoryFlags.Build),
        (".pch",   CategoryFlags.Build),
        (".lib",   CategoryFlags.Build),
        (".log",   CategoryFlags.Log),
        (".tlog",  CategoryFlags.Log | CategoryFlags.Build),
        (".etl",   CategoryFlags.Log),
        (".dmp",   CategoryFlags.Log),
        (".tmp",   CategoryFlags.Cache),
        (".cache", CategoryFlags.Cache),
        (".vhdx",  CategoryFlags.Container),
        (".vhd",   CategoryFlags.Container),
        (".zip",   CategoryFlags.Archive),
        (".7z",    CategoryFlags.Archive),
        (".rar",   CategoryFlags.Archive),
        (".gz",    CategoryFlags.Archive),
        (".iso",   CategoryFlags.Archive | CategoryFlags.Installer),
        (".msi",   CategoryFlags.Installer),
        (".msu",   CategoryFlags.Installer),
        (".appx",  CategoryFlags.Installer),
        (".msix",  CategoryFlags.Installer),
        (".mp4",   CategoryFlags.Media),
        (".mkv",   CategoryFlags.Media),
        (".avi",   CategoryFlags.Media),
        (".mov",   CategoryFlags.Media),
        (".wav",   CategoryFlags.Media),
    };

    /// <summary>16. 삭제 안전성 - 특별 경고가 필요한 시스템 폴더(드라이브 루트 직속 기준).</summary>
    private static readonly string[] SystemRootDirs =
    {
        "windows", "program files", "program files (x86)", "programdata",
        "system volume information", "winsxs", "recovery",
    };

    public static CategoryFlags ForDirectory(ReadOnlySpan<char> name, bool isDriveRootChild)
    {
        CategoryFlags flags = CategoryFlags.None;

        foreach (var (rule, flag) in DirRules)
        {
            if (name.Equals(rule, StringComparison.OrdinalIgnoreCase)) { flags |= flag; break; }
        }

        if (isDriveRootChild)
        {
            foreach (var sys in SystemRootDirs)
            {
                if (name.Equals(sys, StringComparison.OrdinalIgnoreCase)) { flags |= CategoryFlags.System; break; }
            }
        }

        foreach (var sys in ProtectedAnywhere)
        {
            if (name.Equals(sys, StringComparison.OrdinalIgnoreCase)) { flags |= CategoryFlags.System; break; }
        }

        // P0 1차 분류. "config" 는 Windows\System32\config 만 의미가 있으므로
        // 단독으로는 System 플래그가 이미 붙은 트리 안에서만 SystemCore 로 승격된다(상속 단계에서 처리).
        foreach (var core in SystemCoreDirs)
        {
            if (name.Equals(core, StringComparison.OrdinalIgnoreCase))
            {
                flags |= CategoryFlags.SystemCore | CategoryFlags.System;
                break;
            }
        }

        if (name.Equals("appdata", StringComparison.OrdinalIgnoreCase))
            flags |= CategoryFlags.AppData;

        return flags;
    }

    public static CategoryFlags ForExtension(ReadOnlySpan<char> ext)
    {
        if (ext.IsEmpty) return CategoryFlags.None;
        foreach (var (rule, flag) in ExtRules)
        {
            if (ext.Equals(rule, StringComparison.OrdinalIgnoreCase)) return flag;
        }
        return CategoryFlags.None;
    }

    /// <summary>16. 삭제 경고 대상인지 판정한다(전체 경로 기준).</summary>
    public static bool IsProtectedPath(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return true;
        string p = fullPath.Replace('/', '\\').TrimEnd('\\');
        if (p.Length <= 3) return true;  // 드라이브 루트 자체

        int slash = p.IndexOf('\\');
        if (slash < 0 || slash + 1 >= p.Length) return true;

        int next = p.IndexOf('\\', slash + 1);
        string first = next < 0 ? p[(slash + 1)..] : p[(slash + 1)..next];

        foreach (var sys in SystemRootDirs)
        {
            if (string.Equals(first, sys, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return p.Contains("\\WinSxS", StringComparison.OrdinalIgnoreCase)
            || p.Contains("System Volume Information", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 10. 삭제 확인창에 띄울 경고 문구.
    /// "중요 파일일 가능성"을 사용자에게 알리기 위한 것이지, 프로그램이 삭제를 막지는 않는다.
    /// </summary>
    public static IReadOnlyList<string> DeleteWarnings(string fullPath, CategoryFlags flags)
    {
        if (string.IsNullOrEmpty(fullPath)) return Array.Empty<string>();

        var list = new List<string>(2);
        string p = fullPath.Replace('/', '\\');

        if (p.Contains(@"\Program Files", StringComparison.OrdinalIgnoreCase))
            list.Add("Program Files 내부 파일");
        else if (IsProtectedPath(p))
            list.Add("Windows 시스템 영역");

        if (p.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith(@"\.git", StringComparison.OrdinalIgnoreCase) ||
            (flags & CategoryFlags.Git) != 0)
            list.Add("Git Repository 내부 파일");

        if ((flags & CategoryFlags.UserData) != 0 ||
            p.Contains(@"\Documents\", StringComparison.OrdinalIgnoreCase) ||
            p.Contains(@"\Pictures\", StringComparison.OrdinalIgnoreCase) ||
            p.Contains(@"\Videos\", StringComparison.OrdinalIgnoreCase) ||
            p.Contains(@"\Desktop\", StringComparison.OrdinalIgnoreCase))
            list.Add("사용자 문서");

        // 실행 파일/모듈은 지금 돌고 있는 프로세스가 붙들고 있을 수 있다.
        if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith(".sys", StringComparison.OrdinalIgnoreCase))
            list.Add("실행 중인 프로그램에서 사용 중일 수 있음");

        return list;
    }

    public static string ToTagString(CategoryFlags flags)
    {
        if (flags == CategoryFlags.None) return string.Empty;
        var parts = new List<string>(3);
        if ((flags & CategoryFlags.VisualStudio) != 0) parts.Add("VISUAL STUDIO");
        if ((flags & CategoryFlags.Build) != 0) parts.Add("BUILD");
        if ((flags & CategoryFlags.Temp) != 0) parts.Add("TEMP");
        else if ((flags & CategoryFlags.Cache) != 0) parts.Add("CACHE");
        if ((flags & CategoryFlags.Log) != 0) parts.Add("LOG");
        if ((flags & CategoryFlags.Git) != 0) parts.Add("GIT");
        if ((flags & CategoryFlags.Package) != 0) parts.Add("PACKAGE");
        if ((flags & CategoryFlags.Container) != 0) parts.Add("CONTAINER");
        if ((flags & CategoryFlags.System) != 0) parts.Add("SYSTEM");
        if ((flags & CategoryFlags.Media) != 0) parts.Add("MEDIA");
        if ((flags & CategoryFlags.Archive) != 0) parts.Add("ARCHIVE");
        if ((flags & CategoryFlags.Installer) != 0) parts.Add("INSTALLER");
        if ((flags & CategoryFlags.Download) != 0) parts.Add("DOWNLOAD");
        if ((flags & CategoryFlags.UserData) != 0) parts.Add("USER DATA");
        if ((flags & CategoryFlags.SourceCode) != 0) parts.Add("SOURCE");

        var sb = new System.Text.StringBuilder();
        foreach (var p in parts)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append('[').Append(p).Append(']');
        }
        return sb.ToString();
    }
}

using DiskAnalyzer.Core.Interop;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.Core.QuickMove;

/// <summary>
/// 빠른 이동에서 쓰는 경로 계산. 전부 문자열 연산이라 디스크 I/O 를 하지 않는다(GetFreeSpace / VolumeLabel 제외).
/// Windows 경로는 대소문자를 구분하지 않으므로 모든 비교는 OrdinalIgnoreCase 다.
/// </summary>
public static class PathUtil
{
    /// <summary>전체 경로로 바꾸고 끝의 '\' 를 없앤다(드라이브 루트 "C:\" 는 유지). 잘못된 경로면 예외.</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("경로가 비어 있습니다.", nameof(path));

        path = path.Trim().Trim('"');
        if (path.Length == 2 && path[1] == ':') path += "\\";   // "C:" 는 "그 드라이브의 현재 폴더"라서 루트로 고정한다

        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        if (full.Length > root.Length) full = full.TrimEnd('\\', '/');
        return full;
    }

    public static bool TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { normalized = Normalize(path); return true; }
        catch (Exception) { return false; }
    }

    public static bool Equal(string a, string b)
        => string.Equals(TrimEnd(a), TrimEnd(b), StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="child"/> 가 <paramref name="parent"/> 와 같거나 그 아래인가.</summary>
    public static bool IsSameOrUnder(string parent, string child)
        => Equal(parent, child) || IsUnder(parent, child);

    /// <summary><paramref name="child"/> 가 <paramref name="parent"/> 의 <b>안쪽</b>인가(같은 경로는 false).</summary>
    public static bool IsUnder(string parent, string child)
    {
        string p = TrimEnd(parent);
        string c = TrimEnd(child);
        return c.Length > p.Length
               && c.StartsWith(p, StringComparison.OrdinalIgnoreCase)
               && c[p.Length] == '\\';
    }

    /// <summary>상위 폴더. 루트(드라이브)에서는 null.</summary>
    public static string? Parent(string path)
    {
        string p = TrimEnd(path);
        string? root = Path.GetPathRoot(p);
        if (root != null && string.Equals(TrimEnd(root), p, StringComparison.OrdinalIgnoreCase)) return null;
        int i = p.LastIndexOf('\\');
        if (i < 0) return null;
        string parent = p[..i];
        return parent.Length == 2 && parent[1] == ':' ? parent + "\\" : parent;
    }

    /// <summary>경로 조각(Breadcrumb 용). "C:\Work\A" → C:\ / C:\Work / C:\Work\A.</summary>
    public static IReadOnlyList<(string Name, string Path)> Segments(string path)
    {
        var list = new List<(string, string)>();
        string? cur = TrimEnd(path);
        while (cur != null)
        {
            string name = NameOf(cur);
            list.Add((name, cur));
            cur = Parent(cur);
        }
        list.Reverse();
        return list;
    }

    /// <summary>표시용 이름. 루트는 "C:" / "\\server\share", 그 외는 마지막 폴더 이름.</summary>
    public static string NameOf(string path)
    {
        string p = TrimEnd(path);
        string root = TrimEnd(Path.GetPathRoot(p) ?? string.Empty);
        if (string.Equals(root, p, StringComparison.OrdinalIgnoreCase)) return p.TrimEnd('\\');
        int i = p.LastIndexOf('\\');
        return i >= 0 ? p[(i + 1)..] : p;
    }

    public static string RootOf(string path)
    {
        try { return Path.GetPathRoot(Normalize(path)) ?? string.Empty; }
        catch (Exception) { return string.Empty; }
    }

    /// <summary>
    /// 두 경로가 같은 볼륨 위에 있는가(루트 문자열 비교).
    /// 마운트 폴더처럼 예외가 있어서 <b>안내 문구용 추정</b>일 뿐이다. 실제 이동은 MoveFileEx 가 ERROR_NOT_SAME_DEVICE 를
    /// 돌려주는지로 판단하므로 이 추정이 틀려도 결과는 항상 올바르다.
    /// </summary>
    public static bool SameVolume(string a, string b)
        => string.Equals(RootOf(a), RootOf(b), StringComparison.OrdinalIgnoreCase);

    public static string ToExtended(string path) => FastDeleter.ToExtended(path);

    /// <summary>"name (2).ext" 처럼 존재하지 않는 이름을 찾는다.</summary>
    public static string UniqueName(string directory, string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        // 폴더는 확장자 개념이 없다("v1.2" 가 확장자 ".2" 로 잘리면 "v1 (2).2" 가 된다).
        // 호출자가 폴더 여부를 알려 주지 않으므로 존재 여부만으로 후보를 만든다.
        string candidate = Path.Combine(directory, name);
        for (int i = 2; i < 10000 && Exists(candidate); i++)
            candidate = Path.Combine(directory, $"{stem} ({i}){ext}");
        return candidate;
    }

    public static string UniqueNameForDirectory(string directory, string name)
    {
        string candidate = Path.Combine(directory, name);
        for (int i = 2; i < 10000 && Exists(candidate); i++)
            candidate = Path.Combine(directory, $"{name} ({i})");
        return candidate;
    }

    public static bool Exists(string path)
        => Win32.GetFileAttributes(ToExtendedSafe(path)) != 0xFFFFFFFF;

    /// <summary>파일이면 false, 폴더면 true. 없으면 null.</summary>
    public static bool? IsDirectory(string path)
    {
        uint a = Win32.GetFileAttributes(ToExtendedSafe(path));
        if (a == 0xFFFFFFFF) return null;
        return (a & Win32.FILE_ATTRIBUTE_DIRECTORY) != 0;
    }

    /// <summary>이 경로가 있는 볼륨의 사용 가능 용량. 알 수 없으면 -1.</summary>
    public static long GetFreeSpace(string path)
    {
        try
        {
            string p = path;
            // 존재하는 상위 폴더까지 올라간다.
            while (!string.IsNullOrEmpty(p) && !Directory.Exists(p))
            {
                string? parent = Parent(p);
                if (parent == null) break;
                p = parent;
            }
            if (string.IsNullOrEmpty(p)) return -1;
            if (!p.EndsWith('\\')) p += "\\";
            return Win32.GetDiskFreeSpaceEx(p, out ulong free, out _, out _) ? (long)Math.Min(free, long.MaxValue) : -1;
        }
        catch (Exception) { return -1; }
    }

    /// <summary>"USB" / "네트워크" / "" — 속도가 느릴 수 있는 위치를 알려 주는 짧은 표시.</summary>
    public static string VolumeTag(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal))
                return "네트워크";
            string root = RootOf(path);
            if (root.Length < 2 || root[1] != ':') return string.Empty;
            return new DriveInfo(root[..1]).DriveType switch
            {
                DriveType.Removable => "USB",
                DriveType.Network => "네트워크",
                DriveType.CDRom => "광학",
                _ => string.Empty,
            };
        }
        catch (Exception) { return string.Empty; }
    }

    /// <summary>"D:" 처럼 표시할 드라이브 이름.</summary>
    public static string DriveName(string path)
        => RootOf(path).TrimEnd('\\');

    internal static string ToExtendedSafe(string path)
    {
        try { return ToExtended(path); }
        catch (Exception) { return path; }
    }

    private static string TrimEnd(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        string root = Path.GetPathRoot(path) ?? string.Empty;
        return path.Length > root.Length ? path.TrimEnd('\\', '/') : path.TrimEnd('/') is { Length: > 0 } t ? t : path;
    }
}

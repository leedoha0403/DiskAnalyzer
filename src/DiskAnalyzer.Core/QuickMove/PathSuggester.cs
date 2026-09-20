namespace DiskAnalyzer.Core.QuickMove;

/// <summary>
/// 경로를 직접 입력할 때의 폴더 자동완성. 깊은 폴더까지 한 단계씩 클릭해서 들어가는 대신 "C:\Wo" 까지만 쳐도 후보를 보여 준다.
///
///  - 마지막 '\' 앞을 폴더로, 뒤를 접두어로 본다. 후보는 그 폴더 <b>바로 아래의 폴더</b>만이다(파일은 이동 대상 위치가 아니다).
///  - 드라이브 글자만 입력하면("c" / "C:") 드라이브 목록을 준다.
///  - %USERPROFILE% 같은 환경 변수를 펼친다.
///  - 숨김/시스템 폴더는 옵션을 켰을 때만 보인다(목록과 같은 정책).
/// 디스크를 읽으므로 UI 스레드에서 부르지 말 것.
/// </summary>
public static class PathSuggester
{
    private const int MaxScan = 400;   // 폴더 하나에 수만 개가 있어도 입력할 때마다 전부 훑지 않는다

    public static IReadOnlyList<string> Suggest(string? text, int max = 12, bool includeHidden = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        string t;
        try { t = Environment.ExpandEnvironmentVariables(text.Trim().Trim('"')).Replace('/', '\\'); }
        catch (Exception) { return Array.Empty<string>(); }
        if (t.Length == 0) return Array.Empty<string>();

        // 드라이브 글자만: "c" / "C:" → C:\
        if (t.Length <= 2 && char.IsLetter(t[0]) && (t.Length == 1 || t[1] == ':'))
        {
            return DriveInfo.GetDrives()
                .Select(d => d.Name)
                .Where(n => n.StartsWith(t[..1], StringComparison.OrdinalIgnoreCase))
                .Take(max)
                .ToList();
        }

        string dir, prefix;
        int slash = t.LastIndexOf('\\');
        if (slash < 0) return Array.Empty<string>();   // 폴더 부분이 없으면 어디를 뒤질지 알 수 없다
        dir = t[..(slash + 1)];
        prefix = t[(slash + 1)..];

        // UNC 서버 이름 단계(\\server)는 열거하지 않는다. 네트워크를 뒤지느라 멈출 수 있다.
        if (dir.StartsWith(@"\\", StringComparison.Ordinal) && dir.Count(c => c == '\\') < 4)
            return Array.Empty<string>();

        try
        {
            if (!Directory.Exists(dir)) return Array.Empty<string>();

            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = includeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
            };

            var found = new List<string>();
            foreach (string? name in Directory.EnumerateDirectories(dir, "*", options).Select(Path.GetFileName))
            {
                ct.ThrowIfCancellationRequested();
                if (name != null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(name);
                    if (found.Count >= MaxScan) break;
                }
            }

            return found
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .Select(n => dir + n)
                .ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Array.Empty<string>(); }
    }
}

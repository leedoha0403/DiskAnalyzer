using System.Text.Json;

namespace DiskAnalyzer.Core.Services;

/// <summary>
/// 프로세스 정리기에서 "멈춤 의심 / 확인 필요"로 치지 않을 프로세스 이름 목록.
/// node 처럼 조용히 대기하는 게 정상인 프로세스가 계속 의심으로 뜨는 것을 막는다.
/// %LocalAppData%\DiskAnalyzer\process-ignore.json 에 이름 배열로 저장한다(확장자 없는 프로세스 이름, 대소문자 무시).
/// </summary>
public static class ProcessIgnoreList
{
    private static readonly object Gate = new();
    private static HashSet<string>? _names;

    /// <summary>목록이 바뀌면(추가/해제) 발생한다. 어느 스레드에서 올 수 있다.</summary>
    public static event Action? Changed;

    /// <summary>환경 변수 DISKANALYZER_PROCESS_IGNORE 가 있으면 그 경로를 쓴다(자동 검증이 실제 설정을 건드리지 않게).</summary>
    public static string DefaultPath { get; } =
        Environment.GetEnvironmentVariable("DISKANALYZER_PROCESS_IGNORE") is { Length: > 0 } custom
            ? custom
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DiskAnalyzer", "process-ignore.json");

    private static HashSet<string> Load()
    {
        if (_names != null) return _names;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(DefaultPath))
            {
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(DefaultPath));
                if (list != null) foreach (var n in list) if (!string.IsNullOrWhiteSpace(n)) set.Add(Normalize(n));
            }
        }
        catch (Exception)
        {
            // 손상된 파일 때문에 정리기가 안 뜨면 안 된다 - 빈 목록으로 시작한다.
        }

        return _names = set;
    }

    private static string Normalize(string name)
    {
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static void Save(HashSet<string> set)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
            string temp = DefaultPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(set.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, DefaultPath, overwrite: true);
        }
        catch (Exception)
        {
            // 저장이 안 돼도 이번 실행 동안은 메모리 목록이 적용된다.
        }
    }

    public static bool Contains(string processName)
    {
        lock (Gate) return Load().Contains(Normalize(processName));
    }

    public static IReadOnlyList<string> Names
    {
        get { lock (Gate) return Load().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(); }
    }

    public static bool Add(string processName)
    {
        bool changed;
        lock (Gate)
        {
            var set = Load();
            changed = set.Add(Normalize(processName));
            if (changed) Save(set);
        }
        if (changed) Changed?.Invoke();
        return changed;
    }

    public static bool Remove(string processName)
    {
        bool changed;
        lock (Gate)
        {
            var set = Load();
            changed = set.Remove(Normalize(processName));
            if (changed) Save(set);
        }
        if (changed) Changed?.Invoke();
        return changed;
    }

    public static void Clear()
    {
        bool changed;
        lock (Gate)
        {
            var set = Load();
            changed = set.Count > 0;
            set.Clear();
            if (changed) Save(set);
        }
        if (changed) Changed?.Invoke();
    }
}

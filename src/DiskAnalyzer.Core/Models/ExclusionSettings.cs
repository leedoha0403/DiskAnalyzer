using System.Text.Json;

namespace DiskAnalyzer.Core.Models;

/// <summary>
/// 48. 스캔 제외 패턴을 앱을 껐다 켜도 기억한다. %LocalAppData%\DiskAnalyzer\exclusions.json.
/// 패턴 문법은 <see cref="ExclusionRules"/> 에 있다.
/// </summary>
public sealed class ExclusionSettings
{
    public List<string> Patterns { get; set; } = new();

    /// <summary>
    /// 환경 변수 DISKANALYZER_EXCLUSIONS 가 있으면 그 경로를 쓴다 —
    /// 자동 검증 도구가 사용자의 실제 설정을 건드리지 않게 하는 통로다(quickmove.json 과 같은 규칙).
    /// </summary>
    public static string DefaultPath { get; } =
        Environment.GetEnvironmentVariable("DISKANALYZER_EXCLUSIONS") is { Length: > 0 } custom
            ? custom
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DiskAnalyzer", "exclusions.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static ExclusionSettings Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new ExclusionSettings();
            var s = JsonSerializer.Deserialize<ExclusionSettings>(File.ReadAllText(path), Json);
            if (s == null) return new ExclusionSettings();
            s.Patterns = s.Patterns?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new();
            return s;
        }
        catch (Exception)
        {
            // 손상된 설정 파일 때문에 스캔을 못 하면 안 된다. 제외 없음으로 시작한다.
            return new ExclusionSettings();
        }
    }

    public bool Save(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>설정 화면의 여러 줄 입력을 패턴 목록으로 바꾼다.</summary>
    public static List<string> SplitLines(string? text)
        => (text ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    public string ToText() => string.Join(Environment.NewLine, Patterns);
}

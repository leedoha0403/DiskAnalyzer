using System.Text.Json;

namespace DiskAnalyzer.Core.Models;

/// <summary>
/// 앱을 껐다 켜도 기억하는 화면 상태. %LocalAppData%\DiskAnalyzer\ui.json.
/// 스캔 결과나 제외 규칙처럼 "무엇을 읽을지"가 아니라 "어떻게 보여 줄지"만 담는다
/// (제외 규칙은 <see cref="ExclusionSettings"/>, 빠른 이동은 quickmove.json 이 따로 가진다).
/// </summary>
public sealed class UiSettings
{
    /// <summary>
    /// 49. 검색 결과를 폴더 트리 + 이름 접기로 본다. 기본값은 켜짐 —
    /// 검색은 같은 이름이 여러 경로에 흩어져 나오는 것이 보통이라, 평면 목록은 같은 줄을 반복해서 읽게 만든다.
    /// </summary>
    public bool SearchGroupByPath { get; set; } = true;

    /// <summary>왼쪽 저장소 사이드바를 접어 두었는가. 좁은 화면에서 한 번 접으면 계속 접힌 채로 쓰는 것이 보통이다.</summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary>사이드바 폭(px). 0 이면 기본값을 쓴다.</summary>
    public double SidebarWidth { get; set; }

    /// <summary>선버스트 링의 색 기준(<c>SunburstTint</c>). 이름으로 저장해 값이 밀려도 안전하다.</summary>
    public string RingTint { get; set; } = nameof(SunburstTint.Size);

    /// <summary>크기를 논리로 볼지 물리(디스크 할당)로 볼지. 이름으로 저장한다.</summary>
    public string SizeBasis { get; set; } = nameof(Models.SizeBasis.Logical);

    /// <summary>하드 링크를 한 번만 셀지.</summary>
    public bool DeduplicateHardLinks { get; set; }

    /// <summary>
    /// 환경 변수 DISKANALYZER_UI_SETTINGS 가 있으면 그 경로를 쓴다 —
    /// 자동 검증 도구가 사용자의 실제 설정을 건드리지 않게 하는 통로다(quickmove.json 과 같은 규칙).
    /// </summary>
    public static string DefaultPath { get; } =
        Environment.GetEnvironmentVariable("DISKANALYZER_UI_SETTINGS") is { Length: > 0 } custom
            ? custom
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DiskAnalyzer", "ui.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static UiSettings Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new UiSettings();
            return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(path), Json) ?? new UiSettings();
        }
        catch (Exception)
        {
            // 손상된 설정 파일 때문에 화면이 안 뜨면 안 된다. 기본값으로 시작한다.
            return new UiSettings();
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
}

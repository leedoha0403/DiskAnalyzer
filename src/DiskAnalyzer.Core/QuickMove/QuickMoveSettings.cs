using System.Text.Json;

namespace DiskAnalyzer.Core.QuickMove;

/// <summary>
/// 빠른 이동 탭이 앱을 껐다 켜도 기억하는 값. %LocalAppData%\DiskAnalyzer\quickmove.json.
/// 즐겨찾기와 최근 위치는 좌우 패널이 함께 쓴다(프로그램 전역). 진행 중이던 이동 작업은 저장하지 않는다.
/// </summary>
public sealed class QuickMoveSettings
{
    public const int MaxRecents = 20;

    public List<string> Favorites { get; set; } = new();
    public List<string> Recents { get; set; } = new();

    public string? LeftPath { get; set; }
    public string? RightPath { get; set; }

    public string LeftSortKey { get; set; } = "name";
    public bool LeftSortAscending { get; set; } = true;
    public string RightSortKey { get; set; } = "name";
    public bool RightSortAscending { get; set; } = true;

    /// <summary>0 이면 기본 높이. 사용자가 끌어서 정한 대기열 영역 높이.</summary>
    public double QueueHeight { get; set; }

    public bool QueueCollapsed { get; set; }

    /// <summary>폴더 / Treemap 같은 분석 탭 오른쪽에 붙는 "빠른 이동 도크"를 보이는가.</summary>
    public bool DockVisible { get; set; }

    /// <summary>도크 너비. 0 이면 기본 너비.</summary>
    public double DockWidth { get; set; }
    public bool ShowHidden { get; set; }
    public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.Ask;

    /// <summary>
    /// 설정 파일 위치. 환경 변수 DISKANALYZER_QM_SETTINGS 가 있으면 그 경로를 쓴다 —
    /// 자동 화면 검증 도구가 사용자의 실제 설정(즐겨찾기 / 최근 위치)을 건드리지 않게 하는 통로다.
    /// </summary>
    public static string DefaultPath { get; } =
        Environment.GetEnvironmentVariable("DISKANALYZER_QM_SETTINGS") is { Length: > 0 } custom
            ? custom
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DiskAnalyzer", "quickmove.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static QuickMoveSettings Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new QuickMoveSettings();
            var s = JsonSerializer.Deserialize<QuickMoveSettings>(File.ReadAllText(path), Json);
            return s?.Sanitized() ?? new QuickMoveSettings();
        }
        catch (Exception)
        {
            // 손상된 설정 파일 때문에 기능을 못 쓰면 안 된다. 기본값으로 시작한다.
            return new QuickMoveSettings();
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

    public bool IsFavorite(string path) => Favorites.Any(f => PathUtil.Equal(f, path));

    /// <summary>즐겨찾기에 없으면 넣고 있으면 뺀다. 넣었으면 true.</summary>
    public bool ToggleFavorite(string path)
    {
        int i = Favorites.FindIndex(f => PathUtil.Equal(f, path));
        if (i >= 0) { Favorites.RemoveAt(i); return false; }
        Favorites.Add(path);
        return true;
    }

    /// <summary>최근 위치의 맨 앞에 넣는다. 같은 경로는 중복 저장하지 않고 앞으로 올린다.</summary>
    public void AddRecent(string path)
    {
        Recents.RemoveAll(r => PathUtil.Equal(r, path));
        Recents.Insert(0, path);
        if (Recents.Count > MaxRecents) Recents.RemoveRange(MaxRecents, Recents.Count - MaxRecents);
    }

    private QuickMoveSettings Sanitized()
    {
        Favorites = Favorites?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new();
        Recents = Recents?.Where(p => !string.IsNullOrWhiteSpace(p)).Take(MaxRecents).ToList() ?? new();
        if (QueueHeight < 0 || double.IsNaN(QueueHeight)) QueueHeight = 0;
        if (DockWidth < 0 || double.IsNaN(DockWidth)) DockWidth = 0;
        return this;
    }
}

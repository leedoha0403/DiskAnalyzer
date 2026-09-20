namespace DiskAnalyzer.Core.Keymaps;

/// <summary>
/// 단축키는 UI 요소가 아니라 Command ID 에 묶인다. 설정 파일·툴팁·메뉴·도움말이 모두 이 ID 로 현재 키를 찾는다.
/// </summary>
public static class CommandIds
{
    public const string TabsFolder = "Tabs.Folder";
    public const string TabsTreemap = "Tabs.Treemap";
    public const string TabsLargeFiles = "Tabs.LargeFiles";
    public const string TabsFileTypes = "Tabs.FileTypes";
    public const string TabsCleanup = "Tabs.Cleanup";
    public const string TabsQuickMove = "Tabs.QuickMove";
    public const string TabsNext = "Tabs.Next";
    public const string TabsPrevious = "Tabs.Previous";

    public const string GoParent = "Navigation.GoParent";
    public const string GoParentAlt = "Navigation.GoParentAlt";
    public const string GoBack = "Navigation.GoBack";
    public const string GoForward = "Navigation.GoForward";
    public const string FocusPath = "Navigation.FocusPath";
    public const string GoRoot = "Navigation.GoRoot";

    public const string SearchFocus = "Search.Focus";
    public const string SearchCancel = "Search.Cancel";

    public const string Refresh = "View.Refresh";
    public const string ForceRescan = "View.ForceRescan";

    public const string SelectAll = "Selection.SelectAll";
    public const string Open = "Selection.Open";
    public const string ShowDetails = "Selection.ShowDetails";

    public const string CopyPath = "Clipboard.CopyPath";
    public const string CopyPathList = "Clipboard.CopyPathList";

    public const string QueueSelected = "QuickMove.QueueSelected";
    public const string StartMove = "QuickMove.Start";
    public const string RemoveFromQueue = "QuickMove.RemoveFromQueue";
}

/// <param name="Id">설정 파일에 저장되는 안정적인 이름. 한 번 정하면 바꾸지 않는다.</param>
/// <param name="Category">설정 화면의 분류.</param>
/// <param name="Name">설정 화면에 보이는 기능 이름.</param>
/// <param name="Default">기본 단축키. default 이면 기본적으로 미할당.</param>
public sealed record CommandDef(string Id, string Category, string Name, KeyChord Default);

public static class CommandCatalog
{
    private const string Tabs = "탭";
    private const string Nav = "탐색";
    private const string Search = "검색";
    private const string Refresh = "새로고침";
    private const string Select = "선택";
    private const string Copy = "복사";
    private const string QuickMove = "빠른 이동";

    private static KeyChord Ctrl(string key) => new(key, Ctrl: true);
    private static KeyChord Alt(string key) => new(key, Alt: true);
    private static KeyChord Bare(string key) => new(key);

    /// <summary>설정 화면에 나열되는 순서대로.</summary>
    public static IReadOnlyList<CommandDef> All { get; } = new CommandDef[]
    {
        new(CommandIds.TabsFolder, Tabs, "폴더 탭", Ctrl("1")),
        new(CommandIds.TabsTreemap, Tabs, "Treemap 탭", Ctrl("2")),
        new(CommandIds.TabsLargeFiles, Tabs, "큰 파일 탭", Ctrl("3")),
        new(CommandIds.TabsFileTypes, Tabs, "파일 유형 탭", Ctrl("4")),
        new(CommandIds.TabsCleanup, Tabs, "정리 추천 탭", Ctrl("5")),
        new(CommandIds.TabsQuickMove, Tabs, "빠른 이동 탭", Ctrl("6")),
        new(CommandIds.TabsNext, Tabs, "다음 탭", Ctrl("Tab")),
        new(CommandIds.TabsPrevious, Tabs, "이전 탭", new("Tab", Ctrl: true, Shift: true)),

        new(CommandIds.GoParent, Nav, "상위 폴더", Bare("Backspace")),
        new(CommandIds.GoParentAlt, Nav, "상위 폴더 (보조)", Alt("Up")),
        new(CommandIds.GoBack, Nav, "이전 위치", Alt("Left")),
        new(CommandIds.GoForward, Nav, "다음 위치", Alt("Right")),
        new(CommandIds.FocusPath, Nav, "경로 입력", Ctrl("L")),
        new(CommandIds.GoRoot, Nav, "스캔 루트", Ctrl("Home")),

        new(CommandIds.SearchFocus, Search, "검색", Ctrl("F")),
        new(CommandIds.SearchCancel, Search, "검색 / 선택 취소", Bare("Esc")),

        new(CommandIds.Refresh, Refresh, "현재 화면 갱신", Bare("F5")),
        new(CommandIds.ForceRescan, Refresh, "강제 재스캔", Ctrl("F5")),

        new(CommandIds.SelectAll, Select, "전체 선택", Ctrl("A")),
        new(CommandIds.Open, Select, "열기 / 폴더 진입", Bare("Enter")),
        new(CommandIds.ShowDetails, Select, "상세정보", Bare("Space")),

        new(CommandIds.CopyPath, Copy, "선택 항목 경로 복사", Ctrl("C")),
        new(CommandIds.CopyPathList, Copy, "선택 항목 전체 경로 목록 복사", new("C", Ctrl: true, Shift: true)),

        new(CommandIds.QueueSelected, QuickMove, "선택 항목을 이동 대기열에 추가", Ctrl("Enter")),
        new(CommandIds.StartMove, QuickMove, "대기열 이동 시작", new("Enter", Ctrl: true, Shift: true)),
        new(CommandIds.RemoveFromQueue, QuickMove, "대기열에서 제거", Bare("Delete")),
    };

    private static readonly Dictionary<string, CommandDef> ById = All.ToDictionary(c => c.Id, StringComparer.Ordinal);

    public static bool TryGet(string id, out CommandDef def) => ById.TryGetValue(id, out def!);

    public static CommandDef? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>분류 필터에 쓰는 이름들. 목록 순서를 따른다.</summary>
    public static IReadOnlyList<string> Categories { get; } = All.Select(c => c.Category).Distinct().ToList();
}

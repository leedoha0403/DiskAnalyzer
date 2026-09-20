using System.Text.Json;

namespace DiskAnalyzer.Core.Keymaps;

public enum KeymapApplyResult
{
    Ok,

    /// <summary>충돌이 있거나 등록할 수 없는 키가 있어 아무것도 바꾸지 않았다.</summary>
    Rejected,

    /// <summary>이번 실행에는 반영했지만 설정 파일에 저장하지 못했다.</summary>
    SaveFailed,
}

/// <summary>
/// 지금 유효한 키맵. 기본값에 사용자가 바꾼 항목만 덮어쓴 결과이며, 프로그램 전체(라우터·툴팁·메뉴)가 이 객체 하나를 본다.
///
/// 저장 파일에는 "기본값과 다른 항목" 만 들어간다(31). 새 버전에서 명령이 추가돼도 기존 사용자 설정과 섞이지 않는다.
/// 사용자가 일부러 비운 항목(미할당)은 빈 문자열로 저장해, "안 건드림" 과 구분한다.
/// </summary>
public sealed class Keymap
{
    public const int FileVersion = 1;

    // 기본 인코더는 '+' 를 + 로 써서 사람이 읽고 고칠 수 없다. 이 파일에는 키 이름만 들어가므로 완화해도 안전하다.
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string? _path;
    private Dictionary<string, KeyChord> _overrides = new(StringComparer.Ordinal);
    private Dictionary<string, KeyChord> _effective = new(StringComparer.Ordinal);
    private Dictionary<KeyChord, string> _byChord = new();

    private static Keymap? _current;

    /// <summary>앱 전체가 쓰는 키맵. 처음 접근할 때 %LocalAppData%\DiskAnalyzer\keymap.json 에서 읽는다.</summary>
    public static Keymap Current
    {
        get => _current ??= Load();
        set => _current = value;
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskAnalyzer", "keymap.json");

    /// <param name="path">null 이면 저장하지 않는 메모리 전용 키맵(테스트/미리보기).</param>
    public Keymap(string? path = null)
    {
        _path = path;
        Rebuild();
    }

    /// <summary>키맵을 바꾸면(적용) 발생한다. 툴팁 같은 표시부는 이 이벤트로 다시 그린다.</summary>
    public event EventHandler? Changed;

    public string? FilePath => _path;

    /// <summary>모든 명령의 현재 키. 미할당은 default.</summary>
    public IReadOnlyDictionary<string, KeyChord> Effective => _effective;

    /// <summary>기본값과 다르게 저장된 항목(미할당 포함).</summary>
    public IReadOnlyDictionary<string, KeyChord> Overrides => _overrides;

    public KeyChord Get(string commandId) => _effective.TryGetValue(commandId, out var c) ? c : default;

    /// <summary>이 키에 묶인 명령. 없으면 null.</summary>
    public string? Find(KeyChord chord) => !chord.IsEmpty && _byChord.TryGetValue(chord, out var id) ? id : null;

    /// <summary>툴팁/메뉴에 붙일 글자. "Ctrl+F" (spaced 면 "Ctrl + F"). 미할당이면 빈 문자열.</summary>
    public string GetText(string commandId, bool spaced = false) => Get(commandId).ToDisplay(spaced);

    public static Keymap Load(string? path = null)
    {
        path ??= DefaultPath;
        var map = new Keymap(path);
        map.ReadFile();
        return map;
    }

    /// <summary>
    /// 원하는 최종 키맵(모든 명령의 키)을 적용하고 저장한다. 충돌·등록 불가 키가 있으면 아무것도 바꾸지 않고 Rejected.
    /// 카탈로그에 없는 ID 는 무시한다.
    /// </summary>
    public KeymapApplyResult Apply(IReadOnlyDictionary<string, KeyChord> desired)
    {
        var next = new Dictionary<string, KeyChord>(StringComparer.Ordinal);
        var seen = new HashSet<KeyChord>();

        foreach (var def in CommandCatalog.All)
        {
            var chord = desired.TryGetValue(def.Id, out var c) ? c : def.Default;
            if (ChordRules.Validate(chord) != null) return KeymapApplyResult.Rejected;
            if (!chord.IsEmpty && !seen.Add(chord)) return KeymapApplyResult.Rejected;
            next[def.Id] = chord;
        }

        var overrides = new Dictionary<string, KeyChord>(StringComparer.Ordinal);
        foreach (var def in CommandCatalog.All)
            if (next[def.Id] != def.Default) overrides[def.Id] = next[def.Id];

        _overrides = overrides;
        Rebuild();
        bool saved = WriteFile();
        Changed?.Invoke(this, EventArgs.Empty);
        return saved ? KeymapApplyResult.Ok : KeymapApplyResult.SaveFailed;
    }

    /// <summary>모든 변경을 지우고 기본 키맵으로 돌아간다.</summary>
    public KeymapApplyResult ResetToDefaults()
        => Apply(CommandCatalog.All.ToDictionary(c => c.Id, c => c.Default));

    /// <summary>
    /// 기본값에 사용자 변경분을 덮어 실제 키맵을 만든다. 충돌이 있으면 사용자가 바꾼 쪽이 이긴다:
    /// 새 버전이 추가한 기본 키가 사용자가 이미 쓰는 키와 겹치면, 사용자의 설정은 그대로 두고 새 명령만 미할당으로 둔다(31).
    /// </summary>
    private void Rebuild()
    {
        var effective = new Dictionary<string, KeyChord>(StringComparer.Ordinal);
        var byChord = new Dictionary<KeyChord, string>();

        foreach (var def in CommandCatalog.All)
        {
            if (!_overrides.TryGetValue(def.Id, out var chord)) continue;
            if (!chord.IsEmpty && byChord.ContainsKey(chord)) chord = default;
            effective[def.Id] = chord;
            if (!chord.IsEmpty) byChord[chord] = def.Id;
        }

        foreach (var def in CommandCatalog.All)
        {
            if (effective.ContainsKey(def.Id)) continue;
            var chord = def.Default;
            if (!chord.IsEmpty && byChord.ContainsKey(chord)) chord = default;
            effective[def.Id] = chord;
            if (!chord.IsEmpty) byChord[chord] = def.Id;
        }

        _effective = effective;
        _byChord = byChord;
    }

    // ------------------------------------------------------------------ 파일

    private sealed class FileModel
    {
        public int Version { get; set; } = FileVersion;
        public Dictionary<string, string?> Overrides { get; set; } = new();
    }

    private void ReadFile()
    {
        if (_path == null) return;
        try
        {
            if (!File.Exists(_path)) return;
            var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), Json);
            if (model?.Overrides == null) return;

            var overrides = new Dictionary<string, KeyChord>(StringComparer.Ordinal);
            foreach (var (id, text) in model.Overrides)
            {
                // 모르는 ID(다른 버전이 만든 것), 읽을 수 없는 키, 지금은 등록할 수 없는 키는 그 항목만 건너뛰고 기본값을 쓴다.
                if (!CommandCatalog.TryGet(id, out var def)) continue;
                if (!KeyChord.TryParse(text, out var chord)) continue;
                if (ChordRules.Validate(chord) != null) continue;
                if (chord != def.Default) overrides[id] = chord;
            }

            _overrides = overrides;
            Rebuild();
        }
        catch (Exception)
        {
            // 손상된 설정 파일 때문에 단축키를 못 쓰면 안 된다. 기본 키맵으로 시작한다.
            _overrides = new Dictionary<string, KeyChord>(StringComparer.Ordinal);
            Rebuild();
        }
    }

    private bool WriteFile()
    {
        if (_path == null) return true;
        try
        {
            var model = new FileModel();
            foreach (var def in CommandCatalog.All)
                if (_overrides.TryGetValue(def.Id, out var chord)) model.Overrides[def.Id] = chord.Serialize();

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(model, Json));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

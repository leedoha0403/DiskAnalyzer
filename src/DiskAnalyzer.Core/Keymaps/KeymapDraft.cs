namespace DiskAnalyzer.Core.Keymaps;

/// <summary>
/// 설정 화면이 편집하는 임시 키맵(7, 19, 20). [적용] 전에는 실제 <see cref="Keymap"/> 에 아무 영향도 주지 않는다.
/// 충돌은 편집 중에는 허용하고(양쪽을 강조 표시) [적용] 만 막는다(11).
/// </summary>
public sealed class KeymapDraft
{
    private readonly Keymap _keymap;
    private Dictionary<string, KeyChord> _baseline;
    private readonly Dictionary<string, KeyChord> _chords;

    public KeymapDraft(Keymap keymap)
    {
        _keymap = keymap;
        _baseline = Snapshot(keymap);
        _chords = new Dictionary<string, KeyChord>(_baseline, StringComparer.Ordinal);
    }

    private static Dictionary<string, KeyChord> Snapshot(Keymap keymap)
        => CommandCatalog.All.ToDictionary(c => c.Id, c => keymap.Get(c.Id), StringComparer.Ordinal);

    public KeyChord Get(string id) => _chords.TryGetValue(id, out var c) ? c : default;

    public IReadOnlyDictionary<string, KeyChord> Chords => _chords;

    /// <summary>기본값과 다른가(18). 사용자가 일부러 비운 것도 "변경됨" 이다.</summary>
    public bool IsModified(string id)
        => CommandCatalog.TryGet(id, out var def) && Get(id) != def.Default;

    /// <summary>마지막 적용 이후 바뀐 것이 있는가. [적용] 활성화와 창을 닫을 때 확인에 쓴다.</summary>
    public bool IsDirty => _chords.Any(kv => !_baseline.TryGetValue(kv.Key, out var b) || b != kv.Value);

    /// <summary>이 키를 이미 쓰는 다른 명령. 없으면 null. (자기 자신은 제외)</summary>
    public string? FindOther(string id, KeyChord chord)
    {
        if (chord.IsEmpty) return null;
        foreach (var def in CommandCatalog.All)
            if (def.Id != id && Get(def.Id) == chord) return def.Id;
        return null;
    }

    /// <summary>같은 키를 둘 이상의 명령이 쓰는 경우, 그 명령들의 ID.</summary>
    public IReadOnlySet<string> ConflictIds
    {
        get
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in _chords.Where(kv => !kv.Value.IsEmpty).GroupBy(kv => kv.Value))
            {
                var ids = group.Select(kv => kv.Key).ToList();
                if (ids.Count > 1) result.UnionWith(ids);
            }
            return result;
        }
    }

    public bool HasConflicts => ConflictIds.Count > 0;

    /// <summary>
    /// 키를 지정한다. 등록할 수 없는 키(예약 키, 단일 문자 등)면 이유를 돌려주고 바꾸지 않는다.
    /// 다른 명령과 겹치는 것은 오류가 아니다 — 호출한 쪽이 <see cref="FindOther"/> 로 확인해 해결 방법을 보여 준다.
    /// </summary>
    public string? TrySet(string id, KeyChord chord)
    {
        if (!CommandCatalog.TryGet(id, out _)) return "알 수 없는 명령입니다.";
        string? error = ChordRules.Validate(chord);
        if (error != null) return error;
        _chords[id] = chord;
        return null;
    }

    /// <summary>미할당으로 만든다(9, 32).</summary>
    public void Unassign(string id)
    {
        if (CommandCatalog.TryGet(id, out _)) _chords[id] = default;
    }

    /// <summary>이 명령 하나를 기본값으로 되돌린다(17).</summary>
    public void Reset(string id)
    {
        if (CommandCatalog.TryGet(id, out var def)) _chords[id] = def.Default;
    }

    /// <summary>모든 명령을 기본값으로 되돌린다(16). 적용하기 전까지는 임시 상태다.</summary>
    public void ResetAll()
    {
        foreach (var def in CommandCatalog.All) _chords[def.Id] = def.Default;
    }

    /// <summary>[취소]: 마지막으로 적용된 상태로 돌아간다.</summary>
    public void Discard()
    {
        foreach (var (id, chord) in _baseline) _chords[id] = chord;
    }

    /// <summary>[적용]: 충돌이 있으면 적용하지 않는다. 성공하면 지금 상태가 새 기준이 된다.</summary>
    public KeymapApplyResult Commit()
    {
        if (HasConflicts) return KeymapApplyResult.Rejected;
        var result = _keymap.Apply(_chords);
        if (result != KeymapApplyResult.Rejected) _baseline = Snapshot(_keymap);
        return result;
    }
}

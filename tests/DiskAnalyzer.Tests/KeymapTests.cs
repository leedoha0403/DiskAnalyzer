using DiskAnalyzer.Core.Keymaps;
using Xunit;

namespace DiskAnalyzer.Tests;

/// <summary>단축키 설정: 키 조합, 등록 규칙, 충돌, 저장(변경분만), 편집 초안(적용/취소).</summary>
public sealed class KeymapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "da_keymap_" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "keymap.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 정리 실패는 결과와 무관 */ }
    }

    private static KeyChord Chord(string text)
    {
        Assert.True(KeyChord.TryParse(text, out var c), text);
        return c;
    }

    // ------------------------------------------------------------------ KeyChord

    [Theory]
    [InlineData("Ctrl+G", "Ctrl + G")]
    [InlineData("Ctrl+Shift+F", "Ctrl + Shift + F")]
    [InlineData("Alt+Up", "Alt + ↑")]
    [InlineData("Ctrl+Alt+D", "Ctrl + Alt + D")]
    [InlineData("F8", "F8")]
    [InlineData("Shift+F5", "Shift + F5")]
    [InlineData("Alt+Left", "Alt + ←")]
    public void Chord_RoundTripsAndDisplays(string text, string display)
    {
        var chord = Chord(text);
        Assert.Equal(display, chord.ToDisplay());
        Assert.Equal(text, chord.Serialize());
        Assert.Equal(chord, Chord(chord.Serialize()));
    }

    [Fact]
    public void Chord_ParseIsTolerant()
    {
        Assert.Equal(Chord("Ctrl+Shift+F"), Chord(" ctrl + shift + f "));
        Assert.Equal(Chord("Esc"), Chord("Escape"));
        Assert.Equal(Chord("Delete"), Chord("Del"));
        Assert.Equal(Chord("Enter"), Chord("return"));
        Assert.Equal("Ctrl+Shift+F", Chord("shift+CTRL+f").Serialize());
    }

    [Theory]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+Foo")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl++")]
    public void Chord_ParseRejectsGarbage(string text) => Assert.False(KeyChord.TryParse(text, out _));

    [Fact]
    public void Chord_EmptyMeansUnassigned()
    {
        Assert.True(KeyChord.TryParse("", out var c));
        Assert.True(c.IsEmpty);
        Assert.Equal("", c.Serialize());
        Assert.Equal(default, KeyChord.None);
    }

    [Fact]
    public void Chord_CompactTextForTooltips()
        => Assert.Equal("Ctrl+Shift+F", Chord("Ctrl+Shift+F").ToDisplay(spaced: false));

    // ------------------------------------------------------------------ 등록 규칙

    [Theory]
    [InlineData("Ctrl+G")]
    [InlineData("Ctrl+Shift+F")]
    [InlineData("Alt+D")]
    [InlineData("Ctrl+Alt+D")]
    [InlineData("Alt+Up")]
    [InlineData("F8")]
    [InlineData("Shift+F5")]
    [InlineData("Enter")]
    [InlineData("Space")]
    [InlineData("Backspace")]
    [InlineData("Delete")]
    [InlineData("Esc")]
    [InlineData("Ctrl+Tab")]
    [InlineData("Ctrl+Home")]
    public void Rules_AllowsValidChords(string text) => Assert.Null(ChordRules.Validate(Chord(text)));

    [Theory]
    [InlineData("A")]              // 단일 문자
    [InlineData("F")]
    [InlineData("5")]
    [InlineData("Shift+D")]        // Shift 만으로는 부족
    [InlineData("Up")]             // 방향키 단독
    [InlineData("Tab")]
    [InlineData("Shift+Tab")]      // 포커스 이동
    [InlineData("Home")]
    [InlineData("Alt+F4")]         // 종료
    [InlineData("Ctrl+Alt+Delete")]
    [InlineData("Alt+Tab")]
    [InlineData("Win+E")]          // Windows 키
    [InlineData("Ctrl+Win+D")]
    public void Rules_RejectsInvalidChords(string text)
        => Assert.NotNull(ChordRules.Validate(Chord(text)));

    [Fact]
    public void Rules_UnassignedIsAlwaysValid() => Assert.Null(ChordRules.Validate(default));

    [Theory]
    [InlineData("Backspace", true)]
    [InlineData("Delete", true)]
    [InlineData("Enter", true)]
    [InlineData("Space", true)]
    [InlineData("Esc", true)]
    [InlineData("Left", true)]
    [InlineData("Ctrl+A", true)]        // 입력창의 전체 선택
    [InlineData("Ctrl+C", true)]
    [InlineData("Ctrl+V", true)]
    [InlineData("Ctrl+Z", true)]
    [InlineData("Ctrl+Backspace", true)]
    [InlineData("Ctrl+Shift+Left", true)]
    [InlineData("Ctrl+Home", true)]
    [InlineData("Ctrl+Alt+D", true)]    // AltGr 로 글자를 만드는 자판
    [InlineData("F5", false)]
    [InlineData("Ctrl+F5", false)]
    [InlineData("Shift+F5", false)]
    [InlineData("Ctrl+F", false)]
    [InlineData("Ctrl+L", false)]
    [InlineData("Ctrl+1", false)]
    [InlineData("Ctrl+Tab", false)]
    [InlineData("Ctrl+Shift+C", false)]
    [InlineData("Alt+Left", false)]
    [InlineData("Ctrl+Enter", false)]
    public void Rules_TextInputKeepsEditingKeys(string text, bool leftToTextBox)
        => Assert.Equal(leftToTextBox, ChordRules.BelongsToTextInput(Chord(text)));

    // ------------------------------------------------------------------ 기본 키맵

    [Fact]
    public void Defaults_AreValidAndUnique()
    {
        var seen = new Dictionary<KeyChord, string>();
        foreach (var def in CommandCatalog.All)
        {
            Assert.Null(ChordRules.Validate(def.Default));
            if (def.Default.IsEmpty) continue;
            Assert.True(seen.TryAdd(def.Default, def.Id), $"{def.Id} 와 {seen.GetValueOrDefault(def.Default)} 의 기본 키가 같다: {def.Default}");
        }
    }

    [Fact]
    public void Defaults_IdsAreUnique()
        => Assert.Equal(CommandCatalog.All.Count, CommandCatalog.All.Select(c => c.Id).Distinct().Count());

    [Fact]
    public void Defaults_MatchTheSpec()
    {
        var map = new Keymap();
        Assert.Equal(Chord("Ctrl+1"), map.Get(CommandIds.TabsFolder));
        Assert.Equal(Chord("Ctrl+6"), map.Get(CommandIds.TabsQuickMove));
        Assert.Equal(Chord("Ctrl+Shift+Tab"), map.Get(CommandIds.TabsPrevious));
        Assert.Equal(Chord("Backspace"), map.Get(CommandIds.GoParent));
        Assert.Equal(Chord("Alt+Left"), map.Get(CommandIds.GoBack));
        Assert.Equal(Chord("Ctrl+L"), map.Get(CommandIds.FocusPath));
        Assert.Equal(Chord("Ctrl+F"), map.Get(CommandIds.SearchFocus));
        Assert.Equal(Chord("F5"), map.Get(CommandIds.Refresh));
        Assert.Equal(Chord("Ctrl+F5"), map.Get(CommandIds.ForceRescan));
        Assert.Equal(Chord("Ctrl+Shift+C"), map.Get(CommandIds.CopyPathList));
        Assert.Equal(CommandIds.SearchFocus, map.Find(Chord("Ctrl+F")));
        Assert.Null(map.Find(Chord("Ctrl+9")));
    }

    // ------------------------------------------------------------------ Keymap 적용 / 저장

    private static Dictionary<string, KeyChord> Desired(Keymap map, params (string Id, KeyChord Chord)[] changes)
    {
        var d = map.Effective.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (id, chord) in changes) d[id] = chord;
        return d;
    }

    [Fact]
    public void Apply_ChangesAndPersistsOnlyDifferences()
    {
        var map = Keymap.Load(FilePath);
        var result = map.Apply(Desired(map, (CommandIds.SearchFocus, Chord("Ctrl+Shift+F"))));

        Assert.Equal(KeymapApplyResult.Ok, result);
        Assert.Equal(Chord("Ctrl+Shift+F"), map.Get(CommandIds.SearchFocus));
        Assert.Equal("Ctrl+Shift+F", map.GetText(CommandIds.SearchFocus));
        Assert.Null(map.Find(Chord("Ctrl+F")));

        string json = File.ReadAllText(FilePath);
        Assert.Contains("Search.Focus", json);
        Assert.Contains("Ctrl+Shift+F", json);
        Assert.DoesNotContain("View.Refresh", json);     // 안 건드린 항목은 저장하지 않는다
        Assert.DoesNotContain("Tabs.Folder", json);

        var reloaded = Keymap.Load(FilePath);
        Assert.Equal(Chord("Ctrl+Shift+F"), reloaded.Get(CommandIds.SearchFocus));
        Assert.Equal(Chord("F5"), reloaded.Get(CommandIds.Refresh));
    }

    [Fact]
    public void Apply_UnassignedSurvivesRestart_AndIsDistinctFromUntouched()
    {
        var map = Keymap.Load(FilePath);
        map.Apply(Desired(map, (CommandIds.Refresh, default)));

        Assert.True(map.Get(CommandIds.Refresh).IsEmpty);
        Assert.Equal("", map.GetText(CommandIds.Refresh));

        var reloaded = Keymap.Load(FilePath);
        Assert.True(reloaded.Get(CommandIds.Refresh).IsEmpty);
        Assert.Equal(Chord("Ctrl+F5"), reloaded.Get(CommandIds.ForceRescan));
    }

    [Fact]
    public void Apply_BackToDefaultRemovesTheOverride()
    {
        var map = Keymap.Load(FilePath);
        map.Apply(Desired(map, (CommandIds.SearchFocus, Chord("Ctrl+G"))));
        Assert.Contains("Search.Focus", File.ReadAllText(FilePath));

        map.Apply(Desired(map, (CommandIds.SearchFocus, Chord("Ctrl+F"))));
        Assert.DoesNotContain("Search.Focus", File.ReadAllText(FilePath));
        Assert.Empty(map.Overrides);
    }

    [Fact]
    public void Apply_RejectsConflictsAndInvalidKeysWithoutChangingAnything()
    {
        var map = Keymap.Load(FilePath);

        // 검색 = Ctrl+F 인데 경로 복사도 Ctrl+F
        Assert.Equal(KeymapApplyResult.Rejected, map.Apply(Desired(map, (CommandIds.CopyPath, Chord("Ctrl+F")))));
        Assert.Equal(Chord("Ctrl+C"), map.Get(CommandIds.CopyPath));

        Assert.Equal(KeymapApplyResult.Rejected, map.Apply(Desired(map, (CommandIds.CopyPath, Chord("Alt+F4")))));
        Assert.Equal(Chord("Ctrl+C"), map.Get(CommandIds.CopyPath));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Apply_RaisesChangedOnlyWhenApplied()
    {
        var map = new Keymap();
        int raised = 0;
        map.Changed += (_, _) => raised++;

        map.Apply(Desired(map, (CommandIds.CopyPath, Chord("Ctrl+F"))));      // 거부
        Assert.Equal(0, raised);

        map.Apply(Desired(map, (CommandIds.CopyPath, Chord("Ctrl+G"))));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ResetToDefaults_ClearsEverything()
    {
        var map = Keymap.Load(FilePath);
        map.Apply(Desired(map, (CommandIds.SearchFocus, Chord("Ctrl+G")), (CommandIds.Refresh, default)));
        Assert.NotEmpty(map.Overrides);

        Assert.Equal(KeymapApplyResult.Ok, map.ResetToDefaults());
        Assert.Empty(map.Overrides);
        Assert.Equal(Chord("Ctrl+F"), map.Get(CommandIds.SearchFocus));
        Assert.Equal(Chord("F5"), map.Get(CommandIds.Refresh));
    }

    [Fact]
    public void Apply_SwapsKeysBetweenTwoCommands()
    {
        var map = new Keymap();
        var a = map.Get(CommandIds.SearchFocus);
        var b = map.Get(CommandIds.FocusPath);

        var result = map.Apply(Desired(map, (CommandIds.SearchFocus, b), (CommandIds.FocusPath, a)));

        Assert.Equal(KeymapApplyResult.Ok, result);
        Assert.Equal(b, map.Get(CommandIds.SearchFocus));
        Assert.Equal(a, map.Get(CommandIds.FocusPath));
        Assert.Equal(CommandIds.FocusPath, map.Find(a));
    }

    [Fact]
    public void Apply_ReportsSaveFailureButStillAppliesForThisSession()
    {
        // 폴더가 있어야 할 자리에 파일이 있어서 저장 경로를 만들 수 없다.
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        var map = Keymap.Load(Path.Combine(blocker, "keymap.json"));

        var result = map.Apply(Desired(map, (CommandIds.SearchFocus, Chord("Ctrl+G"))));

        Assert.Equal(KeymapApplyResult.SaveFailed, result);
        Assert.Equal(Chord("Ctrl+G"), map.Get(CommandIds.SearchFocus));
    }

    // ------------------------------------------------------------------ 저장 파일 호환성

    private void WriteRaw(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, json);
    }

    [Fact]
    public void Load_MissingOrCorruptFileFallsBackToDefaults()
    {
        Assert.Equal(Chord("Ctrl+F"), Keymap.Load(FilePath).Get(CommandIds.SearchFocus));

        WriteRaw("{ not json");
        var map = Keymap.Load(FilePath);
        Assert.Equal(Chord("Ctrl+F"), map.Get(CommandIds.SearchFocus));
        Assert.Empty(map.Overrides);
    }

    [Fact]
    public void Load_SkipsUnknownCommandsAndBadKeysOnly()
    {
        WriteRaw("""
        { "Version": 1, "Overrides": {
            "Search.Focus": "Ctrl+G",
            "Future.NewCommand": "Ctrl+K",
            "View.Refresh": "Ctrl+Foo",
            "Clipboard.CopyPath": "Alt+F4"
        } }
        """);

        var map = Keymap.Load(FilePath);

        Assert.Equal(Chord("Ctrl+G"), map.Get(CommandIds.SearchFocus));
        Assert.Equal(Chord("F5"), map.Get(CommandIds.Refresh));            // 읽을 수 없는 키 -> 기본값
        Assert.Equal(Chord("Ctrl+C"), map.Get(CommandIds.CopyPath));       // 등록할 수 없는 키 -> 기본값
        Assert.Single(map.Overrides);
    }

    [Fact]
    public void Load_UserKeyWinsOverANewDefaultThatCollidesWithIt()
    {
        // 사용자가 검색을 Ctrl+1 로 바꿔 두었다. 폴더 탭의 기본 키(Ctrl+1)는 손대지 않은 항목이다.
        WriteRaw("""{ "Version": 1, "Overrides": { "Search.Focus": "Ctrl+1" } }""");

        var map = Keymap.Load(FilePath);

        Assert.Equal(Chord("Ctrl+1"), map.Get(CommandIds.SearchFocus));
        Assert.True(map.Get(CommandIds.TabsFolder).IsEmpty);
        Assert.Equal(CommandIds.SearchFocus, map.Find(Chord("Ctrl+1")));
    }

    [Fact]
    public void Load_DuplicateOverridesNeverProduceTwoOwners()
    {
        WriteRaw("""{ "Version": 1, "Overrides": { "Search.Focus": "Ctrl+G", "Clipboard.CopyPath": "Ctrl+G" } }""");

        var map = Keymap.Load(FilePath);

        int owners = CommandCatalog.All.Count(c => map.Get(c.Id) == Chord("Ctrl+G"));
        Assert.Equal(1, owners);
    }

    // ------------------------------------------------------------------ 편집 초안 (적용 / 취소)

    [Fact]
    public void Draft_ChangesDoNotReachTheLiveKeymapUntilCommit()
    {
        var map = Keymap.Load(FilePath);
        var draft = new KeymapDraft(map);

        Assert.Null(draft.TrySet(CommandIds.SearchFocus, Chord("Ctrl+Shift+F")));

        Assert.Equal(Chord("Ctrl+Shift+F"), draft.Get(CommandIds.SearchFocus));
        Assert.Equal(Chord("Ctrl+F"), map.Get(CommandIds.SearchFocus));      // 아직 실제 키맵은 그대로
        Assert.False(File.Exists(FilePath));
        Assert.True(draft.IsDirty);

        Assert.Equal(KeymapApplyResult.Ok, draft.Commit());
        Assert.Equal(Chord("Ctrl+Shift+F"), map.Get(CommandIds.SearchFocus));
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void Draft_DiscardRestoresTheAppliedState()
    {
        var map = new Keymap();
        var draft = new KeymapDraft(map);
        draft.TrySet(CommandIds.SearchFocus, Chord("Ctrl+G"));
        draft.Unassign(CommandIds.Refresh);

        draft.Discard();

        Assert.Equal(Chord("Ctrl+F"), draft.Get(CommandIds.SearchFocus));
        Assert.Equal(Chord("F5"), draft.Get(CommandIds.Refresh));
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void Draft_DetectsConflictsAndBlocksCommit()
    {
        var map = new Keymap();
        var draft = new KeymapDraft(map);

        Assert.Null(draft.TrySet(CommandIds.CopyPath, Chord("Ctrl+F")));

        Assert.Equal(CommandIds.SearchFocus, draft.FindOther(CommandIds.CopyPath, Chord("Ctrl+F")));
        Assert.Equal(new[] { CommandIds.CopyPath, CommandIds.SearchFocus }.Order(), draft.ConflictIds.Order());
        Assert.True(draft.HasConflicts);
        Assert.Equal(KeymapApplyResult.Rejected, draft.Commit());
        Assert.Equal(Chord("Ctrl+C"), map.Get(CommandIds.CopyPath));
    }

    [Fact]
    public void Draft_ResolveConflictByUnassigningTheExistingOwner()
    {
        var map = new Keymap();
        var draft = new KeymapDraft(map);
        draft.TrySet(CommandIds.CopyPath, Chord("Ctrl+F"));

        draft.Unassign(draft.FindOther(CommandIds.CopyPath, Chord("Ctrl+F"))!);

        Assert.False(draft.HasConflicts);
        Assert.True(draft.Get(CommandIds.SearchFocus).IsEmpty);
        Assert.Equal(Chord("Ctrl+F"), draft.Get(CommandIds.CopyPath));
        Assert.Equal(KeymapApplyResult.Ok, draft.Commit());
        Assert.Equal(CommandIds.CopyPath, map.Find(Chord("Ctrl+F")));
    }

    [Fact]
    public void Draft_RejectsUnregistrableKeysWithoutChangingTheRow()
    {
        var draft = new KeymapDraft(new Keymap());

        Assert.NotNull(draft.TrySet(CommandIds.SearchFocus, Chord("Alt+F4")));
        Assert.NotNull(draft.TrySet(CommandIds.SearchFocus, Chord("D")));
        Assert.NotNull(draft.TrySet(CommandIds.SearchFocus, Chord("Win+F")));

        Assert.Equal(Chord("Ctrl+F"), draft.Get(CommandIds.SearchFocus));
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void Draft_ModifiedFlagComparesAgainstTheDefault()
    {
        var draft = new KeymapDraft(new Keymap());
        Assert.False(draft.IsModified(CommandIds.SearchFocus));

        draft.TrySet(CommandIds.SearchFocus, Chord("Ctrl+Shift+F"));
        Assert.True(draft.IsModified(CommandIds.SearchFocus));

        draft.Unassign(CommandIds.Refresh);
        Assert.True(draft.IsModified(CommandIds.Refresh));         // 일부러 비운 것도 변경됨

        draft.Reset(CommandIds.SearchFocus);
        Assert.False(draft.IsModified(CommandIds.SearchFocus));
        Assert.Equal(Chord("Ctrl+F"), draft.Get(CommandIds.SearchFocus));
    }

    [Fact]
    public void Draft_ResetAllReturnsEveryCommandToDefaultButOnlyAfterCommitIsItLive()
    {
        var map = Keymap.Load(FilePath);
        map.Apply(Desired(map, (CommandIds.SearchFocus, Chord("Ctrl+G"))));
        var draft = new KeymapDraft(map);

        draft.ResetAll();

        Assert.Equal(Chord("Ctrl+F"), draft.Get(CommandIds.SearchFocus));
        Assert.Equal(Chord("Ctrl+G"), map.Get(CommandIds.SearchFocus));    // 적용 전
        Assert.True(draft.IsDirty);

        draft.Commit();
        Assert.Equal(Chord("Ctrl+F"), map.Get(CommandIds.SearchFocus));
        Assert.Empty(map.Overrides);
    }

    [Fact]
    public void Draft_ResetOfOneCommandCanCreateAConflictThatMustBeResolved()
    {
        // 검색을 Ctrl+G 로 바꾸고, 경로 복사에 검색의 옛 키 Ctrl+F 를 준 뒤 -> 검색을 기본값으로 되돌리면 Ctrl+F 가 겹친다.
        var draft = new KeymapDraft(new Keymap());
        draft.TrySet(CommandIds.SearchFocus, Chord("Ctrl+G"));
        draft.TrySet(CommandIds.CopyPath, Chord("Ctrl+F"));
        Assert.False(draft.HasConflicts);

        draft.Reset(CommandIds.SearchFocus);

        Assert.True(draft.HasConflicts);
        Assert.Equal(KeymapApplyResult.Rejected, draft.Commit());
    }
}

using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Scanning;
using Xunit;

namespace DiskAnalyzer.Tests;

/// <summary>
/// 48. 스캔 제외 규칙. 패턴 분류 / 매칭 규칙을 먼저 고정하고,
/// 실제 임시 트리로 Compatibility 스캔 · 폴더 새로고침까지 같은 규칙이 적용되는지 확인한다.
/// </summary>
public sealed class ExclusionTests : IDisposable
{
    private readonly string _root;

    public ExclusionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "da_exclude_" + Guid.NewGuid().ToString("N"));

        //  root
        //   ├ keep\a.txt, a.tmp
        //   ├ node_modules\deep\junk.txt        (이름 규칙으로 통째 제외)
        //   ├ build\obj\Debug\x.obj             (경로 규칙으로 Debug 아래만 제외)
        //   └ build\obj\Release\y.obj
        Make("keep", "a.txt", "a.tmp");
        Make(Path.Combine("node_modules", "deep"), "junk.txt");
        Make(Path.Combine("build", "obj", "Debug"), "x.obj");
        Make(Path.Combine("build", "obj", "Release"), "y.obj");
    }

    private void Make(string relDir, params string[] files)
    {
        string dir = Path.Combine(_root, relDir);
        Directory.CreateDirectory(dir);
        foreach (string f in files) File.WriteAllText(Path.Combine(dir, f), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static ScanOptions Options(params string[] patterns) => new()
    {
        Mode = ScanMode.Compatibility,
        WorkerCount = 2,
        CacheResults = false,
        ExclusionPatterns = patterns,
    };

    // ------------------------------------------------------------ 패턴 분류

    [Theory]
    [InlineData("*.tmp", "a.tmp", true)]
    [InlineData("*.tmp", "a.TMP", true)]         // 대소문자 무시
    [InlineData("*.tmp", "a.tmp.txt", false)]    // 확장자는 마지막 것만 본다
    [InlineData(".iso", "win.iso", true)]
    [InlineData(".iso", "iso", false)]
    [InlineData("*.log", "a.txt", false)]
    public void Extension_rules_match_files_by_last_extension(string pattern, string name, bool expected)
        => Assert.Equal(expected, ExclusionRules.Parse(new[] { pattern }).ExcludesFile(name));

    [Fact]
    public void A_starred_extension_rule_does_not_touch_folders()
    {
        var r = ExclusionRules.Parse(new[] { "*.tmp" });
        Assert.True(r.ExcludesFile("a.tmp"));
        Assert.False(r.ExcludesDirectoryName("a.tmp"));
    }

    /// <summary>`.git` / `.vs` 는 확장자로도 폴더 이름으로도 읽힌다. 둘 다로 쳐야 사용자가 기대한 대로 동작한다.</summary>
    [Fact]
    public void A_bare_dot_rule_also_matches_a_folder_of_that_name()
    {
        var r = ExclusionRules.Parse(new[] { ".git" });
        Assert.True(r.ExcludesDirectoryName(".git"));
        Assert.True(r.ExcludesFile("repo.git"));      // 확장자로도 여전히 걸린다
        Assert.False(r.ExcludesDirectoryName("git"));
    }

    [Theory]
    [InlineData("node_modules", "node_modules", true)]
    [InlineData("node_modules", "Node_Modules", true)]
    [InlineData("node_modules", "node_modules2", false)]
    [InlineData("~$*", "~$report.docx", true)]
    [InlineData("~$*", "report.docx", false)]
    [InlineData("log?.txt", "log1.txt", true)]
    [InlineData("log?.txt", "log12.txt", false)]
    public void Name_rules_match_both_files_and_folders(string pattern, string name, bool expected)
    {
        var r = ExclusionRules.Parse(new[] { pattern });
        Assert.Equal(expected, r.ExcludesDirectoryName(name));
        Assert.Equal(expected, r.ExcludesFile(name));
    }

    // -------------------------------------------------------------- 경로 규칙

    [Theory]
    [InlineData(@"C:\Windows\Temp", @"C:\Windows\Temp", true)]
    [InlineData(@"C:\Windows\Temp", @"c:\windows\temp", true)]
    [InlineData(@"C:\Windows\Temp", @"C:\Windows\Temp2", false)]
    [InlineData(@"C:\Windows\Temp", @"D:\Windows\Temp", false)]     // 드라이브가 다르면 다른 경로다
    [InlineData(@"obj\Debug", @"C:\p\obj\Debug", true)]             // 조각 규칙은 어디에 나타나도 걸린다
    [InlineData(@"obj\Debug", @"C:\p\obj\Release", false)]
    [InlineData(@"C:\Users\*\AppData", @"C:\Users\me\AppData", true)]
    [InlineData(@"C:\Users\*\AppData", @"C:\Users\AppData", false)]
    public void Path_rules_match_folder_paths(string pattern, string path, bool expected)
        => Assert.Equal(expected, ExclusionRules.Parse(new[] { pattern }).ExcludesPath(path));

    [Fact]
    public void Path_rules_are_gated_by_the_last_segment_name()
    {
        var r = ExclusionRules.Parse(new[] { @"C:\Windows\Temp" });
        Assert.True(r.HasPathRules);
        Assert.True(r.MayEndPathRule("Temp"));      // 이때만 전체 경로를 만들어 확인한다
        Assert.False(r.MayEndPathRule("Windows"));
        Assert.False(r.ExcludesDirectoryName("Temp"));   // 이름만으로는 제외하지 않는다
    }

    [Fact]
    public void Forward_slashes_and_comments_and_duplicates_are_normalized()
    {
        var r = ExclusionRules.Parse(new[] { "  # 주석 ", "", "obj/Debug", @"obj\Debug", "\"*.tmp\"" });
        Assert.Equal(new[] { @"obj\Debug", "*.tmp" }, r.Patterns);
    }

    [Fact]
    public void Empty_or_null_pattern_list_is_empty()
    {
        Assert.True(ExclusionRules.Parse(null).IsEmpty);
        Assert.True(ExclusionRules.Parse(new[] { "  ", "# only a comment" }).IsEmpty);
        Assert.False(ExclusionRules.Parse(new[] { "*.tmp" }).IsEmpty);
    }

    [Fact]
    public void Options_recompile_when_patterns_change()
    {
        var o = new ScanOptions();
        Assert.True(o.Exclusions.IsEmpty);

        o.ExclusionPatterns = new[] { "*.tmp" };
        Assert.True(o.Exclusions.ExcludesFile("a.tmp"));

        o.ExclusionPatterns = new[] { "*.log" };
        Assert.False(o.Exclusions.ExcludesFile("a.tmp"));
        Assert.True(o.Exclusions.ExcludesFile("a.log"));
    }

    // ---------------------------------------------------------------- 스캔

    [Fact]
    public async Task Scan_without_rules_sees_everything()
    {
        var result = await new ScanController().ScanAsync(_root, Options());

        Assert.Equal(5, result.FileCount);        // a.txt a.tmp junk.txt x.obj y.obj
        Assert.Equal(0, result.ExcludedFiles);
        Assert.Equal(0, result.ExcludedFolders);
    }

    [Fact]
    public async Task Extension_rule_drops_only_matching_files()
    {
        var result = await new ScanController().ScanAsync(_root, Options("*.tmp"));

        Assert.Equal(4, result.FileCount);
        Assert.Equal(1, result.ExcludedFiles);
        Assert.Equal(0, result.ExcludedFolders);
        Assert.DoesNotContain(AllNames(result), n => n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Name_rule_prunes_the_whole_subtree()
    {
        var result = await new ScanController().ScanAsync(_root, Options("node_modules"));

        Assert.Equal(4, result.FileCount);                       // junk.txt 가 빠졌다
        Assert.Equal(1, result.ExcludedFolders);                 // 가지치기한 것은 node_modules 하나
        Assert.DoesNotContain("deep", AllNames(result));         // 하위 폴더는 열거조차 하지 않는다
        Assert.DoesNotContain("junk.txt", AllNames(result));
    }

    [Fact]
    public async Task Path_rule_prunes_only_the_matching_folder()
    {
        var result = await new ScanController().ScanAsync(_root, Options(@"obj\Debug"));

        Assert.DoesNotContain("x.obj", AllNames(result));
        Assert.Contains("y.obj", AllNames(result));              // Release 는 남는다
        Assert.Equal(1, result.ExcludedFolders);
    }

    [Fact]
    public async Task Excluded_bytes_are_not_counted_in_folder_sizes()
    {
        File.WriteAllBytes(Path.Combine(_root, "keep", "big.tmp"), new byte[4096]);

        var all = await new ScanController().ScanAsync(_root, Options());
        var some = await new ScanController().ScanAsync(_root, Options("*.tmp"));

        Assert.Equal(all.TotalSize - 4096 - 1, some.TotalSize);   // big.tmp(4096) + a.tmp(1)
    }

    // -------------------------------------------------- 폴더 새로고침 / 변경 감지

    [Fact]
    public void Folder_listing_applies_the_same_rules()
    {
        var listing = FolderScanner.List(Path.Combine(_root, "keep"), Options("*.tmp"));

        Assert.True(listing.Ok);
        Assert.Contains(listing.Files, f => f.Name == "a.txt");
        Assert.DoesNotContain(listing.Files, f => f.Name == "a.tmp");
    }

    [Fact]
    public void Folder_contents_scan_prunes_excluded_folders()
    {
        var tree = FolderScanner.ScanContents(_root, Options("node_modules", @"obj\Debug"));

        var names = tree.Nodes.Select(n => n.Name).ToList();
        Assert.Contains("keep", names);
        Assert.DoesNotContain("node_modules", names);
        Assert.DoesNotContain("Debug", names);
        Assert.Contains("Release", names);
    }

    // -------------------------------------------------------------- 설정 저장

    [Fact]
    public void Settings_round_trip_through_a_file()
    {
        string path = Path.Combine(_root, "exclusions.json");
        new ExclusionSettings { Patterns = { "*.tmp", @"obj\Debug" } }.Save(path);

        var loaded = ExclusionSettings.Load(path);
        Assert.Equal(new[] { "*.tmp", @"obj\Debug" }, loaded.Patterns);
        Assert.Equal("*.tmp" + Environment.NewLine + @"obj\Debug", loaded.ToText());
    }

    [Fact]
    public void A_broken_settings_file_falls_back_to_no_rules()
    {
        string path = Path.Combine(_root, "broken.json");
        File.WriteAllText(path, "{ this is not json");

        Assert.Empty(ExclusionSettings.Load(path).Patterns);
    }

    [Fact]
    public void Settings_split_lines_trims_and_drops_blanks()
        => Assert.Equal(new[] { "*.tmp", "node_modules" },
            ExclusionSettings.SplitLines("  *.tmp \r\n\r\n node_modules\n"));

    private static List<string> AllNames(ScanResult result)
    {
        var names = new List<string>();
        Walk(NodeStore.RootId);
        return names;

        void Walk(int dirId)
        {
            foreach (var row in result.Store.GetChildren(dirId, includeFiles: true))
            {
                names.Add(row.Name);
                if (row.IsDirectory) Walk(row.Id);
            }
        }
    }
}

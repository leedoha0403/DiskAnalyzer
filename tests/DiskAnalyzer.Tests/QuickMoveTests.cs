using DiskAnalyzer.Core.QuickMove;
using Xunit;

namespace DiskAnalyzer.Tests;

/// <summary>
/// 빠른 이동 엔진 / 검증 / 설정. 임시 폴더 안에서만 만들고 지운다.
/// 이동 테스트는 forceCopy = false(같은 볼륨 이름 바꾸기)와 true(다른 드라이브 이동 재현: 복사 후 원본 삭제)를 모두 돌린다.
/// </summary>
public sealed class QuickMoveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "da_quickmove_" + Guid.NewGuid().ToString("N"));
    private readonly string _src;
    private readonly string _dst;

    public QuickMoveTests()
    {
        _src = Path.Combine(_root, "src");
        _dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_dst);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch { /* 정리 실패는 테스트 결과와 무관 */ }
    }

    private static MoveRequest Req(string source, string destDir)
    {
        bool isDir = Directory.Exists(source);
        var m = TreeMeasure.Measure(source);
        return new MoveRequest
        {
            SourcePath = source,
            DestDirectory = destDir,
            IsDirectory = isDir,
            Size = m.Bytes,
            FileCount = isDir ? m.Files : 1,
        };
    }

    private string File1(string dir, string name, string content = "hello")
    {
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private static MoveSummary Run(MoveOptions options, params MoveRequest[] reqs)
        => new MoveEngine(options).Run(reqs);

    // ------------------------------------------------------------------ 기본 이동

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Moves_a_file_and_removes_the_source(bool forceCopy)
    {
        string f = File1(_src, "a.txt", "content-A");

        var s = Run(new MoveOptions { ForceCopy = forceCopy }, Req(f, _dst));

        Assert.Equal(1, s.Moved);
        Assert.True(s.AllSucceeded);
        Assert.False(File.Exists(f));
        Assert.Equal("content-A", File.ReadAllText(Path.Combine(_dst, "a.txt")));
        Assert.Equal(9, s.BytesMoved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Moves_a_folder_tree_completely(bool forceCopy)
    {
        string top = Path.Combine(_src, "Data");
        File1(top, "root.txt", "r");
        File1(Path.Combine(top, "sub", "deep"), "leaf.bin", "leaf");
        Directory.CreateDirectory(Path.Combine(top, "empty"));

        var s = Run(new MoveOptions { ForceCopy = forceCopy }, Req(top, _dst));

        Assert.Equal(1, s.Moved);
        Assert.False(Directory.Exists(top));
        Assert.Equal("r", File.ReadAllText(Path.Combine(_dst, "Data", "root.txt")));
        Assert.Equal("leaf", File.ReadAllText(Path.Combine(_dst, "Data", "sub", "deep", "leaf.bin")));
        Assert.True(Directory.Exists(Path.Combine(_dst, "Data", "empty")));
    }

    [Fact]
    public void Missing_source_fails_without_touching_anything()
    {
        var req = new MoveRequest { SourcePath = Path.Combine(_src, "nope.txt"), DestDirectory = _dst };
        var s = Run(new MoveOptions(), req);

        Assert.Equal(1, s.Failed);
        Assert.Empty(Directory.GetFileSystemEntries(_dst));
    }

    // ------------------------------------------------------------------ 충돌

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Policy_skip_keeps_both_files(bool forceCopy)
    {
        string f = File1(_src, "x.txt", "new");
        File1(_dst, "x.txt", "old");

        var s = Run(new MoveOptions { Policy = ConflictPolicy.Skip, ForceCopy = forceCopy }, Req(f, _dst));

        Assert.Equal(1, s.Skipped);
        Assert.True(File.Exists(f));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_dst, "x.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Policy_overwrite_replaces_even_readonly_target(bool forceCopy)
    {
        string f = File1(_src, "x.txt", "new");
        string existing = File1(_dst, "x.txt", "old");
        File.SetAttributes(existing, FileAttributes.ReadOnly);

        var s = Run(new MoveOptions { Policy = ConflictPolicy.Overwrite, ForceCopy = forceCopy }, Req(f, _dst));

        Assert.Equal(1, s.Moved);
        Assert.False(File.Exists(f));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_dst, "x.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Policy_rename_keeps_both_with_a_new_name(bool forceCopy)
    {
        string f = File1(_src, "x.txt", "new");
        File1(_dst, "x.txt", "old");

        var s = Run(new MoveOptions { Policy = ConflictPolicy.Rename, ForceCopy = forceCopy }, Req(f, _dst));

        Assert.Equal(1, s.Moved);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_dst, "x.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_dst, "x (2).txt")));
        Assert.Equal(Path.Combine(_dst, "x (2).txt"), s.Results[0].FinalPath);
    }

    [Fact]
    public void Ask_policy_uses_the_resolver_and_apply_to_all_asks_only_once()
    {
        string a = File1(_src, "a.txt", "A-new");
        string b = File1(_src, "b.txt", "B-new");
        File1(_dst, "a.txt", "A-old");
        File1(_dst, "b.txt", "B-old");

        int asked = 0;
        var options = new MoveOptions
        {
            Policy = ConflictPolicy.Ask,
            ResolveConflict = info =>
            {
                asked++;
                Assert.False(info.SourceIsDirectory);
                Assert.Equal("A-old".Length, info.ExistingSize);
                return new ConflictDecision(ConflictChoice.Overwrite, ApplyToAll: true);
            },
        };

        var s = Run(options, Req(a, _dst), Req(b, _dst));

        Assert.Equal(1, asked);
        Assert.Equal(2, s.Moved);
        Assert.Equal("A-new", File.ReadAllText(Path.Combine(_dst, "a.txt")));
        Assert.Equal("B-new", File.ReadAllText(Path.Combine(_dst, "b.txt")));
    }

    [Fact]
    public void Ask_policy_without_a_resolver_skips_instead_of_overwriting()
    {
        string f = File1(_src, "x.txt", "new");
        File1(_dst, "x.txt", "old");

        var s = Run(new MoveOptions { Policy = ConflictPolicy.Ask }, Req(f, _dst));

        Assert.Equal(1, s.Skipped);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_dst, "x.txt")));
    }

    [Fact]
    public void A_file_can_not_overwrite_a_folder_of_the_same_name()
    {
        string f = File1(_src, "thing", "file");
        Directory.CreateDirectory(Path.Combine(_dst, "thing"));

        var s = Run(new MoveOptions { Policy = ConflictPolicy.Overwrite }, Req(f, _dst));

        Assert.Equal(1, s.Skipped);
        Assert.True(File.Exists(f));
        Assert.True(Directory.Exists(Path.Combine(_dst, "thing")));
    }

    // ------------------------------------------------------------------ 폴더 병합

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Skipping_a_conflicting_folder_leaves_both_folders_untouched_and_asks_once(bool forceCopy)
    {
        string top = Path.Combine(_src, "Data");
        File1(top, "same.txt", "src");
        File1(top, "only-src.txt", "s");
        File1(Path.Combine(_dst, "Data"), "same.txt", "dst");

        var asked = new List<string>();
        var options = new MoveOptions
        {
            ForceCopy = forceCopy,
            ResolveConflict = info =>
            {
                asked.Add(info.Name);
                Assert.True(info.SourceIsDirectory && info.ExistingIsDirectory);
                return new ConflictDecision(ConflictChoice.Skip, false);
            },
        };

        var s = Run(options, Req(top, _dst));

        Assert.Equal(new[] { "Data" }, asked);           // 폴더를 건너뛰면 안쪽 파일은 묻지 않는다
        Assert.Equal(1, s.Skipped);
        Assert.True(File.Exists(Path.Combine(top, "only-src.txt")));
        Assert.False(File.Exists(Path.Combine(_dst, "Data", "only-src.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Overwrite_merges_folders_and_keeps_unrelated_files(bool forceCopy)
    {
        string top = Path.Combine(_src, "Data");
        File1(top, "same.txt", "src");
        File1(top, "only-src.txt", "s");
        File1(Path.Combine(top, "sub"), "n.txt", "n");
        File1(Path.Combine(_dst, "Data"), "same.txt", "dst");
        File1(Path.Combine(_dst, "Data"), "only-dst.txt", "d");

        var s = Run(new MoveOptions { Policy = ConflictPolicy.Overwrite, ForceCopy = forceCopy }, Req(top, _dst));

        Assert.Equal(1, s.Moved);
        Assert.False(Directory.Exists(top));
        Assert.Equal("src", File.ReadAllText(Path.Combine(_dst, "Data", "same.txt")));
        Assert.Equal("d", File.ReadAllText(Path.Combine(_dst, "Data", "only-dst.txt")));
        Assert.Equal("s", File.ReadAllText(Path.Combine(_dst, "Data", "only-src.txt")));
        Assert.Equal("n", File.ReadAllText(Path.Combine(_dst, "Data", "sub", "n.txt")));
    }

    [Fact]
    public void Merge_with_skipped_files_leaves_them_in_the_source_folder()
    {
        string top = Path.Combine(_src, "Data");
        File1(top, "same.txt", "src");
        File1(top, "other.txt", "o");
        File1(Path.Combine(_dst, "Data"), "same.txt", "dst");

        // 폴더 충돌은 병합(덮어쓰기), 파일 충돌은 건너뜀으로 답한다.
        var options = new MoveOptions
        {
            ResolveConflict = info => info.SourceIsDirectory
                ? new ConflictDecision(ConflictChoice.Overwrite, false)
                : new ConflictDecision(ConflictChoice.Skip, false),
        };

        var s = Run(options, Req(top, _dst));

        Assert.Equal(1, s.Moved);
        Assert.NotNull(s.Results[0].Note);
        Assert.True(File.Exists(Path.Combine(top, "same.txt")));         // 건너뛴 파일은 원본에 그대로
        Assert.False(File.Exists(Path.Combine(top, "other.txt")));       // 나머지는 옮겨졌다
        Assert.Equal("dst", File.ReadAllText(Path.Combine(_dst, "Data", "same.txt")));
    }

    // ------------------------------------------------------------------ 취소 / 일시정지

    [Fact]
    public void Cancel_stops_before_the_next_item_and_keeps_finished_ones()
    {
        var reqs = new List<MoveRequest>();
        for (int i = 0; i < 5; i++) reqs.Add(Req(File1(_src, $"f{i}.txt", "x"), _dst));

        using var cts = new CancellationTokenSource();
        int seen = 0;
        var progress = new SyncProgress(p =>
        {
            if (p.ItemIndex == 3 && Interlocked.Exchange(ref seen, 1) == 0) cts.Cancel();
        });

        var s = new MoveEngine(new MoveOptions { Progress = progress, Parallelism = 1 }).Run(reqs, cts.Token);   // 순서를 고정해야 "3번째에서 취소"가 성립한다

        Assert.True(s.WasCancelled);
        Assert.Equal(2, s.Moved);                      // 3번째 항목 시작 시점에 취소 → 앞의 2개만 완료
        Assert.Equal(3, s.NotProcessed);
        Assert.True(File.Exists(Path.Combine(_dst, "f0.txt")));
        Assert.True(File.Exists(Path.Combine(_src, "f4.txt")));   // 처리하지 않은 것은 원본에 그대로
    }

    [Fact]
    public void Pause_blocks_until_resumed()
    {
        string f = File1(_src, "p.txt", "x");
        var engine = new MoveEngine();
        engine.Pause();

        var task = Task.Run(() => engine.Run(new[] { Req(f, _dst) }));
        Assert.False(task.Wait(300));                  // 멈춰 있는 동안은 끝나지 않는다
        Assert.True(File.Exists(f));

        engine.Resume();
        Assert.True(task.Wait(5000));
        Assert.Equal(1, task.Result.Moved);
    }

    // ------------------------------------------------------------------ 병렬

    private string MakeTree(string name, int dirs, int filesPerDir)
    {
        string top = Path.Combine(_src, name);
        for (int d = 0; d < dirs; d++)
        {
            string dir = Path.Combine(top, $"d{d}", "n");
            Directory.CreateDirectory(dir);
            for (int f = 0; f < filesPerDir; f++)
            {
                File.WriteAllText(Path.Combine(dir, $"f{f}.txt"), $"{d}-{f}");
                File.WriteAllText(Path.Combine(top, $"d{d}", $"t{f}.txt"), $"{d}-{f}");
            }
        }
        return top;
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 8)]
    [InlineData(true, 1)]
    [InlineData(true, 8)]
    public void Big_tree_merge_moves_every_file_regardless_of_parallelism(bool forceCopy, int parallelism)
    {
        string top = MakeTree("Data", dirs: 12, filesPerDir: 40);       // 960 파일
        Directory.CreateDirectory(Path.Combine(_dst, "Data"));          // 대상에 같은 폴더 → 병합 경로(안쪽을 하나씩)

        var s = Run(new MoveOptions { ForceCopy = forceCopy, Parallelism = parallelism, Policy = ConflictPolicy.Overwrite }, Req(top, _dst));

        Assert.Equal(1, s.Moved);
        Assert.True(s.AllSucceeded, string.Join("; ", s.Results.Select(r => r.Error)));
        Assert.False(Directory.Exists(top));
        Assert.Equal(960, Directory.EnumerateFiles(Path.Combine(_dst, "Data"), "*", SearchOption.AllDirectories).Count());
        Assert.Equal("5-7", File.ReadAllText(Path.Combine(_dst, "Data", "d5", "n", "f7.txt")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void Many_top_level_items_all_move_and_results_keep_request_order(int parallelism)
    {
        var reqs = Enumerable.Range(0, 300).Select(i => Req(File1(_src, $"m{i:D3}.txt", i.ToString()), _dst)).ToArray();

        var s = Run(new MoveOptions { Parallelism = parallelism, ForceCopy = parallelism == 8 }, reqs);

        Assert.Equal(300, s.Moved);
        for (int i = 0; i < 300; i++)
        {
            Assert.Equal(reqs[i].SourcePath, s.Results[i].Request.SourcePath);   // 병렬이어도 결과 순서는 요청 순서
            Assert.Equal(i.ToString(), File.ReadAllText(Path.Combine(_dst, $"m{i:D3}.txt")));
        }
    }

    [Fact]
    public void Parallel_conflicts_ask_only_once_when_apply_to_all_is_chosen()
    {
        var reqs = new List<MoveRequest>();
        for (int i = 0; i < 40; i++)
        {
            reqs.Add(Req(File1(_src, $"c{i}.txt", "new"), _dst));
            File1(_dst, $"c{i}.txt", "old");
        }

        int asked = 0;
        var options = new MoveOptions
        {
            Parallelism = 8,
            ResolveConflict = _ =>
            {
                Interlocked.Increment(ref asked);
                Thread.Sleep(20);                                       // 대화 상자가 열려 있는 동안 다른 스레드도 충돌을 만난다
                return new ConflictDecision(ConflictChoice.Overwrite, ApplyToAll: true);
            },
        };

        var s = Run(options, reqs.ToArray());

        Assert.Equal(1, asked);                                         // 동시에 여러 번 묻지 않는다
        Assert.Equal(40, s.Moved);
        Assert.Equal("new", File.ReadAllText(Path.Combine(_dst, "c17.txt")));
    }

    [Fact]
    public void Cancelling_a_parallel_move_never_loses_data()
    {
        string top = MakeTree("Data", dirs: 20, filesPerDir: 50);    // 2,000 파일
        long total = Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories).Count();

        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.FilesDone > 300) cts.Cancel(); });
        Directory.CreateDirectory(Path.Combine(_dst, "Data"));

        var s = new MoveEngine(new MoveOptions { Parallelism = 8, ForceCopy = true, Progress = progress, Policy = ConflictPolicy.Overwrite })
            .Run(new[] { Req(top, _dst) }, cts.Token);

        // 취소되든 끝까지 갔든, 파일은 원본과 대상 중 한 곳에 반드시 하나씩 있다(복사 후에만 원본 삭제).
        long inSrc = Directory.Exists(top) ? Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories).Count() : 0;
        long inDst = Directory.EnumerateFiles(Path.Combine(_dst, "Data"), "*", SearchOption.AllDirectories).Count();
        Assert.True(inSrc + inDst >= total, $"src {inSrc} + dst {inDst} < total {total}");
        Assert.True(s.WasCancelled || s.AllSucceeded);
    }

    // ------------------------------------------------------------------ 진행률

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Progress_reaches_the_total(bool forceCopy)
    {
        var reqs = new List<MoveRequest>
        {
            Req(File1(_src, "a.bin", new string('a', 1000)), _dst),
            Req(File1(_src, "b.bin", new string('b', 3000)), _dst),
        };

        var last = new MoveProgress();
        var progress = new SyncProgress(p => last = p);
        Run(new MoveOptions { Progress = progress, ForceCopy = forceCopy }, reqs.ToArray());

        Assert.Equal(4000, last.BytesTotal);
        Assert.Equal(4000, last.BytesDone);
        Assert.Equal(2, last.FilesDone);
        Assert.Equal(1d, last.Fraction, 3);
    }

    private sealed class SyncProgress(Action<MoveProgress> onReport) : IProgress<MoveProgress>
    {
        public void Report(MoveProgress value) => onReport(value);
    }

    // ------------------------------------------------------------------ 검증

    [Fact]
    public void CheckAdd_rejects_same_location_and_moving_into_itself()
    {
        string file = File1(_src, "a.txt");
        string folder = Path.Combine(_src, "F");
        Directory.CreateDirectory(Path.Combine(folder, "child"));

        Assert.False(MoveValidator.CheckAdd(file, false, _src, out string? e1));
        Assert.Equal(MoveValidator.SameLocationFile, e1);

        Assert.False(MoveValidator.CheckAdd(folder, true, _src, out string? e2));
        Assert.Equal(MoveValidator.SameLocationFolder, e2);

        Assert.False(MoveValidator.CheckAdd(folder, true, Path.Combine(folder, "child"), out string? e3));
        Assert.Equal(MoveValidator.IntoSelf, e3);

        Assert.False(MoveValidator.CheckAdd(folder, true, folder, out string? e4));
        Assert.Equal(MoveValidator.IntoSelf, e4);

        // 이름만 접두어가 같은 형제 폴더는 "하위 폴더"가 아니다.
        string sibling = Path.Combine(_src, "F2");
        Directory.CreateDirectory(sibling);
        Assert.True(MoveValidator.CheckAdd(folder, true, sibling, out _));

        Assert.True(MoveValidator.CheckAdd(file, false, _dst, out _));
    }

    [Fact]
    public void Validate_blocks_protected_system_files()
    {
        // 존재하는 시스템 파일 경로 하나로 P0 판정만 확인한다(실제 이동은 하지 않는다).
        string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "kernel32.dll");
        if (!File.Exists(system)) return;

        var req = new MoveRequest { SourcePath = system, DestDirectory = _dst, Size = 1 };
        var issues = MoveValidator.Validate(new[] { req });

        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Request == req);
        Assert.True(File.Exists(system));
    }

    [Fact]
    public void Validate_passes_a_normal_move_and_reports_missing_destination()
    {
        string f = File1(_src, "ok.txt");
        Assert.Empty(MoveValidator.Validate(new[] { Req(f, _dst) }));

        var missing = new MoveRequest { SourcePath = f, DestDirectory = Path.Combine(_root, "no-such-dir") };
        Assert.Contains(MoveValidator.Validate(new[] { missing }), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void CheckSpace_counts_only_cross_volume_data_as_required()
    {
        string f = File1(_src, "a.bin", new string('x', 500));
        var same = Req(f, _dst);
        var rows = MoveValidator.CheckSpace(new[] { same });

        Assert.Single(rows);
        Assert.Equal(0, rows[0].Required);       // 같은 드라이브 안의 이동은 공간을 쓰지 않는다
        Assert.Equal(500, rows[0].InPlace);
        Assert.True(rows[0].Enough);
        Assert.True(rows[0].Free > 0);
    }

    // ------------------------------------------------------------------ 경로 유틸 / 설정 / 브라우저

    [Fact]
    public void PathUtil_parent_segments_and_prefix_rules()
    {
        Assert.Equal(@"C:\Work", PathUtil.Parent(@"C:\Work\DNF"));
        Assert.Equal(@"C:\", PathUtil.Parent(@"C:\Work"));
        Assert.Null(PathUtil.Parent(@"C:\"));

        var seg = PathUtil.Segments(@"C:\Work\DNF\Bin");
        Assert.Equal(new[] { "C:", "Work", "DNF", "Bin" }, seg.Select(s => s.Name).ToArray());
        Assert.Equal(@"C:\Work\DNF", seg[2].Path);

        Assert.True(PathUtil.IsUnder(@"C:\Work", @"c:\work\Backup"));
        Assert.False(PathUtil.IsUnder(@"C:\Work", @"C:\Work2"));
        Assert.False(PathUtil.IsUnder(@"C:\Work", @"C:\Work"));
        Assert.True(PathUtil.SameVolume(@"C:\a", @"c:\b\c"));
        Assert.False(PathUtil.SameVolume(@"C:\a", @"D:\a"));
    }

    [Fact]
    public void Settings_roundtrip_dedupe_recents_and_toggle_favorites()
    {
        string file = Path.Combine(_root, "qm.json");
        var s = new QuickMoveSettings();
        s.AddRecent(@"C:\A");
        s.AddRecent(@"C:\B");
        s.AddRecent(@"c:\a");                            // 같은 경로(대소문자만 다름) → 중복 없이 앞으로
        for (int i = 0; i < 40; i++) s.AddRecent($@"C:\X{i}");
        Assert.Equal(QuickMoveSettings.MaxRecents, s.Recents.Count);

        Assert.True(s.ToggleFavorite(@"D:\Backup"));
        Assert.True(s.IsFavorite(@"d:\backup"));
        s.LeftPath = @"C:\Work";
        s.ConflictPolicy = ConflictPolicy.Skip;
        Assert.True(s.Save(file));

        var loaded = QuickMoveSettings.Load(file);
        Assert.Equal(new[] { @"D:\Backup" }, loaded.Favorites);
        Assert.Equal(@"C:\Work", loaded.LeftPath);
        Assert.Equal(ConflictPolicy.Skip, loaded.ConflictPolicy);
        Assert.Equal(s.Recents, loaded.Recents);

        Assert.False(s.ToggleFavorite(@"D:\Backup"));
        Assert.Empty(s.Favorites);

        File.WriteAllText(file, "{ broken json");
        Assert.Empty(QuickMoveSettings.Load(file).Favorites);   // 손상된 파일은 기본값으로
    }

    [Fact]
    public void DirectoryBrowser_lists_entries_and_reports_errors()
    {
        File1(_src, "a.txt", "12345");
        Directory.CreateDirectory(Path.Combine(_src, "Sub"));

        var ok = DirectoryBrowser.List(_src, showHidden: false);
        Assert.Equal(BrowseStatus.Ok, ok.Status);
        var file = Assert.Single(ok.Entries, e => !e.IsDirectory);
        Assert.Equal(5, file.Size);
        var dir = Assert.Single(ok.Entries, e => e.IsDirectory);
        Assert.Equal(-1, dir.Size);
        Assert.Equal(string.Empty, dir.SizeText);

        Assert.Equal(BrowseStatus.NotFound, DirectoryBrowser.List(Path.Combine(_root, "none"), false).Status);
    }

    [Fact]
    public void DirectoryBrowser_hides_hidden_files_unless_asked()
    {
        string h = File1(_src, "secret.txt");
        File.SetAttributes(h, FileAttributes.Hidden);
        File1(_src, "shown.txt");

        Assert.DoesNotContain(DirectoryBrowser.List(_src, false).Entries, e => e.Name == "secret.txt");
        Assert.Contains(DirectoryBrowser.List(_src, true).Entries, e => e.Name == "secret.txt");
    }

    [Fact]
    public void PathSuggester_suggests_matching_subfolders_only()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        Directory.CreateDirectory(Path.Combine(_root, "Alpine"));
        Directory.CreateDirectory(Path.Combine(_root, "beta"));
        File.WriteAllText(Path.Combine(_root, "alfile.txt"), "x");   // 파일은 후보가 아니다

        string dir = _root + Path.DirectorySeparatorChar;

        var al = PathSuggester.Suggest(dir + "al");
        Assert.Equal(new[] { dir + "alpha", dir + "Alpine" }, al.ToArray());          // 대소문자 무시, 이름순

        Assert.Equal(5, PathSuggester.Suggest(dir).Count);                             // 접두어가 없으면 폴더 전부: src, dst, alpha, Alpine, beta (파일 제외)
        Assert.Empty(PathSuggester.Suggest(dir + "zzz"));
        Assert.Empty(PathSuggester.Suggest(Path.Combine(_root, "no-such") + Path.DirectorySeparatorChar));
        Assert.Empty(PathSuggester.Suggest("   "));
        Assert.Empty(PathSuggester.Suggest(null));
        Assert.Equal(new[] { dir + "alpha" }, PathSuggester.Suggest(dir + "alph").ToArray());
    }

    [Fact]
    public void PathSuggester_offers_drives_and_expands_environment_variables()
    {
        Assert.Contains(PathSuggester.Suggest("c"), d => d.StartsWith("C:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(PathSuggester.Suggest("C:"), d => d.StartsWith("C:", StringComparison.OrdinalIgnoreCase));

        Environment.SetEnvironmentVariable("DA_QM_TEST_ROOT", _root);
        try
        {
            var viaEnv = PathSuggester.Suggest("%DA_QM_TEST_ROOT%" + Path.DirectorySeparatorChar + "s");
            Assert.Contains(Path.Combine(_root, "src"), viaEnv);
            Assert.Contains(Path.Combine(_root, "dst"), PathSuggester.Suggest("%DA_QM_TEST_ROOT%" + Path.DirectorySeparatorChar + "d"));
        }
        finally { Environment.SetEnvironmentVariable("DA_QM_TEST_ROOT", null); }
    }

    [Fact]
    public void TreeMeasure_counts_files_and_bytes_recursively()
    {
        File1(_src, "a.bin", new string('x', 10));
        File1(Path.Combine(_src, "d1", "d2"), "b.bin", new string('y', 20));

        var m = TreeMeasure.Measure(_src);
        Assert.Equal(30, m.Bytes);
        Assert.Equal(2, m.Files);
        Assert.Equal(2, m.Directories);
    }
}

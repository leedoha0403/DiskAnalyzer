using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Scanning;
using Xunit;

namespace DiskAnalyzer.Tests;

/// <summary>
/// 폴더 단위 변경 감지 / 새로고침 / 경로 찾기. 기준은 항상 "같은 트리를 처음부터 다시 스캔한 결과" —
/// 부분 새로고침을 거친 저장소가 전체 재스캔과 같은 숫자를 내야 한다.
/// </summary>
public sealed class FolderRefreshTests : IDisposable
{
    private readonly string _root;

    public FolderRefreshTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "DiskAnalyzerRefresh_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        Write("a.txt", 100);
        Write("b.log", 200);
        Write(@"docs\readme.md", 50);
        Write(@"docs\deep\notes.txt", 70);
        Write(@"docs\deep\more\x.bin", 30);
        Write(@"media\clip.mp4", 5000);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static ScanOptions Options() => new() { Mode = ScanMode.Compatibility, WorkerCount = 4, CacheResults = false };

    private string P(string relative) => Path.Combine(_root, relative);

    private void Write(string relative, int size)
    {
        string path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }

    private async Task<NodeStore> ScanAsync() => (await new ScanController().ScanAsync(_root, Options())).Store;

    private static void AssertSameAsFresh(NodeStore actual, NodeStore fresh)
    {
        Assert.Equal(fresh.TotalSize, actual.TotalSize);
        Assert.Equal(fresh.FileCount, actual.FileCount);
        Assert.Equal(fresh.DirectoryCount, actual.DirectoryCount);
    }

    private FolderRefreshPlan Refresh(NodeStore store, string relative, bool deep)
    {
        int id = relative.Length == 0 ? NodeStore.RootId : store.ResolvePath(P(relative)).Id;
        var plan = FolderRefresher.Build(store.SnapshotChildren(id), Options(), deep);
        store.ApplyRefresh(plan);
        return plan;
    }

    // ------------------------------------------------------------------ 변경 감지

    [Fact]
    public async Task Untouched_folder_reports_no_changes()
    {
        var store = await ScanAsync();

        var diff = FolderRefresher.Detect(store.SnapshotChildren(NodeStore.RootId), Options());

        Assert.False(diff.Any);
        Assert.True(diff.Readable);
    }

    [Fact]
    public async Task Detect_counts_added_removed_and_resized_direct_children()
    {
        var store = await ScanAsync();
        Write("new1.txt", 10);
        Write("new2.txt", 20);
        File.Delete(P("b.log"));
        Write("a.txt", 999);                                   // 크기 변경
        Directory.CreateDirectory(P("fresh"));
        Directory.Delete(P("media"), true);

        var diff = FolderRefresher.Detect(store.SnapshotChildren(NodeStore.RootId), Options());

        Assert.Equal(2, diff.AddedFiles.Count);
        Assert.Single(diff.RemovedFiles);
        Assert.Single(diff.Resized);
        Assert.Single(diff.AddedDirs);
        Assert.Single(diff.RemovedDirs);
        Assert.Contains("파일 +2 −1", diff.Summary);
        Assert.Contains("크기 변경 1", diff.Summary);
        Assert.Contains("폴더 +1 −1", diff.Summary);
    }

    [Fact]
    public async Task Detect_ignores_a_modified_time_change_when_the_size_is_the_same()
    {
        var store = await ScanAsync();
        File.SetLastWriteTimeUtc(P("a.txt"), DateTime.UtcNow.AddDays(-3));

        Assert.False(FolderRefresher.Detect(store.SnapshotChildren(NodeStore.RootId), Options()).Any);
    }

    [Fact]
    public async Task Detect_does_not_see_changes_inside_subfolders()
    {
        var store = await ScanAsync();
        Write(@"docs\deep\more\extra.bin", 500);

        Assert.False(FolderRefresher.Detect(store.SnapshotChildren(NodeStore.RootId), Options()).Any);
        int deep = store.ResolvePath(P(@"docs\deep\more")).Id;
        Assert.True(FolderRefresher.Detect(store.SnapshotChildren(deep), Options()).Any);   // 그 폴더를 열면 보인다
    }

    [Fact]
    public async Task A_deleted_folder_is_reported_as_gone()
    {
        var store = await ScanAsync();
        int deep = store.ResolvePath(P(@"docs\deep")).Id;
        Directory.Delete(P("docs"), true);

        Assert.True(FolderRefresher.Detect(store.SnapshotChildren(deep), Options()).DirectoryGone);
    }

    // ------------------------------------------------------------------ 반영

    [Fact]
    public async Task Refreshing_a_folder_matches_a_full_rescan()
    {
        var store = await ScanAsync();
        Write("new1.txt", 10);
        File.Delete(P("b.log"));
        Write("a.txt", 999);
        Write(@"brandnew\one.dat", 300);                       // 새 폴더 — 안쪽까지 읽어야 한다
        Write(@"brandnew\sub\two.dat", 400);
        Directory.Delete(P("media"), true);

        var plan = Refresh(store, "", deep: false);

        Assert.True(plan.Diff.Any);
        AssertSameAsFresh(store, await ScanAsync());
        var brandNew = store.ResolvePath(P(@"brandnew\sub\two.dat"));
        Assert.Equal(PathKind.File, brandNew.Kind);
        Assert.Equal(700, store.GetDirectorySize(store.ResolvePath(P("brandnew")).Id));
        Assert.Equal(PathKind.NotFound, store.ResolvePath(P("media")).Kind);
        Assert.Equal(999, store.GetFileSize(store.ResolvePath(P("a.txt")).Id));
    }

    [Fact]
    public async Task Refresh_updates_the_totals_of_every_ancestor()
    {
        var store = await ScanAsync();
        long before = store.TotalSize;
        Write(@"docs\deep\more\big.bin", 1000);
        int more = store.ResolvePath(P(@"docs\deep\more")).Id;

        var plan = FolderRefresher.Build(store.SnapshotChildren(more), Options(), deep: false);
        var result = store.ApplyRefresh(plan);

        Assert.Equal(1, result.AddedFiles);
        Assert.Equal(before + 1000, store.TotalSize);
        Assert.Equal(1030, store.GetDirectorySize(more));
        Assert.Equal(1100, store.GetDirectorySize(store.ResolvePath(P(@"docs\deep")).Id));
    }

    [Fact]
    public async Task Shallow_refresh_leaves_stale_subfolders_but_deep_refresh_fixes_them()
    {
        var store = await ScanAsync();
        Write(@"docs\deep\more\extra.bin", 500);
        File.Delete(P(@"docs\readme.md"));
        Write("touch.txt", 1);

        Refresh(store, "", deep: false);
        Assert.NotEqual((await ScanAsync()).TotalSize, store.TotalSize);   // 안쪽 변경은 아직 모른다

        Refresh(store, "", deep: true);
        AssertSameAsFresh(store, await ScanAsync());
    }

    [Fact]
    public async Task A_folder_that_vanished_is_removed_from_the_store()
    {
        var store = await ScanAsync();
        int docs = store.ResolvePath(P("docs")).Id;
        long docsSize = store.GetDirectorySize(docs);
        long before = store.TotalSize;
        Directory.Delete(P("docs"), true);

        var plan = FolderRefresher.Build(store.SnapshotChildren(docs), Options(), deep: false);
        var result = store.ApplyRefresh(plan);

        Assert.True(plan.DirectoryGone);
        Assert.Equal(before - docsSize, store.TotalSize);
        Assert.Equal(PathKind.NotFound, store.ResolvePath(P(@"docs\deep\notes.txt")).Kind);
        Assert.Equal(NodeStore.RootId, store.NearestLiveDirectory(docs));
        Assert.True(result.RemovedDirectories >= 3);
        AssertSameAsFresh(store, await ScanAsync());
    }

    [Fact]
    public async Task Applying_the_same_plan_twice_does_not_duplicate_anything()
    {
        var store = await ScanAsync();
        Write("dup.txt", 40);
        Write(@"dupdir\f.txt", 60);
        var plan = FolderRefresher.Build(store.SnapshotChildren(NodeStore.RootId), Options(), deep: false);

        store.ApplyRefresh(plan);
        store.ApplyRefresh(plan);

        AssertSameAsFresh(store, await ScanAsync());
    }

    [Fact]
    public async Task Items_deleted_elsewhere_after_the_plan_was_made_are_skipped()
    {
        var store = await ScanAsync();
        File.Delete(P("b.log"));
        var plan = FolderRefresher.Build(store.SnapshotChildren(NodeStore.RootId), Options(), deep: false);

        store.RemoveNodes(new[] { (RowKind.File, store.ResolvePath(P("b.log")).Id) });   // 그 사이 삭제 화면에서 먼저 지웠다
        store.ApplyRefresh(plan);

        AssertSameAsFresh(store, await ScanAsync());
    }

    [Fact]
    public async Task Top_files_and_extensions_follow_a_refresh()
    {
        var store = await ScanAsync();
        Write("huge.iso", 9000);
        File.Delete(P(@"media\clip.mp4"));

        Refresh(store, "", deep: false);
        Refresh(store, "media", deep: false);

        var top = store.GetTopFiles(3);
        Assert.Equal("huge.iso", top[0].Name);
        Assert.DoesNotContain(top, r => r.Name == "clip.mp4");
        Assert.Contains(store.GetExtensionRows(), r => r.Extension == ".iso");
    }

    // ------------------------------------------------------------------ 경로 찾기

    [Fact]
    public async Task ResolvePath_tells_directory_file_missing_and_out_of_scope_apart()
    {
        var store = await ScanAsync();

        Assert.Equal(PathKind.Directory, store.ResolvePath(P("DOCS\\Deep")).Kind);                 // 대소문자 무시
        Assert.Equal(PathKind.Directory, store.ResolvePath(P(@"docs\deep") + "\\").Kind);          // 끝의 \
        Assert.Equal(NodeStore.RootId, store.ResolvePath(_root + "\\").Id);
        Assert.Equal(PathKind.File, store.ResolvePath(P(@"docs\readme.md")).Kind);
        Assert.Equal(store.ResolvePath(P("docs")).Id, store.ResolvePath(P(@"docs\readme.md")).NearestDirId);

        var missing = store.ResolvePath(P(@"docs\nope\deeper"));
        Assert.Equal(PathKind.NotFound, missing.Kind);
        Assert.Equal(store.ResolvePath(P("docs")).Id, missing.NearestDirId);                      // 가장 가까운 있는 폴더

        Assert.Equal(PathKind.OutOfScope, store.ResolvePath(@"Z:\other").Kind);
        Assert.Equal(PathKind.OutOfScope, store.ResolvePath(_root + "X\\y").Kind);                // 접두사만 같은 다른 폴더
        Assert.Equal(PathKind.OutOfScope, store.ResolvePath("").Kind);
    }

    [Fact]
    public async Task ChildDirectoryNames_lists_live_folders_only()
    {
        var store = await ScanAsync();
        store.RemoveNodes(new[] { (RowKind.Directory, store.ResolvePath(P("media")).Id) });

        var names = store.ChildDirectoryNames(NodeStore.RootId);

        Assert.Equal(new[] { "docs" }, names);
    }
}

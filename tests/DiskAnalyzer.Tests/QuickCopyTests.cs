using DiskAnalyzer.Core.QuickMove;
using Xunit;

namespace DiskAnalyzer.Tests;

/// <summary>빠른 복사: 원본은 어떤 경우에도 남고, 충돌 처리 / 폴더 병합 / 진행률은 이동과 같다.</summary>
public sealed class QuickCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "da_quickcopy_" + Guid.NewGuid().ToString("N"));
    private readonly string _src;
    private readonly string _dst;

    public QuickCopyTests()
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

    private static MoveRequest Req(string source, string destDir, bool copy = true)
    {
        bool isDir = Directory.Exists(source);
        var m = TreeMeasure.Measure(source);
        return new MoveRequest
        {
            SourcePath = source,
            DestDirectory = destDir,
            IsDirectory = isDir,
            IsCopy = copy,
            Size = m.Bytes,
            FileCount = isDir ? m.Files : 1,
        };
    }

    private static string File1(string dir, string name, string content = "hello")
    {
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Copies_a_file_and_keeps_the_source()
    {
        string f = File1(_src, "a.txt", "content-A");

        var s = new MoveEngine().Run(new[] { Req(f, _dst) });

        Assert.True(s.AllSucceeded);
        Assert.Equal(1, s.Moved);
        Assert.Equal("content-A", File.ReadAllText(f));
        Assert.Equal("content-A", File.ReadAllText(Path.Combine(_dst, "a.txt")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Copies_a_folder_tree_and_keeps_the_source(int parallelism)
    {
        string root = Path.Combine(_src, "tree");
        for (int i = 0; i < 12; i++) File1(Path.Combine(root, "sub" + (i % 3)), $"f{i}.txt", "x" + i);
        File1(root, "top.txt", "top");

        var s = new MoveEngine(new MoveOptions { Parallelism = parallelism }).Run(new[] { Req(root, _dst) });

        Assert.True(s.AllSucceeded);
        Assert.Equal(13, Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count());
        Assert.Equal(13, Directory.EnumerateFiles(Path.Combine(_dst, "tree"), "*", SearchOption.AllDirectories).Count());
        Assert.Equal("x7", File.ReadAllText(Path.Combine(_dst, "tree", "sub1", "f7.txt")));
    }

    [Fact]
    public void Skip_policy_keeps_the_existing_target()
    {
        string f = File1(_src, "a.txt", "new");
        File1(_dst, "a.txt", "old");

        var s = new MoveEngine(new MoveOptions { Policy = ConflictPolicy.Skip }).Run(new[] { Req(f, _dst) });

        Assert.Equal(1, s.Skipped);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_dst, "a.txt")));
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void Overwrite_policy_replaces_even_a_readonly_target()
    {
        string f = File1(_src, "a.txt", "new");
        string t = File1(_dst, "a.txt", "old");
        File.SetAttributes(t, FileAttributes.ReadOnly);

        var s = new MoveEngine(new MoveOptions { Policy = ConflictPolicy.Overwrite }).Run(new[] { Req(f, _dst) });

        Assert.True(s.AllSucceeded);
        Assert.Equal("new", File.ReadAllText(t));
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void Rename_policy_keeps_both_and_reports_the_final_path()
    {
        string f = File1(_src, "a.txt", "new");
        File1(_dst, "a.txt", "old");

        var s = new MoveEngine(new MoveOptions { Policy = ConflictPolicy.Rename }).Run(new[] { Req(f, _dst) });

        Assert.True(s.AllSucceeded);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_dst, "a.txt")));
        Assert.NotEqual(Path.Combine(_dst, "a.txt"), s.Results[0].FinalPath);
        Assert.Equal("new", File.ReadAllText(s.Results[0].FinalPath));
    }

    [Fact]
    public void Overwrite_merges_folders_and_keeps_unrelated_target_files()
    {
        string root = Path.Combine(_src, "tree");
        File1(root, "a.txt", "src-a");
        File1(root, "b.txt", "src-b");
        File1(Path.Combine(_dst, "tree"), "a.txt", "old-a");
        File1(Path.Combine(_dst, "tree"), "keep.txt", "keep");

        var s = new MoveEngine(new MoveOptions { Policy = ConflictPolicy.Overwrite }).Run(new[] { Req(root, _dst) });

        Assert.True(s.AllSucceeded);
        Assert.Equal("src-a", File.ReadAllText(Path.Combine(_dst, "tree", "a.txt")));
        Assert.Equal("src-b", File.ReadAllText(Path.Combine(_dst, "tree", "b.txt")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_dst, "tree", "keep.txt")));
        Assert.True(File.Exists(Path.Combine(root, "a.txt")));
    }

    [Fact]
    public void Missing_source_fails_without_creating_anything()
    {
        var req = new MoveRequest { SourcePath = Path.Combine(_src, "nope.txt"), DestDirectory = _dst, IsCopy = true, Size = 1 };

        var s = new MoveEngine().Run(new[] { req });

        Assert.Equal(1, s.Failed);
        Assert.Empty(Directory.GetFileSystemEntries(_dst));
    }

    [Fact]
    public void Mixed_queue_moves_and_copies_each_item_its_own_way()
    {
        string moveMe = File1(_src, "move.txt", "m");
        string copyMe = File1(_src, "copy.txt", "c");

        var s = new MoveEngine().Run(new[] { Req(moveMe, _dst, copy: false), Req(copyMe, _dst) });

        Assert.True(s.AllSucceeded);
        Assert.False(File.Exists(moveMe));
        Assert.True(File.Exists(copyMe));
        Assert.True(File.Exists(Path.Combine(_dst, "move.txt")));
        Assert.True(File.Exists(Path.Combine(_dst, "copy.txt")));
    }

    [Fact]
    public void Copy_does_not_evaluate_source_protection_but_move_does()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string sys = Path.Combine(win, "System32", "notepad.exe");
        if (!File.Exists(sys)) return;

        var copy = new MoveRequest { SourcePath = sys, DestDirectory = _dst, IsCopy = true, Size = 1 };
        var move = new MoveRequest { SourcePath = sys, DestDirectory = _dst, Size = 1 };

        Assert.DoesNotContain(MoveValidator.Validate(new[] { copy }), i => i.Severity == IssueSeverity.Error);
        Assert.Contains(MoveValidator.Validate(new[] { move }), i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void CheckSpace_counts_same_volume_copies_as_required()
    {
        string f = File1(_src, "a.txt", "0123456789");

        var row = MoveValidator.CheckSpace(new[] { Req(f, _dst) }).Single();

        Assert.Equal(10, row.Required);
        Assert.Equal(0, row.InPlace);
    }

    [Fact]
    public void Progress_reaches_the_total_for_a_copy()
    {
        string root = Path.Combine(_src, "tree");
        for (int i = 0; i < 5; i++) File1(root, $"f{i}.txt", new string('x', 100 + i));
        MoveProgress? last = null;

        new MoveEngine(new MoveOptions { Progress = new SyncProgress(p => last = p) }).Run(new[] { Req(root, _dst) });

        Assert.NotNull(last);
        Assert.Equal(1d, last!.Fraction, 3);
    }

    private sealed class SyncProgress(Action<MoveProgress> onReport) : IProgress<MoveProgress>
    {
        public void Report(MoveProgress value) => onReport(value);
    }
}

using System.Diagnostics;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;
using Xunit;

namespace DiskAnalyzer.Tests;

/// <summary>
/// 실제 파일 시스템을 쓰는 테스트. 임시 폴더 안에서만 만들고 지운다.
/// 시스템 경로나 사용자 데이터에는 절대 접근하지 않는다.
/// </summary>
public sealed class FastDeleterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "da_fastdel_" + Guid.NewGuid().ToString("N"));

    public FastDeleterTests() => Directory.CreateDirectory(_root);

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

    private string MakeTree(string name, int dirs = 5, int filesPerDir = 20)
    {
        string top = Path.Combine(_root, name);
        for (int d = 0; d < dirs; d++)
        {
            string dir = Path.Combine(top, $"d{d}", "nested", "deeper");
            Directory.CreateDirectory(dir);
            for (int f = 0; f < filesPerDir; f++)
            {
                File.WriteAllText(Path.Combine(dir, $"f{f}.txt"), "x");
                File.WriteAllText(Path.Combine(top, $"d{d}", $"top{f}.txt"), "x");
            }
        }
        return top;
    }

    [Fact]
    public void Deletes_A_Nested_Tree_Completely()
    {
        string top = MakeTree("tree");

        var r = FastDeleter.Delete(top);

        Assert.True(r.Success);
        Assert.Equal(5 * 20 * 2, r.FilesDeleted);
        Assert.False(Directory.Exists(top));
    }

    [Fact]
    public void Deletes_A_Single_File()
    {
        string file = Path.Combine(_root, "one.bin");
        File.WriteAllText(file, "x");

        var r = FastDeleter.Delete(file);

        Assert.True(r.Success);
        Assert.Equal(1, r.FilesDeleted);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Missing_Path_Is_Reported_As_NotFound_Not_As_Failure()
    {
        var r = FastDeleter.Delete(Path.Combine(_root, "nope"));

        Assert.True(r.NotFound);
        Assert.Equal(0, r.Failed);
    }

    [Fact]
    public void Read_Only_And_Hidden_Files_Are_Deleted()
    {
        string top = MakeTree("ro", dirs: 2, filesPerDir: 3);
        foreach (var f in Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);

        var r = FastDeleter.Delete(top);

        Assert.True(r.Success);
        Assert.False(Directory.Exists(top));
    }

    [Fact]
    public void A_Locked_File_Fails_But_The_Rest_Is_Still_Deleted()
    {
        string top = MakeTree("locked", dirs: 3, filesPerDir: 4);
        string locked = Path.Combine(top, "d1", "top2.txt");

        // 공유 없이 열어 두면 다른 프로세스(여기서는 삭제기)가 지울 수 없다.
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = FastDeleter.Delete(top);

            Assert.False(r.Success);
            Assert.Equal(1, r.Failed);
            Assert.Equal(1, r.InUse);
            Assert.EndsWith("top2.txt", r.FirstFailedPath);
            Assert.True(File.Exists(locked));                          // 잠긴 파일은 남고
            Assert.Single(Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories));   // 나머지는 모두 지워졌다
        }

        // 잠금이 풀리면 다시 시도해서 지울 수 있다.
        var again = FastDeleter.Delete(top);
        Assert.True(again.Success);
        Assert.False(Directory.Exists(top));
    }

    [Fact]
    public void Junction_Is_Removed_Without_Deleting_Its_Target()
    {
        string target = Path.Combine(_root, "precious");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep me");

        string top = Path.Combine(_root, "withlink");
        Directory.CreateDirectory(top);
        File.WriteAllText(Path.Combine(top, "a.txt"), "x");
        string link = Path.Combine(top, "link");

        if (!TryMakeJunction(link, target)) return;   // 이 환경에서 junction 을 만들 수 없으면 건너뛴다

        var r = FastDeleter.Delete(top);

        Assert.True(r.Success);
        Assert.False(Directory.Exists(top));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));    // 링크 대상은 무사해야 한다
    }

    [Fact]
    public void Deleting_The_Junction_Itself_Leaves_The_Target()
    {
        string target = Path.Combine(_root, "precious2");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep me");
        string link = Path.Combine(_root, "link2");

        if (!TryMakeJunction(link, target)) return;

        var r = FastDeleter.Delete(link);

        Assert.True(r.Success);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public void Paths_Longer_Than_MaxPath_Are_Deleted()
    {
        string dir = Path.Combine(_root, "long");
        for (int i = 0; i < 12; i++) dir = Path.Combine(dir, new string('a', 30) + i);   // 300자 이상
        Assert.True(dir.Length > 300);

        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "deep.txt"), "x");

        var r = FastDeleter.Delete(Path.Combine(_root, "long"));

        Assert.True(r.Success);
        Assert.False(Directory.Exists(Path.Combine(_root, "long")));
    }

    [Fact]
    public void A_Large_Flat_Folder_Is_Deleted_Using_Inner_Parallelism()
    {
        string dir = Path.Combine(_root, "flat");
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 3000; i++) File.WriteAllBytes(Path.Combine(dir, $"f{i}.dat"), new byte[1]);

        var r = FastDeleter.Delete(dir, parallelism: 8);

        Assert.True(r.Success);
        Assert.Equal(3000, r.FilesDeleted);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Cancelled_Token_Stops_Before_Deleting()
    {
        string top = MakeTree("cancel", dirs: 2, filesPerDir: 2);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var r = FastDeleter.Delete(top, ct: cts.Token);

        Assert.True(r.Cancelled);
        Assert.False(r.Success);
        Assert.True(Directory.Exists(top));
    }

    [Fact]
    public void Progress_Callback_Reports_Cumulative_File_Count()
    {
        string top = MakeTree("progress", dirs: 4, filesPerDir: 200);   // 1,600 files
        long last = 0;

        var r = FastDeleter.Delete(top, parallelism: 4, onFiles: n => Interlocked.Exchange(ref last, Math.Max(n, Interlocked.Read(ref last))));

        Assert.True(r.Success);
        Assert.True(last > 0 && last <= r.FilesDeleted);
    }

    // ---- DeletionService 연동 -------------------------------------------------

    [Fact]
    public async Task DeletionService_Permanent_Deletes_Folder_And_File_Items()
    {
        string top = MakeTree("svc", dirs: 2, filesPerDir: 5);
        string file = Path.Combine(_root, "solo.bin");
        File.WriteAllText(file, "x");

        var summary = await DeletionService.DeleteAsync(new[]
        {
            new DeletionRequest { Kind = RowKind.Directory, Id = 1, Path = top, Size = 100, Name = "svc" },
            new DeletionRequest { Kind = RowKind.File, Id = 2, Path = file, Size = 1, Name = "solo.bin" },
        }, permanent: true);

        Assert.Equal(2, summary.SucceededCount);
        Assert.False(Directory.Exists(top));
        Assert.False(File.Exists(file));
        Assert.Equal(2, summary.RemovedNodes.Count);
    }

    [Fact]
    public async Task DeletionService_Reports_Already_Gone_And_Keeps_Order()
    {
        string a = Path.Combine(_root, "a.bin");
        string c = Path.Combine(_root, "c.bin");
        File.WriteAllText(a, "x");
        File.WriteAllText(c, "x");

        var summary = await DeletionService.DeleteAsync(new[]
        {
            new DeletionRequest { Kind = RowKind.File, Id = 1, Path = a, Size = 1, Name = "a" },
            new DeletionRequest { Kind = RowKind.File, Id = 2, Path = Path.Combine(_root, "missing.bin"), Size = 1, Name = "b" },
            new DeletionRequest { Kind = RowKind.File, Id = 3, Path = c, Size = 1, Name = "c" },
        }, permanent: true);

        Assert.Equal(new[] { 1, 2, 3 }, summary.Outcomes.Select(o => o.Request.Id));
        Assert.Equal(DeletionOutcomeKind.AlreadyGone, summary.Outcomes[1].Kind);
        Assert.Equal(2, summary.SucceededCount);
    }

    [Fact]
    public async Task DeletionService_Locked_File_Is_Reported_As_InUse()
    {
        string file = Path.Combine(_root, "busy.bin");
        File.WriteAllText(file, "x");

        DeletionSummary summary;
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            summary = await DeletionService.DeleteAsync(new[]
            {
                new DeletionRequest { Kind = RowKind.File, Id = 1, Path = file, Size = 1, Name = "busy" },
            }, permanent: true);
        }

        // 사용 중인 파일은 삭제 직전 재검증에서 P1 로 올라가지만 P0 는 아니다. 삭제를 시도하고 실패를 보고한다.
        Assert.Equal(1, summary.FailedCount);
        Assert.Equal(DeletionOutcomeKind.InUse, summary.Outcomes[0].Kind);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task DeletionService_Progress_Is_Throttled_And_Finishes_At_Total()
    {
        var items = new List<DeletionRequest>();
        for (int i = 0; i < 300; i++)
        {
            string f = Path.Combine(_root, $"p{i}.bin");
            File.WriteAllText(f, "x");
            items.Add(new DeletionRequest { Kind = RowKind.File, Id = i, Path = f, Size = 1, Name = $"p{i}" });
        }

        var reports = new List<DeletionProgress>();
        var progress = new SyncProgress<DeletionProgress>(p => { lock (reports) reports.Add(p); });

        var summary = await DeletionService.DeleteAsync(items, permanent: true, progress);

        Assert.Equal(300, summary.SucceededCount);
        Assert.NotEmpty(reports);
        Assert.Equal(300, reports[^1].Done);          // 마지막 보고는 전체 완료 상태
        Assert.True(reports.Count < 300 * 3);          // 항목마다 보고하지 않는다(쓰로틀)
    }

    [Fact]
    public void ToExtended_Handles_Drive_And_Unc_Paths()
    {
        Assert.Equal(@"\\?\C:\Temp\x", FastDeleter.ToExtended(@"C:\Temp\x\"));
        Assert.Equal(@"\\?\UNC\server\share\dir", FastDeleter.ToExtended(@"\\server\share\dir"));
        Assert.Equal(@"\\?\C:\already", FastDeleter.ToExtended(@"\\?\C:\already"));
    }

    // ---- 도우미 --------------------------------------------------------------

    private static bool TryMakeJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Progress&lt;T&gt; 는 SynchronizationContext 로 넘기며 순서가 흔들릴 수 있어서 테스트에서는 동기 구현을 쓴다.</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}

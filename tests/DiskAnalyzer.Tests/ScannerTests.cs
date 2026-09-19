using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Scanning;
using DiskAnalyzer.Core.Services;
using Xunit;

namespace DiskAnalyzer.Tests;

/// <summary>
/// 실제 임시 디렉터리 트리를 만들어 Compatibility Scan 전체 파이프라인
/// (Worker Pool -> Channel -> Aggregator -> NodeStore -> 롤업) 을 검증한다.
/// </summary>
public sealed class ScannerTests : IDisposable
{
    private readonly string _root;
    private readonly long _expectedBytes;
    private readonly int _expectedFiles;
    private readonly int _expectedDirs;

    public ScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "DiskAnalyzerTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var rnd = new Random(7);
        int files = 0, dirs = 0;
        long bytes = 0;

        // 3단계 깊이 * 폴더 4개 * 파일 6개
        for (int a = 0; a < 4; a++)
        {
            string da = Path.Combine(_root, "dir" + a);
            Directory.CreateDirectory(da);
            dirs++;

            for (int b = 0; b < 4; b++)
            {
                string db = Path.Combine(da, "sub" + b);
                Directory.CreateDirectory(db);
                dirs++;

                for (int f = 0; f < 6; f++)
                {
                    string path = Path.Combine(db, $"f{f}{(f % 2 == 0 ? ".log" : ".pdb")}");
                    int size = rnd.Next(1, 4096);
                    File.WriteAllBytes(path, new byte[size]);
                    files++;
                    bytes += size;
                }
            }
        }

        _expectedFiles = files;
        _expectedDirs = dirs;
        _expectedBytes = bytes;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static ScanOptions Options(int workers = 4) => new()
    {
        Mode = ScanMode.Compatibility,
        WorkerCount = workers,
        CacheResults = false,
    };

    [Fact]
    public async Task Scan_Counts_Everything_Exactly()
    {
        var result = await new ScanController().ScanAsync(_root, Options());

        Assert.Equal(_expectedFiles, result.FileCount);
        Assert.Equal(_expectedDirs, result.DirectoryCount);
        Assert.Equal(_expectedBytes, result.TotalSize);
        Assert.False(result.Cancelled);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(16)]
    public async Task Result_Is_Identical_Regardless_Of_WorkerCount(int workers)
    {
        // 병렬 열거에서 "폴더 배치보다 파일 배치가 먼저 도착" 하는 경우에도
        // 크기가 누락되지 않는지 확인한다(워커 수를 늘릴수록 재현 확률이 높아진다).
        var result = await new ScanController().ScanAsync(_root, Options(workers));

        Assert.Equal(_expectedFiles, result.FileCount);
        Assert.Equal(_expectedBytes, result.TotalSize);
    }

    [Fact]
    public async Task ExtensionStats_Match_Total()
    {
        var result = await new ScanController().ScanAsync(_root, Options());
        var rows = result.Store.GetExtensionRows();

        Assert.Equal(_expectedFiles, rows.Sum(r => r.Count));
        Assert.Equal(_expectedBytes, rows.Sum(r => r.Size));
        Assert.Contains(rows, r => r.Extension == ".log");
        Assert.Contains(rows, r => r.Extension == ".pdb");
    }

    [Fact]
    public async Task TopFiles_Match_A_Full_Sort()
    {
        var result = await new ScanController().ScanAsync(_root, Options());

        var top = result.Store.GetTopFiles(10).Select(r => r.Size).ToArray();
        var reference = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f).Length)
            .OrderByDescending(x => x)
            .Take(10)
            .ToArray();

        Assert.Equal(reference, top);
    }

    [Fact]
    public async Task Cancel_Stops_Quickly_And_Keeps_PartialData()
    {
        var controller = new ScanController();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(1);

        var result = await controller.ScanAsync(_root, Options(), cts.Token);

        // 취소 여부와 무관하게 결과 객체는 항상 일관된 상태여야 한다.
        Assert.True(result.FileCount <= _expectedFiles);
        Assert.True(result.TotalSize <= _expectedBytes);
    }

    [Fact]
    public async Task Cache_RoundTrips()
    {
        var result = await new ScanController().ScanAsync(_root, Options());
        Assert.True(CacheService.TrySave(result));

        var loaded = CacheService.TryLoad(_root);
        Assert.NotNull(loaded);
        Assert.True(loaded!.FromCache);
        Assert.Equal(result.FileCount, loaded.FileCount);
        Assert.Equal(result.DirectoryCount, loaded.DirectoryCount);
        Assert.Equal(result.TotalSize, loaded.TotalSize);

        var before = result.Store.GetChildren(0, includeFiles: false).Select(r => (r.Name, r.Size));
        var after = loaded.Store.GetChildren(0, includeFiles: false).Select(r => (r.Name, r.Size));
        Assert.Equal(before, after);

        try { File.Delete(CacheService.GetCachePath(ScanController.NormalizeRoot(_root))); } catch { }
    }

    [Fact]
    public async Task Csv_Export_Writes_All_Rows()
    {
        var result = await new ScanController().ScanAsync(_root, Options());
        var rows = result.Store.GetChildren(0, includeFiles: true);

        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            ExportService.ExportCsv(path, rows);
            var lines = File.ReadAllLines(path);
            Assert.Equal(rows.Count + 1, lines.Length);   // 헤더 1줄 + 데이터
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}

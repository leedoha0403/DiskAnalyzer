using System.Runtime.InteropServices;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Scanning;
using Xunit;

namespace DiskAnalyzer.Tests;

/// <summary>
/// 하드 링크 · 물리 크기를 실제 파일 시스템에서 확인한다.
/// 같은 실체가 여러 경로에 걸려 있을 때 합계가 부풀지 않아야 한다.
/// </summary>
public sealed class HardLinkScanTests : IDisposable
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "da_hardlink_" + Guid.NewGuid().ToString("N")[..8]);

    private const int Payload = 300_000;

    /// <summary>원본 1개 + 하드 링크 2개. 링크를 만들 수 없는 환경이면 false.</summary>
    private bool Prepare()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "copies"));

        string original = Path.Combine(_root, "payload.bin");
        File.WriteAllBytes(original, new byte[Payload]);

        return CreateHardLinkW(Path.Combine(_root, "copies", "link1.bin"), original, IntPtr.Zero)
            && CreateHardLinkW(Path.Combine(_root, "copies", "link2.bin"), original, IntPtr.Zero);
    }

    private static async Task<ScanResult> ScanAsync(string path, bool dedupe, SizeBasis basis)
    {
        var controller = new ScanController();
        var options = new ScanOptions
        {
            Mode = ScanMode.Compatibility,
            CacheResults = false,
            DeduplicateHardLinks = dedupe,
            SizeBasis = basis,
        };

        return await controller.ScanAsync(path, options, CancellationToken.None);
    }

    [Fact]
    public async Task Hard_Links_Are_Counted_Once_When_Deduplicating()
    {
        if (!Prepare()) return;   // 링크를 못 만드는 파일 시스템이면 확인할 것이 없다

        var counted = await ScanAsync(_root, dedupe: false, SizeBasis.Logical);
        var deduped = await ScanAsync(_root, dedupe: true, SizeBasis.Logical);

        // 끄면 같은 바이트를 세 번 센다.
        Assert.InRange(counted.TotalSize, Payload * 3L - 4096, Payload * 3L + 4096);

        // 켜면 한 번만. 파일 수는 그대로다 - 항목이 사라지는 것이 아니라 크기가 0 이 된다.
        Assert.InRange(deduped.TotalSize, Payload - 4096, Payload + 4096);
        Assert.Equal(counted.FileCount, deduped.FileCount);
    }

    [Fact]
    public async Task Physical_Size_Rounds_To_Clusters_And_Keeps_Totals_Sane()
    {
        if (!Prepare()) return;

        var logical = await ScanAsync(_root, dedupe: true, SizeBasis.Logical);
        var physical = await ScanAsync(_root, dedupe: true, SizeBasis.Physical);

        // 할당 크기는 논리 크기보다 작지 않고, 클러스터 하나(보통 4 KB) 안쪽에서 커진다.
        Assert.True(physical.TotalSize >= logical.TotalSize,
            $"물리 {physical.TotalSize} < 논리 {logical.TotalSize}");
        Assert.InRange(physical.TotalSize - logical.TotalSize, 0, 64 * 1024);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* 임시 폴더가 남아도 시험 결과에는 영향이 없다 */ }
    }
}

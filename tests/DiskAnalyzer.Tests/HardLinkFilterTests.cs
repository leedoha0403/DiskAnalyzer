using DiskAnalyzer.Core.Scanning;
using Xunit;

namespace DiskAnalyzer.Tests;

/// <summary>
/// 하드 링크를 한 번만 세기 위한 비트맵 필터.
/// Dictionary 로는 2,900만 파일에서 1GB 를 넘기므로, NTFS 파일 id 의 하위 48비트(MFT 레코드 번호)를
/// 비트 자리로 쓰는 구조다 — 그래서 "id 를 그대로 키로 쓰지 않는다" 는 점이 시험 대상이다.
/// </summary>
public class HardLinkFilterTests
{
    private static long Id(long record, int sequence = 1) => ((long)sequence << 48) | record;

    [Fact]
    public void First_Sighting_Wins_And_The_Rest_Are_Duplicates()
    {
        var f = new HardLinkFilter();

        Assert.True(f.TryClaim(Id(1234)));
        Assert.False(f.TryClaim(Id(1234)));
        Assert.False(f.TryClaim(Id(1234)));

        Assert.Equal(2, f.Duplicates);
    }

    /// <summary>시퀀스 번호는 같은 레코드를 재사용할 때 올라간다. 같은 실체 판정은 레코드 번호로 한다.</summary>
    [Fact]
    public void Sequence_Number_Is_Ignored()
    {
        var f = new HardLinkFilter();

        Assert.True(f.TryClaim(Id(99, sequence: 1)));
        Assert.False(f.TryClaim(Id(99, sequence: 7)));
    }

    [Fact]
    public void Different_Records_Do_Not_Collide()
    {
        var f = new HardLinkFilter();

        for (long r = 1; r <= 5000; r++) Assert.True(f.TryClaim(Id(r)));
        Assert.Equal(0, f.Duplicates);

        for (long r = 1; r <= 5000; r++) Assert.False(f.TryClaim(Id(r)));
        Assert.Equal(5000, f.Duplicates);
    }

    /// <summary>덩어리 경계를 넘어가도 자란다. 1M 비트마다 새 덩어리가 붙는다.</summary>
    [Fact]
    public void Grows_Across_Chunks()
    {
        var f = new HardLinkFilter();

        foreach (long r in new[] { 1L, (1L << 20) + 5, (1L << 22) + 7, (1L << 26) + 9 })
        {
            Assert.True(f.TryClaim(Id(r)));
            Assert.False(f.TryClaim(Id(r)));
        }
    }

    /// <summary>id 를 모르면(0) 무조건 센다 — 모른다고 0 바이트로 버리면 용량이 사라진다.</summary>
    [Fact]
    public void Unknown_Id_Is_Always_Counted()
    {
        var f = new HardLinkFilter();

        Assert.True(f.TryClaim(0));
        Assert.True(f.TryClaim(0));
        Assert.Equal(0, f.Duplicates);
    }

    [Fact]
    public void Concurrent_Claims_Elect_Exactly_One_Winner()
    {
        var f = new HardLinkFilter();
        const int threads = 16;
        int wins = 0;

        Parallel.For(0, threads, _ =>
        {
            if (f.TryClaim(Id(777))) Interlocked.Increment(ref wins);
        });

        Assert.Equal(1, wins);
        Assert.Equal(threads - 1, f.Duplicates);
    }
}

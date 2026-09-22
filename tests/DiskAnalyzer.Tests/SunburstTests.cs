using DiskAnalyzer.Core.Analysis;
using DiskAnalyzer.Core.Models;
using Xunit;

namespace DiskAnalyzer.Tests;

/// <summary>
/// 선버스트 레이아웃 — "원 한 바퀴가 언제나 현재 폴더 전체" 라는 불변식이 핵심이다.
/// </summary>
public class SunburstLayoutTests
{
    private const long Mb = 1024L * 1024L;

    /// <summary>
    /// C:\
    ///  ├ A 700MB (a1=100, a2=200)
    ///  │  └ B 400MB (b1=400)
    ///  ├ C 200MB (c1=200)
    ///  └ big.bin 100MB
    /// </summary>
    private static NodeStore Build()
    {
        var store = new NodeStore("C:\\");
        store.SetDirectory(1, NodeStore.RootId, "A", 0, 0);
        store.SetDirectory(2, 1, "B", 0, 0);
        store.SetDirectory(3, NodeStore.RootId, "C", 0, 0);

        store.AddFile(1, "a1.txt", 100 * Mb, 0, 0);
        store.AddFile(1, "a2.log", 200 * Mb, 0, 0);
        store.AddFile(2, "b1.pdb", 400 * Mb, 0, 0);
        store.AddFile(3, "c1.dat", 200 * Mb, 0, 0);
        store.AddFile(NodeStore.RootId, "big.bin", 100 * Mb, 0, 0);

        store.Seal();
        return store;
    }

    [Fact]
    public void First_Ring_Fills_The_Whole_Circle()
    {
        var layout = SunburstLayout.Build(Build(), NodeStore.RootId);

        var ring0 = layout.Segments.Where(s => s.Ring == 0).OrderBy(s => s.Start).ToList();
        Assert.NotEmpty(ring0);
        Assert.Equal(0d, ring0[0].Start, 6);
        Assert.Equal(360d, ring0[^1].End, 6);

        // 조각 사이에 틈이 없다.
        for (int i = 1; i < ring0.Count; i++)
            Assert.Equal(ring0[i - 1].End, ring0[i].Start, 6);
    }

    [Fact]
    public void Sweep_Is_Proportional_To_Size()
    {
        var layout = SunburstLayout.Build(Build(), NodeStore.RootId);

        // 루트 = 1000MB. A=700, C=200, big.bin=100
        var a = layout.Segments.Single(s => s.Ring == 0 && s.Name == "A");
        var c = layout.Segments.Single(s => s.Ring == 0 && s.Name == "C");
        var big = layout.Segments.Single(s => s.Ring == 0 && s.Name == "big.bin");

        Assert.Equal(252d, a.Sweep, 3);    // 360 * 0.7
        Assert.Equal(72d, c.Sweep, 3);     // 360 * 0.2
        Assert.Equal(36d, big.Sweep, 3);   // 360 * 0.1
    }

    [Fact]
    public void Children_Stay_Inside_Their_Parent_Wedge()
    {
        var layout = SunburstLayout.Build(Build(), NodeStore.RootId);

        foreach (var s in layout.Segments)
        {
            if (s.ParentIndex < 0) continue;
            var parent = layout.Segments[s.ParentIndex];
            Assert.Equal(parent.Ring + 1, s.Ring);
            Assert.True(s.Start >= parent.Start - 1e-6, $"{s.Name} 이 부모보다 앞에서 시작한다");
            Assert.True(s.End <= parent.End + 1e-6, $"{s.Name} 이 부모보다 뒤에서 끝난다");
        }
    }

    [Fact]
    public void Files_Are_Drawn_As_File_Segments()
    {
        var layout = SunburstLayout.Build(Build(), NodeStore.RootId);

        var big = layout.Segments.Single(s => s.Name == "big.bin");
        Assert.Equal(SunburstKind.File, big.Kind);
        Assert.False(big.CanDrill);
        Assert.True(big.CanCollect);
    }

    [Fact]
    public void Ring_Count_Is_Capped()
    {
        var store = new NodeStore("C:\\");
        for (int i = 1; i <= 12; i++) store.SetDirectory(i, i - 1, $"d{i}", 0, 0);
        store.AddFile(12, "leaf.bin", 100 * Mb, 0, 0);
        store.Seal();

        var layout = SunburstLayout.Build(store, NodeStore.RootId, new SunburstOptions { Rings = 4 });

        Assert.Equal(3, layout.Segments.Max(s => s.Ring));
    }

    /// <summary>
    /// 작은 항목을 조용히 버리지 않는다 — 기존 Treemap 의 동작과 달라지는 지점이다.
    /// </summary>
    [Fact]
    public void Tiny_Items_Are_Folded_Into_One_Smaller_Segment()
    {
        var store = new NodeStore("C:\\");
        store.AddFile(NodeStore.RootId, "huge.bin", 10_000 * Mb, 0, 0);
        for (int i = 0; i < 400; i++) store.AddFile(NodeStore.RootId, $"tiny{i}.dat", Mb, 0, 0);
        store.Seal();

        var layout = SunburstLayout.Build(store, NodeStore.RootId);
        var ring0 = layout.Segments.Where(s => s.Ring == 0).ToList();

        var smaller = Assert.Single(ring0, s => s.Kind == SunburstKind.Smaller);
        Assert.Equal(400, smaller.GroupCount);
        Assert.Equal(400 * Mb, smaller.Size);
        Assert.False(smaller.CanCollect);   // 펼쳐야 개별로 담을 수 있다
        Assert.False(smaller.CanDrill);

        // 버린 것이 없으니 여전히 한 바퀴를 채운다.
        Assert.Equal(360d, ring0.OrderBy(s => s.Start).Last().End, 6);
    }

    [Fact]
    public void Empty_Or_Missing_Store_Is_Safe()
    {
        Assert.True(SunburstLayout.Build(null, 0).IsEmpty);
        Assert.True(SunburstLayout.Build(Build(), 9999).IsEmpty);
    }

    [Fact]
    public void HitTest_Finds_The_Segment_Under_An_Angle()
    {
        var layout = SunburstLayout.Build(Build(), NodeStore.RootId);

        var a = layout.Segments.Single(s => s.Ring == 0 && s.Name == "A");
        Assert.Same(a, layout.HitTest(a.Mid, 0));
        Assert.Same(a, layout.HitTest(a.Mid + 360d, 0));    // 각도가 한 바퀴 넘어도 같은 곳
        Assert.Null(layout.HitTest(a.Mid, 9));
    }

    [Fact]
    public void Deleted_Nodes_Never_Appear()
    {
        var store = Build();
        store.RemoveNodes(new[] { (RowKind.Directory, 3) });   // C

        var layout = SunburstLayout.Build(store, NodeStore.RootId);

        Assert.DoesNotContain(layout.Segments, s => s.Name == "C");
        Assert.DoesNotContain(layout.Segments, s => s.Name == "c1.dat");
        Assert.Equal(360d, layout.Segments.Where(s => s.Ring == 0).Max(s => s.End), 6);
    }
}

/// <summary>
/// 색 모델 — DaisyDisk 실측값(docs/daisydisk.html 3.2)을 회귀 테스트로 굳혀 둔다.
/// </summary>
public class SunburstPaletteTests
{
    /// <summary>제품 스크린샷에서 잰 (각도 중심, 색조) 표본. 최대 오차 6°.</summary>
    [Theory]
    [InlineData(93d, 74d)]     // Library      #d1e787
    [InlineData(222d, 172d)]   // Archive      #67e8d7
    [InlineData(282d, 217d)]   // Pictures     #659ff9
    [InlineData(312d, 244d)]   // Documents    #7c72fc
    [InlineData(351d, 270d)]   // Trash        #b872fc
    [InlineData(111d, 92d)]    // Library 자식 #b7e78c
    [InlineData(204d, 159d)]   // Library 손자 #71e8be
    [InlineData(231d, 179d)]   // Library 손자 #72e8e6
    public void Hue_Matches_DaisyDisk_Samples(double angle, double expectedHue)
    {
        Assert.InRange(SunburstPalette.HueAt(angle), expectedHue - 7d, expectedHue + 7d);
    }

    [Fact]
    public void Hue_Stays_In_Range_For_Any_Angle()
    {
        for (double a = -720d; a <= 720d; a += 3.7d)
            Assert.InRange(SunburstPalette.HueAt(a), 0d, 360d);
    }

    [Fact]
    public void Adjacent_Angles_Never_Collide()
    {
        // 각도가 다르면 색조가 다르다 - 팔레트를 돌려쓸 때 생기는 "옆에 같은 색"이 구조적으로 없다.
        Assert.NotEqual(SunburstPalette.HueAt(10d), SunburstPalette.HueAt(40d));
        Assert.NotEqual(SunburstPalette.HueAt(0d), SunburstPalette.HueAt(180d));
    }

    [Fact]
    public void Files_Are_Grey_And_Groups_Are_Translucent()
    {
        var file = SunburstPalette.Fill(SunburstKind.File, 120d);
        Assert.InRange(Math.Abs(file.R - file.G), 0, 22);
        Assert.InRange(Math.Abs(file.G - file.B), 0, 22);
        Assert.Equal(255, file.A);

        var group = SunburstPalette.Fill(SunburstKind.Smaller, 120d);
        Assert.True(group.A < 160, "묶음은 반투명이어야 구분된다");
    }

    [Fact]
    public void Hsl_Conversion_Hits_Known_Colors()
    {
        var red = SunburstPalette.FromHsl(0d, 1d, 0.5d, 255);
        Assert.Equal((byte)255, red.R);
        Assert.Equal((byte)0, red.G);
        Assert.Equal((byte)0, red.B);

        var grey = SunburstPalette.FromHsl(210d, 0d, 0.5d, 255);
        Assert.Equal(grey.R, grey.G);
        Assert.Equal(grey.G, grey.B);
    }
}

public class NodeStoreChildrenTests
{
    private const long Mb = 1024L * 1024L;

    [Fact]
    public void Batch_Collect_Matches_GetChildren()
    {
        var store = new NodeStore("C:\\");
        store.SetDirectory(1, NodeStore.RootId, "A", 0, 0);
        store.SetDirectory(2, NodeStore.RootId, "B", 0, 0);
        store.SetDirectory(3, 1, "A1", 0, 0);
        store.AddFile(1, "a.txt", 30 * Mb, 0, 0);
        store.AddFile(3, "a1.bin", 70 * Mb, 0, 0);
        store.AddFile(2, "b.bin", 10 * Mb, 0, 0);
        store.Seal();

        var batch = store.CollectChildren(new[] { NodeStore.RootId, 1, 2 }, includeFiles: true);

        foreach (int dirId in new[] { NodeStore.RootId, 1, 2 })
        {
            var expected = store.GetChildren(dirId, includeFiles: true)
                                .Select(r => (r.Kind, r.Id, r.Size)).ToList();
            var actual = batch[dirId].Select(c => (c.Kind, c.Id, c.Size)).ToList();
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Scratch_Is_Reusable_Across_Calls()
    {
        var store = new NodeStore("C:\\");
        store.SetDirectory(1, NodeStore.RootId, "A", 0, 0);
        store.AddFile(1, "a.txt", Mb, 0, 0);
        store.Seal();

        var scratch = new ChildScratch();
        for (int i = 0; i < 5; i++)
        {
            Assert.Single(store.CollectChildren(new[] { NodeStore.RootId }, false, scratch)[NodeStore.RootId]);
            Assert.Single(store.CollectChildren(new[] { 1 }, true, scratch)[1]);
        }
    }

    [Fact]
    public void Unknown_Ids_Are_Ignored()
    {
        var store = new NodeStore("C:\\");
        store.AddFile(NodeStore.RootId, "a.txt", Mb, 0, 0);
        store.Seal();

        var batch = store.CollectChildren(new[] { NodeStore.RootId, 500, -3 }, includeFiles: true);

        Assert.Single(batch);
        Assert.True(batch.ContainsKey(NodeStore.RootId));
    }
}

/// <summary>
/// 이전 스캔과 맞춰 보기 — 두 저장소의 id 는 호환되지 않으므로 경로/이름으로만 맞춰야 한다.
/// </summary>
public class ScanDiffTests
{
    private const long Mb = 1024L * 1024L;

    private static NodeStore Build(long aSize, long bSize, bool withC, bool withNewFile)
    {
        var store = new NodeStore("C:\\");
        store.SetDirectory(1, NodeStore.RootId, "A", 0, 0);
        store.AddFile(1, "a.bin", aSize, 0, 0);
        store.SetDirectory(2, NodeStore.RootId, "B", 0, 0);
        store.AddFile(2, "b.bin", bSize, 0, 0);
        if (withC)
        {
            store.SetDirectory(3, NodeStore.RootId, "C", 0, 0);
            store.AddFile(3, "c.bin", 50 * Mb, 0, 0);
        }
        if (withNewFile) store.AddFile(NodeStore.RootId, "new.bin", 10 * Mb, 0, 0);
        store.Seal();
        return store;
    }

    [Fact]
    public void Matches_By_Path_Not_By_Id()
    {
        // 이전에는 C 가 있었고 지금은 없다 → 지금 저장소에서 A · B 의 id 가 이전과 달라진다.
        var previous = Build(100 * Mb, 200 * Mb, withC: true, withNewFile: false);
        var current = Build(300 * Mb, 200 * Mb, withC: false, withNewFile: true);

        var layout = SunburstLayout.Build(current, NodeStore.RootId);
        var sizes = ScanDiff.PreviousSizes(current, previous, layout, NodeStore.RootId);

        var a = layout.Segments.Single(s => s.Ring == 0 && s.Name == "A");
        var b = layout.Segments.Single(s => s.Ring == 0 && s.Name == "B");

        Assert.Equal(100 * Mb, sizes[SunburstLayout.KeyOf(a.Kind, a.Id)]);
        Assert.Equal(200 * Mb, sizes[SunburstLayout.KeyOf(b.Kind, b.Id)]);
    }

    /// <summary>이전에 없던 항목은 0 이 아니라 "없음"이어야 한다 — 0 바이트였던 것과 구별된다.</summary>
    [Fact]
    public void Brand_New_Items_Are_Absent_Not_Zero()
    {
        var previous = Build(100 * Mb, 200 * Mb, withC: false, withNewFile: false);
        var current = Build(100 * Mb, 200 * Mb, withC: false, withNewFile: true);

        var layout = SunburstLayout.Build(current, NodeStore.RootId);
        var sizes = ScanDiff.PreviousSizes(current, previous, layout, NodeStore.RootId);

        var fresh = layout.Segments.Single(s => s.Name == "new.bin");
        Assert.False(sizes.ContainsKey(SunburstLayout.KeyOf(fresh.Kind, fresh.Id)));
    }

    [Fact]
    public void Deeper_Rings_Are_Matched_Too()
    {
        var previous = Build(100 * Mb, 200 * Mb, withC: false, withNewFile: false);
        var current = Build(300 * Mb, 200 * Mb, withC: false, withNewFile: false);

        var layout = SunburstLayout.Build(current, NodeStore.RootId);
        var sizes = ScanDiff.PreviousSizes(current, previous, layout, NodeStore.RootId);

        var inner = layout.Segments.Single(s => s.Ring == 1 && s.Name == "a.bin");
        Assert.Equal(100 * Mb, sizes[SunburstLayout.KeyOf(inner.Kind, inner.Id)]);
    }

    [Fact]
    public void Missing_Previous_Scan_Is_Safe()
    {
        var current = Build(100 * Mb, 200 * Mb, withC: false, withNewFile: false);
        var layout = SunburstLayout.Build(current, NodeStore.RootId);

        Assert.Empty(ScanDiff.PreviousSizes(current, null, layout, NodeStore.RootId));
        Assert.Empty(ScanDiff.PreviousSizes(null, current, layout, NodeStore.RootId));
        Assert.Empty(ScanDiff.PreviousSizes(current, current, null, NodeStore.RootId));
    }
}

public class SunburstTintTests
{
    [Fact]
    public void Non_Candidates_Are_Pushed_Down_To_Grey()
    {
        var grey = SunburstPalette.CleanupFill(0, isCandidate: false);
        Assert.InRange(Math.Abs(grey.R - grey.B), 0, 30);

        var hot = SunburstPalette.CleanupFill(100, isCandidate: true);
        Assert.True(hot.R > hot.G && hot.R > hot.B, "우선 정리는 붉게");

        var mild = SunburstPalette.CleanupFill(30, isCandidate: true);
        Assert.True(mild.G > mild.R, "참고 대상은 푸른 쪽");
    }

    [Fact]
    public void Growth_Is_Warm_And_Shrink_Is_Cool()
    {
        const long Gb = 1024L * 1024 * 1024;

        var grew = SunburstPalette.DeltaFill(+2 * Gb, 4 * Gb, known: true);
        var shrank = SunburstPalette.DeltaFill(-2 * Gb, 4 * Gb, known: true);
        var same = SunburstPalette.DeltaFill(0, 4 * Gb, known: true);
        var fresh = SunburstPalette.DeltaFill(Gb, 0, known: false);

        Assert.True(grew.R > grew.B, "늘어난 쪽은 따뜻하게");
        Assert.True(shrank.B > shrank.R, "줄어든 쪽은 차갑게");
        Assert.InRange(Math.Abs(same.R - same.B), 0, 30);
        Assert.True(fresh.B > fresh.G, "새로 생긴 것은 보라 계열");
    }

    /// <summary>작은 파일이 두 배가 됐다고 가장 붉게 칠하면 그림이 소음으로 가득 찬다.</summary>
    [Fact]
    public void Small_Items_Do_Not_Saturate()
    {
        var tiny = SunburstPalette.DeltaFill(1024 * 1024, 1024 * 1024, known: true);
        var huge = SunburstPalette.DeltaFill(80L * 1024 * 1024 * 1024, 80L * 1024 * 1024 * 1024, known: true);

        Assert.True(huge.G < tiny.G, "같은 배율이어도 절대량이 큰 쪽이 더 짙어야 한다");
    }
}

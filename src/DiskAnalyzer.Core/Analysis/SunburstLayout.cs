using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Analysis;

/// <summary>선버스트 레이아웃 설정.</summary>
public sealed class SunburstOptions
{
    /// <summary>현재 폴더의 자식부터 몇 겹을 그릴지. DaisyDisk 는 5겹이다.</summary>
    public int Rings { get; init; } = 5;

    /// <summary>이보다 좁은 조각은 그리지 않고 <see cref="SunburstKind.Smaller"/> 하나로 합친다(도).</summary>
    public double MinSweepDegrees { get; init; } = 0.9d;

    /// <summary>조각 수 상한. 렌더 비용을 고정하기 위한 안전장치다.</summary>
    public int MaxSegments { get; init; } = 4000;

    /// <summary>파일도 조각으로 그릴지. 끄면 폴더 구조만 보인다.</summary>
    public bool IncludeFiles { get; init; } = true;

    public static SunburstOptions Default { get; } = new();
}

/// <summary>선버스트 조각 하나. 각도는 12시 방향 0°, 시계 방향으로 잰 도(degree) 단위다.</summary>
public sealed class SunburstSegment
{
    /// <summary>0 = 중앙 구멍 바로 바깥 겹.</summary>
    public required int Ring { get; init; }

    public required double Start { get; init; }
    public required double Sweep { get; init; }

    public required SunburstKind Kind { get; init; }

    /// <summary>폴더면 dirId, 파일이면 file index. 가상 항목(<see cref="SunburstKind.Smaller"/> 등)은 -1.</summary>
    public required int Id { get; init; }

    public required long Size { get; init; }
    public required string Name { get; init; }

    /// <summary><see cref="SunburstLayout.Segments"/> 안의 부모 인덱스. 첫 겹은 -1.</summary>
    public required int ParentIndex { get; init; }

    /// <summary><see cref="SunburstKind.Smaller"/> 가 묶고 있는 항목 수.</summary>
    public int GroupCount { get; init; }

    public double Mid => Start + Sweep / 2d;
    public double End => Start + Sweep;

    /// <summary>들어갈 수 있는 조각인가. 묶음과 파일은 들어갈 데가 없다.</summary>
    public bool CanDrill => Kind == SunburstKind.Directory;

    /// <summary>삭제 대상이 될 수 있는 조각인가. 묶음은 펼쳐야 개별로 담을 수 있다.</summary>
    public bool CanCollect => Kind is SunburstKind.Directory or SunburstKind.File;
}

/// <summary>
/// <see cref="NodeStore"/> 한 폴더를 동심원 조각들로 펼친 결과. UI 타입을 전혀 참조하지 않는다.
///
/// <para><b>작은 항목을 버리지 않는다.</b> 기존 Treemap 은 면적이 1px 미만이면 <c>break</c> 로 조용히
/// 빠뜨려서 그림의 합이 100% 가 되지 않았다. 여기서는 남은 꼬리를 <see cref="SunburstKind.Smaller"/>
/// 조각 하나로 합쳐 각도를 끝까지 채운다 — 원 한 바퀴가 언제나 현재 폴더 전체다.</para>
///
/// <para><b>겹 단위로 만든다.</b> 폴더마다 자식을 따로 조회하면 폴더 하나당 전체 배열을 훑게 되므로,
/// 한 겹에 있는 폴더들의 자식을 <see cref="NodeStore.CollectChildren"/> 로 한 번에 모은다.</para>
/// </summary>
public sealed class SunburstLayout
{
    private static readonly SunburstSegment[] Empty = [];

    private SunburstLayout(IReadOnlyList<SunburstSegment> segments, int dirId, long total, string rootName)
    {
        Segments = segments;
        DirectoryId = dirId;
        TotalSize = total;
        RootName = rootName;
    }

    /// <summary>안쪽 겹부터 차례로. 같은 겹 안에서는 각도 오름차순이다.</summary>
    public IReadOnlyList<SunburstSegment> Segments { get; }

    /// <summary>이 레이아웃의 중심이 된 폴더.</summary>
    public int DirectoryId { get; }

    /// <summary>중앙에 표시할 현재 폴더 전체 크기.</summary>
    public long TotalSize { get; }

    public string RootName { get; }

    public bool IsEmpty => Segments.Count == 0;

    public static SunburstLayout CreateEmpty() => new(Empty, -1, 0, string.Empty);

    public static SunburstLayout Build(NodeStore? store, int dirId, SunburstOptions? options = null)
    {
        options ??= SunburstOptions.Default;
        if (store == null || dirId < 0) return CreateEmpty();

        long total = store.GetDirectorySize(dirId);
        string rootName = dirId == NodeStore.RootId ? store.RootPath : store.GetDirectoryName(dirId);
        if (total <= 0) return new SunburstLayout(Empty, dirId, 0, rootName);

        var segments = new List<SunburstSegment>(256);
        var scratch = new ChildScratch();

        // 이번 겹에서 펼칠 폴더들. (저장소 id, Segments 안의 인덱스, 각도 시작, 각도 폭)
        var level = new List<(int DirId, int SegIndex, double Start, double Sweep)> { (dirId, -1, 0d, 360d) };
        var next = new List<(int DirId, int SegIndex, double Start, double Sweep)>();
        var ids = new List<int>(64);

        for (int ring = 0; ring < options.Rings && level.Count > 0; ring++)
        {
            ids.Clear();
            foreach (var (id, _, _, _) in level) ids.Add(id);

            var children = store.CollectChildren(ids, options.IncludeFiles, scratch);
            next.Clear();

            foreach (var (parentId, parentSeg, start, sweep) in level)
            {
                if (!children.TryGetValue(parentId, out var kids) || kids.Count == 0) continue;

                long denom = 0;
                foreach (var k in kids) denom += k.Size;
                if (denom <= 0) continue;

                double angle = start;
                long tailSize = 0;
                int tailCount = 0;

                foreach (var k in kids)
                {
                    double w = sweep * ((double)k.Size / denom);

                    // 목록은 크기 내림차순이므로 한 번 기준 아래로 내려가면 뒤는 전부 아래다.
                    if (w < options.MinSweepDegrees || segments.Count >= options.MaxSegments)
                    {
                        tailSize += k.Size;
                        tailCount++;
                        continue;
                    }

                    int index = segments.Count;
                    bool isDir = k.Kind == RowKind.Directory;
                    segments.Add(new SunburstSegment
                    {
                        Ring = ring,
                        Start = angle,
                        Sweep = w,
                        Kind = isDir ? SunburstKind.Directory : SunburstKind.File,
                        Id = k.Id,
                        Size = k.Size,
                        Name = isDir ? store.GetDirectoryName(k.Id) : store.GetFileName(k.Id),
                        ParentIndex = parentSeg,
                    });

                    if (isDir && ring + 1 < options.Rings) next.Add((k.Id, index, angle, w));
                    angle += w;
                }

                if (tailCount > 0 && tailSize > 0)
                {
                    // 남은 각도를 그대로 준다. 부동소수 누적 오차까지 여기서 흡수되어
                    // 한 겹의 마지막 조각이 언제나 부모의 끝에서 정확히 끝난다.
                    double rest = start + sweep - angle;
                    if (rest > 0)
                    {
                        segments.Add(new SunburstSegment
                        {
                            Ring = ring,
                            Start = angle,
                            Sweep = rest,
                            Kind = SunburstKind.Smaller,
                            Id = -1,
                            Size = tailSize,
                            Name = SmallerName(tailCount),
                            ParentIndex = parentSeg,
                            GroupCount = tailCount,
                        });
                    }
                }
            }

            (level, next) = (next, level);
        }

        return new SunburstLayout(segments, dirId, total, rootName);
    }

    /// <summary>묶음 조각의 이름. 사이드바와 툴팁이 같은 문구를 쓴다.</summary>
    public static string SmallerName(int count) => $"작은 항목 {count:N0}개";

    /// <summary>각도(0~360, 12시 기준 시계 방향) 와 겹 번호로 조각을 찾는다. 없으면 null.</summary>
    public SunburstSegment? HitTest(double angleDegrees, int ring)
    {
        double a = ((angleDegrees % 360d) + 360d) % 360d;
        foreach (var s in Segments)
        {
            if (s.Ring != ring) continue;
            if (a >= s.Start && a < s.End) return s;
        }
        return null;
    }
}

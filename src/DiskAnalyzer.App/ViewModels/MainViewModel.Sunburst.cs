using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using DiskAnalyzer.Core.Analysis;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.QuickMove;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.ViewModels;

/// <summary>
/// 선버스트 옆 목록의 한 줄. 컬럼이 없다 — 색 점 · 이름 · 크기가 전부다.
/// 점 색이 링의 색과 같은 함수에서 나오므로 범례를 따로 맞출 일이 없다.
/// </summary>
public sealed class SunburstSidebarRow
{
    public required string Name { get; init; }
    public required string SizeText { get; init; }
    public required Brush Dot { get; init; }

    /// <summary>묶음 · 여유 공간처럼 "실체가 아닌" 줄. 흐리게 그린다.</summary>
    public bool IsDim { get; init; }

    public bool IsDirectory { get; init; }
    public int Id { get; init; } = -1;
    public string FullPath { get; init; } = string.Empty;
    public string Tooltip { get; init; } = string.Empty;

    /// <summary>눌러서 들어갈 수 있는 줄인가.</summary>
    public bool CanNavigate => IsDirectory && Id >= 0;
}

public sealed partial class MainViewModel
{
    /// <summary>
    /// 수집함. 탭·폴더와 무관하게 살아 있다 — 어디를 돌아다니며 담아도 목록과 합계가 남는다.
    /// </summary>
    public Collector Collector { get; } = new();

    public ObservableCollection<SunburstSidebarRow> SunburstSidebar { get; } = [];

    private string _sunburstTitle = string.Empty;
    public string SunburstTitle { get => _sunburstTitle; private set => Set(ref _sunburstTitle, value); }

    private string _sunburstTotal = string.Empty;
    public string SunburstTotal { get => _sunburstTotal; private set => Set(ref _sunburstTotal, value); }

    /// <summary>목록이 지금 <b>가리킨 폴더</b>를 보여 주는 중인가. 그렇다면 제목 옆에 표시한다.</summary>
    private bool _sunburstPeeking;
    public bool SunburstPeeking { get => _sunburstPeeking; private set => Set(ref _sunburstPeeking, value); }

    // ---------------------------------------------------------------- 숫자 정합

    /// <summary>
    /// 스캔 합계와 드라이브 사용량을 맞춰 본 결과. 파생 값이라 스캔이 바뀌면 저절로 따라온다.
    /// 드라이브 루트를 통째로 스캔했을 때만 의미가 있다.
    /// </summary>
    public SpaceLedger Ledger
    {
        get
        {
            var r = _current;
            if (r == null) return SpaceLedger.None;

            string root = Path.GetPathRoot(r.RootPath) ?? string.Empty;
            bool isVolumeRoot = root.Length > 0 && PathUtil.Equal(r.RootPath, root);

            long volumeTotal = 0;
            foreach (var d in Drives)
            {
                if (!PathUtil.Equal(d.RootPath, root)) continue;
                volumeTotal = d.TotalSize;
                break;
            }

            return SpaceLedger.Create(r.TotalSize, r.VolumeUsedBytes, volumeTotal, isVolumeRoot);
        }
    }

    // ---------------------------------------------------------------- 옆 목록

    /// <summary>
    /// 링 옆 목록을 <paramref name="dirId"/> 의 내용으로 채운다.
    ///
    /// <para>링과 <b>같은 규칙</b>으로 접는다 — 링에서 한 조각으로 합쳐진 것은 목록에서도 한 줄이다.
    /// 둘이 어긋나면 "그림에는 있는데 목록에 없다"가 되어 곧바로 신뢰를 잃는다.</para>
    ///
    /// <para><paramref name="peeking"/> 이면 가리키기만 한 상태다 — 현재 위치는 바뀌지 않았다.</para>
    /// </summary>
    public void ShowSunburstSidebar(int dirId, bool peeking = false)
    {
        SunburstSidebar.Clear();
        SunburstPeeking = peeking;

        var store = _current?.Store;
        if (store == null || dirId < 0 || store.IsDirectoryDeleted(dirId))
        {
            SunburstTitle = string.Empty;
            SunburstTotal = string.Empty;
            return;
        }

        SunburstTitle = dirId == NodeStore.RootId ? store.RootPath : store.GetDirectoryName(dirId);
        long total = store.GetDirectorySize(dirId);
        SunburstTotal = SizeFormatter.Format(total);

        var children = store.GetChildren(dirId, includeFiles: true);
        long denom = 0;
        foreach (var c in children) denom += c.Size;

        if (denom > 0)
        {
            double angle = 0;
            long tailSize = 0;
            int tailCount = 0;

            foreach (var c in children)
            {
                double sweep = 360d * ((double)c.Size / denom);
                if (sweep < SunburstOptions.Default.MinSweepDegrees)
                {
                    tailSize += c.Size;
                    tailCount++;
                    continue;
                }

                var kind = c.IsDirectory ? SunburstKind.Directory : SunburstKind.File;
                SunburstSidebar.Add(new SunburstSidebarRow
                {
                    Name = c.Name,
                    SizeText = c.SizeText,
                    Dot = DotFor(kind, angle + sweep / 2d),
                    IsDirectory = c.IsDirectory,
                    Id = c.Id,
                    FullPath = c.FullPath,
                    Tooltip = $"{c.FullPath}\n{c.SizeText} · {c.RatioText}",
                });
                angle += sweep;
            }

            if (tailCount > 0)
            {
                SunburstSidebar.Add(new SunburstSidebarRow
                {
                    Name = SunburstLayout.SmallerName(tailCount),
                    SizeText = SizeFormatter.Format(tailSize),
                    Dot = DotFor(SunburstKind.Smaller, angle + (360d - angle) / 2d),
                    IsDim = true,
                    Tooltip = "링에서 따로 보이기엔 너무 얇은 항목들입니다. 폴더 탭에서 개별로 볼 수 있습니다.",
                });
            }
        }

        AppendLedgerRows(dirId);
    }

    /// <summary>
    /// 스캔 중용 — 저장소를 타고 내려가지 않고 게시된 스냅샷 행만으로 목록을 만든다.
    /// 링(<c>SunburstLayout.BuildFlat</c>)과 같은 규칙으로 접어 둘이 어긋나지 않게 한다.
    /// </summary>
    public void ShowSunburstSidebarFromRows(IReadOnlyList<EntryRow> rows, string title)
    {
        SunburstSidebar.Clear();
        SunburstPeeking = false;
        SunburstTitle = title;

        long denom = 0;
        foreach (var r in rows) denom += r.Size;
        SunburstTotal = SizeFormatter.Format(denom);
        if (denom <= 0) return;

        double angle = 0;
        long tailSize = 0;
        int tailCount = 0;

        foreach (var r in rows)
        {
            double sweep = 360d * ((double)r.Size / denom);
            if (sweep < SunburstOptions.Default.MinSweepDegrees)
            {
                tailSize += r.Size;
                tailCount++;
                continue;
            }

            SunburstSidebar.Add(new SunburstSidebarRow
            {
                Name = r.Name,
                SizeText = r.SizeText,
                Dot = DotFor(r.IsDirectory ? SunburstKind.Directory : SunburstKind.File, angle + sweep / 2d),
                IsDirectory = r.IsDirectory,
                Id = r.Id,
                FullPath = r.FullPath,
                Tooltip = r.FullPath,
            });
            angle += sweep;
        }

        if (tailCount > 0)
        {
            SunburstSidebar.Add(new SunburstSidebarRow
            {
                Name = SunburstLayout.SmallerName(tailCount),
                SizeText = SizeFormatter.Format(tailSize),
                Dot = DotFor(SunburstKind.Smaller, angle + (360d - angle) / 2d),
                IsDim = true,
            });
        }
    }

    /// <summary>
    /// 스캔 합계로 설명되지 않는 용량을 목록 맨 아래에 올린다.
    /// 접근 거부 <b>건수</b>만 알리고 용량을 감추면 "합계가 안 맞는다"가 그대로 남는다.
    /// </summary>
    private void AppendLedgerRows(int dirId)
    {
        if (dirId != NodeStore.RootId) return;

        var ledger = Ledger;
        if (!ledger.HasVolumeInfo) return;

        if (ledger.HasHidden)
        {
            SunburstSidebar.Add(new SunburstSidebarRow
            {
                Name = "숨은 공간",
                SizeText = ledger.HiddenText,
                Dot = DotFor(SunburstKind.Hidden, 0d),
                Tooltip = "드라이브 사용량에서 스캔 합계를 뺀 값입니다.\n"
                          + ledger.ExplainHidden(
                              _current?.AccessDenied ?? 0,
                              _current?.SkippedFolders ?? 0,
                              _current?.ExcludedFolders ?? 0),
            });
        }

        SunburstSidebar.Add(new SunburstSidebarRow
        {
            Name = "여유 공간",
            SizeText = ledger.FreeText,
            Dot = DotFor(SunburstKind.Free, 0d),
            IsDim = true,
            Tooltip = "링에는 그리지 않습니다 — 원 한 바퀴는 언제나 '쓰고 있는 것'입니다.",
        });
    }

    private static readonly Dictionary<int, Brush> DotCache = [];

    /// <summary>점 브러시는 (종류 × 각도 3° 단위)로 캐시한다. 목록을 다시 그릴 때마다 할당하지 않는다.</summary>
    private static Brush DotFor(SunburstKind kind, double midAngle)
    {
        int key = ((int)kind << 9) | (((int)(midAngle / 3d) % 120 + 120) % 120);
        if (DotCache.TryGetValue(key, out var cached)) return cached;

        var c = SunburstPalette.Fill(kind, midAngle);
        var brush = new SolidColorBrush(Color.FromArgb(Math.Max(c.A, (byte)190), c.R, c.G, c.B));
        brush.Freeze();
        DotCache[key] = brush;
        return brush;
    }

    // ---------------------------------------------------------------- 수집함 표시

    public string CollectorText => Collector.IsEmpty
        ? "수집함이 비어 있습니다 — 링이나 목록에서 가운데 버튼 / 우클릭으로 담으세요"
        : $"{Collector.Count:N0}개 · {Collector.TotalSizeText} 담김";

    public bool HasCollected => !Collector.IsEmpty;
    public bool CollectorHasRisky => Collector.HasRisky;

    /// <summary><see cref="Collector.Changed"/> 를 받아 화면 문구를 다시 읽게 한다.</summary>
    public void RaiseCollectorText()
    {
        Raise(nameof(CollectorText));
        Raise(nameof(HasCollected));
        Raise(nameof(CollectorHasRisky));
    }
}

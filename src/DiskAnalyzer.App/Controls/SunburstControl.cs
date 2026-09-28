using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiskAnalyzer.App.Keymaps;
using DiskAnalyzer.Core.Analysis;
using DiskAnalyzer.Core.Keymaps;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.App.Controls;

/// <summary>
/// 선버스트(동심원) 디스크 맵.
///
/// <para>Treemap 과 목적이 다르다. Treemap 은 "가장 큰 사각형 하나"를 찾는 데 강하고,
/// 선버스트는 <b>"이 폴더가 무엇으로 이루어져 있는가"</b>를 한눈에 보여 준다 — 각도가 곧 비율이고,
/// 안쪽 겹이 바깥 겹을 품고 있어 계층이 그림 자체로 읽힌다.</para>
///
/// <para>규칙 세 가지만 있다. <b>가리키면 읽고, 누르면 들어가고, 가운데를 누르면 나온다.</b>
/// 조각에 이름을 쓰지 않는다 — 이름은 옆 목록이 담당하고, 그림은 비율만 말한다. 덕분에
/// 텍스트 렌더링이 0 이고(트리맵 비용의 대부분이 그것이다) 좁은 조각도 왜곡 없이 그려진다.</para>
///
/// <para>[성능] 레이아웃(각도)은 탐색할 때만 다시 계산한다. 크기가 바뀌면 반지름만 달라지므로
/// <see cref="Geometry"/> 만 다시 만든다. 만든 기하는 <c>Freeze</c> 해 두어 렌더 중 할당이 없다.</para>
/// </summary>
public sealed class SunburstControl : FrameworkElement, IShortcutTarget
{
    /// <summary>중앙 빈 원의 반지름 비율(바깥 반지름 대비). 원본 실측은 0.14 인데
    /// 두 줄짜리 총량 숫자가 들어가야 해서 조금 키웠다.</summary>
    private const double HoleRatio = 0.20d;

    /// <summary>
    /// 겹별 두께 배율. 안쪽 다섯 겹은 두께가 같고, 그 바깥은 <b>겹마다 더</b> 얇아진다.
    ///
    /// <para>처음에 바깥을 일괄 0.6배로 두었더니 6·7·8겹이 여전히 두꺼웠다 —
    /// 남은 반지름을 채우도록 한 칸 두께를 역산하기 때문에, 배율이 같으면 바깥 세 겹이
    /// 통째로 굵어진다. 원본 실측 비율(21 : 13 : 13 : 8 → 1 : 0.62 : 0.62 : 0.38)보다
    /// 조금 더 빠르게 줄여 바깥으로 갈수록 확실히 힘이 빠지게 했다.</para>
    /// </summary>
    private static readonly double[] RingScales = { 1d, 1d, 1d, 1d, 1d, 0.55d, 0.38d, 0.26d };

    /// <summary>표 바깥 겹(9겹 이상)의 두께 배율. 실제로는 여기까지 오는 트리가 드물다.</summary>
    private const double BeyondTableScale = 0.2d;

    private static double ScaleOf(int ring) => ring < RingScales.Length ? RingScales[ring] : BeyondTableScale;

    /// <summary>겹 사이 간격(px). 배경이 비치는 것이 아니라 경계가 보일 만큼만.</summary>
    private const double RingGap = 1.5d;

    private const double EdgePadding = 10d;

    /// <summary>조각 하나가 이보다 좁으면 테두리를 그리지 않는다 — 테두리가 조각을 덮어 버린다.</summary>
    private const double StrokeMinSweep = 1.6d;

    private SunburstLayout _layout = SunburstLayout.CreateEmpty();
    private NodeStore? _store;
    private Geometry[] _geometry = [];
    private Brush[] _fill = [];
    private Size _lastSize;
    private Point _center;
    private double _hole;
    private double _ringWidth;
    private int _hoverIndex = -1;
    private int _focusIndex = -1;          // 키보드가 가리키는 조각. 마우스 호버와 따로 둔다.
    private bool _hoverCenter;
    private Typeface? _typeface;

    private SunburstTint _tint = SunburstTint.Size;
    private IReadOnlyDictionary<long, int>? _scores;
    private IReadOnlyDictionary<long, long>? _previous;

    /// <summary>가리킨 조각이 바뀌었다. null 이면 조각 밖(가운데 또는 바깥)이다.</summary>
    public event EventHandler<SunburstSegment?>? HoverChanged;

    /// <summary>조각을 눌렀다.</summary>
    public event EventHandler<SunburstSegment>? ItemSelected;

    /// <summary>폴더 조각을 눌러 들어간다.</summary>
    public event EventHandler<SunburstSegment>? ItemActivated;

    /// <summary>가운데 원을 눌렀다 — 상위 폴더로.</summary>
    public event EventHandler? GoUpRequested;

    /// <summary>가운데 버튼 클릭 / 우클릭 메뉴로 수집함에 담기를 요청했다.</summary>
    public event EventHandler<SunburstSegment>? CollectRequested;

    /// <summary>
    /// 조각을 끌기 시작했다 — DaisyDisk 의 "꽃잎을 뜯어 수집함에 떨어뜨리기".
    /// 실제 <c>DragDrop.DoDragDrop</c> 는 창이 한다(컨트롤은 데이터 형식을 모른다).
    /// </summary>
    public event EventHandler<SunburstSegment>? DragStartRequested;

    public SunburstControl()
    {
        ClipToBounds = true;
        Focusable = true;

        // OnRender 의 DrawText 는 창에 건 TextOptions 를 물려받지 않는다. 어두운 바탕에 밝은 글자를
        // ClearType 으로 그리면 획 양쪽에 주황 / 청록 가장자리가 떠서 글자가 물든 것처럼 보인다.
        // 회색조 안티에일리어싱은 색을 만들지 않는다.
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
    }

    // ---------------------------------------------------------------- 색

    public static readonly DependencyProperty BackdropBrushProperty = DependencyProperty.Register(
        nameof(BackdropBrush), typeof(Brush), typeof(SunburstControl),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(SunburstControl),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MutedBrushProperty = DependencyProperty.Register(
        nameof(MutedBrush), typeof(Brush), typeof(SunburstControl),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionBrushProperty = DependencyProperty.Register(
        nameof(SelectionBrush), typeof(Brush), typeof(SunburstControl),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CollectedBrushProperty = DependencyProperty.Register(
        nameof(CollectedBrush), typeof(Brush), typeof(SunburstControl),
        new FrameworkPropertyMetadata(Brushes.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>조각 사이 이음선 색. 창 배경과 같게 두면 조각이 떨어져 보인다.</summary>
    public Brush BackdropBrush { get => (Brush)GetValue(BackdropBrushProperty); set => SetValue(BackdropBrushProperty, value); }

    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public Brush MutedBrush { get => (Brush)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }
    public Brush SelectionBrush { get => (Brush)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }

    /// <summary>수집함에 담긴 조각의 테두리 색.</summary>
    public Brush CollectedBrush { get => (Brush)GetValue(CollectedBrushProperty); set => SetValue(CollectedBrushProperty, value); }

    // ---------------------------------------------------------------- 원본

    public SunburstLayout Layout => _layout;

    /// <summary>가운데에 쓸 이름. 현재 폴더 이름이다.</summary>
    public string CenterName { get; private set; } = string.Empty;

    /// <summary>스캔이 끝난 뒤: 저장소를 타고 내려가며 여러 겹을 만든다.</summary>
    public void SetSource(NodeStore? store, int dirId, SunburstOptions? options = null)
    {
        var previous = _layout;

        _store = store;
        _layout = SunburstLayout.Build(store, dirId, options);
        CenterName = store == null || dirId < 0
            ? string.Empty
            : (dirId == NodeStore.RootId ? store.RootPath : store.GetDirectoryName(dirId));

        _selected.Clear();
        _hoverIndex = -1;
        _focusIndex = -1;
        _lastSize = default;

        // 조각 번호가 통째로 밀린다. 밝기도 함께 접지 않으면 엉뚱한 조각이 밝은 채로 남는다.
        _glowIndex = -1;
        Motion.Settle(this, GlowProperty, 0d);

        _fillFading = null;

        Rebuild();
        BeginZoom(previous);
        // 줌(크기)과 각도 보간을 같이 건다 — 새로 들어온 조각은 제 자리에서 폭 0 으로 열려
        // "링이 자라난다"는 느낌이 스캔 중뿐 아니라 폴더를 드나들 때도 보인다.
        BeginMorph(previous);
    }

    /// <summary>
    /// 스캔 중: <see cref="NodeStore"/> 는 Aggregator 스레드가 쓰고 있으므로 직접 타고 내려가면 안 된다.
    /// 게시된 현재 폴더 스냅샷만으로 한 겹을 그린다 — 링이 자라는 것이 그대로 보인다.
    /// </summary>
    public void SetFlatSource(IReadOnlyList<EntryRow> rows, string name)
    {
        var previous = _layout;

        _store = null;
        _layout = SunburstLayout.BuildFlat(rows, name);
        CenterName = name;
        _selected.Clear();
        _hoverIndex = -1;
        _focusIndex = -1;
        _lastSize = default;
        _fillFading = null;
        _glowIndex = -1;
        Motion.Settle(this, GlowProperty, 0d);

        Rebuild();
        BeginMorph(previous);
    }

    public void Clear()
    {
        _store = null;
        _layout = SunburstLayout.CreateEmpty();
        CenterName = string.Empty;
        _geometry = [];
        _fill = [];
        _selected.Clear();
        _hoverIndex = -1;
        _focusIndex = -1;
        StopMotion();
        InvalidateVisual();
    }

    /// <summary>조각의 전체 경로. 필요할 때만 만든다(조각마다 들고 있으면 수천 개의 문자열이 된다).</summary>
    public string PathOf(SunburstSegment segment)
    {
        if (_store == null || segment.Id < 0) return string.Empty;
        return segment.Kind == SunburstKind.Directory
            ? _store.GetDirectoryPath(segment.Id)
            : _store.GetFilePath(segment.Id);
    }

    /// <summary>
    /// 링의 색 기준을 바꾼다. 레이아웃(각도)은 그대로 두고 채움색만 다시 만든다 —
    /// 같은 그림을 다른 질문으로 읽는 것이지, 다른 그림을 그리는 것이 아니다.
    /// </summary>
    /// <param name="scores">조각 키 → 정리 추천 점수. 후보가 아닌 조각은 들어 있지 않다.</param>
    /// <param name="previous">조각 키 → 이전 스캔에서의 크기. 그때 없던 조각은 들어 있지 않다.</param>
    public void SetTint(SunburstTint tint,
                        IReadOnlyDictionary<long, int>? scores = null,
                        IReadOnlyDictionary<long, long>? previous = null)
    {
        var wasFilledWith = _fill;

        _tint = tint;
        _scores = scores;
        _previous = previous;
        RebuildBrushes();
        BeginTintFade(wasFilledWith);
        InvalidateVisual();
    }

    public SunburstTint Tint => _tint;

    // ---------------------------------------------------------------- 강조

    private readonly HashSet<long> _selected = [];
    private readonly HashSet<long> _collected = [];

    private static long KeyOf(SunburstKind kind, int id)
        => ((kind == SunburstKind.Directory ? 1L : 0L) << 32) | (uint)id;

    /// <summary>목록에서 고른 항목을 링에서 테두리로 강조한다. 레이아웃은 다시 계산하지 않는다.</summary>
    public void SetSelection(IEnumerable<(bool IsDirectory, int Id)> items)
    {
        _selected.Clear();
        foreach (var (isDir, id) in items)
            _selected.Add(KeyOf(isDir ? SunburstKind.Directory : SunburstKind.File, id));
        InvalidateVisual();
    }

    /// <summary>수집함에 담긴 항목을 링에 표시한다.</summary>
    public void SetCollected(IEnumerable<(bool IsDirectory, int Id)> items)
    {
        _collected.Clear();
        foreach (var (isDir, id) in items)
        {
            if (id < 0) continue;
            _collected.Add(KeyOf(isDir ? SunburstKind.Directory : SunburstKind.File, id));
        }
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- 기하

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Rebuild();
    }

    private void Rebuild()
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (size.Width < 16 || size.Height < 16) return;
        if (size == _lastSize && _geometry.Length == _layout.Segments.Count) return;
        _lastSize = size;

        _center = new Point(size.Width / 2d, size.Height / 2d);
        double outer = Math.Min(size.Width, size.Height) / 2d - EdgePadding;
        if (outer <= 8)
        {
            _geometry = [];
            _fill = [];
            InvalidateVisual();
            return;
        }

        _hole = outer * HoleRatio;

        int rings = 1;
        foreach (var s in _layout.Segments) rings = Math.Max(rings, s.Ring + 1);

        // 두께는 "나온 겹 수" 가 아니라 "그릴 수 있는 최대 겹 수" 로 나눈다.
        // 나온 만큼으로 채우면 얕은 폴더에서 겹이 뚱뚱해지고, 폴더를 옮길 때마다 두께가 바뀐다.
        // 고정해 두면 대신 바깥 테두리가 들쭉날쭉해지는데, 그게 원본의 모습이고
        // "이 방향은 깊고 저 방향은 얕다" 를 그림이 그대로 말해 준다.
        int slots = Math.Max(rings, _layout.MaxRings);

        double units = 0;
        for (int k = 0; k < slots; k++) units += ScaleOf(k);
        _ringWidth = (outer - _hole) / units;
        _ringCount = rings;

        _ringStart = new double[slots + 1];
        _ringStart[0] = _hole;
        for (int k = 0; k < slots; k++) _ringStart[k + 1] = _ringStart[k] + ScaleOf(k) * _ringWidth;

        int n = _layout.Segments.Count;
        _geometry = new Geometry[n];

        for (int i = 0; i < n; i++)
        {
            var s = _layout.Segments[i];
            double r0 = RadiusAt(s.Ring);
            double r1 = RadiusAt(s.Ring + 1) - RingGap;
            _geometry[i] = BuildSegment(r0, Math.Max(r0 + 0.5d, r1), s.Start, s.Sweep);
        }

        RebuildBrushes();
        InvalidateVisual();
    }

    /// <summary>채움색만 다시 만든다. 기하는 건드리지 않는다(색 기준을 바꿀 때 쓴다).</summary>
    private void RebuildBrushes()
    {
        int n = _layout.Segments.Count;
        _fill = new Brush[n];

        for (int i = 0; i < n; i++)
        {
            var c = FillFor(_layout.Segments[i]);
            var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
            brush.Freeze();
            _fill[i] = brush;
        }
    }

    private SunburstColor FillFor(SunburstSegment s)
    {
        // 묶음과 가상 항목은 어떤 기준에서도 실체가 없으므로 기본 색을 유지한다.
        if (_tint == SunburstTint.Size || s.Id < 0) return SunburstPalette.Fill(s.Kind, s.Mid);

        long key = SunburstLayout.KeyOf(s.Kind, s.Id);

        if (_tint == SunburstTint.Cleanup)
        {
            bool candidate = _scores != null && _scores.TryGetValue(key, out int score);
            return SunburstPalette.CleanupFill(candidate ? _scores![key] : 0, candidate);
        }

        bool known = _previous != null && _previous.TryGetValue(key, out long before);
        long prev = known ? _previous![key] : 0;
        return SunburstPalette.DeltaFill(s.Size - prev, prev, known);
    }

    private int _ringCount = 1;
    private double[] _ringStart = [];

    /// <summary><paramref name="ring"/> 번째 겹이 시작하는 반지름. Rebuild 에서 쌓아 둔 값을 읽는다.</summary>
    private double RadiusAt(int ring)
        => ring < _ringStart.Length ? _ringStart[ring] : (_ringStart.Length > 0 ? _ringStart[^1] : _hole);

    /// <summary>12시 방향 0°, 시계 방향. 화면 좌표는 y 가 아래로 자라므로 cos/sin 을 그대로 쓰면 된다.</summary>
    private Point Polar(double radius, double degrees)
    {
        double t = (degrees - 90d) * Math.PI / 180d;
        return new Point(_center.X + radius * Math.Cos(t), _center.Y + radius * Math.Sin(t));
    }

    private Geometry BuildSegment(double rIn, double rOut, double start, double sweep)
    {
        // 자식이 하나뿐인 폴더는 한 바퀴를 통째로 차지한다. 360° 호는 시작점과 끝점이 같아
        // 아무것도 그려지지 않으므로 도넛으로 만든다.
        if (sweep >= 359.9d)
        {
            var donut = new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(_center, rOut, rOut),
                new EllipseGeometry(_center, rIn, rIn));
            donut.Freeze();
            return donut;
        }

        double end = start + sweep;
        bool large = sweep > 180d;

        var geo = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = geo.Open())
        {
            // isStroked 를 켜 둬야 한다. 끄면 채우기는 되지만 어떤 Pen 도 이 도형에 그려지지 않아
            // 선택 · 수집함 표시 · 키보드 초점 테두리가 전부 조용히 사라진다.
            ctx.BeginFigure(Polar(rIn, start), isFilled: true, isClosed: true);
            ctx.LineTo(Polar(rOut, start), isStroked: true, isSmoothJoin: false);
            ctx.ArcTo(Polar(rOut, end), new Size(rOut, rOut), 0d, large,
                      SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
            ctx.LineTo(Polar(rIn, end), isStroked: true, isSmoothJoin: false);
            ctx.ArcTo(Polar(rIn, start), new Size(rIn, rIn), 0d, large,
                      SweepDirection.Counterclockwise, isStroked: true, isSmoothJoin: false);
        }
        geo.Freeze();
        return geo;
    }

    // ---------------------------------------------------------------- 50. 움직임

    /// <summary>
    /// 폴더를 드나드는 줌. 0 = 출발(들어갔으면 작게, 나왔으면 크게), 1 = 제자리.
    /// 그림 전체를 한 번 감싸는 변환이라 조각 수와 무관하게 비용이 일정하다.
    /// </summary>
    private static readonly DependencyProperty TransitionProperty = DependencyProperty.Register(
        nameof(Transition), typeof(double), typeof(SunburstControl),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>가리킨 조각이 밝아진 정도. 0 = 원래 색, 1 = 강조색.</summary>
    private static readonly DependencyProperty GlowProperty = DependencyProperty.Register(
        nameof(Glow), typeof(double), typeof(SunburstControl),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>새 색이 옛 색 위로 떠오른 정도. 1 이면 옛 색은 이미 지워졌다.</summary>
    private static readonly DependencyProperty TintBlendProperty = DependencyProperty.Register(
        nameof(TintBlend), typeof(double), typeof(SunburstControl),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>스캔 중, 각도가 옛 자리에서 새 자리로 옮겨 간 정도.</summary>
    private static readonly DependencyProperty MorphProperty = DependencyProperty.Register(
        nameof(Morph), typeof(double), typeof(SunburstControl),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    private double Transition => (double)GetValue(TransitionProperty);
    private double Glow => (double)GetValue(GlowProperty);
    private double TintBlend => (double)GetValue(TintBlendProperty);
    private double Morph => (double)GetValue(MorphProperty);

    /// <summary>
    /// 각도 보간은 프레임마다 조각 기하를 다시 만든다. 조각이 아주 많으면(수천 개) 그 비용이
    /// 프레임 하나를 무겁게 만들므로 그 이상은 조용히 끈다 — Zoom 과 같은 한계를 쓴다.
    ///
    /// <para>예전에는 조각이 많을수록(<see cref="MorphDurationFor"/>) 길이를 줄여 프레임 비용을 아꼈는데,
    /// 그러면 최상위 뎁스처럼 조각이 많은 자리에서 펼쳐지는 게 아니라 툭 튀어 보여 어지럽다는 말이 나왔다.
    /// 느려지는 것은 괜찮으니 항상 같은 길이로 부드럽게 펼치고, 정말 무거워지는 지점(수천 개)에서만 끈다.</para>
    /// </summary>
    private const int MorphSegmentLimit = ZoomSegmentLimit;

    /// <summary>조각 수와 무관하게 항상 같은 길이로 펼친다 — 짧아질수록 "촤라락"이 아니라 "툭" 나타난 것처럼 보인다.</summary>
    private static Duration MorphDurationFor(int segmentCount) => Motion.Normal;

    /// <summary>줌은 조각 수와 무관하지만, 조각이 수천 개면 한 프레임이 이미 무겁다.</summary>
    private const int ZoomSegmentLimit = 3000;

    private double _zoomFrom = 1d;
    private Brush[]? _fillFading;
    private (double Start, double Sweep)[]? _morphFrom;
    private Geometry[]? _morphGeometry;

    /// <summary>
    /// 지금 밝아져 있는 조각. <see cref="_hoverIndex"/> 와 따로 두는 이유는 <b>꺼지는 동안</b> 때문이다 —
    /// 마우스가 링을 벗어나면 가리킨 조각은 즉시 없어지지만, 밝기는 아직 돌아오는 중이라
    /// 그동안 누구를 칠할지 알아야 한다. 이것이 없으면 들어올 때만 부드럽고 나갈 때는 툭 꺼진다.
    /// </summary>
    private int _glowIndex = -1;

    /// <summary>
    /// 걸려 있던 연출을 전부 걷고 제자리로 돌린다.
    /// 설정에서 애니메이션을 끄는 순간 반쯤 줌인된 채로 멈춰 있으면 안 된다.
    /// </summary>
    public void StopMotion()
    {
        Motion.Settle(this, TransitionProperty, 1d);
        Motion.Settle(this, GlowProperty, 0d);
        _glowIndex = -1;
        Motion.Settle(this, TintBlendProperty, 1d);
        Motion.Settle(this, MorphProperty, 1d);

        _fillFading = null;
        _morphFrom = null;
        _morphGeometry = null;
    }

    /// <summary>
    /// 폴더를 드나들 때의 줌. <b>어느 쪽으로 움직였는지는 두 레이아웃이 말해 준다</b> —
    /// 부르는 쪽이 "들어간다 / 나온다"를 따로 알려 주지 않아도 된다.
    /// </summary>
    private void BeginZoom(SunburstLayout previous)
    {
        if (!Motion.Enabled || _layout.Segments.Count > ZoomSegmentLimit)
        {
            Motion.Settle(this, TransitionProperty, 1d);
            return;
        }

        // 처음에는 0.72 / 1.34 배에서 출발했는데, 폴더를 몇 번만 타고 내려가도 화면이 계속
        // 크게 출렁여 멀미가 났다. 방향만 읽히면 되는 연출이라 폭을 10% 안쪽으로 줄였다.
        _zoomFrom = Relation(previous, _layout) switch
        {
            // 들어갔다: 안쪽에서 조금 자라 나온다.
            -1 => 0.93d,
            // 나왔다: 한 발 물러난다.
            1 => 1.08d,
            // 이어지지 않는 화면(새 스캔 · 경로 직접 입력): 방향을 지어내지 않고 살짝 떠오르기만 한다.
            _ => 0.98d,
        };

        Motion.From(this, TransitionProperty, 0d, 1d, Motion.Glide, Motion.Land);
    }

    /// <summary>-1 = 안으로 들어갔다, 1 = 밖으로 나왔다, 0 = 이어지지 않는 화면.</summary>
    private static int Relation(SunburstLayout from, SunburstLayout to)
    {
        if (from.IsEmpty || to.IsEmpty || from.DirectoryId == to.DirectoryId) return 0;
        if (HasDirectory(from, to.DirectoryId)) return -1;
        if (HasDirectory(to, from.DirectoryId)) return 1;
        return 0;
    }

    private static bool HasDirectory(SunburstLayout layout, int directoryId)
    {
        if (directoryId < 0) return false;
        foreach (var s in layout.Segments)
            if (s.Kind == SunburstKind.Directory && s.Id == directoryId) return true;
        return false;
    }

    /// <summary>
    /// 스캔 중에는 150ms 마다 한 겹이 통째로 다시 배분된다. 그대로 그리면 조각들이 매번 툭 하고
    /// 자리를 바꾼다 — 각도를 옛 자리에서 새 자리로 흘려 보내면 <b>링이 자라는 것</b>으로 보인다.
    /// 처음 나타난 조각은 제 자리에서 폭 0 으로 열린다.
    /// </summary>
    private void BeginMorph(SunburstLayout previous)
    {
        int n = _layout.Segments.Count;

        if (!Motion.Enabled || n == 0 || n > MorphSegmentLimit || previous.IsEmpty)
        {
            _morphFrom = null;
            _morphGeometry = null;
            Motion.Settle(this, MorphProperty, 1d);
            return;
        }

        var before = new Dictionary<long, (double Start, double Sweep)>(previous.Segments.Count);
        foreach (var s in previous.Segments) before[MorphKey(s)] = (s.Start, s.Sweep);

        _morphFrom = new (double Start, double Sweep)[n];
        for (int i = 0; i < n; i++)
        {
            var s = _layout.Segments[i];
            _morphFrom[i] = before.TryGetValue(MorphKey(s), out var was) ? was : (s.Start, 0d);
        }

        // 스냅샷 간격(150ms)보다 조금 길다. 다음 스냅샷이 올 때까지 멈추지 않아 링이 끊기지 않고 자란다.
        // 다만 조각이 많은 자리(최상위 뎁스)는 그만큼 프레임 비용도 커지므로, 조각 수에 맞춰 길이를 줄인다.
        Motion.From(this, MorphProperty, 0d, 1d, MorphDurationFor(n), Motion.Smooth);
    }

    /// <summary>묶음 조각("작은 항목 N개")은 실체가 없어 Id 가 없다. 겹 번호로 이어 준다.</summary>
    private static long MorphKey(SunburstSegment s)
        => s.Id >= 0 ? KeyOf(s.Kind, s.Id) : -1L - s.Ring;

    /// <summary>
    /// 색 기준을 바꿀 때의 교차 페이드. 조각마다 색을 섞으면 프레임마다 브러시를 수천 개
    /// 새로 만들어야 한다 — 옛 색을 한 번 깔고 그 위에 새 색을 통째로 띄우면 비용이 두 배로 끝난다.
    /// </summary>
    private void BeginTintFade(Brush[] previousFill)
    {
        // 색이 하나도 안 바뀌었으면 섞을 것이 없다.
        //
        // [왜 이 검사가 필요한가] 폴더를 옮길 때마다 창이 SetSource 바로 뒤에 SetTint 를 한 번 더 부른다
        // (색 기준을 화면에 다시 적용하는 경로다). 기준이 "크기" 그대로면 옛 색과 새 색이 완전히 같은데도
        // 교차 페이드가 260ms 동안 돌았다 — 그동안 반투명 묶음 조각이 진해져 <b>이동이 끝날 때
        // 조각 몇 개가 반짝 빛났다</b>. 섞을 것이 없으면 아무것도 하지 않는 것이 맞다.
        if (!Motion.Enabled || _fill.Length == 0 || previousFill.Length != _fill.Length ||
            SameFill(previousFill, _fill))
        {
            _fillFading = null;
            Motion.Settle(this, TintBlendProperty, 1d);
            return;
        }

        _fillFading = previousFill;
        Motion.From(this, TintBlendProperty, 0d, 1d, Motion.Slow, Motion.Smooth, () =>
        {
            _fillFading = null;
            InvalidateVisual();
        });
    }

    /// <summary>두 칠이 화면에 똑같이 나오는가. 하나라도 다르면 바로 거짓이다.</summary>
    private static bool SameFill(Brush[] a, Brush[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] is not SolidColorBrush x || b[i] is not SolidColorBrush y || x.Color != y.Color)
                return false;
        }
        return true;
    }

    /// <summary>
    /// 가리킨 조각을 부드럽게 밝힌다.
    ///
    /// <para>처음에는 조각을 바깥으로 밀어냈는데(4px), 이 링은 조각이 수백 개라 밀려난 하나가
    /// 이웃을 덮고 겹 사이 이음선이 벌어져 <b>링이 깨져 보였다</b>. 테두리까지 함께 밀어도
    /// "조각 하나가 떠서 따로 논다"는 인상은 남는다 — 기하를 건드리지 않고 색만 옮긴다.</para>
    ///
    /// <para>조각과 조각 사이를 옮길 때는 0 으로 돌아갔다 오지 않는다. 강조가 커서를 즉시 따라가야지,
    /// 매번 다시 밝아지면 손보다 화면이 느린 것처럼 느껴진다.</para>
    /// </summary>
    private void BeginGlow(int index)
    {
        if (index >= 0)
        {
            _glowIndex = index;
            Motion.To(this, GlowProperty, 1d, Motion.Quick, Motion.Ease);
            return;
        }

        if (_glowIndex < 0) return;

        Motion.To(this, GlowProperty, 0d, Motion.Quick, Motion.Ease, () =>
        {
            // 꺼지는 사이에 다시 들어왔으면 지금 끝난 것은 지난 애니메이션이다. 값으로 판단한다.
            if (Glow > 0.002d) return;
            _glowIndex = -1;
            InvalidateVisual();
        });
    }

    /// <summary>밝아져 있는 조각의 지금 색. 원래 색과 강조색 사이를 <see cref="Glow"/> 만큼 지난 자리다.</summary>
    private Brush HoverFill(int index)
    {
        double g = Math.Clamp(Glow, 0d, 1d);
        if (g <= 0.002d) return _fill[index];

        var lit = Lighten(_fill[index]);
        if (g >= 0.998d || _fill[index] is not SolidColorBrush from || lit is not SolidColorBrush to)
            return lit;

        // 프레임마다 브러시 하나. 조각 전체를 섞는 것과 달리 이건 언제나 가리킨 하나뿐이다.
        var mixed = new SolidColorBrush(Color.FromArgb(
            Mix(from.Color.A, to.Color.A, g), Mix(from.Color.R, to.Color.R, g),
            Mix(from.Color.G, to.Color.G, g), Mix(from.Color.B, to.Color.B, g)));
        mixed.Freeze();
        return mixed;
    }

    private static byte Mix(byte a, byte b, double t) => (byte)Math.Clamp(a + (b - a) * t, 0d, 255d);

    /// <summary>이 프레임에서 쓸 기하와 밀림 벡터를 미리 정한다. 그리는 쪽은 계산하지 않는다.</summary>
    private void PrepareFrame()
    {
        double morph = Morph;

        if (_morphFrom != null && _morphFrom.Length == _geometry.Length && morph < 1d)
        {
            if (_morphGeometry == null || _morphGeometry.Length != _geometry.Length)
                _morphGeometry = new Geometry[_geometry.Length];

            for (int i = 0; i < _geometry.Length; i++)
            {
                var s = _layout.Segments[i];
                var was = _morphFrom[i];
                double start = was.Start + (s.Start - was.Start) * morph;
                double sweep = was.Sweep + (s.Sweep - was.Sweep) * morph;

                double r0 = RadiusAt(s.Ring);
                double r1 = RadiusAt(s.Ring + 1) - RingGap;
                _morphGeometry[i] = BuildSegment(r0, Math.Max(r0 + 0.5d, r1), start, Math.Max(0.02d, sweep));
            }
        }
        else
        {
            _morphGeometry = null;
            if (morph >= 1d) _morphFrom = null;
        }

    }

    /// <summary>이 프레임에서 조각 <paramref name="index"/> 를 그릴 기하.</summary>
    private Geometry GeometryAt(int index) => _morphGeometry?[index] ?? _geometry[index];

    /// <summary>줌을 건다. 되돌릴 Pop 횟수를 준다.</summary>
    private int PushTransition(DrawingContext dc)
    {
        double t = Transition;
        if (t >= 1d) return 0;

        // 크기만 바꾼다. 투명도는 걸지 않는다 —
        // PushOpacity 는 내용을 중간 버퍼에 한 번 그린 뒤 합성하는데, 그 층이 <b>사라지는 마지막 프레임</b>에
        // 모든 경계가 실제 배경 위로 다시 안티에일리어싱된다. 조각 수백 개의 링에서는 그 한 프레임이
        // 조각 몇 개가 반짝 빛나는 것으로 보였다. 0.93 → 1 배면 크기만으로도 방향은 충분히 읽힌다.
        double scale = _zoomFrom + (1d - _zoomFrom) * t;
        dc.PushTransform(new ScaleTransform(scale, scale, _center.X, _center.Y));
        return 1;
    }

    // ---------------------------------------------------------------- 렌더

    protected override void OnRender(DrawingContext dc)
    {
        // 히트 테스트를 받으려면 배경이 칠해져 있어야 한다.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        _typeface ??= new Typeface(new FontFamily("Segoe UI, Malgun Gothic"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        if (_geometry.Length == 0)
        {
            DrawCentered(dc, "스캔 결과가 없습니다.", 13d, MutedBrush, 0d);
            return;
        }

        // 50. 이 프레임에 쓸 기하를 먼저 정한다. 아래 세 번의 순회는 모두 같은 자리를 본다.
        PrepareFrame();

        // 폴더를 드나드는 줌. 그림 전체를 한 번 감싸므로 조각마다 드는 비용이 없다.
        int pushed = PushTransition(dc);

        var seam = new Pen(BackdropBrush, 0.8d);
        seam.Freeze();

        // 색 기준을 바꾸는 중이면 옛 칠과 새 칠을 <b>각각 제 투명도로 감싸</b> 겹친다.
        //
        // [왜 그냥 위에 덧칠하면 안 되는가] 묶음 조각("작은 항목 N개")은 <b>알파 102 의 반투명</b>이다
        // (SunburstPalette — "실체가 아니라 묶음"이라는 표시). 반투명한 것을 같은 자리에 두 번 칠하면
        // 두 번째가 첫 번째 위에 쌓여 훨씬 진해진다. 그래서 색이 하나도 안 바뀌는 경우에도 페이드가 도는
        // 동안 <b>반투명 조각만 골라 반짝 빛났다</b>. 층으로 나누면 (1-t)·옛 + t·새 가 되어
        // 알파가 보존된다 — t 가 얼마든 묶음 조각은 늘 알파 102 그대로다.
        double blend = Math.Clamp(TintBlend, 0d, 1d);
        bool crossFade = _fillFading != null && _fillFading.Length == _geometry.Length && blend < 0.999d;

        if (crossFade)
        {
            dc.PushOpacity(1d - blend);
            DrawFills(dc, _fillFading!, seam, withGlow: false);
            dc.Pop();

            dc.PushOpacity(blend);
            DrawFills(dc, _fill, seam, withGlow: false);
            dc.Pop();
        }
        else
        {
            DrawFills(dc, _fill, seam, withGlow: true);
        }

        DrawOutlines(dc, _collected, CollectedBrush, 2.2d);
        DrawOutlines(dc, _selected, SelectionBrush, 1.8d);

        // 키보드 초점은 선택과 따로 그린다 - 마우스 없이 어디에 있는지 보여야 한다.
        if (_focusIndex >= 0 && _focusIndex < _geometry.Length)
        {
            DrawOutline(dc, LabelBrush, _focusIndex, 2.4d);
        }

        DrawCenter(dc);

        for (int i = 0; i < pushed; i++) dc.Pop();
    }

    /// <summary>조각 채우기 한 벌.</summary>
    private void DrawFills(DrawingContext dc, Brush[] fill, Pen seam, bool withGlow)
    {
        for (int i = 0; i < _geometry.Length; i++)
        {
            var s = _layout.Segments[i];
            var brush = withGlow && i == _glowIndex ? HoverFill(i) : fill[i];
            dc.DrawGeometry(brush, s.Sweep >= StrokeMinSweep ? seam : null, GeometryAt(i));
        }
    }

    private void DrawOutlines(DrawingContext dc, HashSet<long> keys, Brush brush, double thickness)
    {
        if (keys.Count == 0) return;

        for (int i = 0; i < _geometry.Length; i++)
        {
            var s = _layout.Segments[i];
            if (s.Id < 0 || !keys.Contains(KeyOf(s.Kind, s.Id))) continue;
            DrawOutline(dc, brush, i, thickness);
        }
    }

    /// <summary>테두리 하나. 겹이 얇으면 선도 함께 얇아져 조각을 덮지 않는다.</summary>
    private void DrawOutline(DrawingContext dc, Brush brush, int index, double thickness)
    {
        double width = OutlineWidth(index, thickness);
        var pen = new Pen(brush, width);
        pen.Freeze();
        dc.DrawGeometry(null, pen, OutlineAt(index, width));
    }

    /// <summary>
    /// 이 조각에 쓸 선 두께. 바깥 겹은 두께가 8px 아래로 내려가는데(<see cref="RingScales"/>),
    /// 거기에 2.2px 선을 그대로 그리면 <b>선이 조각을 덮는다</b>.
    ///
    /// <para>처음엔 겹 두께의 3분의 1(최소 0.9px)로 잡았는데 너무 얌전했다 — 바깥 겹에 담으면
    /// 표시가 났는지 안 났는지 알 수 없을 만큼 흐렸다. <b>담았다는 표시는 보여야 쓸모가 있다.</b>
    /// 절반 가까이 쓰되 최소 1.4px 은 지킨다.</para>
    /// </summary>
    private double OutlineWidth(int index, double thickness)
    {
        var s = _layout.Segments[index];
        double band = (RadiusAt(s.Ring + 1) - RingGap) - RadiusAt(s.Ring);
        return Math.Clamp(band / 2.2d, Math.Min(1.4d, thickness), thickness);
    }

    /// <summary>
    /// 테두리를 그릴 기하. 채움과 <b>같은 기하를 쓰면 안 된다</b> — WPF 의 Pen 은 선을 경로 위에
    /// 가운데 맞춤으로 그려서, 두께의 절반이 조각 <b>바깥</b>으로 삐져나간다. 그 절반이 이웃 조각과
    /// 겹 사이 이음선을 덮으면 테두리가 호에 얹힌 것이 아니라 호 옆에 따로 떠 있는 것처럼 보인다.
    ///
    /// <para>그래서 반지름과 각도를 <b>두께의 절반만큼 안으로</b> 들여 그린다. 그러면 선의 바깥쪽
    /// 가장자리가 조각의 경계와 정확히 맞는다(Treemap 이 사각형에 쓰는 것과 같은 방법이다).</para>
    /// </summary>
    private Geometry OutlineAt(int index, double thickness)
    {
        // 각도가 흐르는 동안(스캔 중)에는 그 기하를 그대로 쓴다. 어차피 매 프레임 자리가 바뀐다.
        if (_morphGeometry != null) return _morphGeometry[index];

        var s = _layout.Segments[index];
        double half = thickness / 2d;

        double r0 = RadiusAt(s.Ring);
        double r1 = RadiusAt(s.Ring + 1) - RingGap;

        double inner = r0 + half, outer = r1 - half;
        if (outer - inner < 0.6d)
        {
            // 겹이 선 두께보다 얇다. 한가운데에 실선 하나로 남긴다.
            double mid = (r0 + r1) / 2d;
            inner = mid - 0.3d;
            outer = mid + 0.3d;
        }

        // 한 바퀴를 통째로 차지하는 조각은 각도를 줄이면 도넛이 깨져 틈이 생긴다.
        if (s.Sweep >= 359.9d) return BuildSegment(inner, outer, s.Start, s.Sweep);

        // 같은 px 라도 반지름마다 각도가 다르다. 가장 넓게 벌어지는 바깥 반지름을 기준으로 잡는다.
        double pad = outer > 0.5d ? half / outer * (180d / Math.PI) : 0d;
        double start = s.Start + pad, sweep = s.Sweep - pad * 2d;

        if (sweep < 0.04d)
        {
            // 원래도 선보다 좁은 조각. 한가운데에 최소 폭으로 세운다.
            start = s.Mid - 0.02d;
            sweep = 0.04d;
        }

        return BuildSegment(inner, outer, start, sweep);
    }

    /// <summary>가운데 원 — 현재 폴더의 총량과 이름. 눌러서 상위로 올라가는 버튼이기도 하다.</summary>
    private void DrawCenter(DrawingContext dc)
    {
        if (_hole <= 4) return;

        dc.DrawEllipse(BackdropBrush, null, _center, _hole - RingGap, _hole - RingGap);

        if (_hoverCenter)
        {
            var ring = new Pen(SelectionBrush, 1.4d);
            ring.Freeze();
            dc.DrawEllipse(null, ring, _center, _hole - RingGap - 1d, _hole - RingGap - 1d);
        }

        // 원본처럼 숫자와 단위를 두 줄로 나눈다. 한 줄로 쓰면 구멍 폭에 맞추느라 글자가 작아지고,
        // 폴더 이름은 옆 목록 머리와 경로 막대에 이미 있어 여기서 또 쓰면 세 번 같은 말을 한다.
        string total = SizeFormatter.Format(_layout.TotalSize);
        int space = total.LastIndexOf(' ');
        string value = space > 0 ? total[..space] : total;
        string unit = space > 0 ? total[(space + 1)..] : string.Empty;

        double fontSize = Math.Clamp(_hole * 0.46d, 11d, 30d);

        if (unit.Length == 0)
        {
            DrawCentered(dc, value, fontSize, LabelBrush, 0d);
            return;
        }

        DrawCentered(dc, value, fontSize, LabelBrush, -fontSize * 0.52d);
        DrawCentered(dc, unit, fontSize * 0.82d, LabelBrush, fontSize * 0.5d);
    }

    private void DrawCentered(DrawingContext dc, string text, double fontSize, Brush brush, double dy)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 링이 없을 때는 중앙 구멍이 0 이라 구멍 폭으로 재면 문구가 통째로 잘린다.
        double maxWidth = _geometry.Length == 0 ? Math.Max(80d, ActualWidth - 24d) : Math.Max(20d, _hole * 1.9d);

        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            _typeface!, fontSize, brush, dpi)
        {
            MaxTextWidth = maxWidth,
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = 1,
        };

        double x = _geometry.Length == 0 ? (ActualWidth - ft.Width) / 2d : _center.X - ft.Width / 2d;
        double y = (_geometry.Length == 0 ? ActualHeight / 2d : _center.Y) - ft.Height / 2d + dy;
        dc.DrawText(ft, new Point(x, y));
    }

    private static Brush Lighten(Brush brush)
    {
        if (brush is not SolidColorBrush s) return brush;
        var c = SunburstPalette.Highlight(new SunburstColor(s.Color.R, s.Color.G, s.Color.B, s.Color.A));
        var lit = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        lit.Freeze();
        return lit;
    }

    // ---------------------------------------------------------------- 입력

    /// <summary>화면 좌표 → 조각. 반지름으로 겹을 고르고 각도로 그 안에서 찾는다(O(조각 수) 선형 탐색 1회).</summary>
    private int HitTest(Point p, out bool center)
    {
        center = false;
        if (_geometry.Length == 0) return -1;

        double dx = p.X - _center.X, dy = p.Y - _center.Y;
        double r = Math.Sqrt(dx * dx + dy * dy);

        if (r <= _hole) { center = true; return -1; }
        if (_ringWidth <= 0) return -1;

        // 겹마다 두께가 다르므로 나눗셈 한 번으로 찾을 수 없다. 겹 수가 한 자릿수라 그냥 훑는다.
        int ring = -1;
        for (int k = 0; k < _ringCount; k++)
        {
            if (r < RadiusAt(k) || r >= RadiusAt(k + 1)) continue;
            if (r > RadiusAt(k + 1) - RingGap) return -1;   // 겹 사이 이음선
            ring = k;
            break;
        }
        if (ring < 0) return -1;

        // atan2 는 3시 방향 기준이므로 12시 기준 시계 방향으로 옮긴다.
        double angle = Math.Atan2(dy, dx) * 180d / Math.PI + 90d;
        if (angle < 0) angle += 360d;

        for (int i = 0; i < _layout.Segments.Count; i++)
        {
            var s = _layout.Segments[i];
            if (s.Ring != ring) continue;
            if (angle >= s.Start && angle < s.End) return i;
        }
        return -1;
    }

    private SunburstSegment? SegmentAt(int index)
        => index >= 0 && index < _layout.Segments.Count ? _layout.Segments[index] : null;

    /// <summary>지금 마우스가 가리키고 있는 조각. 단축키가 "가리킨 것"에 작동하기 위해 필요하다.</summary>
    public SunburstSegment? Hovered => SegmentAt(_hoverIndex);

    // 누른 자리와 대상. 끌기로 넘어가지 않고 버튼을 떼면 그때 "클릭"으로 친다.
    private Point _pressOrigin;
    private SunburstSegment? _pressSegment;
    private bool _pressCenter;
    private bool _dragging;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // 누른 채로 문턱을 넘겨 움직이면 끌기다. 클릭(들어가기)과 끌기(담기)를 이렇게 가른다.
        if (!_dragging && _pressSegment is { CanCollect: true } && e.LeftButton == MouseButtonState.Pressed)
        {
            var now = e.GetPosition(this);
            if (Math.Abs(now.X - _pressOrigin.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(now.Y - _pressOrigin.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _dragging = true;
                var dragged = _pressSegment;
                _pressSegment = null;
                DragStartRequested?.Invoke(this, dragged);
                return;
            }
        }

        int hit = HitTest(e.GetPosition(this), out bool center);
        if (hit == _hoverIndex && center == _hoverCenter) return;

        _hoverIndex = hit;
        _hoverCenter = center;
        Cursor = center || hit >= 0 ? Cursors.Hand : Cursors.Arrow;
        HoverChanged?.Invoke(this, SegmentAt(hit));
        BeginGlow(hit);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex < 0 && !_hoverCenter) return;
        _hoverIndex = -1;
        _hoverCenter = false;
        BeginGlow(-1);

        // 키보드로 가리켜 둔 것이 있으면 그리로 돌아간다. 마우스가 창 밖으로 나갔다고
        // 키보드 사용자가 보고 있던 것까지 지우면 설명 줄이 계속 초기화된다.
        HoverChanged?.Invoke(this, SegmentAt(_focusIndex));
        InvalidateVisual();
    }

    /// <summary>마지막으로 우클릭한 조각. 우클릭 메뉴가 이 항목에 대해 동작한다.</summary>
    public SunburstSegment? ContextItem { get; private set; }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        ContextItem = SegmentAt(HitTest(e.GetPosition(this), out _));
        if (ContextItem != null) ItemSelected?.Invoke(this, ContextItem);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        _dragging = false;
        _pressOrigin = e.GetPosition(this);
        _pressSegment = SegmentAt(HitTest(_pressOrigin, out bool center));
        _pressCenter = center;
        CaptureMouse();
    }

    /// <summary>
    /// 끌기로 넘어가지 않았을 때만 "눌렀다"로 친다. 그래서 같은 왼쪽 버튼이
    /// <b>누르면 들어가기 · 끌면 담기</b> 두 가지로 갈린다(DaisyDisk 와 같은 방식).
    /// </summary>
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();

        var segment = _pressSegment;
        bool center = _pressCenter;
        bool dragged = _dragging;
        _pressSegment = null;
        _pressCenter = false;
        _dragging = false;
        if (dragged) return;

        // 뗄 때도 같은 자리인지 본다 - 눌렀다가 밖으로 끌고 나가 떼면 아무 일도 없어야 한다.
        int hit = HitTest(e.GetPosition(this), out bool upCenter);
        if (center)
        {
            if (upCenter) GoUpRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (segment == null || !ReferenceEquals(segment, SegmentAt(hit))) return;

        // DaisyDisk 와 같다 - 한 번 누르면 들어간다. 두 번 누를 필요가 없다.
        if (segment.CanDrill) ItemActivated?.Invoke(this, segment);
        else ItemSelected?.Invoke(this, segment);
    }

    // ---------------------------------------------------------------- 키보드

    /// <summary>
    /// 키보드가 가리키는 조각. 마우스를 쓰지 않는 사람에게는 이것이 "가리킨 것" 이다.
    /// </summary>
    public SunburstSegment? Focused => SegmentAt(_focusIndex);

    /// <summary>
    /// 화살표로 링을 돈다. 방향은 그림과 같게 둔다 —
    /// 위 / 아래는 같은 겹의 형제, 오른쪽은 바깥(자식), 왼쪽은 안쪽(부모).
    ///
    /// <para>화살표는 명령이 아니라 위젯 자체의 이동이라 키맵에 올리지 않는다.
    /// 이 앱의 다른 목록들도 화살표는 컨트롤이 직접 처리한다.</para>
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_layout.Segments.Count == 0) return;

        switch (e.Key)
        {
            case Key.Down or Key.Right when _focusIndex < 0:
                SetFocus(0);
                break;
            case Key.Up: MoveSibling(-1); break;
            case Key.Down: MoveSibling(+1); break;
            case Key.Home: MoveToEdge(first: true); break;
            case Key.End: MoveToEdge(first: false); break;
            case Key.Left: FocusParent(); break;
            case Key.Right: FocusChild(); break;
            case Key.Escape when _focusIndex >= 0: SetFocus(-1); break;
            default: return;
        }
        e.Handled = true;
    }

    private void SetFocus(int index)
    {
        if (index == _focusIndex) return;
        _focusIndex = index;
        HoverChanged?.Invoke(this, SegmentAt(index));   // 옆 목록이 호버와 똑같이 따라온다
        InvalidateVisual();
    }

    /// <summary>같은 겹 · 같은 부모를 가진 조각들. 레이아웃이 각도 순으로 넣으므로 그 순서가 곧 화면 순서다.</summary>
    private List<int> Siblings(int index)
    {
        var target = _layout.Segments[index];
        var list = new List<int>(16);
        for (int i = 0; i < _layout.Segments.Count; i++)
        {
            var s = _layout.Segments[i];
            if (s.Ring == target.Ring && s.ParentIndex == target.ParentIndex) list.Add(i);
        }
        return list;
    }

    private void MoveSibling(int delta)
    {
        if (_focusIndex < 0) { SetFocus(0); return; }

        var siblings = Siblings(_focusIndex);
        int at = siblings.IndexOf(_focusIndex);
        if (at < 0) return;

        // 형제 끝에서 한 바퀴 돈다. 원형 그림이라 끝에서 멈추는 쪽이 오히려 어색하다.
        SetFocus(siblings[(at + delta + siblings.Count) % siblings.Count]);
    }

    private void MoveToEdge(bool first)
    {
        if (_focusIndex < 0) { SetFocus(0); return; }
        var siblings = Siblings(_focusIndex);
        if (siblings.Count > 0) SetFocus(first ? siblings[0] : siblings[^1]);
    }

    private void FocusParent()
    {
        if (_focusIndex < 0) return;
        int parent = _layout.Segments[_focusIndex].ParentIndex;
        if (parent >= 0) SetFocus(parent);
    }

    private void FocusChild()
    {
        if (_focusIndex < 0) { SetFocus(0); return; }
        for (int i = _focusIndex + 1; i < _layout.Segments.Count; i++)
            if (_layout.Segments[i].ParentIndex == _focusIndex) { SetFocus(i); return; }
    }

    /// <summary>
    /// 창 단축키 중 "지금 가리킨 것" 에 걸리는 것들만 여기서 처리한다.
    /// 키 이름은 나오지 않는다 — 무엇으로 부를지는 키맵이 정한다.
    /// </summary>
    public bool TryExecuteShortcut(string commandId)
    {
        if (Focused is not { } segment) return false;

        switch (commandId)
        {
            case CommandIds.Open when segment.CanDrill:
                ItemActivated?.Invoke(this, segment);
                return true;
            case CommandIds.ToggleCollect when segment.CanCollect:
                CollectRequested?.Invoke(this, segment);
                return true;
            default:
                return false;
        }
    }

    /// <summary>가운데 버튼 = 수집함에 담기 / 빼기. 마우스만으로 모으는 가장 짧은 길이다.</summary>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle) return;

        var segment = SegmentAt(HitTest(e.GetPosition(this), out _));
        if (segment is { CanCollect: true }) CollectRequested?.Invoke(this, segment);
    }
}

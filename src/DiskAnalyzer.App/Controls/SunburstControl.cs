using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiskAnalyzer.Core.Analysis;
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
public sealed class SunburstControl : FrameworkElement
{
    /// <summary>중앙 빈 원의 반지름 비율. DaisyDisk 실측값(바깥 반지름의 0.22).</summary>
    private const double HoleRatio = 0.22d;

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
    private bool _hoverCenter;
    private Typeface? _typeface;

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
        _store = store;
        _layout = SunburstLayout.Build(store, dirId, options);
        CenterName = store == null || dirId < 0
            ? string.Empty
            : (dirId == NodeStore.RootId ? store.RootPath : store.GetDirectoryName(dirId));

        _selected.Clear();
        _hoverIndex = -1;
        _lastSize = default;
        Rebuild();
    }

    /// <summary>
    /// 스캔 중: <see cref="NodeStore"/> 는 Aggregator 스레드가 쓰고 있으므로 직접 타고 내려가면 안 된다.
    /// 게시된 현재 폴더 스냅샷만으로 한 겹을 그린다 — 링이 자라는 것이 그대로 보인다.
    /// </summary>
    public void SetFlatSource(IReadOnlyList<EntryRow> rows, string name)
    {
        _store = null;
        _layout = SunburstLayout.BuildFlat(rows, name);
        CenterName = name;
        _selected.Clear();
        _hoverIndex = -1;
        _lastSize = default;
        Rebuild();
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
        _ringWidth = (outer - _hole) / rings;

        int n = _layout.Segments.Count;
        _geometry = new Geometry[n];
        _fill = new Brush[n];

        for (int i = 0; i < n; i++)
        {
            var s = _layout.Segments[i];
            double r0 = _hole + s.Ring * _ringWidth;
            double r1 = r0 + _ringWidth - RingGap;
            _geometry[i] = BuildSegment(r0, Math.Max(r0 + 0.5d, r1), s.Start, s.Sweep);

            var c = SunburstPalette.Fill(s.Kind, s.Mid);
            var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
            brush.Freeze();
            _fill[i] = brush;
        }

        InvalidateVisual();
    }

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
            ctx.BeginFigure(Polar(rIn, start), isFilled: true, isClosed: true);
            ctx.LineTo(Polar(rOut, start), false, false);
            ctx.ArcTo(Polar(rOut, end), new Size(rOut, rOut), 0d, large, SweepDirection.Clockwise, false, false);
            ctx.LineTo(Polar(rIn, end), false, false);
            ctx.ArcTo(Polar(rIn, start), new Size(rIn, rIn), 0d, large, SweepDirection.Counterclockwise, false, false);
        }
        geo.Freeze();
        return geo;
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

        var seam = new Pen(BackdropBrush, 0.8d);
        seam.Freeze();

        for (int i = 0; i < _geometry.Length; i++)
        {
            var s = _layout.Segments[i];
            var brush = i == _hoverIndex ? Lighten(_fill[i]) : _fill[i];
            dc.DrawGeometry(brush, s.Sweep >= StrokeMinSweep ? seam : null, _geometry[i]);
        }

        DrawOutlines(dc, _collected, CollectedBrush, 2.2d);
        DrawOutlines(dc, _selected, SelectionBrush, 1.8d);

        DrawCenter(dc);
    }

    private void DrawOutlines(DrawingContext dc, HashSet<long> keys, Brush brush, double thickness)
    {
        if (keys.Count == 0) return;
        var pen = new Pen(brush, thickness);
        pen.Freeze();

        for (int i = 0; i < _geometry.Length; i++)
        {
            var s = _layout.Segments[i];
            if (s.Id < 0 || !keys.Contains(KeyOf(s.Kind, s.Id))) continue;
            dc.DrawGeometry(null, pen, _geometry[i]);
        }
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

        string total = SizeFormatter.Format(_layout.TotalSize);
        double fontSize = Math.Clamp(_hole * 0.34d, 10d, 21d);

        DrawCentered(dc, total, fontSize, LabelBrush, -fontSize * 0.62d);
        if (_hole >= 34 && CenterName.Length > 0)
            DrawCentered(dc, CenterName, Math.Max(9d, fontSize * 0.55d), MutedBrush, fontSize * 0.55d);
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

        int ring = (int)((r - _hole) / _ringWidth);
        double within = (r - _hole) - ring * _ringWidth;
        if (within > _ringWidth - RingGap) return -1;   // 겹 사이 이음선

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
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex < 0 && !_hoverCenter) return;
        _hoverIndex = -1;
        _hoverCenter = false;
        HoverChanged?.Invoke(this, null);
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

    /// <summary>가운데 버튼 = 수집함에 담기 / 빼기. 마우스만으로 모으는 가장 짧은 길이다.</summary>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle) return;

        var segment = SegmentAt(HitTest(e.GetPosition(this), out _));
        if (segment is { CanCollect: true }) CollectRequested?.Invoke(this, segment);
    }
}

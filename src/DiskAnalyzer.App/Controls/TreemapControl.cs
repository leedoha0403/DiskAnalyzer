using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.App.Controls;

/// <summary>
/// 14. Treemap.
/// 장식이 아니라 "비정상적으로 큰 파일을 즉시 찾는" 용도이므로
///  - 면적이 곧 크기(squarified 배치로 가로세로비를 1에 가깝게 유지)
///  - 색은 카테고리 6종으로 제한(17. 색상 남용 금지)
///  - 의미 있는 크기의 사각형에만 라벨을 그린다
///
/// [성능] 레이아웃은 탐색/리사이즈 시에만 1회 계산하고 결과를 리스트로 들고 있는다(34. Lazy).
/// 사각형 수 상한(MaxItems)과 최소 면적(MinArea)으로 렌더 비용을 고정한다.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    public sealed record TreemapItem(
        Rect Bounds, string Name, string FullPath, long Size,
        bool IsDirectory, int Id, int Depth, string Extension, CategoryFlags Category)
    {
        /// <summary>하위 사각형이 실제로 그려졌는지. 그려졌다면 이름은 상단 헤더 띠에만 쓴다.</summary>
        public bool Expanded { get; set; }

        /// <summary>이름을 쓸 헤더 띠를 확보했는지.</summary>
        public double HeaderHeight { get; set; }

        /// <summary>
        /// 라벨을 미리 그려 둔 것. <see cref="FormattedText"/> 생성(글자 배치)이 treemap 렌더 비용의
        /// 대부분이라, 폴더를 드나드는 줌 애니메이션 중 매 프레임 새로 만들면 그만큼 버벅인다 —
        /// Bounds 는 바뀌지 않고 화면 전체에 걸린 변환(<see cref="PushTransition"/>)만 움직이므로,
        /// <see cref="Rebuild"/> 가 항목을 새로 채울 때 한 번만 만들어 두고 렌더링에서는 재사용한다.
        /// </summary>
        public FormattedText? CachedLabel { get; set; }
    }

    private const int MaxItems = 4000;
    private const double MinArea = 120d;
    private const int MaxDepth = 4;

    private readonly List<TreemapItem> _items = new();
    private NodeStore? _store;
    private IReadOnlyList<EntryRow>? _flatRows;
    private int _dirId;
    private Size _lastLayoutSize;
    private TreemapItem? _hovered;
    private Typeface? _typeface;
    private Pen? _cachedStrokePen;
    private Brush? _cachedStrokeBrush;

    public event EventHandler<TreemapItem?>? HoverChanged;
    public event EventHandler<TreemapItem>? ItemActivated;
    public event EventHandler<TreemapItem>? ItemSelected;

    public TreemapControl()
    {
        ClipToBounds = true;
        Focusable = true;

        // 선버스트와 같은 이유. DrawText 는 창의 TextOptions 를 물려받지 않아
        // 어두운 사각형 위의 흰 라벨에 ClearType 색 가장자리가 남는다.
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
    }

    public static readonly DependencyProperty StrokeBrushProperty = DependencyProperty.Register(
        nameof(StrokeBrush), typeof(Brush), typeof(TreemapControl),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(TreemapControl),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender, OnLabelBrushChanged));

    // 라벨은 만들 때 색을 굳혀 캐시해 둔다(버벅임 방지) - 테마가 바뀌어 브러시가 달라지면
    // 다시 만들어야 낡은 색으로 남지 않는다.
    private static void OnLabelBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TreemapControl t) t.BuildLabels();
    }

    public static readonly DependencyProperty SelectionBrushProperty = DependencyProperty.Register(
        nameof(SelectionBrush), typeof(Brush), typeof(TreemapControl),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush StrokeBrush { get => (Brush)GetValue(StrokeBrushProperty); set => SetValue(StrokeBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    /// <summary>폴더 목록에서 선택한 항목을 Treemap 에서 강조하는 테두리 색.</summary>
    public Brush SelectionBrush { get => (Brush)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }

    // 선택 강조. (폴더/파일 구분 + id) 를 한 개의 long 키로 만들어 렌더링 중 조회 비용을 낮춘다.
    private readonly HashSet<long> _selected = new();

    private static long KeyOf(bool isDirectory, int id) => ((isDirectory ? 1L : 0L) << 32) | (uint)id;

    /// <summary>선택 강조를 바꾼다. 레이아웃은 다시 계산하지 않고 다시 그리기만 한다.</summary>
    public void SetSelection(IEnumerable<(bool IsDirectory, int Id)> items)
    {
        _selected.Clear();
        foreach (var (isDir, id) in items) _selected.Add(KeyOf(isDir, id));
        InvalidateVisual();
    }

    /// <summary>스캔 완료 후: 저장소를 직접 타고 내려가며 여러 단계를 그린다.</summary>
    public void SetSource(NodeStore? store, int dirId)
    {
        int leaving = _dirId;
        Rect? origin = null;

        if (dirId != _dirId)
        {
            _selected.Clear();   // 다른 폴더로 옮겼으면 이전 선택 강조는 의미가 없다

            // 들어간 폴더가 지금 화면 어디에 있었는가. 다시 그리기 전에 물어야 한다.
            origin = RectOf(dirId);
        }

        _store = store;
        _flatRows = null;
        _dirId = dirId;
        _lastLayoutSize = default;
        Rebuild();

        // 들어간 자리가 없었다면 상위로 올라온 것이다 — 떠나온 폴더가 새 화면에서 차지한 자리를 찾는다.
        if (origin == null && leaving != dirId && RectOf(leaving) is { } back)
            origin = Widen(back, new Rect(0, 0, ActualWidth, ActualHeight));

        BeginZoom(origin);
    }

    /// <summary>
    /// 스캔 중: NodeStore 는 Aggregator 스레드가 쓰고 있으므로 UI 에서 직접 타고 내려가면 안 된다.
    /// 대신 Aggregator 가 게시한 현재 폴더 스냅샷만으로 1단계 트리맵을 그린다.
    /// </summary>
    public void SetFlatSource(IReadOnlyList<EntryRow> rows)
    {
        _store = null;
        _flatRows = rows;
        _lastLayoutSize = default;
        StopMotion();
        Rebuild();
    }

    public void Clear()
    {
        _items.Clear();
        _store = null;
        _flatRows = null;
        StopMotion();
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- 50. 움직임

    /// <summary>폴더를 드나드는 줌. 0 = 출발한 사각형 안, 1 = 화면 전체.</summary>
    private static readonly DependencyProperty TransitionProperty = DependencyProperty.Register(
        nameof(Transition), typeof(double), typeof(TreemapControl),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    private double Transition => (double)GetValue(TransitionProperty);

    /// <summary>t=0 에서 새 화면이 들어 있을 자리.</summary>
    private Rect _zoomFrom;

    /// <summary>상위로 올라갈 때 화면이 이보다 더 크게 벌어지지는 않는다 — 작은 조각에서 올라오면 눈이 멀미한다.</summary>
    private const double MaxZoomOut = 12d;

    /// <summary>출발 사각형을 목적지 쪽으로 미리 당겨 두는 비율. 0 이면 원래 자리에서, 1 이면 아예 움직이지 않는다.</summary>
    private const double ZoomRestraint = 0.72d;

    /// <summary>걸려 있던 줌을 걷는다. 설정에서 애니메이션을 끄는 순간 반쯤 펴진 채로 멈추면 안 된다.</summary>
    public void StopMotion()
    {
        Motion.Settle(this, TransitionProperty, 1d);
        _zoomFrom = default;
    }

    /// <summary>
    /// 트리맵의 줌에는 선버스트에 없는 것이 있다 — <b>출발한 사각형</b>.
    /// 방금 누른 폴더가 있던 자리에서 화면 전체로 펴지면, 새 화면이 그 폴더의 안쪽이라는 것이
    /// 설명 없이 읽힌다. 상위로 올라갈 때는 거꾸로, 화면 전체가 있던 자리의 크기로 접힌다.
    /// </summary>
    private void BeginZoom(Rect? origin)
    {
        if (!Motion.Enabled || origin is not { Width: > 1d, Height: > 1d } from ||
            ActualWidth < 8 || ActualHeight < 8)
        {
            StopMotion();
            return;
        }

        // 사각형에서 화면 전체까지 통째로 펴면 배율이 수십 배가 되어 화면이 터져 나오는 것처럼 보인다.
        // 출발점을 목적지 쪽으로 미리 당겨 두면 "어디서 왔는지"는 남고 출렁임만 빠진다.
        _zoomFrom = Between(from, new Rect(0, 0, ActualWidth, ActualHeight), ZoomRestraint);
        Motion.From(this, TransitionProperty, 0d, 1d, Motion.Glide, Motion.Land);
    }

    /// <summary>
    /// <paramref name="target"/> 가 화면을 가득 채우게 하는 자리. 들어갈 때 쓰는 대응의 역이다.
    /// 너무 작은 사각형에서 올라오면 배율이 터무니없어지므로 그때는 줌을 포기한다(null).
    /// </summary>
    private static Rect? Widen(Rect target, Rect area)
    {
        if (target.Width < 6d || target.Height < 6d || area.Width < 8d || area.Height < 8d) return null;

        double sx = area.Width / target.Width, sy = area.Height / target.Height;
        if (sx > MaxZoomOut || sy > MaxZoomOut) return null;

        return new Rect(-target.X * sx, -target.Y * sy, area.Width * sx, area.Height * sy);
    }

    private Rect? RectOf(int directoryId)
    {
        foreach (var item in _items)
            if (item.IsDirectory && item.Id == directoryId) return item.Bounds;
        return null;
    }

    private static Rect Between(Rect a, Rect b, double t) => new(
        a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t,
        Math.Max(0.01d, a.Width + (b.Width - a.Width) * t),
        Math.Max(0.01d, a.Height + (b.Height - a.Height) * t));

    /// <summary>줌을 건다. 되돌릴 Pop 횟수를 준다.</summary>
    private int PushTransition(DrawingContext dc)
    {
        double t = Transition;
        if (t >= 1d || _zoomFrom.Width <= 0d || _zoomFrom.Height <= 0d) return 0;

        var area = new Rect(0, 0, ActualWidth, ActualHeight);
        if (area.Width < 8d || area.Height < 8d) return 0;

        // 자리와 크기만 바꾼다. 선버스트와 같은 이유로 투명도 층은 걸지 않는다 —
        // 층이 걷히는 마지막 프레임에 사각형 경계가 전부 다시 안티에일리어싱되어 한 번 반짝인다.
        var r = Between(_zoomFrom, area, t);
        dc.PushTransform(new MatrixTransform(r.Width / area.Width, 0d, 0d, r.Height / area.Height, r.X, r.Y));
        return 1;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Rebuild();
    }

    private void Rebuild()
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (size.Width < 8 || size.Height < 8) return;
        if (_store == null && _flatRows == null) { _items.Clear(); InvalidateVisual(); return; }
        if (size == _lastLayoutSize && _items.Count > 0) return;

        _lastLayoutSize = size;
        _items.Clear();
        var area = new Rect(0, 0, size.Width, size.Height);

        if (_store != null) BuildLevel(_dirId, area, 0);
        else if (_flatRows != null) BuildFlat(_flatRows, area);

        BuildLabels();
        SyncHover();
        InvalidateVisual();
    }

    /// <summary>
    /// 라벨을 그릴 사각형마다 <see cref="FormattedText"/> 를 미리 만들어 둔다. 레이아웃이 바뀔 때(=<see cref="Rebuild"/>)
    /// 한 번만 하는 일이고, 줌 애니메이션 동안의 <see cref="OnRender"/> 는 이 결과를 그대로 그리기만 한다.
    /// </summary>
    private void BuildLabels()
    {
        _typeface ??= new Typeface(AppFont.Family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var item in _items)
        {
            if (item.Expanded && item.HeaderHeight <= 0) continue;
            if (item.Bounds.Width < 54 || item.Bounds.Height < 16) continue;

            item.CachedLabel = new FormattedText(item.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                _typeface, 11.5, LabelBrush, dpi)
            {
                MaxTextWidth = Math.Max(1, item.Bounds.Width - 8),
                Trimming = TextTrimming.CharacterEllipsis,
                MaxLineCount = 1,
            };
        }
    }

    /// <summary>
    /// 다시 그린 뒤 가리킨 사각형을 다시 잰다. <see cref="_hovered"/> 는 옛 <see cref="_items"/> 의
    /// 객체라 <c>Rebuild</c> 가 목록을 새로 채우면 낡은 자리를 계속 가리킨다 — 더블 클릭이나
    /// Backspace 로 이동하면 마우스는 그대로인데 화면이 바뀌어서, 흰 테두리가 방금까지 커서가
    /// 있던 <b>엉뚱한 자리</b>에 남는다. 마우스가 실제로 움직이기를 기다리지 않고, 지금 위치로
    /// 새 배치를 다시 맞혀 본다.
    /// </summary>
    private void SyncHover()
    {
        var hit = IsMouseOver ? HitTest(Mouse.GetPosition(this)) : null;
        if (ReferenceEquals(hit, _hovered)) return;

        _hovered = hit;
        HoverChanged?.Invoke(this, hit);
    }

    private void BuildFlat(IReadOnlyList<EntryRow> rows, Rect area)
    {
        long total = 0;
        foreach (var r in rows) total += r.Size;
        if (total <= 0) return;

        double totalArea = area.Width * area.Height;
        var entries = new List<(EntryRow Row, double Area)>(rows.Count);
        foreach (var r in rows)
        {
            double a = totalArea * ((double)r.Size / total);
            if (a < 1d) break;
            entries.Add((r, a));
        }
        if (entries.Count > 0) Squarify(entries, area, 0);
    }

    private void BuildLevel(int dirId, Rect area, int depth)
    {
        if (_store == null || _items.Count >= MaxItems) return;
        if (area.Width < 3 || area.Height < 3) return;

        var children = _store.GetChildren(dirId, includeFiles: true);
        if (children.Count == 0) return;

        long total = 0;
        foreach (var c in children) total += c.Size;
        if (total <= 0) return;

        var entries = new List<(EntryRow Row, double Area)>(children.Count);
        double totalArea = area.Width * area.Height;
        foreach (var c in children)
        {
            double a = totalArea * ((double)c.Size / total);
            if (a < 1d) break;                     // 정렬되어 있으므로 여기서부터는 전부 작다
            entries.Add((c, a));
        }
        if (entries.Count == 0) return;

        Squarify(entries, area, depth);
    }

    /// <summary>Squarified Treemap (Bruls, Huizing, van Wijk). 행을 끊는 기준은 "최악 종횡비"가 나빠지기 직전.</summary>
    private void Squarify(List<(EntryRow Row, double Area)> entries, Rect rect, int depth)
    {
        int i = 0;
        while (i < entries.Count && rect.Width > 1 && rect.Height > 1 && _items.Count < MaxItems)
        {
            bool vertical = rect.Width >= rect.Height;
            double side = vertical ? rect.Height : rect.Width;
            if (side <= 0) break;

            double rowSum = entries[i].Area;
            double rowMax = rowSum, rowMin = rowSum;
            int count = 1;
            double best = Worst(rowSum, rowMax, rowMin, side);

            while (i + count < entries.Count)
            {
                double v = entries[i + count].Area;
                double newSum = rowSum + v;
                double newMin = Math.Min(rowMin, v);
                double newMax = Math.Max(rowMax, v);
                double w = Worst(newSum, newMax, newMin, side);
                if (w > best) break;
                best = w;
                rowSum = newSum;
                rowMin = newMin;
                rowMax = newMax;
                count++;
            }

            double thickness = rowSum / side;
            double offset = 0;

            for (int k = 0; k < count; k++)
            {
                var (row, a) = entries[i + k];
                double length = a / thickness;
                Rect r = vertical
                    ? new Rect(rect.X, rect.Y + offset, thickness, length)
                    : new Rect(rect.X + offset, rect.Y, length, thickness);
                offset += length;

                if (r.Width <= 0 || r.Height <= 0) continue;
                Emit(row, r, depth);
            }

            if (vertical) rect = new Rect(rect.X + thickness, rect.Y, Math.Max(0, rect.Width - thickness), rect.Height);
            else rect = new Rect(rect.X, rect.Y + thickness, rect.Width, Math.Max(0, rect.Height - thickness));

            i += count;
        }
    }

    private static double Worst(double sum, double max, double min, double side)
    {
        if (sum <= 0 || min <= 0) return double.MaxValue;
        double s2 = side * side;
        return Math.Max(s2 * max / (sum * sum), sum * sum / (s2 * min));
    }

    private void Emit(EntryRow row, Rect r, int depth)
    {
        var item = new TreemapItem(r, row.Name, row.FullPath, row.Size, row.IsDirectory,
            row.Id, depth, row.Extension, row.Category);
        _items.Add(item);

        if (_store == null || !row.IsDirectory) return;
        if (depth + 1 > MaxDepth) return;
        if (r.Width * r.Height < MinArea * 8) return;

        // 폴더 이름을 쓸 헤더 띠를 위쪽에 확보한다.
        // 이렇게 하지 않으면 부모 이름과 첫 번째 자식 이름이 같은 좌표에 겹쳐 그려진다.
        double header = r.Height >= 38 && r.Width >= 64 ? 17d : 0d;
        var inner = new Rect(r.X + 2, r.Y + 2 + header,
            Math.Max(0, r.Width - 4), Math.Max(0, r.Height - 4 - header));
        if (inner.Width < 8 || inner.Height < 8) return;

        int before = _items.Count;
        BuildLevel(row.Id, inner, depth + 1);

        if (_items.Count > before)
        {
            item.Expanded = true;
            item.HeaderHeight = header;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_items.Count == 0)
        {
            DrawCentered(dc, "스캔 결과가 없습니다.");
            return;
        }

        int pushed = PushTransition(dc);

        if (_cachedStrokePen == null || !ReferenceEquals(_cachedStrokeBrush, StrokeBrush))
        {
            _cachedStrokeBrush = StrokeBrush;
            _cachedStrokePen = new Pen(StrokeBrush, 1d);
            _cachedStrokePen.Freeze();
        }
        var pen = _cachedStrokePen;

        foreach (var item in _items)
        {
            var brush = BrushFor(item);
            dc.DrawRectangle(brush, item.Bounds.Width > 3 && item.Bounds.Height > 3 ? pen : null, item.Bounds);
        }

        // 라벨은 Rebuild 때 미리 만들어 둔 것을 그대로 그린다 - 줌 애니메이션 중 매 프레임
        // FormattedText 를 새로 만들면(텍스트 렌더링이 treemap 비용의 대부분이다) 버벅인다.
        //
        // 흰 받침 · 글자 테두리(halo) 둘 다 글자에 손을 대는 방식이라 부자연스러웠다 - 사각형
        // 색(Palette) 자체가 깊이마다 뚜렷이 갈리도록 고쳐서, 글자는 원래대로 한 번만 그려도
        // 항상 충분한 대비가 나오게 한다.
        foreach (var item in _items)
        {
            if (item.CachedLabel == null) continue;
            dc.DrawText(item.CachedLabel, new Point(item.Bounds.X + 4, item.Bounds.Y + 1));
        }

        if (_selected.Count > 0)
        {
            var sp = new Pen(SelectionBrush, 3d);
            sp.Freeze();
            foreach (var item in _items)
            {
                if (!_selected.Contains(KeyOf(item.IsDirectory, item.Id))) continue;
                // 테두리가 이웃 사각형으로 번지지 않게 안쪽으로 절반만큼 줄여 그린다.
                var r = item.Bounds;
                if (r.Width <= 3 || r.Height <= 3) continue;
                dc.DrawRectangle(null, sp, new Rect(r.X + 1.5, r.Y + 1.5, r.Width - 3, r.Height - 3));
            }
        }

        if (_hovered != null)
        {
            var hp = new Pen(LabelBrush, 2d);
            hp.Freeze();
            dc.DrawRectangle(null, hp, _hovered.Bounds);
        }

        for (int i = 0; i < pushed; i++) dc.Pop();
    }

    private void DrawCentered(DrawingContext dc, string message)
    {
        _typeface ??= new Typeface(AppFont.Family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(message, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            _typeface, 13, LabelBrush, dpi);
        dc.DrawText(text, new Point((ActualWidth - text.Width) / 2, (ActualHeight - text.Height) / 2));
    }

    /// <summary>
    /// 색은 카테고리 7종으로 제한하고(17. 색상 남용 금지), 폴더는 깊이에 따라 밝기를 단계적으로 낮춘다
    /// (중첩 경계가 드러나야 트리 구조가 읽힌다). 파일(리프)은 깊이와 무관하게 카테고리 색 그대로 둔다 -
    /// 깊이만큼 계속 어두워지면 "같은 분홍인데 왜 폴더마다 톤이 다르지"처럼 색이 겹쳐 보인다는 인상을 줬다.
    /// 브러시는 (카테고리 × 깊이) 조합으로 미리 만들어 Freeze 해 두므로 렌더링 중 할당이 없다.
    /// </summary>
    private static Brush BrushFor(TreemapItem item)
    {
        int slot = item.Category switch
        {
            var f when (f & CategoryFlags.Log) != 0 => 2,
            var f when (f & (CategoryFlags.Build | CategoryFlags.VisualStudio)) != 0 => 3,
            var f when (f & CategoryFlags.Cache) != 0 => 4,
            var f when (f & CategoryFlags.Package) != 0 => 5,
            var f when (f & (CategoryFlags.Media | CategoryFlags.Archive)) != 0 => 6,
            _ => item.IsDirectory ? 0 : 1,
        };
        return Palette.Get(slot, item.IsDirectory ? item.Depth : 0);
    }

    private static class Palette
    {
        private const int Depths = MaxDepth + 2;

        // 어두운 캔버스(Dark · Navy) 용 - 짙게 시작해 깊이마다 밝아진다.
        private static readonly Color[] DarkBase =
        {
            Color.FromRgb(0x36, 0x47, 0x5A),   // 0 폴더
            Color.FromRgb(0x44, 0x63, 0x7C),   // 1 파일
            Color.FromRgb(0x6E, 0x53, 0x47),   // 2 로그
            Color.FromRgb(0x70, 0x54, 0x37),   // 3 빌드
            Color.FromRgb(0x55, 0x49, 0x6A),   // 4 캐시
            Color.FromRgb(0x38, 0x5B, 0x4D),   // 5 패키지
            Color.FromRgb(0x63, 0x43, 0x4E),   // 6 미디어/압축
        };

        // 흰 캔버스(Mint · Light) 용 - 짙은 남색 계열 그대로 밝기만 올리면 칙칙해서(옛날 느낌),
        // 선버스트와 같은 파스텔 계열로 새로 잡았다. 카테고리별 색조(파랑 · 갈색 · 보라 · 초록 · 장미)는 유지하되
        // 처음부터 좀 더 또렷하게 잡았다 - 너무 옅으면 글자와 구분도 안 되고 깊이 단계끼리도 서로 안 갈린다.
        private static readonly Color[] LightBase =
        {
            Color.FromRgb(0x7F, 0xB0, 0xE0),   // 0 폴더
            Color.FromRgb(0xAC, 0xD0, 0xEC),   // 1 파일
            Color.FromRgb(0xD9, 0x9A, 0x63),   // 2 로그
            Color.FromRgb(0xE0, 0xB5, 0x63),   // 3 빌드
            Color.FromRgb(0xAD, 0x8F, 0xD9),   // 4 캐시
            Color.FromRgb(0x74, 0xC7, 0x9E),   // 5 패키지
            Color.FromRgb(0xDB, 0x8C, 0xA0),   // 6 미디어/압축
        };

        private static Brush[]? _cache;
        private static bool _cacheIsLight;

        /// <summary>테마를 바꾸면 다음 <see cref="Get"/> 호출에서 새 캔버스에 맞는 팔레트로 다시 만든다.</summary>
        public static Brush Get(int slot, int depth)
        {
            bool light = SunburstPalette.LightCanvas;
            if (_cache == null || _cacheIsLight != light)
            {
                _cache = Build(light);
                _cacheIsLight = light;
            }

            slot = Math.Clamp(slot, 0, DarkBase.Length - 1);
            depth = Math.Clamp(depth, 0, Depths - 1);
            return _cache[slot * Depths + depth];
        }

        private static Brush[] Build(bool light)
        {
            var baseColors = light ? LightBase : DarkBase;
            var brushes = new Brush[baseColors.Length * Depths];
            for (int s = 0; s < baseColors.Length; s++)
            {
                for (int d = 0; d < Depths; d++)
                {
                    var c = baseColors[s];
                    // 어두운 캔버스는 깊이마다 밝아지고, 밝은 캔버스는 반대로 깊이마다 짙어진다 -
                    // 처음엔 0.055 씩만 줄였는데 depth 0~3 이 거의 같은 색으로 보여("Users · leedo ·
                    // Videos · ozmo" 가 전부 한 덩어리) 글자까지 묻혔다. 단계를 두 배 넘게 키워
                    // 옆 깊이와 확실히 갈리게 한다.
                    double k = light ? 1d - d * 0.12d : 1d + d * 0.16d;
                    var brush = new SolidColorBrush(Color.FromRgb(
                        (byte)Math.Clamp(c.R * k, 0d, 255d),
                        (byte)Math.Clamp(c.G * k, 0d, 255d),
                        (byte)Math.Clamp(c.B * k, 0d, 255d)));
                    brush.Freeze();
                    brushes[s * Depths + d] = brush;
                }
            }
            return brushes;
        }
    }

    // ---------------- 입력 ----------------

    private TreemapItem? HitTest(Point p)
    {
        // 나중에 그려진(=더 깊은) 항목이 위에 있으므로 뒤에서부터 찾는다.
        for (int i = _items.Count - 1; i >= 0; i--)
            if (_items[i].Bounds.Contains(p)) return _items[i];
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.GetPosition(this));
        if (ReferenceEquals(hit, _hovered)) return;
        _hovered = hit;
        HoverChanged?.Invoke(this, hit);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hovered == null) return;
        _hovered = null;
        HoverChanged?.Invoke(this, null);
        InvalidateVisual();
    }

    /// <summary>마지막으로 우클릭한 사각형. 우클릭 메뉴가 이 항목에 대해 동작한다. 사각형이 없는 빈 곳이면 null.</summary>
    public TreemapItem? ContextItem { get; private set; }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        ContextItem = HitTest(e.GetPosition(this));
        if (ContextItem != null) ItemSelected?.Invoke(this, ContextItem);   // 메뉴가 어느 항목에 대한 것인지 보이게 선택 표시
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var hit = HitTest(e.GetPosition(this));
        if (hit == null) return;

        if (e.ClickCount >= 2) ItemActivated?.Invoke(this, hit);
        else ItemSelected?.Invoke(this, hit);
    }
}

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

    public event EventHandler<TreemapItem?>? HoverChanged;
    public event EventHandler<TreemapItem>? ItemActivated;
    public event EventHandler<TreemapItem>? ItemSelected;

    public TreemapControl()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public static readonly DependencyProperty StrokeBrushProperty = DependencyProperty.Register(
        nameof(StrokeBrush), typeof(Brush), typeof(TreemapControl),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(TreemapControl),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush StrokeBrush { get => (Brush)GetValue(StrokeBrushProperty); set => SetValue(StrokeBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    /// <summary>스캔 완료 후: 저장소를 직접 타고 내려가며 여러 단계를 그린다.</summary>
    public void SetSource(NodeStore? store, int dirId)
    {
        _store = store;
        _flatRows = null;
        _dirId = dirId;
        _lastLayoutSize = default;
        Rebuild();
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
        Rebuild();
    }

    public void Clear()
    {
        _items.Clear();
        _store = null;
        _flatRows = null;
        InvalidateVisual();
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

        InvalidateVisual();
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

        var pen = new Pen(StrokeBrush, 1d);
        pen.Freeze();
        _typeface ??= new Typeface(new FontFamily("Segoe UI, Malgun Gothic"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var item in _items)
        {
            var brush = BrushFor(item);
            dc.DrawRectangle(brush, item.Bounds.Width > 3 && item.Bounds.Height > 3 ? pen : null, item.Bounds);
        }

        // 라벨은 충분히 큰 사각형에만 그린다. (텍스트 렌더링이 treemap 비용의 대부분이다)
        foreach (var item in _items)
        {
            // 하위가 그려진 폴더는 헤더 띠가 있을 때만 이름을 쓴다 - 자식 라벨과 겹치지 않게.
            if (item.Expanded && item.HeaderHeight <= 0) continue;
            if (item.Bounds.Width < 54 || item.Bounds.Height < 16) continue;

            // MaxTextHeight 가 한 줄 높이보다 작으면 FormattedText 는 아무것도 그리지 않는다.
            // 헤더 띠는 이미 높이를 확보해 두었으므로 높이 제한을 걸지 않고 MaxLineCount 로만 한 줄을 강제한다.
            var text = new FormattedText(item.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                _typeface, 11.5, LabelBrush, dpi)
            {
                MaxTextWidth = Math.Max(1, item.Bounds.Width - 8),
                Trimming = TextTrimming.CharacterEllipsis,
                MaxLineCount = 1,
            };
            dc.DrawText(text, new Point(item.Bounds.X + 4, item.Bounds.Y + 1));
        }

        if (_hovered != null)
        {
            var hp = new Pen(LabelBrush, 2d);
            hp.Freeze();
            dc.DrawRectangle(null, hp, _hovered.Bounds);
        }
    }

    private void DrawCentered(DrawingContext dc, string message)
    {
        _typeface ??= new Typeface(new FontFamily("Segoe UI, Malgun Gothic"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(message, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            _typeface, 13, LabelBrush, dpi);
        dc.DrawText(text, new Point((ActualWidth - text.Width) / 2, (ActualHeight - text.Height) / 2));
    }

    /// <summary>
    /// 색은 카테고리 7종으로 제한하고(17. 색상 남용 금지), 깊이에 따라 밝기만 단계적으로 올린다.
    /// 같은 색을 그대로 쓰면 중첩된 폴더 경계가 보이지 않아 트리 구조가 읽히지 않는다.
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
        return Palette.Get(slot, item.Depth);
    }

    private static class Palette
    {
        private const int Depths = MaxDepth + 2;

        private static readonly Color[] Base =
        {
            Color.FromRgb(0x36, 0x47, 0x5A),   // 0 폴더
            Color.FromRgb(0x44, 0x63, 0x7C),   // 1 파일
            Color.FromRgb(0x6E, 0x53, 0x47),   // 2 로그
            Color.FromRgb(0x70, 0x54, 0x37),   // 3 빌드
            Color.FromRgb(0x55, 0x49, 0x6A),   // 4 캐시
            Color.FromRgb(0x38, 0x5B, 0x4D),   // 5 패키지
            Color.FromRgb(0x63, 0x43, 0x4E),   // 6 미디어/압축
        };

        private static readonly Brush[] Cache = Build();

        public static Brush Get(int slot, int depth)
        {
            slot = Math.Clamp(slot, 0, Base.Length - 1);
            depth = Math.Clamp(depth, 0, Depths - 1);
            return Cache[slot * Depths + depth];
        }

        private static Brush[] Build()
        {
            var brushes = new Brush[Base.Length * Depths];
            for (int s = 0; s < Base.Length; s++)
            {
                for (int d = 0; d < Depths; d++)
                {
                    double k = 1d + d * 0.16d;
                    var c = Base[s];
                    var brush = new SolidColorBrush(Color.FromRgb(
                        (byte)Math.Min(255, c.R * k),
                        (byte)Math.Min(255, c.G * k),
                        (byte)Math.Min(255, c.B * k)));
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

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var hit = HitTest(e.GetPosition(this));
        if (hit == null) return;

        if (e.ClickCount >= 2) ItemActivated?.Invoke(this, hit);
        else ItemSelected?.Invoke(this, hit);
    }
}

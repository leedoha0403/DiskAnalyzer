using System.Windows;
using System.Windows.Media;

namespace DiskAnalyzer.App.Controls;

/// <summary>
/// 3/6. 행마다 표시하는 점유율 가로 Bar.
///
/// Rectangle + Grid 조합으로 만들면 행 하나당 Visual 이 3~4개 늘어난다.
/// 수십만 행을 스크롤하는 화면에서는 이 차이가 그대로 렌더링 비용이 되므로
/// FrameworkElement 하나가 직접 OnRender 로 두 개의 사각형만 그린다.
/// </summary>
public sealed class RatioBar : FrameworkElement
{
    public static readonly DependencyProperty RatioProperty = DependencyProperty.Register(
        nameof(Ratio), typeof(double), typeof(RatioBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(RatioBar),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightFillProperty = DependencyProperty.Register(
        nameof(HighlightFill), typeof(Brush), typeof(RatioBar),
        new FrameworkPropertyMetadata(Brushes.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(RatioBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>이 비율을 넘으면 강조색을 쓴다. 색만으로 정보를 주지 않기 위해 옆에는 항상 숫자를 함께 표시한다(17).</summary>
    public static readonly DependencyProperty HighlightThresholdProperty = DependencyProperty.Register(
        nameof(HighlightThreshold), typeof(double), typeof(RatioBar),
        new FrameworkPropertyMetadata(0.5d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Ratio { get => (double)GetValue(RatioProperty); set => SetValue(RatioProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush HighlightFill { get => (Brush)GetValue(HighlightFillProperty); set => SetValue(HighlightFillProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public double HighlightThreshold { get => (double)GetValue(HighlightThresholdProperty); set => SetValue(HighlightThresholdProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        double barHeight = Math.Min(10d, h - 6d);
        if (barHeight <= 1) barHeight = h;
        double y = (h - barHeight) / 2d;
        double radius = barHeight / 2d;

        dc.DrawRoundedRectangle(Track, null, new Rect(0, y, w, barHeight), radius, radius);

        double r = double.IsFinite(Ratio) ? Math.Clamp(Ratio, 0d, 1d) : 0d;
        if (r <= 0) return;

        // 아주 작은 비율도 "있다"는 것은 보이게 최소 2px 은 그린다.
        double fw = Math.Max(2d, w * r);
        var brush = r >= HighlightThreshold ? HighlightFill : Fill;
        dc.DrawRoundedRectangle(brush, null, new Rect(0, y, fw, barHeight), radius, radius);
    }
}

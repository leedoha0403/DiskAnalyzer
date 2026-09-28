using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DiskAnalyzer.App.Controls;

/// <summary>나타날 때 어떻게 들어오는가. <see cref="Motion.RevealProperty"/> 에 붙인다.</summary>
public enum RevealKind
{
    /// <summary>연출 없음(기본값). 붙이지 않은 것과 같다.</summary>
    None,

    /// <summary>제자리에서 흐리게 → 또렷하게.</summary>
    Fade,

    /// <summary>아래에서 살짝 올라오며. 아래쪽에 붙는 패널에 쓴다.</summary>
    Rise,

    /// <summary>위에서 살짝 내려오며. 머리 쪽에서 펼쳐지는 패널에 쓴다.</summary>
    Drop,

    /// <summary>오른쪽에서 밀려 들어오며. 오른쪽에 붙는 패널(설정 · 도크)에 쓴다.</summary>
    SlideLeft,

    /// <summary>왼쪽에서 밀려 들어오며.</summary>
    SlideRight,

    /// <summary>가운데에서 살짝 커지며. 그림(선버스트 · Treemap)처럼 화면을 가득 채우는 것에 쓴다.</summary>
    Grow,
}

/// <summary>
/// 50. 앱 전체의 <b>애니메이션 관문</b>.
///
/// <para>새로 넣는 연출은 하나도 빠짐없이 여기를 지난다 — 화면마다 <c>if (애니메이션 켜짐)</c> 를
/// 흩뿌리지 않기 위해서다. <see cref="Enabled"/> 가 꺼져 있으면 <see cref="To{T}"/> 는
/// 애니메이션을 만들지 않고 <b>끝값을 즉시 대입</b>한다. 그래서 부르는 쪽은 분기하지 않고,
/// 설정을 끈 화면도 "움직이지 않을 뿐" 결과 상태는 언제나 같다.</para>
///
/// <para>[왜 코드에 두는가] XAML <c>Storyboard</c> 의 <c>Duration</c> 은 Freeze 되어 바인딩이 걸리지
/// 않는다. 트리거로 깔아 두면 설정을 꺼도 계속 움직인다. 그래서 연출을 전부 이 관문을 통과하는
/// 첨부 속성 · 헬퍼로 만들었다.</para>
///
/// <para>[시스템 설정] Windows 의 '애니메이션 표시'(<see cref="SystemParameters.ClientAreaAnimation"/>)
/// 는 <b>첫 실행 기본값을 정할 때만</b> 읽는다(<see cref="SystemPrefersAnimation"/>).
/// 한 번 사용자가 정한 뒤로는 앱 설정이 이긴다.</para>
/// </summary>
public static class Motion
{
    /// <summary>지금 애니메이션을 쓰는가. 설정 화면의 체크박스 하나가 이 값을 정한다.</summary>
    public static bool Enabled { get; private set; } = true;

    /// <summary>켜고 끌 때. 이미 화면에 걸려 있는 연출을 정리해야 하는 쪽이 듣는다.</summary>
    public static event EventHandler? EnabledChanged;

    public static void Apply(bool enabled)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        EnabledChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// 이 PC 가 애니메이션을 원하는가. 사용자가 Windows 에서 '애니메이션 표시' 를 꺼 두었거나
    /// 소프트웨어 렌더링(원격 데스크톱 등)이면 거짓이다. <b>첫 실행 기본값을 정할 때만</b> 읽는다.
    /// </summary>
    public static bool SystemPrefersAnimation
        => SystemParameters.ClientAreaAnimation && RenderCapability.Tier > 0;

    // ---------------------------------------------------------------- 시간과 곡선

    /// <summary>110ms. 손끝에 붙어야 하는 것 — 호버, 강조.</summary>
    public static readonly Duration Quick = new(TimeSpan.FromMilliseconds(110));

    /// <summary>180ms. 패널이 뜨고 지는 기본 길이.</summary>
    public static readonly Duration Normal = new(TimeSpan.FromMilliseconds(180));

    /// <summary>260ms. 화면이 통째로 바뀌는 것 — 폴더로 들어가고 나오기.</summary>
    public static readonly Duration Slow = new(TimeSpan.FromMilliseconds(260));

    /// <summary>빠르게 나갔다가 부드럽게 멈춘다. 대부분의 연출이 이것이다.</summary>
    public static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    /// <summary>더 급하게 멈춘다. 줌처럼 거리가 큰 움직임에 쓴다.</summary>
    public static readonly IEasingFunction Land = Frozen(new QuarticEase { EasingMode = EasingMode.EaseOut });

    /// <summary>양끝이 모두 부드럽다. 폭이 늘고 주는 것처럼 되돌아오는 움직임에 쓴다.</summary>
    public static readonly IEasingFunction Smooth = Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut });

    private static IEasingFunction Frozen(EasingFunctionBase e)
    {
        e.Freeze();
        return e;
    }

    // ---------------------------------------------------------------- 기본 동작

    /// <summary>
    /// 값 하나를 <paramref name="to"/> 로 옮긴다. <b>꺼져 있으면 즉시 대입한다</b> —
    /// 부르는 쪽은 켜졌는지 묻지 않는다.
    /// </summary>
    public static void To<T>(T target, DependencyProperty property, double to,
                             Duration? duration = null, IEasingFunction? ease = null,
                             Action? completed = null)
        where T : DependencyObject, IAnimatable
    {
        if (!Enabled)
        {
            Settle(target, property, to);
            completed?.Invoke();
            return;
        }

        var animation = new DoubleAnimation(to, duration ?? Normal)
        {
            EasingFunction = ease ?? Ease,
            FillBehavior = FillBehavior.HoldEnd,
        };
        if (completed != null) animation.Completed += (_, _) => completed();
        target.BeginAnimation(property, animation);
    }

    /// <summary><paramref name="from"/> 에서 출발한다. 지금 값과 무관하게 매번 같은 곳에서 들어오는 연출에 쓴다.</summary>
    public static void From<T>(T target, DependencyProperty property, double from, double to,
                               Duration? duration = null, IEasingFunction? ease = null,
                               Action? completed = null)
        where T : DependencyObject, IAnimatable
    {
        if (!Enabled)
        {
            Settle(target, property, to);
            completed?.Invoke();
            return;
        }

        var animation = new DoubleAnimation(from, to, duration ?? Normal)
        {
            EasingFunction = ease ?? Ease,
            FillBehavior = FillBehavior.HoldEnd,
        };
        if (completed != null) animation.Completed += (_, _) => completed();
        target.BeginAnimation(property, animation);
    }

    /// <summary>
    /// 걸려 있던 애니메이션을 떼고 값을 못 박는다.
    /// <c>BeginAnimation(p, null)</c> 없이 <c>SetValue</c> 만 하면 애니메이션이 계속 이겨 값이 먹지 않는다.
    /// </summary>
    public static void Settle<T>(T target, DependencyProperty property, double value)
        where T : DependencyObject, IAnimatable
    {
        target.BeginAnimation(property, null);
        target.SetValue(property, value);
    }

    /// <summary>한 번 톡 튀었다가 제자리로. 수를 세는 글자처럼 "방금 바뀌었다"를 알릴 때 쓴다.</summary>
    public static void Bump(FrameworkElement element, double peak = 1.16, Point? origin = null)
    {
        if (!Enabled || element.ActualWidth <= 0 || !CanTransform(element)) return;

        var (scale, _) = EnsureTransform(element);
        element.RenderTransformOrigin = origin ?? new Point(0d, 0.5d);

        var frames = new DoubleAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromMilliseconds(260)) };
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromPercent(0.35), Ease));
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(1d, KeyTime.FromPercent(1d), Smooth));
        frames.FillBehavior = FillBehavior.Stop;

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, frames);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, frames);
    }

    // ---------------------------------------------------------------- Reveal: 나타날 때

    /// <summary>
    /// 이 요소가 보이게 될 때 어떻게 들어올지. <c>Visibility</c> 를 코드에서 바꾸든 바인딩으로 바꾸든
    /// 똑같이 걸린다 — <see cref="UIElement.IsVisibleChanged"/> 를 듣기 때문이다.
    /// </summary>
    public static readonly DependencyProperty RevealProperty = DependencyProperty.RegisterAttached(
        "Reveal", typeof(RevealKind), typeof(Motion),
        new PropertyMetadata(RevealKind.None, OnRevealChanged));

    public static void SetReveal(DependencyObject o, RevealKind value) => o.SetValue(RevealProperty, value);
    public static RevealKind GetReveal(DependencyObject o) => (RevealKind)o.GetValue(RevealProperty);

    private static void OnRevealChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not FrameworkElement element) return;

        element.IsVisibleChanged -= OnRevealVisibilityChanged;
        if ((RevealKind)e.NewValue != RevealKind.None)
            element.IsVisibleChanged += OnRevealVisibilityChanged;
    }

    private static void OnRevealVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        if (e.NewValue is not true)
        {
            // 숨을 때 원래 자리로 되돌려 둔다. 다음에 다시 나타날 때 출발점이 어긋나지 않는다.
            Rest(element);
            return;
        }

        Reveal(element, GetReveal(element));
    }

    /// <summary>
    /// 이 요소가 가만히 있을 때의 불투명도. XAML 에 <c>Opacity="0.97"</c> 처럼 적어 둔 값이 있으면
    /// 연출이 그것을 1 로 덮어써서는 안 된다 — <c>NaN</c> 은 "아직 안 읽어 봤다"는 뜻이고,
    /// 값을 한 번이라도 건드리기 전에 읽어 두므로 애니메이션 값을 원래 값으로 착각하지 않는다.
    /// </summary>
    private static readonly DependencyProperty RestOpacityProperty = DependencyProperty.RegisterAttached(
        "RestOpacity", typeof(double), typeof(Motion), new PropertyMetadata(double.NaN));

    private static double RestOpacity(FrameworkElement element)
    {
        double known = (double)element.GetValue(RestOpacityProperty);
        if (!double.IsNaN(known)) return known;

        double baseline = element.ReadLocalValue(UIElement.OpacityProperty) is double d ? d : 1d;
        element.SetValue(RestOpacityProperty, baseline);
        return baseline;
    }

    /// <summary>지금 당장 한 번 들어오는 연출을 재생한다. 목록에 새로 붙은 항목처럼 직접 부를 데가 있다.</summary>
    public static void Reveal(FrameworkElement element, RevealKind kind)
    {
        if (!Enabled || kind == RevealKind.None)
        {
            Rest(element);
            return;
        }

        From(element, UIElement.OpacityProperty, 0d, RestOpacity(element), Normal, Ease);

        // 사람이 XAML 에 걸어 둔 변환이 있으면 밀거나 키우지 않는다 — 말없이 덮어쓰면 화면이 어긋난다.
        if (kind == RevealKind.Fade || !CanTransform(element)) return;

        var (scale, translate) = EnsureTransform(element);

        // 10px. 더 크게 밀면 "화면이 흔들린다"가 되고, 더 작으면 방향이 읽히지 않는다.
        const double Shift = 10d;

        switch (kind)
        {
            case RevealKind.Grow:
                element.RenderTransformOrigin = new Point(0.5d, 0.5d);
                From(scale, ScaleTransform.ScaleXProperty, 0.97d, 1d, Normal, Land);
                From(scale, ScaleTransform.ScaleYProperty, 0.97d, 1d, Normal, Land);
                break;

            case RevealKind.Rise:
                From(translate, TranslateTransform.YProperty, Shift, 0d, Normal, Ease);
                break;

            case RevealKind.Drop:
                From(translate, TranslateTransform.YProperty, -Shift, 0d, Normal, Ease);
                break;

            case RevealKind.SlideLeft:
                From(translate, TranslateTransform.XProperty, Shift, 0d, Normal, Ease);
                break;

            case RevealKind.SlideRight:
                From(translate, TranslateTransform.XProperty, -Shift, 0d, Normal, Ease);
                break;
        }
    }

    /// <summary>연출을 전부 떼고 원래 모습으로 돌린다. 설정을 끄는 순간에도 이것으로 정리한다.</summary>
    public static void Rest(FrameworkElement element)
    {
        double baseline = RestOpacity(element);
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = baseline;

        if (element.RenderTransform is not TransformGroup group) return;
        foreach (var t in group.Children)
        {
            switch (t)
            {
                case ScaleTransform s:
                    Settle(s, ScaleTransform.ScaleXProperty, 1d);
                    Settle(s, ScaleTransform.ScaleYProperty, 1d);
                    break;
                case TranslateTransform tr:
                    Settle(tr, TranslateTransform.XProperty, 0d);
                    Settle(tr, TranslateTransform.YProperty, 0d);
                    break;
            }
        }
    }

    /// <summary>
    /// 이 요소가 쓸 크기 · 이동 변환을 준비한다. 이미 사람이 걸어 둔 <c>RenderTransform</c> 이 있으면
    /// 건드리지 않고 그 안에서 찾는다 — 남의 변환을 말없이 덮어쓰면 화면이 어긋난다.
    /// </summary>
    /// <summary>우리가 설치한 변환인가. 남이 걸어 둔 <c>RenderTransform</c> 을 덮어쓰지 않기 위한 표시다.</summary>
    private static readonly DependencyProperty OwnsTransformProperty = DependencyProperty.RegisterAttached(
        "OwnsTransform", typeof(bool), typeof(Motion), new PropertyMetadata(false));

    private static bool CanTransform(FrameworkElement element)
        => (bool)element.GetValue(OwnsTransformProperty) ||
           element.RenderTransform is null ||
           element.RenderTransform.Value.IsIdentity;

    private static (ScaleTransform Scale, TranslateTransform Translate) EnsureTransform(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup existing &&
            existing.Children.Count == 2 &&
            existing.Children[0] is ScaleTransform s &&
            existing.Children[1] is TranslateTransform t)
        {
            return (s, t);
        }

        var scale = new ScaleTransform(1d, 1d);
        var translate = new TranslateTransform(0d, 0d);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);
        element.RenderTransform = group;
        element.SetValue(OwnsTransformProperty, true);
        return (scale, translate);
    }

    // ---------------------------------------------------------------- 가리키면 떠오르는 카드

    /// <summary>
    /// 마우스를 올리면 이만큼(px) 떠오른다. 0 이면 붙이지 않은 것과 같다.
    ///
    /// <para>색만 바꾸는 호버는 카드가 여러 장 쌓인 사이드바에서 어느 것을 가리키고 있는지를
    /// 잘 말해 주지 못한다 — 한 장만 떠 있으면 형태로 읽힌다.</para>
    /// </summary>
    public static readonly DependencyProperty HoverLiftProperty = DependencyProperty.RegisterAttached(
        "HoverLift", typeof(double), typeof(Motion),
        new PropertyMetadata(0d, OnHoverLiftChanged));

    public static void SetHoverLift(DependencyObject o, double value) => o.SetValue(HoverLiftProperty, value);
    public static double GetHoverLift(DependencyObject o) => (double)o.GetValue(HoverLiftProperty);

    private static void OnHoverLiftChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not FrameworkElement element) return;

        element.MouseEnter -= OnHoverLiftEnter;
        element.MouseLeave -= OnHoverLiftLeave;
        if ((double)e.NewValue <= 0d) return;

        element.MouseEnter += OnHoverLiftEnter;
        element.MouseLeave += OnHoverLiftLeave;
    }

    private static void OnHoverLiftEnter(object sender, MouseEventArgs e) => LiftTo(sender, true);
    private static void OnHoverLiftLeave(object sender, MouseEventArgs e) => LiftTo(sender, false);

    private static void LiftTo(object sender, bool up)
    {
        if (sender is not FrameworkElement element || !CanTransform(element)) return;

        var (_, translate) = EnsureTransform(element);

        // 꺼져 있으면 아예 들지 않는다. 시간만 0 이 되어 2px 이 툭 튀면 그게 더 이상하다.
        double to = Enabled && up ? -GetHoverLift(element) : 0d;
        To(translate, TranslateTransform.YProperty, to, Quick, Ease);
    }

    // ---------------------------------------------------------------- 숫자가 흐르는 막대

    /// <summary>
    /// <see cref="RangeBase.Value"/> 를 곧바로 꽂지 않고 흘려 보낸다.
    /// 스캔 진행률은 150ms 마다 갱신되어 그대로 쓰면 눈금이 툭툭 튄다.
    ///
    /// <para>XAML 에서 <c>Value="{Binding ...}"</c> 대신 <c>ctl:Motion.Progress="{Binding ...}"</c> 로 쓴다.</para>
    /// </summary>
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.RegisterAttached(
        "Progress", typeof(double), typeof(Motion),
        new PropertyMetadata(0d, OnProgressChanged));

    public static void SetProgress(DependencyObject o, double value) => o.SetValue(ProgressProperty, value);
    public static double GetProgress(DependencyObject o) => (double)o.GetValue(ProgressProperty);

    private static void OnProgressChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not RangeBase bar) return;

        double to = (double)e.NewValue;
        if (!double.IsFinite(to)) return;

        // 처음 값이거나 뒤로 크게 튀면(새 스캔) 흘리지 않고 바로 놓는다.
        // 되감기는 애니메이션은 "줄어들고 있다"는 거짓말이 된다.
        double from = (double)e.OldValue;
        if (to < from - 1d)
        {
            Settle(bar, RangeBase.ValueProperty, to);
            return;
        }

        To(bar, RangeBase.ValueProperty, to, Normal, Smooth);
    }

    // ---------------------------------------------------------------- 칸 폭

    /// <summary>
    /// <see cref="ColumnDefinition.Width"/> 는 <c>GridLength</c> 라 기본 애니메이션이 없다.
    /// 그래서 px 하나를 애니메이션하고, 그 값이 바뀔 때마다 칸 폭에 옮겨 담는다.
    /// </summary>
    private static readonly DependencyProperty PixelWidthProperty = DependencyProperty.RegisterAttached(
        "PixelWidth", typeof(double), typeof(Motion),
        new PropertyMetadata(0d, OnPixelWidthChanged));

    private static void OnPixelWidthChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is ColumnDefinition column) column.Width = new GridLength(Math.Max(0d, (double)e.NewValue));
    }

    /// <summary>
    /// 흘리지 않고 칸 폭을 지금 못 박는다. 걸려 있던 폭 애니메이션도 함께 걷는다 —
    /// 이것 없이 <c>Width</c> 만 대입하면 진행 중인 애니메이션이 다음 프레임에 다시 이긴다.
    /// </summary>
    public static void WidthNow(ColumnDefinition column, double px)
    {
        column.BeginAnimation(PixelWidthProperty, null);
        column.Width = new GridLength(Math.Max(0d, px));
    }

    /// <summary>
    /// 칸을 <paramref name="to"/> px 로 접거나 편다. 접힌 뒤에는 <c>Width</c> 를 값으로 못 박아
    /// 이후 <c>GridLength.Auto</c> · 별(star) 로 되돌리려는 쪽과 다투지 않는다.
    /// </summary>
    public static void Width(ColumnDefinition column, double to, Action? completed = null)
    {
        double from = column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth;

        if (!Enabled || Math.Abs(from - to) < 0.5d)
        {
            WidthNow(column, to);
            completed?.Invoke();
            return;
        }

        column.SetValue(PixelWidthProperty, from);
        From(column, PixelWidthProperty, from, to, Normal, Smooth, completed);
    }
}

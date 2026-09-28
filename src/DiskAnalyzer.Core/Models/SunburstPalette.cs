namespace DiskAnalyzer.Core.Models;

/// <summary>선버스트 한 조각이 무엇인가. 색과 상호작용이 여기서 갈린다.</summary>
public enum SunburstKind : byte
{
    /// <summary>폴더. 눌러서 들어갈 수 있다.</summary>
    Directory,

    /// <summary>파일. 회색으로 그린다.</summary>
    File,

    /// <summary>따로 보이기엔 너무 얇아 하나로 합친 묶음. 반투명이며 삭제 대상이 될 수 없다.</summary>
    Smaller,

    /// <summary>드라이브 사용량과 스캔 합계의 차이(권한 없는 폴더 · 파일시스템 오버헤드).</summary>
    Hidden,

    /// <summary>남은 공간. 링에는 그리지 않고 목록에만 둔다.</summary>
    Free,
}

/// <summary>
/// 링의 색을 무엇으로 정할지. 그림이 답하는 질문 자체가 바뀐다.
/// </summary>
public enum SunburstTint
{
    /// <summary>기본. 색 = 위치. "무엇이 큰가"를 본다.</summary>
    Size,

    /// <summary>정리 추천 점수. "무엇을 지워도 되는가"를 본다.</summary>
    Cleanup,

    /// <summary>이전 스캔 대비 증감. "무엇이 늘었는가"를 본다.</summary>
    Delta,
}

/// <summary>
/// 선버스트 색.
///
/// <para>DaisyDisk 의 색 모델을 그대로 쓴다 — <b>색은 카테고리도 깊이도 아니고 위치에서 나온다.</b>
/// 제품 스크린샷을 극좌표로 샘플링해 얻은 규칙은 <c>hue = 4° + 0.75 × 각도</c> 이고,
/// 세 개의 겹에 걸친 표본 8개가 모두 최대 오차 6° 안에서 맞았다(<c>docs/daisydisk.html</c> 3.2).</para>
///
/// <para>이 한 줄이 주는 것:</para>
/// <list type="bullet">
/// <item>자식은 부모의 각도 범위 안에 있으므로 <b>색 계열이 저절로 상속된다</b>. 상속 로직이 필요 없다.</item>
/// <item>각도가 다르면 색이 다르므로 <b>이웃한 조각이 같은 색이 되는 일이 구조적으로 없다</b>.</item>
/// <item>목록의 색 점은 같은 함수에 같은 각도를 넣으면 되므로 <b>범례를 따로 관리하지 않는다</b>.</item>
/// </list>
/// </summary>
public static class SunburstPalette
{
    /// <summary>
    /// 지금 캔버스가 밝은 배경인가. App 쪽에서 테마를 바꿀 때 갱신한다.
    ///
    /// <para>File · Free 처럼 "실체가 아니라 뒤로 물러나 보여야 하는" 색은 DaisyDisk 의 짙은 남색
    /// 캔버스를 기준으로 어둡게 잡았다 - 흰 배경(Mint · Light)에서는 그 어두운 청회색이 반대로
    /// 튀어 보인다("갑자기 진한 회색이 있다"). 캔버스가 밝으면 같은 색조를 옅게 뒤집어 똑같이
    /// 물러나 보이게 한다.</para>
    /// </summary>
    public static bool LightCanvas { get; set; }

    /// <summary>12시 방향(0°)에서 시계 방향으로 잰 각도에 대응하는 색조. 한 바퀴에 0° → 270° 를 훑는다.</summary>
    public static double HueAt(double angleDegrees)
    {
        double h = (4d + 0.75d * angleDegrees) % 360d;
        return h < 0 ? h + 360d : h;
    }

    /// <summary>
    /// 색조마다의 채도 · 밝기. 제품 스크린샷에서 잰 값이고 사이는 선형 보간한다.
    ///
    /// <para>처음에는 채도 · 밝기를 상수로 뒀는데 색감이 눈에 띄게 달랐다. 실제로 재 보니
    /// <b>밝기는 66~73%로 거의 일정한데 채도는 색조를 따라 57% → 96% 로 오른다</b> —
    /// 주황·연두 쪽은 묽고 파랑·보라 쪽은 진하다. 상수로 두면 따뜻한 쪽이 과하게 쨍하고
    /// 차가운 쪽이 탁해진다. 사람 눈이 색조마다 채도를 다르게 느끼는 것을 보정한 값으로 보인다.</para>
    /// </summary>
    private static readonly (double Hue, double Saturation, double Lightness)[] Tones =
    {
        (27.9, 0.573, 0.706),    // #dfb189
        (73.8, 0.667, 0.718),    // #d1e787
        (91.6, 0.655, 0.727),    // #b7e78c
        (129.4, 0.694, 0.692),   // #7ae78b
        (158.8, 0.721, 0.676),   // #71e8be
        (172.1, 0.737, 0.657),   // #67e8d7
        (188.3, 0.945, 0.716),   // #72e8fb
        (216.5, 0.925, 0.686),   // #659ff9
        (244.3, 0.958, 0.718),   // #7c72fc
        (270.4, 0.958, 0.718),   // #b872fc
    };

    /// <summary>표에서 색조에 해당하는 채도 · 밝기를 찾는다. 양 끝 바깥은 끝값을 그대로 쓴다.</summary>
    private static (double S, double L) ToneAt(double hue)
    {
        if (hue <= Tones[0].Hue) return (Tones[0].Saturation, Tones[0].Lightness);
        if (hue >= Tones[^1].Hue) return (Tones[^1].Saturation, Tones[^1].Lightness);

        for (int i = 1; i < Tones.Length; i++)
        {
            if (hue > Tones[i].Hue) continue;

            var (h0, s0, l0) = Tones[i - 1];
            var (h1, s1, l1) = Tones[i];
            double t = (hue - h0) / (h1 - h0);
            return (s0 + (s1 - s0) * t, l0 + (l1 - l0) * t);
        }

        return (Tones[^1].Saturation, Tones[^1].Lightness);
    }

    /// <summary>
    /// 파일 꽃잎 색. 무채색이 아니라 <b>배경 쪽으로 기운 청회색</b>이고, 색 있는 꽃잎보다 확실히 어둡다
    /// (실측 H221 S11~17% L40~50%). 그래서 파일이 뒤로 물러나고 폴더가 앞에 선다.
    /// </summary>
    private const double FileHue = 221d;

    /// <summary>조각의 채움 색. <paramref name="midAngle"/> 은 조각의 각도 중심이다.</summary>
    public static SunburstColor Fill(SunburstKind kind, double midAngle)
    {
        if (kind == SunburstKind.Hidden) return FromHsl(28d, 0.85, 0.66, 255);
        if (kind == SunburstKind.Free) return LightCanvas ? FromHsl(222d, 0.12, 0.87, 255) : FromHsl(222d, 0.20, 0.45, 255);

        if (kind == SunburstKind.File)
        {
            // 고정된 청회색은 무지개 사이에서 홀로 이질적인 "검은 얼룩"처럼 보였다(흰 배경일수록
            // 두드러졌다). 흰 배경에서는 파일도 부모와 같은 색조 계열을 따르되 채도를 크게 죽이고
            // 아주 밝게 둔다 - 가문은 같지만 옅어서 물러나 보인다. 어두운 캔버스는 원래 값을 유지한다.
            if (LightCanvas)
            {
                double fileHue = HueAt(midAngle);
                var (fs, _) = ToneAt(fileHue);
                return FromHsl(fileHue, fs * 0.30d, 0.90d, 255);
            }
            return FromHsl(FileHue, 0.14, 0.45, 255);
        }

        double hue = HueAt(midAngle);
        var (s, l) = ToneAt(hue);

        // DaisyDisk 의 짙은 남색 캔버스 위에서 잰 채도 · 밝기다 - 흰 배경은 동시대비 때문에 같은 색이
        // 오히려 더 탁하게 보인다(채도를 죽이면 칙칙해지기만 했다). 채도는 그대로 두고 밝기만 올려
        // 흰 종이 위에서도 산뜻하게 뜨도록 한다. 색조 순서 · 상속 구조는 그대로다.
        if (LightCanvas) l = Math.Min(0.86d, l + 0.07d);

        // 반투명 = "이건 실체가 아니라 묶음이다". 채도를 낮춰 색 있는 꽃잎과 더 확실히 갈라 둔다.
        return kind == SunburstKind.Smaller
            ? FromHsl(hue, s * 0.45, l, 102)
            : FromHsl(hue, s, l, 255);
    }

    /// <summary>
    /// 정리 추천 점수(0~100) 색. 후보가 아닌 것은 짙은 회색으로 눕혀 후보만 떠오르게 한다 —
    /// 여기서는 "큰 것"이 아니라 "지워도 되는 것"이 눈에 먼저 들어와야 한다.
    /// </summary>
    public static SunburstColor CleanupFill(int score, bool isCandidate)
    {
        if (!isCandidate) return LightCanvas ? FromHsl(220d, 0.06, 0.88, 255) : FromHsl(220d, 0.08, 0.34, 255);

        // 30점(참고) 초록 → 100점(우선 정리) 빨강. 점수 구간과 같은 축이다.
        double t = Math.Clamp((score - 30) / 70d, 0d, 1d);
        return FromHsl(140d - 140d * t, 0.62, 0.58, 255);
    }

    /// <summary>
    /// 이전 스캔 대비 증감 색. 기준은 "그 조각 자신의 이전 크기" 다 —
    /// 절대 바이트로 칠하면 큰 폴더만 빨개져서 증감 그림이 크기 그림과 똑같아진다.
    /// </summary>
    /// <param name="delta">현재 − 이전(바이트). 이전 기록이 없으면 <paramref name="known"/> 가 false 다.</param>
    public static SunburstColor DeltaFill(long delta, long previous, bool known)
    {
        if (!known) return LightCanvas ? FromHsl(280d, 0.22, 0.80, 255) : FromHsl(280d, 0.30, 0.52, 255);          // 이전 스캔에 없던 것 = 새로 생김
        if (delta == 0) return LightCanvas ? FromHsl(220d, 0.06, 0.88, 255) : FromHsl(220d, 0.08, 0.36, 255);      // 그대로

        double baseline = Math.Max(previous, 64L * 1024 * 1024);    // 작은 파일의 배율 폭주를 막는다
        double t = Math.Clamp(Math.Abs(delta) / baseline, 0d, 1d);

        return delta > 0
            ? FromHsl(8d, 0.40 + 0.42 * t, 0.62 - 0.10 * t, 255)    // 늘었다 = 붉게
            : FromHsl(190d, 0.40 + 0.42 * t, 0.62 - 0.10 * t, 255); // 줄었다 = 푸르게
    }

    /// <summary>호버 강조용. 같은 색을 조금 밝게 올린다.</summary>
    public static SunburstColor Highlight(SunburstColor c) => new(
        Lift(c.R), Lift(c.G), Lift(c.B), c.A);

    private static byte Lift(byte v) => (byte)Math.Min(255, v + (255 - v) * 0.34 + 10);

    /// <summary>HSL(0~360, 0~1, 0~1) → sRGB.</summary>
    public static SunburstColor FromHsl(double h, double s, double l, byte a)
    {
        h = ((h % 360d) + 360d) % 360d;
        double c = (1d - Math.Abs(2d * l - 1d)) * s;
        double x = c * (1d - Math.Abs(h / 60d % 2d - 1d));
        double m = l - c / 2d;

        (double r, double g, double b) = h switch
        {
            < 60d => (c, x, 0d),
            < 120d => (x, c, 0d),
            < 180d => (0d, c, x),
            < 240d => (0d, x, c),
            < 300d => (x, 0d, c),
            _ => (c, 0d, x),
        };

        return new SunburstColor(Byte(r + m), Byte(g + m), Byte(b + m), a);
    }

    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v * 255d), 0d, 255d);
}

/// <summary>UI 프레임워크에 묶이지 않은 색 값. App 쪽에서 <c>Color</c> 로 옮긴다.</summary>
public readonly record struct SunburstColor(byte R, byte G, byte B, byte A)
{
    public string ToHex() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

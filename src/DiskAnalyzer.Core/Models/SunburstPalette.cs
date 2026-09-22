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
    /// <summary>12시 방향(0°)에서 시계 방향으로 잰 각도에 대응하는 색조. 한 바퀴에 0° → 270° 를 훑는다.</summary>
    public static double HueAt(double angleDegrees)
    {
        double h = (4d + 0.75d * angleDegrees) % 360d;
        return h < 0 ? h + 360d : h;
    }

    /// <summary>조각의 채움 색. <paramref name="midAngle"/> 은 조각의 각도 중심이다.</summary>
    public static SunburstColor Fill(SunburstKind kind, double midAngle) => kind switch
    {
        // 파일은 회색이되 계열의 흔적만 남긴다 - 완전 무채색이면 어느 폴더에 속했는지 읽히지 않는다.
        SunburstKind.File => FromHsl(HueAt(midAngle), 0.08, 0.62, 255),
        SunburstKind.Smaller => FromHsl(HueAt(midAngle), 0.30, 0.62, 102),   // 반투명 = "이건 묶음이다"
        SunburstKind.Hidden => FromHsl(28d, 0.85, 0.66, 255),
        SunburstKind.Free => FromHsl(222d, 0.20, 0.45, 255),
        _ => FromHsl(HueAt(midAngle), 0.72, 0.68, 255),
    };

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

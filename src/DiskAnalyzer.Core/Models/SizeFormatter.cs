using System.Globalization;

namespace DiskAnalyzer.Core.Models;

public enum SizeUnitMode { Auto, KB, MB, GB, TB }

/// <summary>18. 숫자 표현 - 자동 단위 변환 + 천 단위 구분.</summary>
public static class SizeFormatter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>47. 설정의 Size Unit. 전역 기본 단위이며 개별 호출에서 덮어쓸 수 있다.</summary>
    public static SizeUnitMode DefaultMode { get; set; } = SizeUnitMode.Auto;

    public static string Format(long bytes) => Format(bytes, DefaultMode);

    public static string Format(long bytes, SizeUnitMode mode)
    {
        if (bytes < 0) return "-";
        if (bytes == 0) return "0 B";

        int unit;
        double value;

        if (mode == SizeUnitMode.Auto)
        {
            unit = 0;
            value = bytes;
            while (value >= 1024d && unit < Units.Length - 1) { value /= 1024d; unit++; }
        }
        else
        {
            unit = mode switch
            {
                SizeUnitMode.KB => 1,
                SizeUnitMode.MB => 2,
                SizeUnitMode.GB => 3,
                SizeUnitMode.TB => 4,
                _ => 0,
            };
            value = bytes / Math.Pow(1024d, unit);
        }

        // 742 MB / 12.8 GB / 128.4 GB / 1.28 TB 형태를 재현하는 규칙
        int decimals = value < 10 ? 2 : (value < 100 ? 1 : (unit >= 3 ? 1 : 0));
        if (unit == 0) decimals = 0;

        return value.ToString("N" + decimals, CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    public static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    public static string Percent(double ratio)
        => (ratio * 100d).ToString(ratio >= 0.1 ? "N0" : "N1", CultureInfo.InvariantCulture) + "%";

    public static string Rate(double perSecond)
        => perSecond >= 1000
            ? perSecond.ToString("N0", CultureInfo.InvariantCulture)
            : perSecond.ToString("N1", CultureInfo.InvariantCulture);
}

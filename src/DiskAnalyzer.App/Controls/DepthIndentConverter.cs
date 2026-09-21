using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DiskAnalyzer.App.Controls;

/// <summary>
/// 49. 깊이(int) -> 왼쪽 여백. 검색 결과를 같은 목록 안에서 트리처럼 들여쓰기 위해 쓴다.
/// 묶지 않은 목록의 행은 Depth 가 0 이라 여백이 0 이고, 지금까지의 화면과 완전히 같다.
/// </summary>
public sealed class DepthIndentConverter : IValueConverter
{
    private const double PerLevel = 16d;
    private const int MaxLevels = 12;      // 아주 깊은 경로에서 이름이 오른쪽으로 밀려 사라지는 것을 막는다

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => new Thickness(Math.Clamp(value as int? ?? 0, 0, MaxLevels) * PerLevel, 0, 0, 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

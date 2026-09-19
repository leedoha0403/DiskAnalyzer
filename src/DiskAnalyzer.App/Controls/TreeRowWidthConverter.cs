using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DiskAnalyzer.App.Controls;

/// <summary>
/// TreeView 의 행 너비 = 트리 너비 - (깊이 × 들여쓰기) - 여백.
///
/// TreeViewItem 의 머리글은 내용 크기만큼만 자라서, 그냥 두면 깊은 행일수록 오른쪽 열(크기/등급/분류)이
/// 들여쓰기만큼 밀려 세로로 정렬되지 않는다. 모든 행의 오른쪽 끝을 트리 오른쪽에 맞추면 열이 정렬된다.
/// values: [0] 트리 ActualWidth, [1] 노드 깊이
/// </summary>
public sealed class TreeRowWidthConverter : IMultiValueConverter
{
    private const double IndentPerLevel = 19d;    // 기본 TreeViewItem 템플릿의 확장 화살표 폭
    private const double Chrome = 58d;            // 트리 Padding + 세로 스크롤바 + 화살표/여백
    private const double MinWidth = 640d;         // 이보다 좁아지면 가로 스크롤로 넘긴다

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double width || values[1] is not int depth || double.IsNaN(width))
            return DependencyProperty.UnsetValue;
        return Math.Max(MinWidth, width - Chrome - depth * IndentPerLevel);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

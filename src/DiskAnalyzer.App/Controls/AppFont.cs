using System.Windows.Media;

namespace DiskAnalyzer.App.Controls;

/// <summary>
/// Canvas 에 직접 <c>DrawText</c> 하는 곳(Treemap · 선버스트)은 XAML 의 <c>FontFamily</c> 상속을 타지 않는다 -
/// 여기서 같은 내장 폰트(Spoqa Han Sans Neo)를 코드로 한 번 만들어 공유한다.
/// </summary>
internal static class AppFont
{
    public static readonly FontFamily Family = new(
        new Uri("pack://application:,,,/"),
        "./Fonts/Regular/#Spoqa Han Sans Neo, Segoe UI, Malgun Gothic");
}

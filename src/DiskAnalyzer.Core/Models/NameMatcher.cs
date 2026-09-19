namespace DiskAnalyzer.Core.Models;

/// <summary>
/// 12. 검색어 매처.
/// 와일드카드(`*`, `?`)가 없으면 부분 일치, 있으면 이름 전체에 대한 glob 매칭이다(대소문자 무시).
///   "log"      -> catalog.txt, app.log 모두 일치 (부분 일치)
///   "*.log"    -> app.log 만 일치 (이름 전체가 패턴과 맞아야 함)
///   "log_??.*" -> log_01.txt 일치
/// Windows 탐색기 검색 습관과 같게 하려는 의도이며, 스팬만 다루므로 파일 수백만 개를 훑어도 할당이 없다.
/// </summary>
internal readonly struct NameMatcher
{
    private readonly string _pattern;
    private readonly bool _wildcard;

    public NameMatcher(string term)
    {
        _pattern = term.Trim();
        _wildcard = _pattern.AsSpan().IndexOfAny('*', '?') >= 0;
    }

    public bool IsEmpty => _pattern.Length == 0;

    public bool IsMatch(ReadOnlySpan<char> name)
        => _wildcard
            ? Glob(_pattern, name)
            : name.IndexOf(_pattern.AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool Glob(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        int p = 0, t = 0, star = -1, mark = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                // 직전 '*' 가 글자 하나를 더 삼키도록 되돌아간다.
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static bool Same(char a, char b)
        => a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}

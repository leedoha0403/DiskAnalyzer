namespace DiskAnalyzer.Core.Models;

/// <summary>
/// 48. 스캔 제외 규칙. 사용자가 적은 패턴 목록을 스캔 중에 값싸게 물어볼 수 있는 형태로 미리 컴파일해 둔다.
///
/// 패턴 한 줄이 셋 중 하나로 분류된다.
///  · 확장자  `*.tmp`                   → 파일만 제외
///  · 경로    `C:\Windows\Temp` / `obj\Debug` → 폴더만 제외(하위 전체를 읽지 않는다)
///  · 이름    `node_modules` / `~$*`    → 파일 · 폴더 이름이 맞으면 제외(깊이 무관)
///  · `.git` 처럼 `*` 없이 점으로 시작하면 둘 다 — `.git` 폴더도, 확장자가 `.git` 인 파일도 뺀다
/// `#` 로 시작하는 줄과 빈 줄은 주석이다. 모든 비교는 대소문자를 구분하지 않는다.
///
/// 경로 규칙을 폴더에만 적용하는 이유: 파일마다 전체 경로 문자열을 만들면 그것만으로 수십 초가 날아간다
/// (보호 판정이 2단 구조를 쓰는 것과 같은 이유). 파일은 확장자 / 이름 규칙으로 거른다.
/// </summary>
public sealed class ExclusionRules
{
    /// <summary>경로 규칙 하나. 세그먼트별 글롭이라 `C:\Users\*\AppData` 같은 것도 쓸 수 있다.</summary>
    private readonly record struct PathRule(Glob[] Segments, bool Rooted);

    private readonly string[] _extensions;      // ".tmp" 형태, 소문자
    private readonly Glob[] _names;
    private readonly PathRule[] _paths;
    private readonly Glob[] _pathTails;         // 경로 규칙의 마지막 세그먼트 — 값싼 1차 게이트

    public static ExclusionRules Empty { get; } = new(
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<Glob>(), Array.Empty<PathRule>(), Array.Empty<Glob>());

    private ExclusionRules(IReadOnlyList<string> patterns, string[] extensions, Glob[] names, PathRule[] paths, Glob[] pathTails)
    {
        Patterns = patterns;
        _extensions = extensions;
        _names = names;
        _paths = paths;
        _pathTails = pathTails;
    }

    /// <summary>정규화 후 살아남은 패턴(설정 화면에 다시 보여 주는 값).</summary>
    public IReadOnlyList<string> Patterns { get; }

    public bool IsEmpty => _extensions.Length == 0 && _names.Length == 0 && _paths.Length == 0;

    /// <summary>경로 규칙이 하나도 없으면 스캐너가 경로를 만들 준비조차 하지 않는다.</summary>
    public bool HasPathRules => _paths.Length > 0;

    public static ExclusionRules Parse(IEnumerable<string>? patterns)
    {
        if (patterns == null) return Empty;

        var kept = new List<string>();
        var extensions = new List<string>();
        var names = new List<Glob>();
        var paths = new List<PathRule>();
        var tails = new List<Glob>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in patterns)
        {
            string p = (raw ?? string.Empty).Trim().Trim('"');
            if (p.Length == 0 || p[0] == '#') continue;
            p = p.Replace('/', '\\');
            if (!seen.Add(p)) continue;

            if (p.IndexOf('\\') >= 0)
            {
                var segments = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0) continue;

                // "C:" 로 시작하면 드라이브 루트 기준, 아니면 경로 어디에 나타나도 되는 조각이다.
                bool rooted = segments[0].Length == 2 && segments[0][1] == ':';
                var globs = Array.ConvertAll(segments, s => new Glob(s));
                paths.Add(new PathRule(globs, rooted));
                tails.Add(globs[^1]);
            }
            else if (IsExtensionPattern(p, out string ext, out bool bare))
            {
                extensions.Add(ext);

                // `.git` / `.vs` 처럼 확장자로도 폴더 이름으로도 읽히는 것은 둘 다로 친다.
                // `*.tmp` 라고 명시했으면 파일 확장자로만 본다.
                if (bare) names.Add(new Glob(p));
            }
            else
            {
                names.Add(new Glob(p));
            }

            kept.Add(p);
        }

        if (kept.Count == 0) return Empty;
        return new ExclusionRules(kept, extensions.ToArray(), names.ToArray(), paths.ToArray(), tails.ToArray());
    }

    /// <summary>
    /// `*.tmp` / `.tmp` 만 확장자 규칙이다. `log*.txt` 처럼 앞에 글자가 붙으면 이름 규칙으로 넘긴다.
    /// <paramref name="bare"/> 는 `*` 없이 `.git` 처럼 적은 경우 — 폴더 이름으로도 함께 취급한다.
    /// </summary>
    private static bool IsExtensionPattern(string p, out string ext, out bool bare)
    {
        ext = string.Empty;
        bare = p[0] == '.';

        string rest = p.StartsWith("*.", StringComparison.Ordinal) ? p[1..]
            : bare ? p
            : string.Empty;

        if (rest.Length < 2) return false;
        if (rest.IndexOf('*') >= 0 || rest.IndexOf('?') >= 0) return false;
        if (rest.AsSpan(1).IndexOf('.') >= 0) return false;

        ext = rest.ToLowerInvariant();
        return true;
    }

    /// <summary>파일 하나를 제외하는가. 스캔의 가장 뜨거운 경로라 문자열을 만들지 않는다.</summary>
    public bool ExcludesFile(ReadOnlySpan<char> name)
    {
        if (_extensions.Length > 0)
        {
            int dot = name.LastIndexOf('.');
            if (dot >= 0)
            {
                var ext = name[dot..];
                foreach (string e in _extensions)
                    if (ext.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return MatchesName(name);
    }

    /// <summary>폴더 이름만으로 판정한다. 경로 규칙은 <see cref="ExcludesPath"/> 가 따로 본다.</summary>
    public bool ExcludesDirectoryName(ReadOnlySpan<char> name) => MatchesName(name);

    private bool MatchesName(ReadOnlySpan<char> name)
    {
        foreach (var g in _names)
            if (g.IsMatch(name)) return true;
        return false;
    }

    /// <summary>
    /// 이 폴더 이름이 어떤 경로 규칙의 마지막 세그먼트가 될 수 있는가.
    /// 여기서 걸린 폴더에 대해서만 전체 경로를 만들어 <see cref="ExcludesPath"/> 로 정확히 판정한다.
    /// </summary>
    public bool MayEndPathRule(ReadOnlySpan<char> name)
    {
        foreach (var g in _pathTails)
            if (g.IsMatch(name)) return true;
        return false;
    }

    /// <summary>폴더의 전체 경로가 경로 규칙에 걸리는가.</summary>
    public bool ExcludesPath(string fullPath)
    {
        if (_paths.Length == 0 || string.IsNullOrEmpty(fullPath)) return false;

        var segments = fullPath.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;

        foreach (var rule in _paths)
        {
            var r = rule.Segments;
            if (r.Length > segments.Length) continue;

            if (rule.Rooted)
            {
                if (MatchesAt(segments, 0, r)) return true;
            }
            else
            {
                // 조각 규칙은 경로 어디에 나타나도 걸린다. 스캔 중에는 상위에서 이미 가지치기되므로
                // 실제로 걸리는 것은 규칙의 끝 세그먼트인 폴더 자신이고, 임의 경로를 직접 물어볼 때도 답이 맞는다.
                for (int start = 0; start + r.Length <= segments.Length; start++)
                    if (MatchesAt(segments, start, r)) return true;
            }
        }
        return false;
    }

    private static bool MatchesAt(string[] segments, int start, Glob[] rule)
    {
        for (int i = 0; i < rule.Length; i++)
            if (!rule[i].IsMatch(segments[start + i])) return false;
        return true;
    }

    /// <summary>`*` / `?` 만 지원하는 대소문자 무시 글롭. 와일드카드가 없으면 그냥 문자열 비교로 끝낸다.</summary>
    internal readonly struct Glob
    {
        private readonly string _pattern;
        private readonly bool _literal;

        public Glob(string pattern)
        {
            _pattern = pattern;
            _literal = pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0;
        }

        public bool IsMatch(ReadOnlySpan<char> text)
            => _literal
                ? text.Equals(_pattern, StringComparison.OrdinalIgnoreCase)
                : Wildcard(_pattern.AsSpan(), text);

        private static bool Wildcard(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
        {
            int p = 0, t = 0, star = -1, mark = 0;
            while (t < text.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], text[t]))) { p++; t++; }
                else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
                else if (star >= 0) { p = star + 1; t = ++mark; }
                else return false;
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }

        private static bool Same(char a, char b)
            => a == b || char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
    }
}

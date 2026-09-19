using System.Globalization;
using System.Text;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Services;

/// <summary>48. 향후 확장 기능 - CSV / JSON Export.</summary>
public static class ExportService
{
    public static void ExportCsv(string path, IEnumerable<EntryRow> rows)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine("종류,이름,전체 경로,크기(바이트),크기,점유율,파일 수,폴더 수,확장자,수정일,카테고리");
        foreach (var r in rows)
        {
            w.WriteLine(string.Join(',',
                Q(r.IsDirectory ? "폴더" : "파일"),
                Q(r.Name),
                Q(r.FullPath),
                r.Size.ToString(CultureInfo.InvariantCulture),
                Q(r.SizeText),
                Q(r.RatioText),
                r.IsDirectory ? r.FileCount.ToString(CultureInfo.InvariantCulture) : string.Empty,
                r.IsDirectory ? r.DirectoryCount.ToString(CultureInfo.InvariantCulture) : string.Empty,
                Q(r.Extension),
                Q(r.ModifiedText),
                Q(r.CategoryText)));
        }
    }

    public static void ExportExtensionsCsv(string path, IEnumerable<ExtensionRow> rows)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine("확장자,총 용량(바이트),총 용량,파일 개수,비율,카테고리");
        foreach (var r in rows)
        {
            w.WriteLine(string.Join(',',
                Q(r.Extension),
                r.Size.ToString(CultureInfo.InvariantCulture),
                Q(r.SizeText),
                r.Count.ToString(CultureInfo.InvariantCulture),
                Q(r.RatioText),
                Q(r.CategoryText)));
        }
    }

    public static void ExportJson(string path, IEnumerable<EntryRow> rows)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        w.WriteLine("[");
        bool first = true;
        foreach (var r in rows)
        {
            if (!first) w.WriteLine(",");
            first = false;
            w.Write("  {");
            w.Write($"\"kind\":\"{(r.IsDirectory ? "dir" : "file")}\",");
            w.Write($"\"name\":{J(r.Name)},");
            w.Write($"\"path\":{J(r.FullPath)},");
            w.Write($"\"size\":{r.Size},");
            w.Write($"\"ratio\":{r.Ratio.ToString("0.#####", CultureInfo.InvariantCulture)},");
            w.Write($"\"files\":{r.FileCount},");
            w.Write($"\"folders\":{r.DirectoryCount},");
            w.Write($"\"ext\":{J(r.Extension)},");
            w.Write($"\"modified\":{J(r.ModifiedText)},");
            w.Write($"\"category\":{J(r.CategoryText)}");
            w.Write("}");
        }
        w.WriteLine();
        w.WriteLine("]");
    }

    private static string Q(string? s)
    {
        s ??= string.Empty;
        return s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }

    private static string J(string? s)
    {
        s ??= string.Empty;
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}

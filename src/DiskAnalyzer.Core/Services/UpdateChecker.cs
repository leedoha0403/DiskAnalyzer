using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace DiskAnalyzer.Core.Services;

public sealed record UpdateAsset(string Name, string BrowserDownloadUrl, long Size);

public sealed record UpdateRelease(string TagName, string HtmlUrl, IReadOnlyList<UpdateAsset> Assets)
{
    public UpdateAsset? FindZip() => Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    /// <summary>제자리 교체용 단일 exe. 릴리스에 이 이름으로 올려 두면 앱이 스스로 교체한다.</summary>
    public UpdateAsset? FindExe(string exeName) => Assets.FirstOrDefault(a => a.Name.Equals(exeName, StringComparison.OrdinalIgnoreCase));
    public UpdateAsset? FindChecksums() => Assets.FirstOrDefault(a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// GitHub Releases 에서 최신 버전을 확인하고, 있으면 릴리스 zip 을 받아 사용자가 직접 설치하게 넘긴다.
/// 스스로 실행 중인 프로그램을 종료·교체·재시작하지 않는다 — 파일을 디스크에 내려놓을 뿐이다.
/// </summary>
public static class UpdateChecker
{
    private const string ApiUrl = "https://api.github.com/repos/leedoha0403/DiskAnalyzer/releases/latest";

    private static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DiskAnalyzer-UpdateChecker/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<UpdateRelease?> FetchLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync(ApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (tag.Length == 0) return null;
            var htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

            var assets = new List<UpdateAsset>();
            if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                    var size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    if (name.Length > 0 && url.Length > 0) assets.Add(new UpdateAsset(name, url, size));
                }
            }

            return new UpdateRelease(tag, htmlUrl, assets);
        }
        catch
        {
            return null;
        }
    }

    // "v1.2.3" (뒤에 "-suffix" 가 붙기도 함) 형태의 태그와, "1.2.3-internal" 형태의 현재 버전 문자열을 비교한다.
    public static bool IsNewer(string latestTag, string currentVersion)
    {
        var latest = ParseVersion(latestTag);
        var current = ParseVersion(currentVersion);
        return latest is not null && current is not null && latest > current;
    }

    private static Version? ParseVersion(string text)
    {
        // .NET 은 기본적으로 InformationalVersion 뒤에 "+<git-sha>" 를 붙이고, 로컬 개발 빌드는 "-internal" 을 붙인다.
        // 숫자 버전은 언제나 첫 '-' 또는 '+' 앞부분이다.
        var s = text.TrimStart('v', 'V');
        var cut = s.IndexOfAny(['-', '+']);
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? v : null;
    }

    public static async Task<string> DownloadAssetAsync(UpdateAsset asset, string destinationDirectory,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        var path = Path.Combine(destinationDirectory, asset.Name);

        using var response = await Client.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? asset.Size;

        await using var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await httpStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                readTotal += read;
                if (total > 0) progress?.Report((double)readTotal / total);
            }
        }
        return path;
    }

    public static async Task<string?> FetchTextAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            return await Client.GetStringAsync(url, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    // sumsText 에 fileName 에 대한 줄이 있고 그 해시가 디스크의 파일과 다르면 false.
    // 해당 줄이 없으면 true(검증 생략) — requireEntry 면 false. 스스로 실행할 exe 는 항상 requireEntry 로 검증한다.
    public static bool VerifyChecksum(string sumsText, string fileName, string filePath, bool requireEntry = false)
    {
        var expected = sumsText
            .Split('\n')
            .Select(l => l.Trim())
            .Select(l => l.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(parts => parts.Length == 2 && parts[1].TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase))
            ?.FirstOrDefault();
        if (expected is null) return !requireEntry;

        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = Convert.ToHexString(sha256.ComputeHash(stream));
        return hash.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}

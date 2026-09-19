using System.IO.Compression;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Scanning;

namespace DiskAnalyzer.Core.Services;

/// <summary>
/// 40. 결과 Cache.
/// 스캔이 끝나면 노드 테이블을 압축 저장하고, 다음 실행 때 즉시 복원해서 보여준다.
/// 복원된 데이터는 ScanResult.FromCache = true 로 표시되어 UI 가 "이전 스캔 결과"임을 명시한다.
/// </summary>
public static class CacheService
{
    private const uint Magic = 0x48434144;   // "DACH"
    private const int Version = 2;   // v2: 파일 생성일 / 접근일 추가(53. 정리 추천 엔진)

    public static string CacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskAnalyzer", "cache");

    public static string GetCachePath(string rootPath)
    {
        string key = rootPath.Replace(':', '_').Replace('\\', '_').Trim('_');
        if (key.Length == 0) key = "root";
        return Path.Combine(CacheDirectory, key + ".dacache");
    }

    public static bool TrySave(ScanResult result)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            string path = GetCachePath(result.RootPath);
            string temp = path + ".tmp";

            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var gz = new DeflateStream(fs, CompressionLevel.Fastest))
            using (var w = new BinaryWriter(new BufferedStream(gz, 1 << 20)))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(result.RootPath);
                w.Write((int)result.Mode);
                w.Write(result.CompletedAt.ToBinary());
                w.Write(result.ElapsedSeconds);
                w.Write(result.VolumeUsedBytes);
                w.Write(result.SkippedFolders);
                w.Write(result.AccessDenied);
                w.Write(result.Errors);
                result.Store.WriteTo(w);
            }

            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static ScanResult? TryLoad(string rootPath, int topK = 1000)
    {
        try
        {
            string path = GetCachePath(ScanController.NormalizeRoot(rootPath));
            if (!File.Exists(path)) return null;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var gz = new DeflateStream(fs, CompressionMode.Decompress);
            using var r = new BinaryReader(new BufferedStream(gz, 1 << 20));

            if (r.ReadUInt32() != Magic) return null;
            if (r.ReadInt32() != Version) return null;

            string root = r.ReadString();
            var mode = (ScanMode)r.ReadInt32();
            var completed = DateTime.FromBinary(r.ReadInt64());
            double elapsed = r.ReadDouble();
            long volumeUsed = r.ReadInt64();
            int skipped = r.ReadInt32();
            int denied = r.ReadInt32();
            int errors = r.ReadInt32();

            var store = NodeStore.ReadFrom(r, topK);

            return new ScanResult
            {
                Store = store,
                RootPath = root,
                Mode = mode,
                CompletedAt = completed,
                ElapsedSeconds = elapsed,
                VolumeUsedBytes = volumeUsed,
                SkippedFolders = skipped,
                AccessDenied = denied,
                Errors = errors,
                FromCache = true,
            };
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<(string Root, DateTime When)> ListCaches()
    {
        var list = new List<(string, DateTime)>();
        try
        {
            if (!Directory.Exists(CacheDirectory)) return list;
            foreach (var f in Directory.EnumerateFiles(CacheDirectory, "*.dacache"))
            {
                string name = Path.GetFileNameWithoutExtension(f).Replace('_', ':');
                list.Add((name, File.GetLastWriteTime(f)));
            }
        }
        catch { }
        return list;
    }

    public static void Clear()
    {
        try
        {
            if (Directory.Exists(CacheDirectory)) Directory.Delete(CacheDirectory, true);
        }
        catch { }
    }
}

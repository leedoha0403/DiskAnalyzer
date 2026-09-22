using System.Diagnostics;
using System.Globalization;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Scanning;
using DiskAnalyzer.Core.Scanning.Ntfs;
using DiskAnalyzer.Core.Services;

// 44. 벤치마크.
//  실제 스캔 성능을 측정하고 병목(스캔 모드 / 워커 수 / 드라이브 종류)을 비교하기 위한 콘솔 러너.
//  사용법:
//    DiskAnalyzer.Bench C:\                     전체 드라이브 스캔
//    DiskAnalyzer.Bench C:\Windows --mode compat --workers 4
//    DiskAnalyzer.Bench C:\Users --sweep        워커 수 스윕
//    DiskAnalyzer.Bench --gen D:\bench 1000000  합성 파일 트리 생성(테스트용)
//    DiskAnalyzer.Bench C:\ --mft               $MFT 런리스트 진단(관리자 권한 필요)

if (args.Length == 0)
{
    PrintUsage();
    PrintDrives();
    return 0;
}

if (args[0].Equals("--gen", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3) { PrintUsage(); return 1; }
    int genCount = int.Parse(args[2], CultureInfo.InvariantCulture);
    int genPerDir = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 200;
    Generate(args[1], genCount, genPerDir);
    return 0;
}

string path = args[0];

if (args.Contains("--mft", StringComparer.OrdinalIgnoreCase))
{
    PrintMftDiagnostics(path);
    return 0;
}

var mode = ScanMode.Auto;
int workers = 0;
int repeat = 1;
bool sweep = false;
bool physical = false;
bool dedupe = false;
var exclusions = new List<string>();

for (int i = 1; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--mode" when i + 1 < args.Length:
            mode = args[++i].ToLowerInvariant() switch
            {
                "fast" => ScanMode.Fast,
                "compat" or "compatibility" => ScanMode.Compatibility,
                _ => ScanMode.Auto,
            };
            break;
        case "--workers" when i + 1 < args.Length:
            workers = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--repeat" when i + 1 < args.Length:
            repeat = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--sweep":
            sweep = true;
            break;
        case "--exclude" when i + 1 < args.Length:
            exclusions.Add(args[++i]);
            break;
        case "--physical":
            physical = true;
            break;
        case "--dedupe" or "--hardlinks":
            dedupe = true;
            break;
    }
}

var drive = DriveService.GetDrive(path);
Console.WriteLine($"대상          : {path}");
Console.WriteLine($"드라이브 종류 : {drive?.KindText ?? "?"} / {drive?.FileSystem ?? "?"}");
Console.WriteLine($"관리자 권한   : {(DriveService.IsElevated() ? "예" : "아니오")}");
Console.WriteLine($"CPU 코어      : {Environment.ProcessorCount}");
Console.WriteLine();

PrintHeader();

if (sweep)
{
    foreach (int w in new[] { 1, 2, 4, 8, 12, 16, 24 })
    {
        if (w > Environment.ProcessorCount * 2) break;
        await RunOnce(path, ScanMode.Compatibility, w, exclusions, physical, dedupe);
    }
}
else
{
    for (int i = 0; i < repeat; i++) await RunOnce(path, mode, workers, exclusions, physical, dedupe);
}

return 0;

static async Task RunOnce(string path, ScanMode mode, int workers, IReadOnlyList<string> exclusions,
                          bool usePhysical, bool dedupeLinks)
{
    var options = new ScanOptions
    {
        Mode = mode,
        WorkerCount = workers,
        CacheResults = false,
        ExclusionPatterns = exclusions,
        SizeBasis = usePhysical ? SizeBasis.Physical : SizeBasis.Logical,
        DeduplicateHardLinks = dedupeLinks,
    };

    var proc = Process.GetCurrentProcess();
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    long memBefore = GC.GetTotalMemory(true);
    TimeSpan cpuBefore = proc.TotalProcessorTime;

    var controller = new ScanController();
    var sw = Stopwatch.StartNew();
    var result = await controller.ScanAsync(path, options);
    sw.Stop();

    proc.Refresh();
    TimeSpan cpuAfter = proc.TotalProcessorTime;
    long peakWorkingSet = proc.PeakWorkingSet64;
    long managed = GC.GetTotalMemory(false) - memBefore;

    double seconds = sw.Elapsed.TotalSeconds;
    double cpuPercent = seconds > 0
        ? (cpuAfter - cpuBefore).TotalSeconds / (seconds * Environment.ProcessorCount) * 100d
        : 0;

    Console.WriteLine(string.Join(" | ",
        result.ModeText.PadRight(20),
        (workers == 0 ? "auto" : workers.ToString()).PadLeft(6),
        result.FileCount.ToString("N0").PadLeft(11),
        result.DirectoryCount.ToString("N0").PadLeft(10),
        SizeFormatter.Format(result.TotalSize).PadLeft(11),
        seconds.ToString("F2").PadLeft(7) + "s",
        (result.FileCount / Math.Max(seconds, 0.001)).ToString("N0").PadLeft(12),
        SizeFormatter.Format(managed).PadLeft(10),
        SizeFormatter.Format(peakWorkingSet).PadLeft(10),
        cpuPercent.ToString("F0").PadLeft(4) + "%"));

    Console.WriteLine($"      skipped={result.SkippedFolders:N0} denied={result.AccessDenied:N0} errors={result.Errors:N0}"
        + (options.Exclusions.IsEmpty ? "" : $" excluded={result.ExcludedFolders:N0} dirs / {result.ExcludedFiles:N0} files"));
    foreach (var t in result.Store.GetChildren(0, includeFiles: false).Take(3))
        Console.WriteLine($"      {t.Name,-28} {t.SizeText,12} {t.RatioText,6}");
    foreach (var e in result.Store.GetExtensionRows().Take(3))
        Console.WriteLine($"      {e.Extension,-28} {e.SizeText,12} {e.CountText,10}");
    Console.WriteLine();
}

/// <summary>
/// $MFT 를 왜 다 못 읽었는지 눈으로 보기 위한 진단.
/// 런리스트가 $MFT 전체를 덮는지가 핵심이다 - 덜 덮으면 뒤쪽 파일이 통째로 사라지고
/// 그만큼이 "숨은 공간" 으로 둔갑한다.
/// </summary>
static void PrintMftDiagnostics(string path)
{
    if (!DriveService.IsElevated())
    {
        Console.WriteLine("관리자 권한이 필요합니다. 관리자 콘솔에서 다시 실행하세요.");
        return;
    }

    using var reader = new MftReader();
    if (!reader.Open(path[0]))
    {
        Console.WriteLine($"볼륨을 열지 못했습니다: {reader.Error}");
        return;
    }

    var sw = Stopwatch.StartNew();
    var table = reader.ReadAll(showHidden: true, showSystem: true, physical: false, CancellationToken.None);
    sw.Stop();

    Console.WriteLine($"클러스터 크기   : {reader.BytesPerCluster:N0} bytes");
    Console.WriteLine($"레코드 크기     : {reader.FileRecordSize:N0} bytes");
    Console.WriteLine($"$MFT 논리 크기  : {SizeFormatter.Format(reader.MftDataSize)}");
    Console.WriteLine($"$MFT 할당 크기  : {SizeFormatter.Format(reader.MftAllocSize)}");
    Console.WriteLine($"런리스트가 덮은 : {SizeFormatter.Format(reader.MftCoveredBytes)}  (조각 {reader.MftRunCount:N0}개)");
    Console.WriteLine($"$ATTRIBUTE_LIST : {(reader.MftHasAttributeList ? "있음 - $DATA 가 확장 레코드로 흩어져 있다" : "없음")}");
    Console.WriteLine($"레코드 슬롯     : {reader.RecordsTotal:N0}");
    Console.WriteLine($"읽은 레코드     : {reader.RecordsRead:N0}");
    Console.WriteLine($"소요            : {sw.Elapsed.TotalSeconds:F2}s");

    if (table == null)
    {
        Console.WriteLine($"결과            : 실패 - {reader.Error}");
        return;
    }

    int inUse = 0, dirs = 0;
    for (int i = 0; i < table.Count; i++)
    {
        if (!table.IsInUse(i)) continue;
        inUse++;
        if (table.IsDirectory(i)) dirs++;
    }
    Console.WriteLine($"사용 중 레코드  : {inUse:N0}  (폴더 {dirs:N0} / 파일 {inUse - dirs:N0})");
}

static void PrintHeader()
{
    Console.WriteLine(string.Join(" | ",
        "Scan Mode".PadRight(20), "Worker".PadLeft(6), "Files".PadLeft(11), "Folders".PadLeft(10),
        "Size".PadLeft(11), "Elapsed".PadLeft(8), "Files/sec".PadLeft(12),
        "Managed".PadLeft(10), "PeakWS".PadLeft(10), "CPU".PadLeft(5)));
    Console.WriteLine(new string('-', 130));
}

static void PrintDrives()
{
    Console.WriteLine();
    Console.WriteLine("로컬 드라이브:");
    foreach (var d in DriveService.GetLocalDrives())
        Console.WriteLine($"  {d.Name}  {d.FileSystem,-6} {d.KindText,-4} {d.UsedText,10} / {d.TotalText,10} ({d.UsedPercentText})");
}

static void PrintUsage()
{
    Console.WriteLine("DiskAnalyzer.Bench <path> [--mode auto|fast|compat] [--workers N] [--repeat N] [--sweep] [--exclude PATTERN]...");
    Console.WriteLine("DiskAnalyzer.Bench <path> --mft            ($MFT 런리스트 진단, 관리자 권한)");
    Console.WriteLine("DiskAnalyzer.Bench --gen <dir> <fileCount> [filesPerDir]");
}

/// <summary>테스트용 합성 트리 생성. 10만 / 50만 / 100만 파일 시나리오를 만들 때 사용한다.</summary>
static void Generate(string root, int count, int perDir)
{
    Console.WriteLine($"{root} 아래에 파일 {count:N0}개 생성 (폴더당 {perDir}개)...");
    var sw = Stopwatch.StartNew();
    Directory.CreateDirectory(root);

    int created = 0;
    int dirIndex = 0;
    var buffer = new byte[64];
    Random.Shared.NextBytes(buffer);
    string[] exts = { ".log", ".pdb", ".obj", ".txt", ".dat", ".zip" };

    while (created < count)
    {
        string dir = Path.Combine(root, $"g{dirIndex / 100:D3}", $"d{dirIndex:D5}");
        Directory.CreateDirectory(dir);
        for (int i = 0; i < perDir && created < count; i++, created++)
        {
            string file = Path.Combine(dir, $"f{created:D7}{exts[created % exts.Length]}");
            using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 4096);
            fs.Write(buffer, 0, (created % 7) * 8);
        }
        dirIndex++;
        if (dirIndex % 200 == 0) Console.Write($"\r  {created:N0} / {count:N0}");
    }
    Console.WriteLine($"\r  완료: {created:N0}개, {sw.Elapsed.TotalSeconds:F1}초");
}

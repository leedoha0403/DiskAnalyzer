using System.Diagnostics;
using System.Threading.Channels;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.Core.Scanning;

/// <summary>
/// 19. UI Layer 와 Scan Engine 사이의 유일한 접점.
///
/// UI 는 이 클래스의 Progress / LiveView / Result 세 개의 "참조"만 읽는다.
/// 내부적으로 Scanner(Producer) -> Channel -> Aggregator(단일 Consumer) 파이프라인을 돌리고,
/// 결과는 150ms 주기로 스냅샷을 새로 만들어 게시한다(29. UI 업데이트 최적화).
/// </summary>
public sealed class ScanController
{
    private sealed class ProgressBox
    {
        public ProgressBox(ScanProgress p) => Value = p;
        public ScanProgress Value { get; }
    }

    private const int PublishIntervalMs = 150;

    private volatile ProgressBox _progress = new(ScanProgress.Empty);
    private volatile ViewSnapshot? _liveView;
    private volatile ScanResult? _result;
    private CancellationTokenSource? _cts;

    private int _liveViewDirId;
    private int _viewVersion;
    private double _uiLatencyMs;
    private long _peakMemory;
    private ScanMode _activeMode = ScanMode.Compatibility;
    private string? _fastFailure;
    private int _liveTab;
    private int _topCount = 100;

    public ScanProgress Progress => _progress.Value;
    public ViewSnapshot? LiveView => _liveView;
    public ScanResult? Result => _result;
    public bool IsRunning => _progress.Value.IsBusy;

    /// <summary>UI 가 보고 있는 폴더. Aggregator 가 이 폴더의 행만 만들어 준다(34. 필요한 것만).</summary>
    public void SetLiveViewDirectory(int dirId) => Volatile.Write(ref _liveViewDirId, dirId);

    /// <summary>UI 가 보고 있는 탭. 보이지 않는 탭의 데이터는 스냅샷에 만들지 않는다.</summary>
    public void SetLiveTab(LiveTab tab) => Volatile.Write(ref _liveTab, (int)tab);

    /// <summary>큰 파일 탭의 TOP N 선택값.</summary>
    public void SetTopFileCount(int n) => Volatile.Write(ref _topCount, Math.Clamp(n, 10, 1000));

    /// <summary>43. 개발용 성능 모니터 - UI 업데이트 지연 시간 보고.</summary>
    public void ReportUiLatency(double ms) => Volatile.Write(ref _uiLatencyMs, ms);

    public void Cancel() => _cts?.Cancel();

    public async Task<ScanResult> ScanAsync(string rootPath, ScanOptions options, CancellationToken external = default)
    {
        rootPath = NormalizeRoot(rootPath);

        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        var ct = _cts.Token;

        Volatile.Write(ref _liveViewDirId, NodeStore.RootId);
        _liveView = null;
        _result = null;
        _peakMemory = 0;

        var drive = DriveService.GetDrive(rootPath);
        var driveKind = drive?.Kind ?? DriveKind.Unknown;
        long volumeUsed = IsDriveRoot(rootPath) ? DriveService.GetVolumeUsedBytes(rootPath) : 0;

        var store = new NodeStore(rootPath, options.TopFileCount);
        var stats = new ScanSharedState();
        var pool = new ScanBatchPool(options.BatchSize);

        int workerHint = options.ResolveWorkerCount(driveKind);
        var channel = Channel.CreateBounded<ScanBatch>(new BoundedChannelOptions(Math.Max(4, workerHint * 4))
        {
            SingleReader = true,         // Aggregator 는 항상 1개 - 그래서 NodeStore 에 Lock 이 없다
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,   // 32. Aggregator 가 밀리면 워커가 기다린다(메모리 폭주 방지)
        });

        var rt = new ScanRuntime
        {
            RootPath = rootPath,
            Options = options,
            BatchWriter = channel.Writer,
            Pool = pool,
            Stats = stats,
            DriveKind = driveKind,
        };

        var sw = Stopwatch.StartNew();
        var mode = ScanMode.Compatibility;
        string? message = null;

        Publish(new ScanProgress(ScanState.Preparing, options.Mode, rootPath, rootPath,
            0, 0, 0, volumeUsed, 0, 0, 0, 0, 0, 0, workerHint, 0, 0, 0, 0, 0, "준비 중..."));

        var aggregator = Task.Run(() => AggregateAsync(channel.Reader, store, stats, pool, sw, rootPath,
            volumeUsed, options, ct), CancellationToken.None);

        bool cancelled = false;
        try
        {
            if (options.Mode != ScanMode.Compatibility && IsDriveRoot(rootPath))
            {
                _activeMode = ScanMode.Fast;
                var fast = new FastScanner(rt);
                bool ok = false;
                try
                {
                    ok = await fast.TryRunAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _fastFailure = ex.Message; }

                if (ok)
                {
                    mode = ScanMode.Fast;
                }
                else
                {
                    // 28. Fast Scan 이 불가능하면 조용히 Compatibility 로 자동 전환한다.
                    _activeMode = ScanMode.Compatibility;
                    message = $"Fast Scan 사용 불가({fast.FailureReason ?? _fastFailure}) - Compatibility 모드로 전환";
                }
            }
            else if (!IsDriveRoot(rootPath) && options.Mode == ScanMode.Fast)
            {
                message = "하위 폴더 스캔은 Compatibility 모드로만 동작합니다.";
            }

            if (mode != ScanMode.Fast)
            {
                mode = ScanMode.Compatibility;
                var compat = new CompatibilityScanner(rt);
                await compat.RunAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            channel.Writer.TryComplete();
        }

        try { await aggregator.ConfigureAwait(false); }
        catch (OperationCanceledException) { cancelled = true; }

        sw.Stop();
        store.Seal();

        var result = new ScanResult
        {
            Store = store,
            RootPath = rootPath,
            Mode = mode,
            CompletedAt = DateTime.Now,
            ElapsedSeconds = sw.Elapsed.TotalSeconds,
            VolumeUsedBytes = volumeUsed,
            SkippedFolders = stats.SkippedFolders,
            AccessDenied = stats.AccessDenied,
            Errors = stats.Errors,
            Cancelled = cancelled,
        };
        _result = result;

        Publish(new ScanProgress(
            cancelled ? ScanState.Cancelled : ScanState.Completed, mode, rootPath, string.Empty,
            result.FileCount, result.DirectoryCount, result.TotalSize, volumeUsed,
            result.ElapsedSeconds,
            result.ElapsedSeconds > 0 ? result.FileCount / result.ElapsedSeconds : 0,
            result.ElapsedSeconds > 0 ? result.DirectoryCount / result.ElapsedSeconds : 0,
            stats.SkippedFolders, stats.AccessDenied, stats.Errors,
            stats.WorkerCount, 0, 0,
            GC.GetTotalMemory(false), _peakMemory, Volatile.Read(ref _uiLatencyMs),
            cancelled ? "스캔이 취소되었습니다." : message));

        PublishView(store, Volatile.Read(ref _liveViewDirId), partial: false, includeFiles: true);

        if (options.CacheResults && !cancelled)
        {
            _ = Task.Run(() => CacheService.TrySave(result));
        }

        return result;
    }

    /// <summary>
    /// Aggregator. 이 메서드만이 NodeStore 에 쓴다.
    /// 배치를 적용하고, 150ms 마다 진행 상황과 현재 폴더 스냅샷을 게시한다.
    /// </summary>
    private async Task AggregateAsync(ChannelReader<ScanBatch> reader, NodeStore store, ScanSharedState stats,
        ScanBatchPool pool, Stopwatch sw, string rootPath, long volumeUsed, ScanOptions options, CancellationToken ct)
    {
        long files = 0, dirs = 0, bytes = 0;
        var lastPublish = Stopwatch.StartNew();

        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var batch))
                {
                    int n = batch.Count;
                    for (int i = 0; i < n; i++)
                    {
                        int id = batch.Id[i];
                        if (id >= 0)
                        {
                            store.SetDirectory(id, batch.Parent[i], batch.NameAt(i), batch.Time[i], (int)batch.Attr[i]);
                            dirs++;
                        }
                        else
                        {
                            store.AddFile(batch.Parent[i], batch.NameAt(i), batch.Size[i],
                                batch.Time[i], batch.Created[i], batch.Accessed[i], (int)batch.Attr[i]);
                            files++;
                            bytes += batch.Size[i];
                        }
                    }
                    pool.Return(batch);
                    Volatile.Write(ref stats.ResultQueueSize, reader.Count);

                    if (lastPublish.ElapsedMilliseconds >= PublishIntervalMs)
                    {
                        lastPublish.Restart();
                        PublishRunning(stats, sw, rootPath, volumeUsed, files, dirs, bytes, options);
                        PublishView(store, Volatile.Read(ref _liveViewDirId), partial: true, includeFiles: false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 취소: 여기까지 적용된 데이터는 그대로 유지하고 종료한다.
        }

        PublishRunning(stats, sw, rootPath, volumeUsed, files, dirs, bytes, options);
    }

    private void PublishRunning(ScanSharedState stats, Stopwatch sw, string rootPath, long volumeUsed,
        long files, long dirs, long bytes, ScanOptions options)
    {
        double elapsed = sw.Elapsed.TotalSeconds;
        long mem = GC.GetTotalMemory(false);
        if (mem > _peakMemory) _peakMemory = mem;

        long mftRead = Volatile.Read(ref stats.MftRecordsRead);
        long mftTotal = Volatile.Read(ref stats.MftRecordsTotal);
        string? msg = mftTotal > 0 && files == 0
            ? $"MFT 읽는 중 {mftRead:N0} / {mftTotal:N0}"
            : null;

        Publish(new ScanProgress(
            ScanState.Running, _activeMode, rootPath, stats.CurrentPath,
            files, dirs, bytes, volumeUsed, elapsed,
            elapsed > 0 ? files / elapsed : 0,
            elapsed > 0 ? dirs / elapsed : 0,
            stats.SkippedFolders, stats.AccessDenied, stats.Errors,
            stats.WorkerCount, Volatile.Read(ref stats.DirectoryQueueSize), Volatile.Read(ref stats.ResultQueueSize),
            mem, _peakMemory, Volatile.Read(ref _uiLatencyMs), msg));
    }

    private void PublishView(NodeStore store, int dirId, bool partial, bool includeFiles)
    {
        if ((uint)dirId >= (uint)store.DirectorySlots) dirId = NodeStore.RootId;

        // 25. 폴더 크기 롤업은 O(D) 역순 1패스라 150ms 주기로 돌려도 부담이 없다.
        store.Rollup();

        var tab = (LiveTab)Volatile.Read(ref _liveTab);

        // 폴더 행은 O(폴더 수) 라 항상 만든다. 아래 두 개는 파일 수에 비례하므로 보이는 탭에서만 만든다(34).
        var rows = store.GetChildren(dirId, includeFiles);

        var top = tab == LiveTab.LargeFiles || !partial
            ? store.GetTopFiles(Volatile.Read(ref _topCount))
            : null;

        var exts = tab == LiveTab.FileTypes || !partial
            ? store.GetExtensionRows()
            : null;

        _liveView = new ViewSnapshot
        {
            DirectoryId = dirId,
            Path = store.GetDirectoryPath(dirId),
            Rows = rows,
            Breadcrumb = store.GetAncestors(dirId),
            TotalSize = store.GetDirectorySize(dirId),
            Partial = partial,
            Version = Interlocked.Increment(ref _viewVersion),
            TopFiles = top,
            Extensions = exts,
        };
    }

    private void Publish(ScanProgress p) => _progress = new ProgressBox(p);

    internal static bool IsDriveRoot(string path)
        => path.Length is 2 or 3 && path[1] == ':';

    public static string NormalizeRoot(string path)
    {
        path = path.Trim().TrimEnd('\\');
        if (path.Length == 2 && path[1] == ':') return path + "\\";
        return path.Length == 0 ? "C:\\" : path;
    }
}

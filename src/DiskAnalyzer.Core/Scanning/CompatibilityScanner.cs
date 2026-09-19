using System.Runtime.InteropServices;
using System.Threading.Channels;
using DiskAnalyzer.Core.Interop;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Scanning;

internal readonly record struct DirWorkItem(int Id, string Path);

/// <summary>
/// 21. Producer / Consumer 구조의 디렉터리 열거 스캐너.
///
/// Directory Queue(Channel) -> Worker Thread Pool -> Result Queue(Channel) -> Aggregator
///
/// 재귀 함수 하나가 전체 디스크를 도는 구조를 쓰지 않는 이유:
///  - 병렬화가 불가능하고
///  - 깊은 트리에서 스택이 커지며
///  - 취소(39) 시 콜 스택을 풀 때까지 멈출 수 없다.
/// 워커는 큐에서 디렉터리 하나를 꺼내 "한 단계만" 열거하고, 새로 찾은 하위 디렉터리를 다시 큐에 넣는다.
/// </summary>
internal sealed class CompatibilityScanner
{
    private readonly ScanRuntime _rt;
    private Channel<DirWorkItem> _dirQueue = null!;
    private int _pendingDirs;
    private int _nextDirId = 1;       // 0 = 루트
    private int _dirCounterForPathUpdate;

    public CompatibilityScanner(ScanRuntime rt) => _rt = rt;

    public async Task RunAsync(CancellationToken ct)
    {
        int workers = _rt.Options.ResolveWorkerCount(_rt.DriveKind);
        _rt.Stats.WorkerCount = workers;

        _dirQueue = Channel.CreateUnbounded<DirWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });

        _pendingDirs = 1;
        _dirQueue.Writer.TryWrite(new DirWorkItem(NodeStore.RootId, _rt.RootPath));

        var tasks = new Task[workers];
        for (int i = 0; i < workers; i++) tasks[i] = Task.Run(() => WorkerLoopAsync(ct), CancellationToken.None);

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            _dirQueue.Writer.TryComplete();
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        var reader = _dirQueue.Reader;
        var batch = _rt.Pool.Rent();

        // 이동식 미디어 없음 등의 모달 오류 대화상자를 이 스레드에서 억제한다.
        Win32.SetThreadErrorMode(Win32.SEM_FAILCRITICALERRORS, out _);

        try
        {
            while (true)
            {
                // 39. Scan Cancel: 큐에 남은 수십만 개를 계속 처리하지 않고 즉시 빠져나온다.
                if (ct.IsCancellationRequested) break;

                if (!reader.TryRead(out var item))
                {
                    try
                    {
                        if (!await reader.WaitToReadAsync(ct).ConfigureAwait(false)) break;
                    }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                try
                {
                    batch = ProcessDirectory(item, batch, ct);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _rt.Stats.Errors);
                }
                finally
                {
                    if (Interlocked.Decrement(ref _pendingDirs) == 0) _dirQueue.Writer.TryComplete();
                    Volatile.Write(ref _rt.Stats.DirectoryQueueSize, Volatile.Read(ref _pendingDirs));
                }

                if (batch.IsFull) batch = await FlushAsync(batch, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (batch.Count > 0 && !ct.IsCancellationRequested)
            {
                try
                {
                    // FlushAsync 는 새 배치를 빌려 주므로 그것까지 풀에 돌려줘야 누수가 없다.
                    var spare = await FlushAsync(batch, CancellationToken.None).ConfigureAwait(false);
                    _rt.Pool.Return(spare);
                }
                catch { /* 취소 중 */ }
            }
            else
            {
                _rt.Pool.Return(batch);
            }
        }
    }

    private async ValueTask<ScanBatch> FlushAsync(ScanBatch batch, CancellationToken ct)
    {
        if (batch.Count == 0) return batch;
        await _rt.BatchWriter.WriteAsync(batch, ct).ConfigureAwait(false);
        return _rt.Pool.Rent();
    }

    private unsafe ScanBatch ProcessDirectory(DirWorkItem item, ScanBatch batch, CancellationToken ct)
    {
        // "\\?\" 접두사로 MAX_PATH 260자 제한을 우회한다. 깊은 node_modules 트리에서 필수.
        string search = BuildSearchPattern(item.Path);

        using var handle = Win32.FindFirstFileEx(
            search, Win32.FindExInfoBasic, out var data,
            Win32.FindExSearchNameMatch, IntPtr.Zero, Win32.FIND_FIRST_EX_LARGE_FETCH);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            // 38. Access Denied - 해당 폴더만 건너뛰고 전체 스캔은 계속한다.
            if (err == Win32.ERROR_ACCESS_DENIED) Interlocked.Increment(ref _rt.Stats.AccessDenied);
            else if (err != Win32.ERROR_FILE_NOT_FOUND && err != Win32.ERROR_NO_MORE_FILES)
                Interlocked.Increment(ref _rt.Stats.Errors);
            Interlocked.Increment(ref _rt.Stats.SkippedFolders);
            return batch;
        }

        // 표시용 현재 경로는 매 디렉터리마다 갱신할 필요가 없다(UI 는 어차피 150ms 주기로 읽는다).
        if ((Interlocked.Increment(ref _dirCounterForPathUpdate) & 0x3F) == 0)
            _rt.Stats.CurrentPath = item.Path;

        bool showHidden = _rt.Options.ShowHiddenFiles;
        bool showSystem = _rt.Options.ShowSystemFiles;
        bool follow = _rt.Options.FollowReparsePoints;

        do
        {
            if (ct.IsCancellationRequested) break;

            ReadOnlySpan<char> name = GetName(data.cFileName);
            if (name.Length == 0) continue;
            if (name[0] == '.' && (name.Length == 1 || (name.Length == 2 && name[1] == '.'))) continue;

            uint attr = data.dwFileAttributes;
            if (!showHidden && (attr & Win32.FILE_ATTRIBUTE_HIDDEN) != 0) continue;
            if (!showSystem && (attr & Win32.FILE_ATTRIBUTE_SYSTEM) != 0) continue;

            if (batch.IsFull)
            {
                // 동기 컨텍스트에서 채널이 가득 찬 경우에도 블로킹 없이 넘긴다.
                // 결과 채널은 워커 수 * 4 로 제한되어 있어 Aggregator 가 밀리면 여기서 자연스럽게 backpressure 가 걸린다.
                _rt.BatchWriter.WriteAsync(batch, ct).AsTask().GetAwaiter().GetResult();
                batch = _rt.Pool.Rent();
            }

            if ((attr & Win32.FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                bool isReparse = (attr & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0;

                // 37. Symbolic Link / Junction: 기본적으로 따라가지 않는다.
                // 노드는 기록해서 화면에는 보이게 하되 하위로 내려가지 않아 무한 순회를 막는다.
                int childId = Interlocked.Increment(ref _nextDirId) - 1;
                batch.Add(childId, item.Id, name, 0,
                    data.ftLastWriteTime, data.ftCreationTime, data.ftLastAccessTime, attr);

                if (isReparse && !follow)
                {
                    Interlocked.Increment(ref _rt.Stats.SkippedFolders);
                    continue;
                }

                Interlocked.Increment(ref _pendingDirs);
                _dirQueue.Writer.TryWrite(new DirWorkItem(childId, Combine(item.Path, name)));
            }
            else
            {
                // 26/27. 열거 결과에 이미 들어 있는 메타데이터만 사용한다(추가 API 호출 없음).
                batch.Add(-1, item.Id, name, data.Size,
                    data.ftLastWriteTime, data.ftCreationTime, data.ftLastAccessTime, attr);
            }
        }
        while (Win32.FindNextFile(handle, out data));

        return batch;
    }

    private static string BuildSearchPattern(string path)
    {
        bool needsSeparator = path.Length > 0 && path[^1] != '\\';
        string prefix = path.StartsWith(@"\\", StringComparison.Ordinal) ? string.Empty : @"\\?\";
        return needsSeparator ? prefix + path + @"\*" : prefix + path + "*";
    }

    private static string Combine(string dir, ReadOnlySpan<char> name)
        => dir.Length > 0 && dir[^1] == '\\'
            ? string.Concat(dir, name)
            : string.Concat(dir, "\\", name);

    private static unsafe ReadOnlySpan<char> GetName(char* p)
    {
        int len = 0;
        while (len < Win32.MAX_PATH && p[len] != '\0') len++;
        return new ReadOnlySpan<char>(p, len);
    }
}

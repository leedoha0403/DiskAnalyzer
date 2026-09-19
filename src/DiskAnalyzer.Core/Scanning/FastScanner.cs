using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Scanning.Ntfs;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.Core.Scanning;

/// <summary>
/// 28. NTFS Fast Scan 스캐너.
///
/// 1단계) $MFT 순차 읽기 -> 모든 레코드의 (이름, 부모, 크기, 시간, 속성)
/// 2단계) 루트(레코드 5)부터 BFS 로 트리를 만들며 Aggregator 에 배치 전달
///
/// 2단계를 BFS 로 도는 이유는 단순히 순서를 예쁘게 하려는 게 아니다.
/// MFT 인덱스는 부모가 자식보다 클 수 있어서 NodeStore 의 "자식 id > 부모 id" 불변식이 깨진다.
/// BFS 로 노드 id 를 다시 매기면 불변식이 복구되고, 폴더 크기 롤업을 역순 1패스로 끝낼 수 있다(25).
/// 덤으로 루트에서 도달할 수 없는 고아 레코드가 자연스럽게 제외된다.
/// </summary>
internal sealed class FastScanner
{
    private const int RootMftIndex = 5;

    private readonly ScanRuntime _rt;

    public FastScanner(ScanRuntime rt) => _rt = rt;

    public string? FailureReason { get; private set; }

    /// <summary>실패하면 false 를 돌려주고 호출자가 Compatibility 모드로 전환한다.</summary>
    public async Task<bool> TryRunAsync(CancellationToken ct)
    {
        if (!DriveService.IsNtfs(_rt.RootPath)) { FailureReason = "NTFS 볼륨이 아닙니다."; return false; }
        if (!DriveService.IsElevated()) { FailureReason = "관리자 권한이 아닙니다."; return false; }
        if (_rt.RootPath.Length < 2 || _rt.RootPath[1] != ':') { FailureReason = "드라이브 루트가 아닙니다."; return false; }

        using var reader = new MftReader();
        if (!reader.Open(_rt.RootPath[0]))
        {
            FailureReason = reader.Error ?? "볼륨을 열 수 없습니다.";
            return false;
        }

        _rt.Stats.CurrentPath = "$MFT 읽는 중...";

        MftRecordTable? table = null;
        using (var pump = StartProgressPump(reader, ct))
        {
            table = await Task.Run(
                () => reader.ReadAll(_rt.Options.ShowHiddenFiles, _rt.Options.ShowSystemFiles, ct),
                CancellationToken.None).ConfigureAwait(false);
        }

        if (table == null)
        {
            FailureReason = reader.Error ?? "MFT 를 읽지 못했습니다.";
            return false;
        }

        ct.ThrowIfCancellationRequested();
        await BuildTreeAsync(table, ct).ConfigureAwait(false);
        return true;
    }

    private IDisposable StartProgressPump(MftReader reader, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                Volatile.Write(ref _rt.Stats.MftRecordsRead, Volatile.Read(ref reader.RecordsRead));
                Volatile.Write(ref _rt.Stats.MftRecordsTotal, reader.RecordsTotal);
                try { await Task.Delay(100, token).ConfigureAwait(false); } catch { break; }
            }
        }, CancellationToken.None);
        return cts;
    }

    /// <summary>레코드 테이블 -> (부모별 자식 CSR 인덱스) -> BFS -> 배치 전송.</summary>
    private async Task BuildTreeAsync(MftRecordTable table, CancellationToken ct)
    {
        _rt.Stats.CurrentPath = "트리 구성 중...";
        int n = table.Count;

        // CSR(Compressed Sparse Row) 인덱스: 자식 리스트를 List<int>[] 로 만들면
        // 레코드 수만큼 List 객체가 생겨 GC 가 폭발한다. 카운트 -> 누적합 -> 채우기 3패스로 배열 2개만 쓴다.
        var start = new int[n + 1];
        for (int i = 0; i < n; i++)
        {
            if (i == RootMftIndex || !table.IsInUse(i)) continue;
            int p = table.ParentIndex[i];
            if ((uint)p < (uint)n && p != i) start[p + 1]++;
        }
        for (int i = 0; i < n; i++) start[i + 1] += start[i];

        var entries = new int[start[n]];
        var cursor = (int[])start.Clone();
        for (int i = 0; i < n; i++)
        {
            if (i == RootMftIndex || !table.IsInUse(i)) continue;
            int p = table.ParentIndex[i];
            if ((uint)p < (uint)n && p != i) entries[cursor[p]++] = i;
        }

        var batch = _rt.Pool.Rent();
        int nextNodeId = 1;

        // BFS 큐: (MFT 인덱스, NodeStore 노드 id)
        var queueMft = new int[1024];
        var queueNode = new int[1024];
        int head = 0, tail = 0;
        queueMft[tail] = RootMftIndex;
        queueNode[tail] = NodeStore.RootId;
        tail++;

        while (head < tail)
        {
            ct.ThrowIfCancellationRequested();

            int mft = queueMft[head];
            int node = queueNode[head];
            head++;

            for (int k = start[mft]; k < start[mft + 1]; k++)
            {
                int child = entries[k];

                // ReadOnlySpan 은 await 경계를 넘을 수 없으므로 배치 교체를 먼저 끝낸 뒤 이름을 얻는다.
                if (batch.IsFull)
                {
                    await _rt.BatchWriter.WriteAsync(batch, ct).ConfigureAwait(false);
                    batch = _rt.Pool.Rent();
                }

                var name = table.Pool.Get(table.NameHandle[child]);
                if (name.Length == 0) continue;

                if (table.IsDirectory(child))
                {
                    int childNode = nextNodeId++;
                    batch.Add(childNode, node, name, 0, table.Time[child],
                        table.Created[child], table.Accessed[child], table.Attributes[child]);

                    if (tail == queueMft.Length)
                    {
                        Array.Resize(ref queueMft, queueMft.Length * 2);
                        Array.Resize(ref queueNode, queueNode.Length * 2);
                    }
                    queueMft[tail] = child;
                    queueNode[tail] = childNode;
                    tail++;
                }
                else
                {
                    batch.Add(-1, node, name, table.Size[child], table.Time[child],
                        table.Created[child], table.Accessed[child], table.Attributes[child]);
                }
            }

            // 큐 앞부분을 정리해 메모리를 줄인다(폴더가 수십만 개일 때 효과가 있다).
            if (head > 1 << 16 && head * 2 > tail)
            {
                int remain = tail - head;
                Array.Copy(queueMft, head, queueMft, 0, remain);
                Array.Copy(queueNode, head, queueNode, 0, remain);
                head = 0;
                tail = remain;
            }
        }

        if (batch.Count > 0) await _rt.BatchWriter.WriteAsync(batch, ct).ConfigureAwait(false);
        else _rt.Pool.Return(batch);

        _rt.Stats.WorkerCount = 1;
    }
}

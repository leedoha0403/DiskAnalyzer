using System.Collections.Concurrent;

namespace DiskAnalyzer.Core.Scanning;

/// <summary>
/// 23/29. Worker -> Aggregator 사이를 오가는 배치 버퍼.
///
/// [왜 배치인가]
/// 파일 하나마다 공유 자료구조에 접근하면 Lock 경합과 채널 오버헤드가 스캔 시간을 지배한다.
/// 워커는 자기 배치(로컬 버퍼)에 수천 개를 모은 뒤 단 한 번 채널에 넣는다.
/// 공유 상태 접근 횟수가 파일 수 N 에서 N/BatchSize 로 줄어든다.
///
/// [왜 SoA + char 풀인가]
/// 배치 안에서도 엔트리마다 string 을 할당하면 GC 압력이 그대로다.
/// 이름은 하나의 char[] 에 연속 복사하고 (offset, length) 만 기록한다.
///
/// 배치 객체 자체는 ScanBatchPool 로 재사용하여 스캔 전체에서 워커 수만큼만 존재하게 한다.
/// </summary>
internal sealed class ScanBatch
{
    public const int NameBufferChars = 96 * 1024;

    public readonly int Capacity;
    public int Count;

    public readonly int[] Id;           // 디렉터리면 미리 할당된 dirId, 파일이면 -1
    public readonly int[] Parent;
    public readonly long[] Size;
    public readonly long[] Time;        // 최종 수정
    public readonly long[] Created;
    public readonly long[] Accessed;
    public readonly uint[] Attr;
    public readonly int[] NameOffset;
    public readonly ushort[] NameLength;

    public readonly char[] Names;
    public int NameUsed;

    public ScanBatch(int capacity)
    {
        Capacity = capacity;
        Id = new int[capacity];
        Parent = new int[capacity];
        Size = new long[capacity];
        Time = new long[capacity];
        Created = new long[capacity];
        Accessed = new long[capacity];
        Attr = new uint[capacity];
        NameOffset = new int[capacity];
        NameLength = new ushort[capacity];
        Names = new char[NameBufferChars];
    }

    public bool IsFull => Count >= Capacity || NameUsed + 512 > Names.Length;

    public void Add(int id, int parent, ReadOnlySpan<char> name, long size, long time, uint attr)
        => Add(id, parent, name, size, time, time, time, attr);

    public void Add(int id, int parent, ReadOnlySpan<char> name, long size,
        long time, long created, long accessed, uint attr)
    {
        int i = Count++;
        Id[i] = id;
        Parent[i] = parent;
        Size[i] = size;
        Time[i] = time;
        Created[i] = created;
        Accessed[i] = accessed;
        Attr[i] = attr;

        int len = Math.Min(name.Length, 255);
        name[..len].CopyTo(Names.AsSpan(NameUsed));
        NameOffset[i] = NameUsed;
        NameLength[i] = (ushort)len;
        NameUsed += len;
    }

    public ReadOnlySpan<char> NameAt(int i) => Names.AsSpan(NameOffset[i], NameLength[i]);

    public void Reset()
    {
        Count = 0;
        NameUsed = 0;
    }
}

/// <summary>배치 재사용 풀. 스캔 중 새 배치 할당이 거의 발생하지 않도록 한다.</summary>
internal sealed class ScanBatchPool
{
    private readonly ConcurrentBag<ScanBatch> _bag = new();
    private readonly int _capacity;

    public ScanBatchPool(int capacity) => _capacity = capacity;

    public ScanBatch Rent()
    {
        if (_bag.TryTake(out var b)) { b.Reset(); return b; }
        return new ScanBatch(_capacity);
    }

    public void Return(ScanBatch batch)
    {
        batch.Reset();
        if (_bag.Count < 64) _bag.Add(batch);
    }
}

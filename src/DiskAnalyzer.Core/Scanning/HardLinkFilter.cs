namespace DiskAnalyzer.Core.Scanning;

/// <summary>
/// 같은 파일이 여러 경로에 걸려 있을 때(하드 링크) <b>한 번만</b> 세기 위한 필터.
///
/// <para>Windows 에서 <c>C:\Windows\WinSxS</c> 는 대부분 <c>System32</c> 로의 하드 링크다.
/// 디렉터리 항목마다 세면 같은 바이트를 수십 GB 어치 두 번 센다. 원본도 같은 이유로
/// "counts each hard link only once, 나머지는 0 바이트" 로 처리한다.</para>
///
/// <para><b>Dictionary 로 들 수 없다.</b> 파일이 2,900만 개면 id 하나에 8바이트씩만 잡아도
/// 해시 부하까지 1GB 를 넘긴다. 다행히 NTFS 의 파일 id 는 <c>(시퀀스 &lt;&lt; 48) | MFT 레코드 번호</c> 라
/// <b>하위 48비트가 조밀한 정수</b>다. 그래서 레코드 번호를 그대로 비트 자리로 쓰는 비트맵이면
/// 레코드 3,200만 개에 <b>4 MB</b> 면 된다.</para>
///
/// <para>여러 워커가 동시에 찍으므로 <see cref="Interlocked.Or(ref int, int)"/> 로 찍고
/// <b>찍기 전 값</b>을 받아 "내가 처음인가"를 판정한다 — 확인 후 찍는 두 단계가 아니라 한 번에 끝난다.</para>
/// </summary>
public sealed class HardLinkFilter
{
    /// <summary>덩어리 하나가 담는 레코드 수(=비트 수). 1M 비트 = 128 KB.</summary>
    private const int ChunkBits = 1 << 20;
    private const int ChunkInts = ChunkBits / 32;

    /// <summary>레코드 번호가 이 값을 넘으면 비트맵을 쓰지 않는다(정상적인 볼륨에서는 오지 않는다).</summary>
    private const long MaxRecord = 1L << 32;

    private readonly object _grow = new();
    private volatile int[]?[] _chunks = new int[]?[64];

    private int _duplicates;

    /// <summary>두 번째 이후로 나타나 0 바이트로 센 항목 수. 스캔 결과에 몇 개였는지 알린다.</summary>
    public int Duplicates => Volatile.Read(ref _duplicates);

    /// <summary>
    /// 이 파일을 <b>처음 본 것으로 차지</b>한다. 처음이면 true(정상적으로 크기를 센다),
    /// 이미 누가 차지했으면 false(같은 실체이므로 0 바이트로 센다).
    /// </summary>
    /// <param name="fileId">
    /// <c>FILE_ID_BOTH_DIR_INFO.FileId</c> 또는 <c>nFileIndexHigh/Low</c>. 0 이면 알 수 없다는 뜻이라 항상 true.
    /// </param>
    public bool TryClaim(long fileId)
    {
        if (fileId == 0) return true;

        long record = fileId & 0x0000_FFFF_FFFF_FFFF;    // 상위 16비트는 시퀀스 번호라 버린다
        if (record <= 0 || record >= MaxRecord) return true;

        int chunkIndex = (int)(record / ChunkBits);
        int[] chunk = GetChunk(chunkIndex);

        int bit = (int)(record % ChunkBits);
        int mask = 1 << (bit & 31);

        int before = Interlocked.Or(ref chunk[bit >> 5], mask);
        if ((before & mask) == 0) return true;

        Interlocked.Increment(ref _duplicates);
        return false;
    }

    private int[] GetChunk(int index)
    {
        var chunks = _chunks;
        if (index < chunks.Length && chunks[index] is { } ready) return ready;

        lock (_grow)
        {
            if (index >= _chunks.Length)
            {
                int size = _chunks.Length;
                while (size <= index) size *= 2;

                var bigger = new int[]?[size];
                Array.Copy(_chunks, bigger, _chunks.Length);
                _chunks = bigger;
            }

            return _chunks[index] ??= new int[ChunkInts];
        }
    }
}

namespace DiskAnalyzer.Core.Models;

/// <summary>
/// 파일/폴더 "이름" 전용 문자열 풀.
///
/// [왜 이 구조인가]
/// 파일 500만 개를 string 객체로 들고 있으면 객체 헤더(16B) + length(4B) + 정렬 패딩 때문에
/// 실제 문자 데이터보다 오버헤드가 더 큰 경우가 많고, GC Gen2 가 그만큼 커진다.
/// 여기서는 2MB(1,048,576 chars) 청크 char[] 에 연속 저장하고 60bit 핸들만 SoA 배열에 보관한다.
/// - 객체 수: 파일 수 N개 -> 청크 수 (N/수십만) 개로 감소
/// - GC 스캔 대상 참조 수가 거의 0 (char[] 은 참조를 포함하지 않는 배열)
/// - 문자열은 정말 필요할 때(화면에 보이는 수십 행)만 string 으로 materialize -> 34. Lazy Loading
///
/// [스레드 안전성]
/// 이 클래스는 Lock 이 없다. 오직 Aggregator 단일 스레드만 Add() 를 호출한다는 전제.
/// 조회(Get)는 Add 와 동시에 일어나도 안전하다: 청크는 append-only 이며 기존 데이터는 변경되지 않는다.
/// (_chunks 는 List 이므로 성장 시 재할당될 수 있어 조회용 스냅샷 배열 _chunkArray 를 별도로 유지한다.)
/// </summary>
public sealed class StringPool
{
    private const int ChunkBits = 20;                 // 청크당 1,048,576 chars = 2MB
    private const int ChunkSize = 1 << ChunkBits;
    public const int MaxNameLength = 0xFFFF;

    private readonly List<char[]> _chunks = new();
    private char[][] _chunkArray = Array.Empty<char[]>();
    private char[] _current;
    private int _pos;

    public StringPool()
    {
        _current = new char[ChunkSize];
        _chunks.Add(_current);
        _chunkArray = _chunks.ToArray();
    }

    /// <summary>풀에 저장된 총 문자 수(대략적인 메모리 사용량 = 2 * 이 값 바이트).</summary>
    public long CharCount => (long)(_chunks.Count - 1) * ChunkSize + _pos;

    /// <summary>
    /// 핸들 레이아웃: [chunkIndex : 28bit][offset : 20bit][length : 16bit]
    /// 청크 2^28개 * 2MB 까지 표현 가능하므로 사실상 제한 없음.
    /// </summary>
    public long Add(ReadOnlySpan<char> name)
    {
        int len = name.Length;
        if (len > MaxNameLength) len = MaxNameLength;

        if (_pos + len > ChunkSize)
        {
            _current = new char[ChunkSize];
            _chunks.Add(_current);
            _chunkArray = _chunks.ToArray();   // 조회 스레드를 위한 원자적 교체
            _pos = 0;
        }

        name[..len].CopyTo(_current.AsSpan(_pos));
        long handle = ((long)(_chunks.Count - 1) << 36) | ((long)_pos << 16) | (uint)len;
        _pos += len;
        return handle;
    }

    public ReadOnlySpan<char> Get(long handle)
    {
        if (handle < 0) return ReadOnlySpan<char>.Empty;
        int chunk = (int)(handle >> 36);
        int offset = (int)((handle >> 16) & 0xFFFFF);
        int len = (int)(handle & 0xFFFF);
        var arr = _chunkArray;
        if ((uint)chunk >= (uint)arr.Length) return ReadOnlySpan<char>.Empty;
        return arr[chunk].AsSpan(offset, len);
    }

    public string GetString(long handle) => new string(Get(handle));

    public int GetLength(long handle) => handle < 0 ? 0 : (int)(handle & 0xFFFF);
}

using DiskAnalyzer.Core.Interop;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Scanning;

/// <summary>
/// 파일 하나가 <b>디스크를 실제로 얼마나 차지하는가</b>로 바꿔 준다.
///
/// <para>탐색기의 "크기"는 논리 크기이고 "디스크 할당 크기"는 물리 크기다. 압축 폴더나 스파스 파일에서는
/// 둘이 크게 다르다 — 디스크 공간을 찾는 도구라면 물리 쪽이 정직하다(DaisyDisk 도 같은 이유로
/// physical disk space 를 쓴다).</para>
///
/// <para><b>파일마다 API 를 부르지 않는다.</b> 2,900만 파일에서 파일당 syscall 하나면 그것만으로 몇 분이다.
/// 대부분의 파일은 클러스터 경계로 올림한 값이 곧 할당 크기이므로 계산으로 끝내고,
/// <b>압축 · 스파스 속성이 붙은 것만</b> 실제로 물어본다. 그런 파일은 보통 전체의 1% 미만이다.</para>
/// </summary>
public sealed class AllocationSizer
{
    /// <summary>계산으로 답할 수 없어 실제로 물어본 파일 수. 비용이 어디서 났는지 보고할 때 쓴다.</summary>
    private int _queried;

    public AllocationSizer(SizeBasis basis, int bytesPerCluster)
    {
        Basis = basis;
        BytesPerCluster = bytesPerCluster > 0 ? bytesPerCluster : 4096;
    }

    public SizeBasis Basis { get; }
    public int BytesPerCluster { get; }
    public int QueriedFiles => Volatile.Read(ref _queried);

    /// <summary>논리 크기 그대로 쓰는가.</summary>
    public bool IsLogical => Basis == SizeBasis.Logical;

    /// <summary>
    /// 압축 · 스파스 파일의 실제 할당 크기를 물어본다. 실패하면 계산값으로 돌아간다.
    /// 호출자가 <see cref="NeedsQuery"/> 로 미리 걸러 <b>이 경로만</b> 부르게 한다
    /// (경로 문자열을 만드는 비용도 여기서만 든다).
    /// </summary>
    public long Query(long logicalSize, string fullPath)
    {
        Interlocked.Increment(ref _queried);
        long actual = Win32.GetCompressedSize(fullPath);
        return actual >= 0 ? actual : RoundUpToCluster(logicalSize);
    }

    /// <summary>계산으로는 알 수 없어 실제로 물어봐야 하는 파일인가.</summary>
    public static bool NeedsQuery(uint attributes)
        => (attributes & (Win32.FILE_ATTRIBUTE_COMPRESSED | Win32.FILE_ATTRIBUTE_SPARSE_FILE)) != 0;

    /// <summary>
    /// 클러스터 경계로 올린다. 0 바이트 파일은 0 그대로 둔다 —
    /// 빈 파일에 클러스터 하나를 붙이면 빈 파일이 수만 개인 트리에서 없는 용량이 생긴다.
    /// </summary>
    public long RoundUpToCluster(long size)
    {
        if (size <= 0) return 0;
        long c = BytesPerCluster;
        return (size + c - 1) / c * c;
    }
}

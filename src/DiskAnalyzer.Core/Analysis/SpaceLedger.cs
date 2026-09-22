using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Analysis;

/// <summary>
/// 스캔 합계와 OS 가 말하는 사용량을 맞춰 본 결과.
///
/// <para>디스크 분석기에서 신뢰를 가장 먼저 무너뜨리는 것은 "합계가 안 맞는다"이다.
/// 지금까지는 접근 거부 <b>건수</b>만 완료 메시지에 적었고, 그것이 <b>몇 바이트</b>인지는
/// 어디에도 없었다. 여기서 차이를 <see cref="Hidden"/> 으로 드러낸다 — 숨기지 않고 항목으로 만든다.</para>
///
/// <para>드라이브 루트를 스캔했을 때만 의미가 있다. 하위 폴더 스캔은 비교 대상이 없다.</para>
/// </summary>
public sealed class SpaceLedger
{
    /// <summary>스캔해서 실제로 센 바이트.</summary>
    public long Scanned { get; init; }

    /// <summary>드라이브가 쓰고 있다고 OS 가 말하는 바이트.</summary>
    public long VolumeUsed { get; init; }

    /// <summary>드라이브 전체 용량.</summary>
    public long VolumeTotal { get; init; }

    /// <summary>
    /// 사용량 − 스캔 합계. 권한 없어 못 읽은 폴더, 제외 규칙으로 건너뛴 것,
    /// 파일시스템 메타데이터($MFT · 저널 등) 가 여기 들어간다.
    /// </summary>
    public long Hidden { get; init; }

    /// <summary>남은 공간.</summary>
    public long Free { get; init; }

    /// <summary>드라이브 정보와 맞춰 볼 수 있는 스캔이었는가.</summary>
    public bool HasVolumeInfo { get; init; }

    /// <summary>드러낼 만큼 큰 차이인가. 몇 MB 수준의 오차까지 항목으로 만들면 오히려 소음이다.</summary>
    public bool HasHidden => HasVolumeInfo && Hidden >= HiddenThreshold;

    /// <summary>이보다 작은 차이는 항목으로 만들지 않는다(64 MB).</summary>
    public const long HiddenThreshold = 64L * 1024 * 1024;

    public string ScannedText => SizeFormatter.Format(Scanned);
    public string HiddenText => SizeFormatter.Format(Hidden);
    public string FreeText => SizeFormatter.Format(Free);

    public static SpaceLedger None { get; } = new();

    /// <summary>
    /// 디스크 I/O 없이 숫자만으로 만든다 — 호출자가 <c>ScanResult.VolumeUsedBytes</c> 와
    /// <c>DriveInfoRow</c> 에서 값을 가져와 넘긴다.
    /// </summary>
    /// <param name="isVolumeRoot">드라이브 루트를 통째로 스캔했는가. 아니면 비교가 성립하지 않는다.</param>
    public static SpaceLedger Create(long scanned, long volumeUsed, long volumeTotal, bool isVolumeRoot)
    {
        if (!isVolumeRoot || volumeUsed <= 0 || volumeTotal <= 0)
            return new SpaceLedger { Scanned = scanned };

        // 스캔 합계가 사용량보다 클 수도 있다(하드 링크 · 압축 · 스파스 파일).
        // 그때 음수 "숨은 공간"을 만들면 거짓말이 되므로 0 으로 눕힌다.
        long hidden = Math.Max(0, volumeUsed - scanned);

        return new SpaceLedger
        {
            Scanned = scanned,
            VolumeUsed = volumeUsed,
            VolumeTotal = volumeTotal,
            Hidden = hidden,
            Free = Math.Max(0, volumeTotal - volumeUsed),
            HasVolumeInfo = true,
        };
    }

    /// <summary>숨은 공간이 왜 생겼는지 한 줄로. 건수는 호출자가 스캔 결과에서 준다.</summary>
    public string ExplainHidden(int accessDenied, int skipped, int excludedFolders)
    {
        if (!HasHidden) return string.Empty;

        var parts = new List<string>(3);
        if (accessDenied > 0) parts.Add($"접근 거부 {accessDenied:N0}건");
        if (skipped > 0) parts.Add($"건너뜀 {skipped:N0}건");
        if (excludedFolders > 0) parts.Add($"제외 규칙 {excludedFolders:N0}폴더");

        return parts.Count == 0
            ? "파일시스템 메타데이터($MFT · 저널 등)일 가능성이 높습니다."
            : string.Join(" · ", parts) + " + 파일시스템 메타데이터";
    }
}

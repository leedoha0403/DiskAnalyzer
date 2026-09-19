using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Analysis;

/// <summary>
/// 8. 정리 추천 점수 (100점 만점).
///
/// 보호 등급과 완전히 분리된 축이다. 보호 등급은 "삭제해도 되는가", 이 점수는 "삭제할 가치가 있는가".
/// <strong>P0 / P1 에는 점수를 계산하지 않는다</strong> - 애초에 정리 대상이 아니기 때문이다.
///
/// 가중치
///   마지막 수정 후 경과 기간   0 ~ 30
///   파일 크기                 0 ~ 25
///   마지막 접근 후 경과 기간   0 ~ 20
///   Cache / Temp 여부         0 ~ 15
///   재생성 가능 여부           0 ~ 10
/// </summary>
public static class CleanupScorer
{
    private const long Mb = 1024L * 1024L;

    public readonly record struct Input(
        long Size,
        int AgeDays,
        int UnusedDays,
        bool AccessReliable,
        bool IsCacheOrTemp,
        bool IsRegenerable);

    public static int Score(Input input)
    {
        int score = 0;

        // 마지막 수정 후 경과 기간 (0~30) — 2년이면 만점
        score += Curve(input.AgeDays, 730, 30);

        // 파일 크기 (0~25) — 로그 스케일.
        // 선형으로 하면 초대형 파일 하나가 목록을 지배해 "작지만 확실한" 후보가 밀려난다.
        if (input.Size > 0)
        {
            double mb = input.Size / (double)Mb;
            double normalized = Math.Log2(mb + 1) / Math.Log2(20481);   // 20GB 에서 1.0
            score += (int)Math.Round(Math.Clamp(normalized, 0d, 1d) * 25);
        }

        // 마지막 접근 후 경과 기간 (0~20)
        // 접근 시각을 믿을 수 없는 시스템에서는 이 항목을 빼고 수정일 쪽에 의존한다(56).
        if (input.AccessReliable) score += Curve(input.UnusedDays, 365, 20);
        else score += Curve(input.AgeDays, 365, 10);   // 절반만 반영

        // Cache / Temp 여부 (0~15)
        if (input.IsCacheOrTemp) score += 15;

        // 재생성 가능 여부 (0~10)
        if (input.IsRegenerable) score += 10;

        return Math.Clamp(score, 0, 100);
    }

    public static RecommendTier TierOf(int score) => ProtectionText.TierOf(score);

    /// <summary>0 ~ max 구간을 제곱근 곡선으로 매핑한다. 초반 변화가 크고 뒤로 갈수록 완만해진다.</summary>
    private static int Curve(int days, int saturationDays, int max)
    {
        if (days <= 0) return 0;
        double ratio = Math.Min(1d, days / (double)saturationDays);
        return (int)Math.Round(Math.Sqrt(ratio) * max);
    }
}

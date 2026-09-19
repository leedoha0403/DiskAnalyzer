namespace DiskAnalyzer.Core.Models;

/// <summary>
/// 보호 등급. "삭제해도 되는가"에만 답한다. "삭제할 가치가 있는가"(정리 추천)와는 별개 축이다.
///
/// 숫자가 클수록 강한 보호다. 한 대상이 여러 규칙에 걸리면 항상 <c>Math.Max</c> 로 가장 강한 등급이 이긴다.
/// 정리 가능 규칙이 보호 규칙을 덮어쓰는 일은 구조적으로 불가능하다.
/// </summary>
public enum ProtectionLevel
{
    /// <summary>일반 파일. 삭제 가능(= 삭제 추천이라는 뜻은 아니다).</summary>
    P3 = 0,
    /// <summary>정리 가능. 재생성 가능하고 조건을 충족한 대상.</summary>
    P2 = 1,
    /// <summary>고위험. 기본 미선택, 강한 경고 후에만 삭제.</summary>
    P1 = 2,
    /// <summary>절대 보호. 선택/삭제 불가.</summary>
    P0 = 3,
}

/// <summary>8. 정리 추천 점수 구간.</summary>
public enum RecommendTier
{
    None,      //  0 ~ 29  추천 안 함
    Reference, // 30 ~ 59  참고 대상
    Suggested, // 60 ~ 79  정리 추천
    Priority,  // 80 ~ 100 우선 정리 추천
}

/// <summary>보호 등급 판정 결과.</summary>
public sealed class ProtectionVerdict
{
    public required ProtectionLevel Level { get; init; }

    /// <summary>왜 이 등급인지. UI 에 그대로 보여 준다.</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Reparse Point(Junction / Symbolic Link / Mount Point).
    /// 의도치 않은 다른 경로까지 지워지는 사고를 막기 위해 이 지점에서 재귀 탐색·삭제를 중단한다.
    /// </summary>
    public bool BlockRecursion { get; init; }

    /// <summary>알려진 Temp / Cache / Dump 경로인지(P2 후보 조건 중 하나).</summary>
    public bool RegenerableLocation { get; init; }

    public bool CanDelete => Level != ProtectionLevel.P0;
    public bool AutoSelectable => Level <= ProtectionLevel.P2;

    public string LevelText => ProtectionText.Of(Level);
    public string BadgeText => ProtectionText.Badge(Level);

    public static ProtectionVerdict P3Default { get; } =
        new() { Level = ProtectionLevel.P3, Reason = "일반 사용자 파일입니다." };
}

public static class ProtectionText
{
    public static string Of(ProtectionLevel level) => level switch
    {
        ProtectionLevel.P0 => "P0 절대 보호",
        ProtectionLevel.P1 => "P1 고위험",
        ProtectionLevel.P2 => "P2 정리 가능",
        _ => "P3 일반",
    };

    public static string Badge(ProtectionLevel level) => level switch
    {
        ProtectionLevel.P0 => "🔒 보호됨",
        ProtectionLevel.P1 => "⚠ 고위험",
        ProtectionLevel.P2 => "정리 가능",
        _ => "일반",
    };

    public static string Of(RecommendTier tier) => tier switch
    {
        RecommendTier.Priority => "우선 정리 추천",
        RecommendTier.Suggested => "정리 추천",
        RecommendTier.Reference => "참고 대상",
        _ => "추천 안 함",
    };

    public static RecommendTier TierOf(int score) => score switch
    {
        >= 80 => RecommendTier.Priority,
        >= 60 => RecommendTier.Suggested,
        >= 30 => RecommendTier.Reference,
        _ => RecommendTier.None,
    };
}

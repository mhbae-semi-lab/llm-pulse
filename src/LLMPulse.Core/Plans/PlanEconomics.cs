namespace LLMPulse.Core.Plans;

/// <summary>구독 정액 vs API 종량제 비교용 단순 계산 (블렌디드 가격 = 입력:출력 3:1 가중 평균 $/1M 토큰).</summary>
public static class PlanEconomics
{
    /// <summary>정액 월 요금과 같은 비용이 되는 월 토큰 수 (백만 토큰). 가격이 0 이하면 null.</summary>
    public static double? BreakEvenMillionTokens(double monthlyUsd, double blendedPerM) =>
        blendedPerM > 0 && monthlyUsd >= 0 ? monthlyUsd / blendedPerM : null;

    public static double ApiCost(double millionTokens, double blendedPerM) => millionTokens * blendedPerM;
}

using LLMPulse.Core.Scoring;
using LLMPulse.Core.Sources;

namespace LLMPulse.Core;

public enum DisagreementKind
{
    /// <summary>AA 가격과 OpenRouter 가격이 크게 다름.</summary>
    Price,
    /// <summary>AA 지능 지수 순위(벤치마크)와 Arena 종합 순위(사람 선호)가 크게 다름.</summary>
    QualityRank,
}

/// <param name="A">Price: AA 블렌디드 $/1M, QualityRank: AA 지능 지수 백분위.</param>
/// <param name="B">Price: OpenRouter 블렌디드 $/1M, QualityRank: Arena 종합 백분위.</param>
/// <param name="Gap">Price: 차이 비율(%), QualityRank: 백분위 차(점).</param>
public sealed record Disagreement(long ModelId, DisagreementKind Kind, double A, double B, double Gap);

/// <summary>
/// 같은 모델을 두 소스가 다르게 말하는 경우를 찾는다. 검사 장비에서 두 측정기가 어긋나면 표시하듯,
/// 값을 고르지 않고 "어긋남" 자체를 보여 준다.
/// </summary>
public static class SourceDisagreement
{
    /// <summary>
    /// 가격 차이가 이 비율(%) 이상이면 경고. 2026-11-21 실데이터(135쌍)에서 오픈 웨이트 모델은 제공사별 가격 차이로
    /// 중앙값이 25% 라 25% 기준은 절반을 표시했다. 2배(100%) 이상만 매칭 오류 · 오래된 가격 후보로 본다 (17쌍).
    /// </summary>
    public const double PriceGapPct = 100;

    /// <summary>두 순위 백분위 차가 이 점수 이상이면 경고 (0–100 척도).</summary>
    public const double RankGapPoints = 40;

    /// <summary>백분위 비교에 필요한 최소 공통 모델 수 (너무 적으면 백분위가 의미 없다).</summary>
    public const int MinCommonModels = 10;

    public static List<Disagreement> Find(
        IReadOnlyList<ModelInput> models,
        IReadOnlyDictionary<long, (double Primary, double OpenRouter)> pricePairs)
    {
        var result = new List<Disagreement>();

        foreach (var (id, (a, b)) in pricePairs)
        {
            if (a <= 0 || b <= 0) continue;
            var gap = Math.Abs(a - b) / Math.Min(a, b) * 100;
            if (gap >= PriceGapPct) result.Add(new Disagreement(id, DisagreementKind.Price, a, b, gap));
        }

        // 두 지표가 모두 있는 모델끼리만 백분위를 매겨 비교한다
        var both = models
            .Where(m => m.Metrics.ContainsKey(MetricIds.IntelligenceIndex) && m.Metrics.ContainsKey(MetricIds.LmArenaOverall))
            .ToList();
        if (both.Count >= MinCommonModels)
        {
            var aa = Normalizer.Percentile(both.ToDictionary(m => m.Id, m => m.Metrics[MetricIds.IntelligenceIndex]), true);
            var arena = Normalizer.Percentile(both.ToDictionary(m => m.Id, m => m.Metrics[MetricIds.LmArenaOverall]), true);
            foreach (var m in both)
            {
                var gap = Math.Abs(aa[m.Id] - arena[m.Id]);
                if (gap >= RankGapPoints) result.Add(new Disagreement(m.Id, DisagreementKind.QualityRank, aa[m.Id], arena[m.Id], gap));
            }
        }

        return result.OrderBy(d => d.Kind).ThenByDescending(d => d.Gap).ToList();
    }
}

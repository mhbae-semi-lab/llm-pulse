using LLMPulse.Core.Models;

namespace LLMPulse.Core.Scoring;

/// <summary>점수 계산 입력: 모델 한 개의 원본 지표 + 블렌디드 가격.</summary>
public sealed class ModelInput
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public string? Vendor { get; init; }
    public string? ReleaseDate { get; init; }
    public Dictionary<string, double> Metrics { get; init; } = new();
    public double? BlendedPrice { get; init; }
    public bool? OpenWeights { get; init; }
}

public sealed record CategoryScore(long ModelId, string CategoryId, double? Score, int? Rank, double Coverage);

public static class Normalizer
{
    /// <summary>
    /// 순위 백분위(0–100). 동점은 평균 순위. 값이 1개면 100.
    /// 낮을수록 좋은 지표는 뒤집는다 (가장 낮은 값 = 100).
    /// </summary>
    public static Dictionary<long, double> Percentile(
        IReadOnlyDictionary<long, double> values, bool higherIsBetter)
    {
        var result = new Dictionary<long, double>(values.Count);
        var n = values.Count;
        if (n == 0) return result;
        if (n == 1)
        {
            result[values.Keys.First()] = 100;
            return result;
        }

        var all = values.Values.ToArray();
        foreach (var (id, v) in values)
        {
            var worse = 0;
            var equal = 0;
            foreach (var other in all)
            {
                if (other == v) equal++;
                else if (higherIsBetter ? other < v : other > v) worse++;
            }

            result[id] = (worse + 0.5 * (equal - 1)) / (n - 1) * 100.0;
        }

        return result;
    }
}

public static class CategoryScorer
{
    /// <summary>분야 점수 최소 커버리지(값이 있는 지표의 가중치 합 / 분야 가중치 합). 미만이면 점수 없음.</summary>
    public const double MinCoverage = 0.5;

    /// <summary>
    /// 분야 점수(0–100) = 지표별 점수의 가중 평균.
    /// 합성 지수(index)는 원본 그대로, 그 외는 백분위. 누락 지표는 가용 가중치로 재정규화.
    /// 점수가 있는 모델에는 내림차순 순위(동점 공동 순위)를 부여한다.
    /// </summary>
    public static List<CategoryScore> Score(
        CategoryDef category,
        IReadOnlyDictionary<string, MetricDef> metricDefs,
        IReadOnlyList<ModelInput> models)
    {
        var perMetric = PerMetricScores(category, metricDefs, models);

        // 커버리지는 지표 개수가 아니라 가중치 몫: 가중치 큰 지표(예: 코딩)가 빠지고 작은 지표(속도)만 있는 모델이
        // 분야 1위가 되지 않게 한다. 가중치가 모두 같으면 개수 기준과 같다.
        var totalWeight = category.Metrics.Sum(m => Math.Max(0, m.Weight));
        var scored = new List<CategoryScore>(models.Count);

        foreach (var m in models)
        {
            double weightSum = 0, acc = 0;
            foreach (var cm in category.Metrics)
            {
                if (perMetric.TryGetValue(cm.Id, out var map) && map.TryGetValue(m.Id, out var s))
                {
                    acc += s * cm.Weight;
                    weightSum += cm.Weight;
                }
            }

            var coverage = totalWeight <= 0 ? 0 : weightSum / totalWeight;
            double? score = coverage >= MinCoverage && weightSum > 0 ? acc / weightSum : null;
            scored.Add(new CategoryScore(m.Id, category.Id, score, null, coverage));
        }

        return AssignRanks(scored);
    }

    /// <summary>metricId -> (modelId -> 0-100) 지표별 점수 맵.</summary>
    private static Dictionary<string, Dictionary<long, double>> PerMetricScores(
        CategoryDef category,
        IReadOnlyDictionary<string, MetricDef> metricDefs,
        IReadOnlyList<ModelInput> models)
    {
        var perMetric = new Dictionary<string, Dictionary<long, double>>();
        foreach (var cm in category.Metrics)
        {
            if (!metricDefs.TryGetValue(cm.Id, out var def)) continue;

            var raw = new Dictionary<long, double>();
            foreach (var m in models)
            {
                if (TryGetRaw(m, cm.Id, out var v)) raw[m.Id] = v;
            }

            perMetric[cm.Id] = def.IsPassthrough
                ? raw.ToDictionary(kv => kv.Key, kv => Math.Clamp(kv.Value, 0, 100))
                : Normalizer.Percentile(raw, def.HigherIsBetter);
        }

        return perMetric;
    }

    /// <summary>한 모델의 분야 점수를 지표별 기여도로 분해한다 (기여도 내림차순). 값이 없는 지표는 빠지고 가중치는 재정규화.</summary>
    public static List<MetricContribution> Breakdown(
        CategoryDef category,
        IReadOnlyDictionary<string, MetricDef> metricDefs,
        IReadOnlyList<ModelInput> models,
        long modelId)
    {
        var perMetric = PerMetricScores(category, metricDefs, models);
        var parts = new List<(string Id, double Score, double Weight)>();
        foreach (var cm in category.Metrics)
            if (perMetric.TryGetValue(cm.Id, out var map) && map.TryGetValue(modelId, out var s))
                parts.Add((cm.Id, s, cm.Weight));

        var total = parts.Sum(p => p.Weight);
        if (total <= 0) return [];
        return parts
            .Select(p => new MetricContribution(p.Id, p.Score, p.Weight / total, p.Score * p.Weight / total))
            .OrderByDescending(x => x.Contribution)
            .ToList();
    }
    public static bool TryGetRaw(ModelInput m, string metricId, out double value)
    {
        if (metricId == MetricKeys.BlendedPrice)
        {
            if (m.BlendedPrice is { } p) { value = p; return true; }
            value = 0;
            return false;
        }

        return m.Metrics.TryGetValue(metricId, out value);
    }

    private static List<CategoryScore> AssignRanks(List<CategoryScore> scored)
    {
        var ordered = scored.Where(s => s.Score is not null)
            .OrderByDescending(s => s.Score).ToList();
        var ranks = new Dictionary<long, int>();
        for (var i = 0; i < ordered.Count; i++)
        {
            // 공동 순위: 같은 점수는 같은 순위
            ranks[ordered[i].ModelId] = i > 0 && ordered[i].Score == ordered[i - 1].Score
                ? ranks[ordered[i - 1].ModelId]
                : i + 1;
        }

        return scored.Select(s => ranks.TryGetValue(s.ModelId, out var r) ? s with { Rank = r } : s).ToList();
    }
}

/// <summary>metrics.json 의 파생 지표 id (DB raw_metric 이 아닌 price 테이블에서 옴).</summary>
public static class MetricKeys
{
    public const string BlendedPrice = "price_blended_per_m";
}

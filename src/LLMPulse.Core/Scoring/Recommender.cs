namespace LLMPulse.Core.Scoring;

public sealed record Recommendation(
    string CategoryId,
    CategoryScore? Best,
    CategoryScore? BestValue,
    double? BestValuePrice,
    double Tolerance);

public static class Recommender
{
    public const double DefaultTolerance = 0.9;

    /// <summary>점수 높은 순(동점이면 싼 쪽)으로 상위 n 개. allowed 로 후보를 제한할 수 있다.</summary>
    public static List<CategoryScore> TopN(
        IReadOnlyList<CategoryScore> scores,
        IReadOnlyDictionary<long, ModelInput> models,
        int n,
        Func<long, bool>? allowed = null) =>
        scores
            .Where(s => s.Score is not null && (allowed?.Invoke(s.ModelId) ?? true))
            .OrderByDescending(s => s.Score)
            .ThenBy(s => PriceOf(models, s.ModelId) ?? double.MaxValue)
            .Take(n)
            .ToList();
    /// <summary>
    /// Best = 분야 1위(동점이면 더 싼 모델). 
    /// Best Value = 점수가 최고점의 tolerance 이상인 모델 중 블렌디드 가격 최저(동가면 점수 높은 쪽).
    /// 경계는 포함(score &gt;= best * tolerance). 가격 없는 모델은 Best Value 후보에서 제외.
    /// </summary>
    public static Recommendation Recommend(
        string categoryId,
        IReadOnlyList<CategoryScore> scores,
        IReadOnlyDictionary<long, ModelInput> models,
        bool valueMode,
        double tolerance = DefaultTolerance,
        Func<long, bool>? allowed = null)
    {
        var pool = scores
            .Where(s => s.Score is not null && (allowed?.Invoke(s.ModelId) ?? true))
            .ToList();

        if (pool.Count == 0)
            return new Recommendation(categoryId, null, null, null, tolerance);

        var best = pool
            .OrderByDescending(s => s.Score)
            .ThenBy(s => PriceOf(models, s.ModelId) ?? double.MaxValue)
            .First();

        CategoryScore? value = null;
        double? valuePrice = null;
        if (valueMode)
        {
            var threshold = best.Score!.Value * tolerance;
            var pick = pool
                .Where(s => s.Score >= threshold && PriceOf(models, s.ModelId) is not null)
                .OrderBy(s => PriceOf(models, s.ModelId))
                .ThenByDescending(s => s.Score)
                .FirstOrDefault();
            if (pick is not null)
            {
                value = pick;
                valuePrice = PriceOf(models, pick.ModelId);
            }
        }

        return new Recommendation(categoryId, best, value, valuePrice, tolerance);
    }

    /// <summary>
    /// 가격(낮을수록) 대 점수(높을수록) 파레토 프론티어: 더 싸거나 같은 모델 중 자기보다 점수 높은 모델이 없는 점.
    /// </summary>
    public static IReadOnlyList<long> ParetoFrontier(
        IReadOnlyList<CategoryScore> scores,
        IReadOnlyDictionary<long, ModelInput> models)
    {
        var points = scores
            .Where(s => s.Score is not null && PriceOf(models, s.ModelId) is not null)
            .Select(s => (s.ModelId, Score: s.Score!.Value, Price: PriceOf(models, s.ModelId)!.Value))
            .OrderBy(p => p.Price)
            .ThenByDescending(p => p.Score)
            .ToList();

        var frontier = new List<long>();
        var bestSoFar = double.NegativeInfinity;
        foreach (var p in points)
        {
            if (p.Score > bestSoFar)
            {
                frontier.Add(p.ModelId);
                bestSoFar = p.Score;
            }
        }

        return frontier;
    }

    private static double? PriceOf(IReadOnlyDictionary<long, ModelInput> models, long id) =>
        models.TryGetValue(id, out var m) ? m.BlendedPrice : null;
}

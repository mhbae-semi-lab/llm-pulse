using LLMPulse.Core.Models;

namespace LLMPulse.Core.Scoring;

public enum TopChangeCause
{
    /// <summary>새 1위가 이전 시점에는 점수가 없었다 (새 모델이거나 데이터가 새로 붙음).</summary>
    NewEntry,
    /// <summary>이전 1위가 지금은 점수가 없다 (데이터가 빠짐).</summary>
    PreviousUnscored,
    /// <summary>이전 데이터를 지금 계산 기준으로 다시 매겨도 새 1위가 1위다: 데이터가 아니라 기준(지표 제외 · 가중치)이 바뀌었다.</summary>
    RuleChanged,
    /// <summary>지표 값이 바뀌어 순위가 뒤집혔다. MetricId 가 가장 크게 기여한 지표.</summary>
    MetricShift,
}

/// <param name="NewBefore">새 1위의 해당 지표 원본 값 (이전 → 지금).</param>
/// <param name="OldBefore">이전 1위의 해당 지표 원본 값 (이전 → 지금).</param>
public sealed record TopChangeReason(
    TopChangeCause Cause, string? MetricId = null,
    double? NewBefore = null, double? NewAfter = null, double? OldBefore = null, double? OldAfter = null);

/// <summary>"어제 1위 → 오늘 1위" 가 왜 바뀌었는지 지표 단위로 설명한다.</summary>
public static class TopChangeExplainer
{
    public static TopChangeReason Explain(
        CategoryDef category,
        IReadOnlyDictionary<string, MetricDef> metricDefs,
        IReadOnlyList<ModelInput> before,
        IReadOnlyList<ModelInput> after,
        long oldTop,
        long newTop)
    {
        var scoresBefore = CategoryScorer.Score(category, metricDefs, before);
        var scoresAfter = CategoryScorer.Score(category, metricDefs, after);
        if (scoresBefore.FirstOrDefault(s => s.ModelId == newTop)?.Score is null) return new TopChangeReason(TopChangeCause.NewEntry);
        if (scoresAfter.FirstOrDefault(s => s.ModelId == oldTop)?.Score is null) return new TopChangeReason(TopChangeCause.PreviousUnscored);

        // 이전 데이터 + 지금 기준으로도 새 1위가 앞서면, 바뀐 것은 데이터가 아니라 계산 기준이다
        var newB = scoresBefore.First(s => s.ModelId == newTop).Score!.Value;
        var oldB = scoresBefore.First(s => s.ModelId == oldTop).Score;
        if (oldB is null || newB > oldB) return new TopChangeReason(TopChangeCause.RuleChanged);

        // 두 모델의 지표별 기여도 차이(새 − 이전)가 가장 많이 늘어난 지표
        static Dictionary<string, double> Gap(List<MetricContribution> n, List<MetricContribution> o)
        {
            var d = n.ToDictionary(x => x.MetricId, x => x.Contribution);
            foreach (var x in o) d[x.MetricId] = d.GetValueOrDefault(x.MetricId) - x.Contribution;
            return d;
        }

        var gapBefore = Gap(CategoryScorer.Breakdown(category, metricDefs, before, newTop), CategoryScorer.Breakdown(category, metricDefs, before, oldTop));
        var gapAfter = Gap(CategoryScorer.Breakdown(category, metricDefs, after, newTop), CategoryScorer.Breakdown(category, metricDefs, after, oldTop));
        var metric = gapAfter.Keys.Union(gapBefore.Keys)
            .OrderByDescending(id => gapAfter.GetValueOrDefault(id) - gapBefore.GetValueOrDefault(id))
            .ThenBy(id => id, StringComparer.Ordinal)
            .First();

        return new TopChangeReason(TopChangeCause.MetricShift, metric,
            Raw(before, newTop, metric), Raw(after, newTop, metric), Raw(before, oldTop, metric), Raw(after, oldTop, metric));
    }

    private static double? Raw(IReadOnlyList<ModelInput> models, long id, string metricId) =>
        models.FirstOrDefault(m => m.Id == id) is { } m && CategoryScorer.TryGetRaw(m, metricId, out var v) ? v : null;
}

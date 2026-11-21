using LLMPulse.Core.Models;

namespace LLMPulse.Core.Scoring;

/// <summary>사용자가 조정한 지표 가중치를 분야 정의에 적용한다. 가중치 0 이하인 지표는 분야에서 빠진다.</summary>
public static class WeightOverrides
{
    /// <summary>
    /// 분야의 실제 계산용 정의. overrides(metricId → weight)가 있으면 기본 가중치를 덮어쓴다.
    /// 결과가 비면(모두 0) 사용자 조정을 무시하고 기본 가중치로 돌아간다.
    /// </summary>
    public static CategoryDef Effective(CategoryDef cat, IReadOnlyDictionary<string, double>? overrides)
    {
        var metrics = cat.Metrics
            .Select(m => new CategoryMetricRef
            {
                Id = m.Id,
                Weight = overrides is not null && overrides.TryGetValue(m.Id, out var w) ? w : m.Weight,
            })
            .Where(m => m.Weight > 0)
            .ToList();

        if (metrics.Count == 0 && overrides is not null) return Effective(cat, null);
        if (metrics.Count == 0) return cat;

        return new CategoryDef { Id = cat.Id, ValueMode = cat.ValueMode, Name = cat.Name, Metrics = metrics };
    }

    /// <summary>기본값과 같은 항목은 지우고 다른 것만 남긴다 (저장 시 불필요한 override 방지). 전부 같으면 null.</summary>
    public static Dictionary<string, double>? Normalize(CategoryDef cat, IReadOnlyDictionary<string, double> edited)
    {
        var diff = new Dictionary<string, double>();
        foreach (var m in cat.Metrics)
        {
            if (!edited.TryGetValue(m.Id, out var w)) continue;
            w = Math.Max(0, Math.Round(w, 2));
            if (Math.Abs(w - m.Weight) > 1e-9) diff[m.Id] = w;
        }

        return diff.Count == 0 ? null : diff;
    }
}

/// <summary>모델 한 개가 분야 점수에 어떻게 기여했는지(지표별).</summary>
public sealed record MetricContribution(string MetricId, double Score, double WeightShare, double Contribution);

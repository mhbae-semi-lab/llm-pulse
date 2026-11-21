using LLMPulse.Core.Scoring;

namespace LLMPulse.Core.Plans;

/// <summary>구독에서 모델을 쓸 수 있는 정도. 값이 클수록 더 자유롭다.</summary>
public enum PlanAccess
{
    None = 0,
    /// <summary>쓸 수는 있지만 사용량 풀/횟수 제한이 빡빡함.</summary>
    Limited = 1,
    Full = 2,
}

public static class PlanAccessResolver
{
    /// <summary>수동 보정(모델 이름 기준)이 있으면 그것이, 없으면 플랜 규칙(제작사/이름)이 결정한다.</summary>
    public static PlanAccess Resolve(PlanDef plan, ModelInput model, IReadOnlyDictionary<string, PlanAccess>? overridesForPlan)
    {
        if (overridesForPlan is not null && overridesForPlan.TryGetValue(model.Name, out var o)) return o;
        return PlanCatalog.Matches(plan, model) ? PlanAccess.Full : PlanAccess.None;
    }

    /// <summary>선택된 플랜들 중 가장 자유로운 접근 수준.</summary>
    public static PlanAccess Best(
        IEnumerable<PlanDef> selected,
        ModelInput model,
        IReadOnlyDictionary<string, Dictionary<string, PlanAccess>> overrides)
    {
        var best = PlanAccess.None;
        foreach (var p in selected)
        {
            overrides.TryGetValue(p.Id, out var o);
            var a = Resolve(p, model, o);
            if (a > best) best = a;
            if (best == PlanAccess.Full) break;
        }

        return best;
    }
}
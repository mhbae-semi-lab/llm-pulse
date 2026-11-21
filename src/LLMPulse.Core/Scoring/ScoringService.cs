using LLMPulse.Core.Storage;

namespace LLMPulse.Core.Scoring;

public sealed class CategoryResult
{
    public required Models.CategoryDef Category { get; init; }
    public required IReadOnlyList<CategoryScore> Scores { get; init; }
    public required Recommendation Recommendation { get; init; }
    public required IReadOnlyList<long> Pareto { get; init; }
    /// <summary>전일(이전 스냅샷) 대비 1위가 바뀌었는지.</summary>
    public bool TopChanged { get; init; }
    public long? PreviousTopModelId { get; init; }
    /// <summary>내 구독/접근 수단 안에서의 최고 성능 모델 (구독은 정액이라 가격 대신 성능 순위). 구독 미선택이면 null.</summary>
    public CategoryScore? PlanBest { get; init; }
    /// <summary>PlanBest 모델의 접근 수준 (한도 있음 표시용).</summary>
    public Plans.PlanAccess PlanBestAccess { get; init; }
    /// <summary>오래된 소스라서 이 분야 계산에서 뺀 지표 id (없으면 빈 목록).</summary>
    public IReadOnlyList<string> ExcludedMetrics { get; init; } = [];
    /// <summary>Shadow: 뺀 지표까지 넣었다면 1위였을 모델. 결정(Recommendation)에는 쓰지 않고 옆에만 보여 준다.</summary>
    public CategoryScore? ShadowBest { get; init; }
    /// <summary>TopChanged 일 때 바뀐 이유.</summary>
    public TopChangeReason? TopChangeReason { get; init; }
}

public sealed class ScoringSnapshot
{
    public required IReadOnlyDictionary<long, ModelInput> Models { get; init; }
    public required IReadOnlyList<CategoryResult> Categories { get; init; }
    public int NewModelCount { get; init; }
    public int TopChangeCount => Categories.Count(c => c.TopChanged);
}

public sealed class ScoringService
{
    private readonly SqliteStore _store;
    private readonly Catalog _catalog;

    public ScoringService(SqliteStore store, Catalog catalog)
    {
        _store = store;
        _catalog = catalog;
    }

    /// <summary>DB 의 최신 원본 지표로 모든 분야 점수·추천을 계산한다 (저장하지 않음).</summary>
    public ScoringSnapshot Build(DateOnly today, double tolerance = Recommender.DefaultTolerance, Func<ModelInput, Plans.PlanAccess>? planAccess = null,
        bool planOnly = false,
        IReadOnlyDictionary<string, Dictionary<string, double>>? weights = null)
    {
        var inputs = _store.LoadModelInputs();
        var models = inputs.ToDictionary(m => m.Id);
        var date = today.ToString("yyyy-MM-dd");
        var excluded = ExcludedMetrics(today);
        List<ModelInput>? previousInputs = null;

        var results = new List<CategoryResult>();
        foreach (var original in _catalog.Categories)
        {
            Dictionary<string, double>? custom = null;
            weights?.TryGetValue(original.Id, out custom);
            var full = WeightOverrides.Effective(original, custom);
            var excludedHere = full.Metrics.Where(m => excluded.Contains(m.Id)).Select(m => m.Id).ToList();
            var cat = excludedHere.Count == 0 ? full : new Models.CategoryDef
            {
                Id = full.Id, ValueMode = full.ValueMode, Name = full.Name,
                Metrics = full.Metrics.Where(m => !excluded.Contains(m.Id)).ToList(),
            };
            var scores = CategoryScorer.Score(cat, _catalog.MetricsById, inputs);
            var shadow = excludedHere.Count == 0 ? null
                : Recommender.TopN(CategoryScorer.Score(full, _catalog.MetricsById, inputs), models, 1).FirstOrDefault();
            Func<long, bool>? inPlan = planAccess is null ? null : id => planAccess(models[id]) != Plans.PlanAccess.None;

            // 전체 기준 추천은 항상 계산한다 (1위 교체 감지는 구독과 무관해야 한다)
            var globalRec = Recommender.Recommend(cat.Id, scores, models, cat.ValueMode, tolerance);
            var planRec = inPlan is null ? null : Recommender.Recommend(cat.Id, scores, models, cat.ValueMode, tolerance, inPlan);
            var planBest = planRec?.Best;
            var rec = planOnly && planRec is not null ? planRec : globalRec;
            var scoresForPareto = planOnly && inPlan is not null ? scores.Where(s => inPlan(s.ModelId)).ToList() : scores;
            var pareto = cat.ValueMode ? Recommender.ParetoFrontier(scoresForPareto, models) : [];

            var prevTop = _store.PreviousTopModel(cat.Id, date);
            // 가중치를 직접 바꾼 분야는 저장된(기본 가중치) 1위와 비교할 수 없다
            // 이전 1위가 지금도 공동 1위면 바뀐 것이 아니다 (동점 정렬 차이로 깜빡이지 않게)
            var changed = custom is null && prevTop is not null && globalRec.Best is not null && prevTop != globalRec.Best.ModelId
                          && scores.FirstOrDefault(s => s.ModelId == prevTop)?.Rank != 1;
            TopChangeReason? reason = null;
            if (changed && _store.PreviousScoreDate(cat.Id, date) is { } prevDate)
            {
                previousInputs ??= _store.LoadModelInputsAsOf(prevDate);
                reason = TopChangeExplainer.Explain(cat, _catalog.MetricsById, previousInputs, inputs, prevTop!.Value, globalRec.Best!.ModelId);
            }

            results.Add(new CategoryResult
            {
                // 지표가 모두 빠진 분야는 화면이 기본 지표를 알 수 있게 원래 정의를 둔다 (점수는 모두 비어 있음)
                Category = cat.Metrics.Count > 0 ? cat : full,
                ExcludedMetrics = excludedHere,
                ShadowBest = shadow,
                TopChangeReason = reason,
                Scores = scores,
                Recommendation = rec,
                Pareto = pareto,
                TopChanged = changed,
                PreviousTopModelId = prevTop,
                PlanBest = planBest,
                PlanBestAccess = planBest is not null && planAccess is not null ? planAccess(models[planBest.ModelId]) : Plans.PlanAccess.None,
            });
        }

        return new ScoringSnapshot
        {
            Models = models,
            Categories = results,
            NewModelCount = _store.CountNewModels(date),
        };
    }

    /// <summary>오래된 소스(SourceFreshness.StaleDays 초과)에서만 오는 지표. 사용자가 포함을 켰으면 비어 있다.</summary>
    public HashSet<string> ExcludedMetrics(DateOnly today)
    {
        if (_store.GetSetting(SourceFreshness.SettingIncludeStale) == "1") return [];
        var stale = _store.SourceFreshnessAll(today).Where(f => f.IsStale).Select(f => f.SourceId).ToList();
        return _store.MetricsOnlyFrom(stale);
    }

    /// <summary>오늘자 분야 점수를 category_score 에 저장 (전일 대비 변동 감지용).</summary>
    public void Persist(ScoringSnapshot snapshot, DateOnly today)
    {
        _store.SaveCategoryScores(
            today.ToString("yyyy-MM-dd"),
            snapshot.Categories.SelectMany(c => c.Scores));
    }
}

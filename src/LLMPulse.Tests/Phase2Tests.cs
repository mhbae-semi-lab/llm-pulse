using LLMPulse.Core;
using LLMPulse.Core.Models;
using LLMPulse.Core.Plans;
using LLMPulse.Core.Scoring;
using LLMPulse.Core.Sources;
using LLMPulse.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMPulse.Tests;

public class WeightAndBreakdownTests
{
    private static CategoryDef Cat(params (string id, double w)[] metrics) => new()
    {
        Id = "mix",
        ValueMode = true,
        Metrics = metrics.Select(m => new CategoryMetricRef { Id = m.id, Weight = m.w }).ToList(),
    };

    private static readonly IReadOnlyDictionary<string, MetricDef> Defs = new Dictionary<string, MetricDef>
    {
        ["a"] = new() { Id = "a", Scale = "index" },
        ["b"] = new() { Id = "b", Scale = "index" },
    };

    [Fact]
    public void Effective_AppliesOverridesAndDropsZeroWeights()
    {
        var cat = Cat(("a", 0.7), ("b", 0.3));
        var e = WeightOverrides.Effective(cat, new Dictionary<string, double> { ["a"] = 0, ["b"] = 2 });
        Assert.Equal(["b"], e.Metrics.Select(m => m.Id));
        Assert.Equal(2, e.Metrics[0].Weight);

        // 원본은 변하지 않는다
        Assert.Equal(0.7, cat.Metrics[0].Weight);
    }

    [Fact]
    public void Effective_AllZeroFallsBackToDefaults_AndNoOverrideDropsDefaultZeros()
    {
        var cat = Cat(("a", 0.6), ("b", 0.4), ("c", 0));
        var e = WeightOverrides.Effective(cat, new Dictionary<string, double> { ["a"] = 0, ["b"] = 0 });
        Assert.Equal(["a", "b"], e.Metrics.Select(m => m.Id));
        Assert.Equal(["a", "b"], WeightOverrides.Effective(cat, null).Metrics.Select(m => m.Id));
    }

    [Fact]
    public void Normalize_KeepsOnlyDifferencesFromDefault()
    {
        var cat = Cat(("a", 0.5), ("b", 0.5));
        Assert.Null(WeightOverrides.Normalize(cat, new Dictionary<string, double> { ["a"] = 0.5, ["b"] = 0.5 }));
        var d = WeightOverrides.Normalize(cat, new Dictionary<string, double> { ["a"] = 0.5, ["b"] = 0.8 })!;
        Assert.Equal(new Dictionary<string, double> { ["b"] = 0.8 }, d);
    }

    [Fact]
    public void Breakdown_SumsToScoreAndRenormalizesMissingMetrics()
    {
        var models = new List<ModelInput>
        {
            new() { Id = 1, Name = "x", Metrics = new() { ["a"] = 80, ["b"] = 40 } },
            new() { Id = 2, Name = "y", Metrics = new() { ["a"] = 60 } }, // b 없음
        };
        var cat = Cat(("a", 0.5), ("b", 0.5));

        var scoreById = CategoryScorer.Score(cat, Defs, models).ToDictionary(s => s.ModelId, s => s.Score!.Value);

        foreach (var m in models)
        {
            var parts = CategoryScorer.Breakdown(cat, Defs, models, m.Id);
            Assert.Equal(scoreById[m.Id], parts.Sum(p => p.Contribution), 6);
            Assert.Equal(1.0, parts.Sum(p => p.WeightShare), 6);
        }

        var y = CategoryScorer.Breakdown(cat, Defs, models, 2);
        Assert.Single(y);
        Assert.Equal(1.0, y[0].WeightShare);
        Assert.Equal("a", CategoryScorer.Breakdown(cat, Defs, models, 1)[0].MetricId); // 기여도 큰 순
    }

    [Fact]
    public void TopN_OrdersByScoreThenCheaperAndHonorsAllowed()
    {
        var models = new Dictionary<long, ModelInput>
        {
            [1] = new() { Id = 1, Name = "a", BlendedPrice = 10 },
            [2] = new() { Id = 2, Name = "b", BlendedPrice = 5 },
            [3] = new() { Id = 3, Name = "c", BlendedPrice = 1 },
            [4] = new() { Id = 4, Name = "d" },
        };
        var scores = new List<CategoryScore>
        {
            new(1, "x", 90, 1, 1), new(2, "x", 90, 1, 1), new(3, "x", 50, 3, 1), new(4, "x", null, null, 0),
        };

        Assert.Equal([2L, 1L, 3L], Recommender.TopN(scores, models, 5).Select(s => s.ModelId));
        Assert.Equal([3L], Recommender.TopN(scores, models, 5, id => id == 3).Select(s => s.ModelId));
        Assert.Single(Recommender.TopN(scores, models, 1));
    }

    [Fact]
    public void ScoringService_CustomWeightsChangeScoresAndSuppressTopChange()
    {
        var dir = Path.Combine(Path.GetTempPath(), "llmpulse-w-" + Guid.NewGuid().ToString("N"));
        try
        {
            var db = new Database(Path.Combine(dir, "t.db"), NullLogger<Database>.Instance);
            db.Migrate();
            var store = new SqliteStore(db);
            DemoData.Seed(store, DateTimeOffset.UtcNow);
            var catalog = Catalog.LoadEmbedded();
            var svc = new ScoringService(store, catalog);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            var baseline = svc.Build(today).Categories.First(c => c.Category.Id == "my_coding");
            var onlyPrice = svc.Build(today, weights: new Dictionary<string, Dictionary<string, double>>
            {
                ["my_coding"] = new()
                {
                    ["lmarena_coding"] = 0, ["aa_intelligence_index"] = 0, ["aider_polyglot"] = 0,
                    ["output_tok_s"] = 0, ["ttft_s"] = 0, ["price_blended_per_m"] = 1,
                },
            }).Categories.First(c => c.Category.Id == "my_coding");

            Assert.Equal(["price_blended_per_m"], onlyPrice.Category.Metrics.Select(m => m.Id));
            Assert.NotEqual(baseline.Recommendation.Best!.ModelId, onlyPrice.Recommendation.Best!.ModelId);
            Assert.False(onlyPrice.TopChanged);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}

public class ChangeFeedTests
{
    private static Dictionary<string, Dictionary<long, int>> R(string cat, params (long id, int rank)[] r) =>
        new() { [cat] = r.ToDictionary(x => x.id, x => x.rank) };

    [Fact]
    public void TopChange_RankMoves_Entered_AndNew()
    {
        var prev = R("coding", (1, 1), (2, 2), (3, 3), (4, 4), (5, 5), (6, 9), (7, 12));
        var cur = R("coding", (2, 1), (1, 2), (3, 3), (4, 4), (6, 5), (5, 6), (7, 10), (9, 8));

        var feed = ChangeFeed.Build(prev, cur, newModels: [9]);

        Assert.Equal(ChangeKind.NewModel, feed[0].Kind);
        Assert.Equal(9, feed[0].ModelId);
        Assert.Equal(8, feed[0].ToRank);

        var top = feed.Single(f => f.Kind == ChangeKind.TopChanged);
        Assert.Equal((2L, 1L), (top.ModelId, top.OtherModelId!.Value));

        var up = feed.Single(f => f.Kind == ChangeKind.RankUp);
        Assert.Equal((6L, 9, 5), (up.ModelId, up.FromRank!.Value, up.ToRank!.Value)); // 9 → 5

        var entered = feed.Single(f => f.Kind == ChangeKind.EnteredTop);
        Assert.Equal(7L, entered.ModelId);   // 12 → 10 : 이전엔 10위 밖
        Assert.DoesNotContain(feed, f => f.Kind == ChangeKind.RankDown && f.ModelId == 1); // 1→2 는 작은 변화
    }

    [Fact]
    public void TiedFirstPlace_IsNotATopChange()
    {
        var prev = R("c", (1, 1), (2, 1), (3, 3));
        var cur = R("c", (2, 1), (3, 2), (1, 3));
        Assert.DoesNotContain(ChangeFeed.Build(prev, cur, []), f => f.Kind == ChangeKind.TopChanged);
    }

    [Fact]
    public void NoPreviousCategory_OrLimit()
    {
        var cur = R("c", (1, 1));
        Assert.Empty(ChangeFeed.Build(new Dictionary<string, Dictionary<long, int>>(), cur, []));

        var many = Enumerable.Range(1, 50).Select(i => (long)i).ToList();
        Assert.Equal(5, ChangeFeed.Build(new Dictionary<string, Dictionary<long, int>>(), cur, many, limit: 5).Count);
    }

    [Fact]
    public void RankDown_IsReportedForTopTenDrops()
    {
        var prev = R("c", (1, 1), (2, 2), (3, 3), (4, 4));
        var cur = R("c", (1, 1), (3, 2), (4, 3), (5, 4), (6, 5), (2, 6));
        var down = ChangeFeed.Build(prev, cur, []).Single(f => f.Kind == ChangeKind.RankDown);
        Assert.Equal((2L, 2, 6), (down.ModelId, down.FromRank!.Value, down.ToRank!.Value));
    }
}

public class PlanEconomicsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "llmpulse-econ-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BreakEven_And_ApiCost()
    {
        Assert.Equal(40, PlanEconomics.BreakEvenMillionTokens(20, 0.5));
        Assert.Null(PlanEconomics.BreakEvenMillionTokens(20, 0));
        Assert.Equal(15, PlanEconomics.ApiCost(30, 0.5));
    }

    [Fact]
    public void Store_ScoreDatesRanksAndNewModels()
    {
        var db = new Database(Path.Combine(_dir, "t.db"), NullLogger<Database>.Instance);
        db.Migrate();
        var store = new SqliteStore(db);
        Assert.Equal((null, null), store.ScoreDates());

        DemoData.Seed(store, DateTimeOffset.Parse("2026-11-19T00:00:00Z")); // 14일 스냅샷 이력
        store.SaveCategoryScores("2026-11-18", [new CategoryScore(1, "coding", 50, 2, 1)]);
        store.SaveCategoryScores("2026-11-19", [new CategoryScore(1, "coding", 60, 1, 1), new CategoryScore(2, "coding", null, null, 0)]);

        Assert.Equal(("2026-11-19", "2026-11-18"), store.ScoreDates());
        Assert.Equal(1, store.LoadRanks("2026-11-19")["coding"][1]);
        Assert.False(store.LoadRanks("2026-11-19")["coding"].ContainsKey(2)); // 순위 없는 행은 제외
        Assert.Empty(store.NewModelIds("2026-11-19")); // 데모 모델은 모두 이전부터 있었음
    }

    [Fact]
    public void NewModels_ExcludeOldModelsAppearingForTheFirstTime()
    {
        var db = new Database(Path.Combine(_dir, "t.db"), NullLogger<Database>.Instance);
        db.Migrate();
        var store = new SqliteStore(db);
        static FetchedModel M(string name, string? release) => new()
        {
            SourceName = name, CanonicalName = name, ReleaseDate = release,
            Metrics = [new RawMetric(MetricIds.IntelligenceIndex, 50)],
        };

        store.UpsertFetch(new SourceFetchResult { SourceId = "artificial_analysis", Models = [M("Seen", "2026-01-01")] },
            DateTimeOffset.Parse("2026-11-18T01:00:00Z"));
        store.UpsertFetch(new SourceFetchResult
        {
            SourceId = "artificial_analysis",
            Models = [M("Seen", "2026-01-01"), M("OldButNew2Us", "2025-03-01"), M("Fresh", "2026-11-15"), M("Undated", null)],
        }, DateTimeOffset.Parse("2026-11-19T01:00:00Z"));

        Assert.Equal(2, store.CountNewModels("2026-11-19"));
        Assert.Equal(2, store.NewModelIds("2026-11-19").Count);
    }

    [Fact]
    public void Store_LoadScoreTrend_OldestFirst_LimitedToDays_SkipsNulls()
    {
        var db = new Database(Path.Combine(_dir, "t.db"), NullLogger<Database>.Instance);
        db.Migrate();
        var store = new SqliteStore(db);
        DemoData.Seed(store, DateTimeOffset.Parse("2026-11-19T00:00:00Z"));
        for (var d = 1; d <= 9; d++)
            store.SaveCategoryScores($"2026-11-{d:00}", [new CategoryScore(1, "coding", d * 10, 1, 1)]);
        store.SaveCategoryScores("2026-11-10", [new CategoryScore(1, "coding", null, null, 0)]);

        var trend = store.LoadScoreTrend("coding", 1, 7);
        Assert.Equal([30.0, 40, 50, 60, 70, 80, 90], trend);
        Assert.Empty(store.LoadScoreTrend("coding", 99));
    }
    [Fact]
    public void PriceChange_Create_ThresholdAndSign()
    {
        Assert.Null(PriceChange.Create(1, 10, 10.4, 5));
        Assert.Null(PriceChange.Create(1, 0, 10, 5));
        Assert.Equal(-20, PriceChange.Create(1, 10, 8, 5)!.Pct, 6);
        Assert.Equal(50, PriceChange.Create(1, 10, 15, 5)!.Pct, 6);
    }

    [Fact]
    public void Store_LoadPriceChanges_UsesLatestTwoDates_SortedByMagnitude()
    {
        var db = new Database(Path.Combine(_dir, "t.db"), NullLogger<Database>.Instance);
        db.Migrate();
        var store = new SqliteStore(db);
        DemoData.Seed(store, DateTimeOffset.Parse("2026-11-19T00:00:00Z"));
        store.SavePriceHistory("2026-11-01", 1, 100);   // 오래된 값은 무시
        store.SavePriceHistory("2026-12-01", 1, 10);
        store.SavePriceHistory("2026-12-02", 1, 12);    // +20%
        store.SavePriceHistory("2026-12-01", 2, 10);
        store.SavePriceHistory("2026-12-02", 2, 5);     // -50%
        store.SavePriceHistory("2026-12-01", 3, 10);
        store.SavePriceHistory("2026-12-02", 3, 10.2);  // 2% -> 제외

        var changes = store.LoadPriceChanges().Where(c => c.ModelId <= 4).ToList();
        Assert.Equal([2L, 1L], changes.Select(c => c.ModelId));
    }
    [Fact]
    public void Store_HistoryQueries_ReturnDatesScoresAndPricesAsOf()
    {
        var db = new Database(Path.Combine(_dir, "h.db"), NullLogger<Database>.Instance);
        db.Migrate();
        var store = new SqliteStore(db);
        DemoData.Seed(store, DateTimeOffset.Parse("2026-11-19T00:00:00Z"));
        store.SavePriceHistory("2026-12-01", 1, 10);
        store.SavePriceHistory("2026-12-05", 1, 12);

        store.SaveCategoryScores("2026-11-18", [new CategoryScore(1, "coding", 50, 2, 1)]);
        store.SaveCategoryScores("2026-11-19", [new CategoryScore(1, "coding", 60, 1, 1)]);
        var dates = store.ScoreDateList();
        Assert.Equal(["2026-11-19", "2026-11-18"], dates);
        Assert.Equal(dates.OrderByDescending(d => d).ToList(), dates);   // 최신순
        var scores = store.LoadCategoryScoresOn("coding", dates[0]);
        Assert.Equal(1, scores[1].Rank);

        Assert.Equal(10, store.LoadPricesAsOf("2026-12-03")[1]);          // 그 날 이전 가장 최근 값
        Assert.Equal(12, store.LoadPricesAsOf("2026-12-09")[1]);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

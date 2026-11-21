using LLMPulse.Core;
using LLMPulse.Core.Models;
using LLMPulse.Core.Scoring;

namespace LLMPulse.Tests;

public class NormalizerTests
{
    private static Dictionary<long, double> Vals(params double[] v) =>
        v.Select((x, i) => (id: (long)i + 1, x)).ToDictionary(t => t.id, t => t.x);

    [Fact]
    public void Percentile_HigherIsBetter_RanksMinToMax()
    {
        var p = Normalizer.Percentile(Vals(10, 20, 30), higherIsBetter: true);
        Assert.Equal(0, p[1]);
        Assert.Equal(50, p[2]);
        Assert.Equal(100, p[3]);
    }

    [Fact]
    public void Percentile_LowerIsBetter_Inverts()
    {
        var p = Normalizer.Percentile(Vals(1, 2, 3), higherIsBetter: false);
        Assert.Equal(100, p[1]);
        Assert.Equal(0, p[3]);
    }

    [Fact]
    public void Percentile_Ties_UseAverageRank_AndOutlierDoesNotDistort()
    {
        // 이상치(1000) 가 있어도 나머지는 순위 기반이라 균등하게 퍼진다 (min-max 와의 차이)
        var p = Normalizer.Percentile(Vals(1, 2, 3, 1000), higherIsBetter: true);
        Assert.Equal(100.0 / 3, p[2], precision: 6);

        var tie = Normalizer.Percentile(Vals(5, 5, 9), higherIsBetter: true);
        Assert.Equal(tie[1], tie[2]);
        Assert.Equal(25, tie[1], precision: 6);
    }

    [Fact]
    public void Percentile_SingleValue_Is100() =>
        Assert.Equal(100, Normalizer.Percentile(Vals(7), true)[1]);
}

public class CategoryScorerTests
{
    private static MetricDef Def(string id, bool higher = true, string scale = "percentile") =>
        new() { Id = id, HigherIsBetter = higher, Scale = scale };

    private static ModelInput M(long id, double? a = null, double? b = null, double? price = null)
    {
        var d = new Dictionary<string, double>();
        if (a is { } av) d["a"] = av;
        if (b is { } bv) d["b"] = bv;
        return new ModelInput { Id = id, Name = $"m{id}", Metrics = d, BlendedPrice = price };
    }

    private static CategoryDef Cat(params (string id, double w)[] metrics) => new()
    {
        Id = "c",
        Metrics = metrics.Select(m => new CategoryMetricRef { Id = m.id, Weight = m.w }).ToList(),
    };

    [Fact]
    public void IndexMetric_IsPassedThroughWithoutPercentile()
    {
        var defs = new Dictionary<string, MetricDef> { ["a"] = Def("a", scale: "index") };
        var s = CategoryScorer.Score(Cat(("a", 1)), defs, [M(1, 70), M(2, 40)]);
        Assert.Equal(70, s.Single(x => x.ModelId == 1).Score);
        Assert.Equal(40, s.Single(x => x.ModelId == 2).Score);
        Assert.Equal(1, s.Single(x => x.ModelId == 1).Rank);
        Assert.Equal(2, s.Single(x => x.ModelId == 2).Rank);
    }

    [Fact]
    public void MissingMetric_RenormalizesWeights_WhenCoverageAtLeastHalf()
    {
        var defs = new Dictionary<string, MetricDef>
        {
            ["a"] = Def("a", scale: "index"), ["b"] = Def("b", scale: "index"),
        };
        // 모델 2 는 b 만 없음 → a 가중치만으로 평균 (coverage 0.5, 경계 포함)
        var s = CategoryScorer.Score(Cat(("a", 1), ("b", 1)), defs, [M(1, 80, 60), M(2, 50)]);
        var m2 = s.Single(x => x.ModelId == 2);
        Assert.Equal(50, m2.Score);
        Assert.Equal(0.5, m2.Coverage);
        Assert.Equal(70, s.Single(x => x.ModelId == 1).Score);
    }

    [Fact]
    public void CoverageBelowHalf_YieldsNoScore()
    {
        var defs = new Dictionary<string, MetricDef>
        {
            ["a"] = Def("a", scale: "index"), ["b"] = Def("b", scale: "index"), ["c"] = Def("c", scale: "index"),
        };
        var s = CategoryScorer.Score(Cat(("a", 1), ("b", 1), ("c", 1)), defs, [M(1, 50)]);
        Assert.Null(s.Single().Score);
        Assert.Null(s.Single().Rank);
    }

    [Fact]
    public void ReversedMetric_LowerValueScoresHigher()
    {
        var defs = new Dictionary<string, MetricDef> { ["a"] = Def("a", higher: false) };
        var s = CategoryScorer.Score(Cat(("a", 1)), defs, [M(1, 1), M(2, 5), M(3, 9)]);
        Assert.Equal(100, s.Single(x => x.ModelId == 1).Score);
        Assert.Equal(0, s.Single(x => x.ModelId == 3).Score);
    }

    [Fact]
    public void BlendedPrice_ComesFromPriceField()
    {
        var defs = new Dictionary<string, MetricDef>
        {
            [MetricKeys.BlendedPrice] = Def(MetricKeys.BlendedPrice, higher: false),
        };
        var s = CategoryScorer.Score(Cat((MetricKeys.BlendedPrice, 1)), defs,
            [M(1, price: 1), M(2, price: 10), M(3)]);
        Assert.Equal(100, s.Single(x => x.ModelId == 1).Score);
        Assert.Null(s.Single(x => x.ModelId == 3).Score);
    }

    [Fact]
    public void EqualScores_ShareRank()
    {
        var defs = new Dictionary<string, MetricDef> { ["a"] = Def("a", scale: "index") };
        var s = CategoryScorer.Score(Cat(("a", 1)), defs, [M(1, 60), M(2, 60), M(3, 50)]);
        Assert.Equal(1, s.Single(x => x.ModelId == 1).Rank);
        Assert.Equal(1, s.Single(x => x.ModelId == 2).Rank);
        Assert.Equal(3, s.Single(x => x.ModelId == 3).Rank);
    }
}

public class RecommenderTests
{
    private static (List<CategoryScore> scores, Dictionary<long, ModelInput> models) Data(
        params (long id, double score, double? price)[] rows)
    {
        var scores = rows.Select(r => new CategoryScore(r.id, "c", r.score, null, 1)).ToList();
        var models = rows.ToDictionary(r => r.id, r => new ModelInput { Id = r.id, Name = $"m{r.id}", BlendedPrice = r.price });
        return (scores, models);
    }

    [Fact]
    public void Best_IsTopScore_BestValue_IsCheapestWithinTolerance()
    {
        var (s, m) = Data((1, 100, 10), (2, 95, 2), (3, 91, 1), (4, 50, 0.1));
        var r = Recommender.Recommend("c", s, m, valueMode: true, tolerance: 0.9);
        Assert.Equal(1, r.Best!.ModelId);
        Assert.Equal(3, r.BestValue!.ModelId); // 91 >= 90, 가장 쌈. 50점은 허용치 밖.
        Assert.Equal(1, r.BestValuePrice);
    }

    [Fact]
    public void Tolerance_BoundaryIsInclusive()
    {
        var (s, m) = Data((1, 100, 10), (2, 90, 1));
        var r = Recommender.Recommend("c", s, m, true, 0.9);
        Assert.Equal(2, r.BestValue!.ModelId);

        var (s2, m2) = Data((1, 100, 10), (2, 89.99, 1));
        var r2 = Recommender.Recommend("c", s2, m2, true, 0.9);
        Assert.Equal(1, r2.BestValue!.ModelId);
    }

    [Fact]
    public void ValueMode_Off_SkipsBestValue()
    {
        var (s, m) = Data((1, 100, 10), (2, 95, 1));
        Assert.Null(Recommender.Recommend("c", s, m, false).BestValue);
    }

    [Fact]
    public void ModelsWithoutPrice_AreNotValueCandidates_ButCanBeBest()
    {
        var (s, m) = Data((1, 100, null), (2, 95, 3));
        var r = Recommender.Recommend("c", s, m, true, 0.9);
        Assert.Equal(1, r.Best!.ModelId);
        Assert.Equal(2, r.BestValue!.ModelId);
    }

    [Fact]
    public void AllowedFilter_RestrictsPool()
    {
        var (s, m) = Data((1, 100, 10), (2, 80, 1));
        var r = Recommender.Recommend("c", s, m, true, 0.9, allowed: id => id == 2);
        Assert.Equal(2, r.Best!.ModelId);
    }

    [Fact]
    public void NoScores_ReturnsEmpty()
    {
        var r = Recommender.Recommend("c", [], new Dictionary<long, ModelInput>(), true);
        Assert.Null(r.Best);
        Assert.Null(r.BestValue);
    }

    [Fact]
    public void Pareto_KeepsOnlyNonDominated()
    {
        // price asc: 1($1,60) 2($2,50 dominated) 3($3,80) 4($4,70 dominated) 5($5,90)
        var (s, m) = Data((1, 60, 1), (2, 50, 2), (3, 80, 3), (4, 70, 4), (5, 90, 5));
        Assert.Equal([1L, 3L, 5L], Recommender.ParetoFrontier(s, m));
    }
}

public class CatalogTests
{
    [Fact]
    public void EmbeddedCatalog_LoadsAndIsConsistent()
    {
        var c = Catalog.LoadEmbedded();
        Assert.NotEmpty(c.Metrics);
        Assert.NotEmpty(c.Categories);
        foreach (var cat in c.Categories)
            foreach (var cm in cat.Metrics)
                Assert.True(c.MetricsById.ContainsKey(cm.Id), $"{cat.Id} references unknown metric {cm.Id}");
        foreach (var m in c.Metrics)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Name.Get("ko")));
            Assert.False(string.IsNullOrWhiteSpace(m.Caveats.Get("en")));
        }
    }

    [Fact]
    public void LocalizedText_FallsBackToEnglish()
    {
        var t = new LocalizedText { ["en"] = "Hello" };
        Assert.Equal("Hello", t.Get("fr"));
        Assert.Equal("Hello", t.Get("zh-Hans"));
    }
}

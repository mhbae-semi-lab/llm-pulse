using System.Net;
using LLMPulse.Core;
using LLMPulse.Core.Models;
using LLMPulse.Core.Plans;
using LLMPulse.Core.Scoring;
using LLMPulse.Core.Sources;
using LLMPulse.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Parquet.Serialization;

namespace LLMPulse.Tests;

/// <summary>소스 신선도 · 소스 불일치 · 1위 변경 이유 · 원격 플랜 목록.</summary>
public class DifferentiatorTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 21);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "llmpulse-diff-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteStore _store;

    public DifferentiatorTests()
    {
        var db = new Database(Path.Combine(_dir, "t.db"), NullLogger<Database>.Instance);
        db.Migrate();
        _store = new SqliteStore(db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // ---- 신선도 ----

    [Fact]
    public void Freshness_StaleOnlyAfter90Days_AndPrefersSourceDate()
    {
        Assert.False(SourceFreshness.Evaluate("x", Today.AddDays(-90), Today, Today).IsStale);
        var stale = SourceFreshness.Evaluate("aider", new DateOnly(2025, 10, 3), Today, Today);
        Assert.True(stale.IsStale);
        Assert.True(stale.FromSource);
        Assert.Equal(414, stale.AgeDays);

        // 소스가 날짜를 안 주면 마지막 성공일
        var live = SourceFreshness.Evaluate("aa", null, Today, Today);
        Assert.False(live.FromSource);
        Assert.Equal(0, live.AgeDays);
        Assert.Null(SourceFreshness.Evaluate("none", null, null, Today).AgeDays);
    }

    [Fact]
    public void AiderLatestDate_ReadsMaxDateOfEntries()
    {
        const string yaml = """
            - dirname: run-a
              model: model-a
              date: 2025-08-25
              pass_rate_2: 50.1
            - dirname: run-b
              model: model-b
              date: "2025-10-03"
              pass_rate_2: 60.0
            """;
        Assert.Equal(new DateOnly(2025, 10, 3), AiderAdapter.LatestDate(yaml));
        Assert.Null(AiderAdapter.LatestDate("- model: x\n  pass_rate_2: 1"));
    }

    [Fact]
    public async Task ArenaParse_ReturnsLatestPublishDate()
    {
        var rows = new[]
        {
            new LmArenaAdapter.ArenaRow { ModelName = "A", Category = "overall", Rating = 1400, PublishDate = "2026-11-18" },
            new LmArenaAdapter.ArenaRow { ModelName = "B", Category = "overall", Rating = 1300, PublishDate = "2026-11-20" },
        };
        using var ms = new MemoryStream();
        await ParquetSerializer.SerializeAsync(rows, ms);
        ms.Position = 0;
        var (models, asOf) = await LmArenaAdapter.ParseWithDateAsync(ms);
        Assert.Equal(2, models.Count);
        Assert.Equal(new DateOnly(2026, 11, 20), asOf);
    }

    // ---- 불일치 ----

    private static ModelInput M(long id, double? intel = null, double? arena = null)
    {
        var metrics = new Dictionary<string, double>();
        if (intel is { } i) metrics[MetricIds.IntelligenceIndex] = i;
        if (arena is { } a) metrics[MetricIds.LmArenaOverall] = a;
        return new ModelInput { Id = id, Name = $"M{id}", Metrics = metrics };
    }

    [Fact]
    public void Disagreement_FlagsPriceGapAtThreshold()
    {
        var pairs = new Dictionary<long, (double, double)> { [1] = (10, 20), [2] = (10, 19.9), [3] = (2, 1) };
        var found = SourceDisagreement.Find([], pairs);
        Assert.Equal([1L, 3L], found.Select(d => d.ModelId).Order());
        Assert.All(found, d => Assert.Equal(DisagreementKind.Price, d.Kind));
        Assert.Equal(100, found.First(d => d.ModelId == 3).Gap, 6);
    }

    [Fact]
    public void Disagreement_FlagsRankGap_OnlyWithEnoughCommonModels()
    {
        // 10개 모델: 지능과 Arena 순서가 같고, 9번 모델만 Arena 꼴찌
        var models = Enumerable.Range(0, 10).Select(i => M(i, intel: 10 + i, arena: i == 9 ? 1000 : 1100 + i)).ToList();
        var found = SourceDisagreement.Find(models, new Dictionary<long, (double, double)>());
        var d = Assert.Single(found);
        Assert.Equal(9, d.ModelId);
        Assert.Equal(DisagreementKind.QualityRank, d.Kind);
        Assert.Equal(100, d.A, 6);
        Assert.Equal(0, d.B, 6);

        Assert.Empty(SourceDisagreement.Find(models.Take(9).ToList(), new Dictionary<long, (double, double)>()));
    }

    // ---- 1위 변경 이유 ----

    private static readonly IReadOnlyDictionary<string, MetricDef> Defs = new Dictionary<string, MetricDef>
    {
        ["a"] = new() { Id = "a", Scale = "index" },
        ["b"] = new() { Id = "b", Scale = "index" },
    };

    private static readonly CategoryDef Cat = new()
    {
        Id = "c", Metrics = [new CategoryMetricRef { Id = "a", Weight = 0.5 }, new CategoryMetricRef { Id = "b", Weight = 0.5 }],
    };

    private static ModelInput X(long id, double? a, double? b)
    {
        var m = new Dictionary<string, double>();
        if (a is { } va) m["a"] = va;
        if (b is { } vb) m["b"] = vb;
        return new ModelInput { Id = id, Name = $"X{id}", Metrics = m };
    }

    [Fact]
    public void Explain_MetricShift_PicksMetricThatMoved()
    {
        // 어제: 1번(80,80) > 2번(70,85). 오늘: 2번의 a 가 70→90 으로 올라 역전
        var before = new List<ModelInput> { X(1, 80, 80), X(2, 70, 85) };
        var after = new List<ModelInput> { X(1, 80, 80), X(2, 90, 85) };
        var r = TopChangeExplainer.Explain(Cat, Defs, before, after, oldTop: 1, newTop: 2);
        Assert.Equal(TopChangeCause.MetricShift, r.Cause);
        Assert.Equal("a", r.MetricId);
        Assert.Equal(70, r.NewBefore);
        Assert.Equal(90, r.NewAfter);
        Assert.Equal(80, r.OldBefore);
        Assert.Equal(80, r.OldAfter);
    }

    [Fact]
    public void Explain_NewEntry_PreviousUnscored_RuleChanged()
    {
        var before = new List<ModelInput> { X(1, 80, 80) };
        var after = new List<ModelInput> { X(1, 80, 80), X(2, 90, 90) };
        Assert.Equal(TopChangeCause.NewEntry, TopChangeExplainer.Explain(Cat, Defs, before, after, 1, 2).Cause);

        var gone = new List<ModelInput> { X(1, null, null), X(2, 70, 70) };
        Assert.Equal(TopChangeCause.PreviousUnscored,
            TopChangeExplainer.Explain(Cat, Defs, [X(1, 80, 80), X(2, 70, 70)], gone, 1, 2).Cause);

        // 데이터는 그대로인데 지금 기준(지표 a 만)으로는 어제도 2번이 앞선다 = 기준이 바뀜
        var onlyA = new CategoryDef { Id = "c", Metrics = [new CategoryMetricRef { Id = "a", Weight = 1 }] };
        var same = new List<ModelInput> { X(1, 70, 99), X(2, 80, 60) };
        Assert.Equal(TopChangeCause.RuleChanged, TopChangeExplainer.Explain(onlyA, Defs, same, same, 1, 2).Cause);
    }

    // ---- 저장소 + 점수: 오래된 소스 제외와 Shadow ----

    private void SeedCodingData(DateTimeOffset at)
    {
        // 주 소스 모델 3개 + Arena 코딩 + Aider (둘 다 백분위). Arena 만이면 Alpha(100), Aider 를 넣으면 Beta(0.6·50+0.4·100=70 > Alpha 60) 가 1위.
        _store.UpsertFetch(new SourceFetchResult
        {
            SourceId = ArtificialAnalysisAdapter.Source,
            Models =
            [
                new FetchedModel { SourceName = "a", CanonicalName = "Alpha", Metrics = [new RawMetric(MetricIds.IntelligenceIndex, 50)] },
                new FetchedModel { SourceName = "b", CanonicalName = "Beta", Metrics = [new RawMetric(MetricIds.IntelligenceIndex, 40)] },
                new FetchedModel { SourceName = "g", CanonicalName = "Gamma", Metrics = [new RawMetric(MetricIds.IntelligenceIndex, 30)] },
            ],
        }, at);
        _store.UpsertFetch(new SourceFetchResult
        {
            SourceId = LmArenaAdapter.Source, MatchOnly = true,
            Models =
            [
                new FetchedModel { SourceName = "Alpha", CanonicalName = "Alpha", Metrics = [new RawMetric(MetricIds.LmArenaCoding, 1500)] },
                new FetchedModel { SourceName = "Beta", CanonicalName = "Beta", Metrics = [new RawMetric(MetricIds.LmArenaCoding, 1490)] },
                new FetchedModel { SourceName = "Gamma", CanonicalName = "Gamma", Metrics = [new RawMetric(MetricIds.LmArenaCoding, 1400)] },
            ],
        }, at);
        _store.UpsertFetch(new SourceFetchResult
        {
            SourceId = AiderAdapter.Source, MatchOnly = true,
            Models =
            [
                new FetchedModel { SourceName = "Alpha", CanonicalName = "Alpha", Metrics = [new RawMetric(MetricIds.AiderPolyglot, 10)] },
                new FetchedModel { SourceName = "Beta", CanonicalName = "Beta", Metrics = [new RawMetric(MetricIds.AiderPolyglot, 90)] },
                new FetchedModel { SourceName = "Gamma", CanonicalName = "Gamma", Metrics = [new RawMetric(MetricIds.AiderPolyglot, 50)] },
            ],
        }, at);
        foreach (var src in new[] { ArtificialAnalysisAdapter.Source, LmArenaAdapter.Source, AiderAdapter.Source })
            _store.FinishRefreshLog(_store.BeginRefreshLog(src, at), "ok", "3 models", at);
    }

    [Fact]
    public void StaleSource_IsExcludedFromScores_WithShadowAndOptIn()
    {
        var at = new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
        SeedCodingData(at);
        _store.SetSetting(SourceFreshness.SettingKey(AiderAdapter.Source), "2025-10-03");

        Assert.Equal(["aider_polyglot"], _store.MetricsOnlyFrom([AiderAdapter.Source]));

        var scoring = new ScoringService(_store, Catalog.LoadEmbedded());
        var coding = scoring.Build(Today).Categories.Single(c => c.Category.Id == "coding");
        Assert.Equal(["aider_polyglot"], coding.ExcludedMetrics);
        Assert.Equal("Alpha", Name(coding.Recommendation.Best));
        Assert.Equal("Beta", Name(coding.ShadowBest));

        // Aider 만 쓰는 분야는 점수가 모두 비지만 원래 정의는 남는다 (화면이 기본 지표를 안다)
        var aiderOnly = scoring.Build(Today).Categories.Single(c => c.Category.Id == "coding_aider");
        Assert.Null(aiderOnly.Recommendation.Best);
        Assert.NotEmpty(aiderOnly.Category.Metrics);

        _store.SetSetting(SourceFreshness.SettingIncludeStale, "1");
        var included = scoring.Build(Today).Categories.Single(c => c.Category.Id == "coding");
        Assert.Empty(included.ExcludedMetrics);
        Assert.Equal("Beta", Name(included.Recommendation.Best));

        string? Name(CategoryScore? s) => s is null ? null : _store.LoadModelInputs().Single(m => m.Id == s.ModelId).Name;
    }

    [Fact]
    public void TopChanged_HasReason_FromPreviousSnapshot()
    {
        var yesterday = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        SeedCodingData(yesterday);
        var scoring = new ScoringService(_store, Catalog.LoadEmbedded());
        scoring.Persist(scoring.Build(new DateOnly(2026, 11, 20)), new DateOnly(2026, 11, 20));

        // 오늘: Beta 의 지능 지수가 40 → 60 으로 올라 종합 1위가 바뀐다
        _store.UpsertFetch(new SourceFetchResult
        {
            SourceId = ArtificialAnalysisAdapter.Source,
            Models = [new FetchedModel { SourceName = "b", CanonicalName = "Beta", Metrics = [new RawMetric(MetricIds.IntelligenceIndex, 60)] }],
        }, yesterday.AddDays(1));

        var overall = scoring.Build(Today).Categories.Single(c => c.Category.Id == "overall");
        Assert.True(overall.TopChanged);
        var r = overall.TopChangeReason!;
        Assert.Equal(TopChangeCause.MetricShift, r.Cause);
        Assert.Equal(MetricIds.IntelligenceIndex, r.MetricId);
        Assert.Equal(40, r.NewBefore);
        Assert.Equal(60, r.NewAfter);
    }

    [Fact]
    public void PricePairs_JoinPrimaryAndOpenRouter_IgnoringFreeVariants()
    {
        var at = DateTimeOffset.UtcNow;
        _store.UpsertFetch(new SourceFetchResult
        {
            SourceId = ArtificialAnalysisAdapter.Source,
            Models = [new FetchedModel { SourceName = "a", CanonicalName = "Alpha", InputPerM = 1, OutputPerM = 4 }],
        }, at);
        _store.UpsertFetch(new SourceFetchResult
        {
            SourceId = OpenRouterAdapter.Source, MatchOnly = true,
            Models =
            [
                new FetchedModel { SourceName = "x/alpha", CanonicalName = "X: Alpha", InputPerM = 2, OutputPerM = 8 },
                new FetchedModel { SourceName = "x/alpha:free", CanonicalName = "X: Alpha (free)", InputPerM = 0, OutputPerM = 0 },
            ],
        }, at);
        var id = _store.LoadModelInputs().Single().Id;
        _store.LinkAlias(OpenRouterAdapter.Source, "x/alpha", id);
        _store.LinkAlias(OpenRouterAdapter.Source, "x/alpha:free", id);

        var pair = Assert.Single(_store.LoadPricePairs());
        Assert.Equal((1.75, 3.5), pair.Value);
    }

    // ---- 백업 ----

    [Fact]
    public void Backup_RoundTrips_AndRejectsForeignFile()
    {
        _store.SetSetting("k", "before");
        var db = new Database(Path.Combine(_dir, "t.db"), NullLogger<Database>.Instance);
        var backup = Path.Combine(_dir, "backup.db");
        db.BackupTo(backup);

        _store.SetSetting("k", "after");
        Assert.Null(db.RestoreFrom(backup));
        Assert.Equal("before", _store.GetSetting("k"));
        Assert.True(File.Exists(db.Path + ".before-restore"));

        // 이 앱 DB 가 아닌 파일: 거절하고 현재 데이터 유지
        var foreign = Path.Combine(_dir, "foreign.db");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={foreign};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE other (x INTEGER);";
            cmd.ExecuteNonQuery();
        }
        Assert.NotNull(db.RestoreFrom(foreign));
        Assert.Equal("before", _store.GetSetting("k"));

        File.WriteAllText(Path.Combine(_dir, "text.db"), "not sqlite");
        Assert.NotNull(db.RestoreFrom(Path.Combine(_dir, "text.db")));
    }

    // ---- 원격 플랜 목록 ----

    private sealed class StubHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
    }

    [Fact]
    public async Task PlanUpdater_WritesOnlyValidList_AndLoadPrefersUserThenRemote()
    {
        Directory.CreateDirectory(_dir);
        var remote = Path.Combine(_dir, PlanCatalog.RemoteFileName);
        const string good = """[{ "id": "remote_plan", "vendors": ["Acme"], "name": { "en": "Remote" } }]""";

        Assert.Equal("HTTP 404", await new PlanCatalogUpdater(new HttpClient(new StubHandler(HttpStatusCode.NotFound, "")), "http://x/p.json").UpdateAsync(remote));
        Assert.False(File.Exists(remote));
        Assert.NotNull(await new PlanCatalogUpdater(new HttpClient(new StubHandler(HttpStatusCode.OK, "[{\"name\":{}}]")), "http://x/p.json").UpdateAsync(remote));
        Assert.False(File.Exists(remote));

        Assert.Null(await new PlanCatalogUpdater(new HttpClient(new StubHandler(HttpStatusCode.OK, good)), "http://x/p.json").UpdateAsync(remote));
        var c = PlanCatalog.Load(Path.Combine(_dir, "missing.user.json"), remote);
        Assert.True(c.IsRemote);
        Assert.Equal("remote_plan", Assert.Single(c.Plans).Id);

        // 사용자 파일이 있으면 그것이 먼저, 원격 사본이 깨졌으면 내장본
        var user = Path.Combine(_dir, PlanCatalog.UserFileName);
        File.WriteAllText(user, """[{ "id": "mine", "all": true }]""");
        Assert.True(PlanCatalog.Load(user, remote).IsUserDefined);
        File.WriteAllText(remote, "not json");
        var builtIn = PlanCatalog.Load(null, remote);
        Assert.False(builtIn.IsRemote);
        Assert.True(builtIn.Plans.Count > 1);
    }
}

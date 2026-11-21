using LLMPulse.Core.Matching;
using LLMPulse.Core.Models;
using LLMPulse.Core.Sources;
using LLMPulse.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMPulse.Tests;

public class AliasResolverTests
{
    private static readonly IReadOnlyList<(long, string)> Models =
    [
        (1, "Claude 4.5 Sonnet (Reasoning)"),
        (2, "Claude 4.5 Sonnet (Non-reasoning)"),
        (3, "GPT-5 (high)"),
        (4, "Gemini 2.5 Pro"),
    ];

    [Theory]
    [InlineData("Google: Gemini 2.5 Pro", 4)]
    [InlineData("gemini-2-5-pro", 4)]
    [InlineData("OpenAI: GPT-5", 3)]          // 괄호 제외 키로 유일 매칭
    [InlineData("Anthropic: Claude Sonnet 4.5 (Reasoning)", 1)]
    public void FindUnique_Matches(string name, long expected) =>
        Assert.Equal(expected, AliasResolver.FindUnique(name, Models));

    [Fact]
    public void FindUnique_AmbiguousReturnsNull() =>
        Assert.Null(AliasResolver.FindUnique("Anthropic: Claude Sonnet 4.5", Models));

    private static readonly IReadOnlyList<(long, string)> Effort =
    [
        (10, "GPT-6.2 Astra (Low)"), (11, "GPT-6.2 Astra (Medium)"), (12, "GPT-6.2 Astra (High)"),
        (20, "Claude Fable 5.2 (Medium, Default Fallback)"), (21, "Claude Fable 5.2 (High, Default Fallback)"),
        (30, "Foo 1 (Low)"), (31, "Foo 1 (High)"),   // Medium 없음
    ];

    [Theory]
    [InlineData("OpenAI: GPT-6.2 Astra", 11)]
    [InlineData("Anthropic: Claude Fable 5.2", 20)]
    public void FindBest_NoEffortInName_PicksMediumVariant(string name, long expected) =>
        Assert.Equal(expected, AliasResolver.FindBest(name, Effort));

    [Fact]
    public void FindDefaultVariant_NoMedium_OrExplicitEffort_ReturnsNull()
    {
        Assert.Null(AliasResolver.FindDefaultVariant("Foo 1", Effort));
        Assert.Null(AliasResolver.FindDefaultVariant("GPT-6.2 Astra (Max)", Effort));
    }

    [Fact]
    public void FindUnique_UnknownReturnsNull() =>
        Assert.Null(AliasResolver.FindUnique("Mistral: Mistral Large", Models));

    [Theory]
    [InlineData("Anthropic: Claude Sonnet 4.5 (Reasoning) Preview", 1)]   // 한 토큰 차이 + 같은 버전
    [InlineData("claude-sonnet-4-5-reasoning", 1)]
    public void FindConfident_AcceptsCloseSameVersion(string name, long expected) =>
        Assert.Equal(expected, AliasResolver.FindConfident(name, Models));

    [Theory]
    [InlineData("Anthropic: Claude Sonnet 4.5")]      // 동점 후보 둘 -> 거부
    [InlineData("Anthropic: Claude Sonnet 4 (Reasoning)")] // 버전 다름 -> 거부
    [InlineData("OpenAI: GPT-5 mini")]                // 다른 모델 -> 거부
    public void FindConfident_RejectsAmbiguousOrDifferent(string name) =>
        Assert.Null(AliasResolver.FindConfident(name, Models));

    [Fact]
    public void Suggest_RanksByOverlap()
    {
        var s = AliasResolver.Suggest("Anthropic: Claude Sonnet 4.5", Models);
        Assert.Equal(2, s.Count(x => x.Score > 0.9));
        Assert.DoesNotContain(s, x => x.Id == 4);
    }
}

public class AiderTests
{
    [Fact]
    public void ParseLeaderboard_TakesBestRunPerModelAndIgnoresOtherFields()
    {
        const string yaml = """
            - dirname: a
              model: gpt-5 (high)
              pass_rate_1: 50.0
              pass_rate_2: 88.0
              total_cost: 29.08

            - dirname: b
              model: gpt-5 (high)
              pass_rate_2: 80.5

            - dirname: c
              model: Kimi K2
              pass_rate_2: 59.1
            """;
        var list = AiderAdapter.ParseLeaderboard(yaml);

        Assert.Equal(2, list.Count);
        Assert.Equal(88.0, list.Single(m => m.SourceName == "gpt-5 (high)").Metrics.Single().Value);
        Assert.Equal(59.1, list.Single(m => m.SourceName == "Kimi K2").Metrics.Single().Value);
        Assert.All(list, m => Assert.Equal(MetricIds.AiderPolyglot, m.Metrics.Single().MetricId));
    }
}

public class OpenRouterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "llmpulse-or-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteStore _store;

    public OpenRouterTests()
    {
        var db = new Database(Path.Combine(_dir, "t.db"), NullLogger<Database>.Instance);
        db.Migrate();
        _store = new SqliteStore(db);
    }

    [Fact]
    public void ParseModels_ConvertsPricePerMillionAndSkipsFreeVariants()
    {
        const string json = """
            { "data": [
              { "id": "google/gemini-2.5-pro", "name": "Google: Gemini 2.5 Pro", "context_length": 1048576,
                "pricing": { "prompt": "0.00000125", "completion": "0.00001" } },
              { "id": "google/gemini-2.5-pro:free", "name": "Google: Gemini 2.5 Pro (free)", "context_length": 1000,
                "pricing": { "prompt": "0", "completion": "0" } },
              { "id": "openrouter/auto", "name": "Auto Router", "context_length": 2000000,
                "pricing": { "prompt": "-1", "completion": "-1" } }
            ] }
            """;
        var list = OpenRouterAdapter.ParseModels(json);

        Assert.Equal(2, list.Count);
        var g = list[0];
        Assert.Equal(1.25, g.InputPerM!.Value, 6);
        Assert.Equal(10.0, g.OutputPerM!.Value, 6);
        Assert.Equal(1048.576, g.Metrics.Single().Value, 3);
        Assert.Equal("google", g.Vendor);
        Assert.Null(list[1].InputPerM); // 음수 가격은 없음 처리
    }

    private static FetchedModel Aa(string name, double? input = null, double? output = null) => new()
    {
        SourceName = name.ToLowerInvariant().Replace(' ', '-'),
        CanonicalName = name,
        Metrics = [new RawMetric(MetricIds.IntelligenceIndex, 50)],
        InputPerM = input,
        OutputPerM = output,
    };

    private static FetchedModel Or(string id, string name, double? input = null) => new()
    {
        SourceName = id,
        CanonicalName = name,
        Metrics = [new RawMetric(MetricIds.ContextK, 128)],
        InputPerM = input,
        OutputPerM = input,
    };

    [Fact]
    public void MatchOnly_AutoMatchesAndKeepsUnmatchedListed_WithoutCreatingModels()
    {
        var now = DateTimeOffset.UtcNow;
        _store.UpsertFetch(new SourceFetchResult { SourceId = "aa", Models = [Aa("Gemini 2.5 Pro", 2, 8)] }, now);
        _store.UpsertFetch(new SourceFetchResult
        {
            SourceId = OpenRouterAdapter.Source,
            MatchOnly = true,
            Models = [Or("google/gemini-2.5-pro", "Google: Gemini 2.5 Pro", 1), Or("x/unknown", "X: Unknown Model")],
        }, now);

        Assert.Single(_store.ModelChoices());                       // 새 모델은 생기지 않음
        var items = _store.SourceItems(OpenRouterAdapter.Source);
        Assert.Equal(2, items.Count);
        Assert.NotNull(items.Single(i => i.SourceName == "google/gemini-2.5-pro").ModelId);
        Assert.Null(items.Single(i => i.SourceName == "x/unknown").ModelId);

        var input = _store.LoadModelInputs().Single();
        Assert.Equal(128, input.Metrics[MetricIds.ContextK]);
        Assert.Equal(3 * 2 + 8, input.BlendedPrice * 4);            // 주 소스(AA) 가격이 유지됨
    }

    [Fact]
    public void LinkAndUnlink_ApplyAndRemoveSourceData()
    {
        var now = DateTimeOffset.UtcNow;
        _store.UpsertFetch(new SourceFetchResult { SourceId = "aa", Models = [Aa("Alpha One")] }, now);
        _store.UpsertFetch(new SourceFetchResult
        {
            SourceId = OpenRouterAdapter.Source, MatchOnly = true,
            Models = [Or("v/zzz", "V: Totally Different", 4)],
        }, now);
        var modelId = _store.ModelChoices().Single().Id;

        _store.LinkAlias(OpenRouterAdapter.Source, "v/zzz", modelId);
        var linked = _store.LoadModelInputs().Single();
        Assert.Equal(128, linked.Metrics[MetricIds.ContextK]);
        Assert.Equal(4, linked.BlendedPrice);                        // 가격 없던 모델은 보강 소스가 채움

        _store.UnlinkAlias(OpenRouterAdapter.Source, "v/zzz");
        var unlinked = _store.LoadModelInputs().Single();
        Assert.False(unlinked.Metrics.ContainsKey(MetricIds.ContextK));
        Assert.Null(unlinked.BlendedPrice);
        Assert.Null(_store.SourceItems(OpenRouterAdapter.Source).Single().ModelId);
    }

    [Fact]
    public void Refresh_PreservesManualAliasAcrossFetches()
    {
        var now = DateTimeOffset.UtcNow;
        _store.UpsertFetch(new SourceFetchResult { SourceId = "aa", Models = [Aa("Alpha One")] }, now);
        var fetch = new SourceFetchResult
        {
            SourceId = OpenRouterAdapter.Source, MatchOnly = true,
            Models = [Or("v/zzz", "V: Totally Different")],
        };
        _store.UpsertFetch(fetch, now);
        _store.LinkAlias(OpenRouterAdapter.Source, "v/zzz", _store.ModelChoices().Single().Id);

        _store.UpsertFetch(fetch, now.AddDays(1));

        Assert.NotNull(_store.SourceItems(OpenRouterAdapter.Source).Single().ModelId);
    }

    [Fact]
    public void SourceStatuses_HandlesSourceThatNeverSucceeded()
    {
        var now = DateTimeOffset.UtcNow;
        var id = _store.BeginRefreshLog("artificial_analysis", now);
        _store.FinishRefreshLog(id, "error", "API key missing", now);

        var s = _store.SourceStatuses().Single();

        Assert.Null(s.LastSuccessAt);
        Assert.Equal("error", s.LastStatus);
        Assert.Equal("API key missing", s.LastMessage);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }
}
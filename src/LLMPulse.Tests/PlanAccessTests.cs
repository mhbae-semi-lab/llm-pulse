using LLMPulse.Core.Plans;
using LLMPulse.Core.Scoring;
using LLMPulse.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMPulse.Tests;

public class PlanAccessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "llmpulse-access-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteStore _store;

    public PlanAccessTests()
    {
        var db = new Database(Path.Combine(_dir, "t.db"), NullLogger<Database>.Instance);
        db.Migrate();
        _store = new SqliteStore(db);
    }

    private static ModelInput M(string name, string vendor) =>
        new() { Id = 1, Name = name, Vendor = vendor, Metrics = new(), BlendedPrice = 1 };

    private static readonly PlanDef Claude = new() { Id = "claude_pro", Vendors = ["Anthropic"] };

    [Fact]
    public void Resolve_DefaultsToRulesAndOverrideWins()
    {
        var opus = M("Claude Opus", "Anthropic");
        Assert.Equal(PlanAccess.Full, PlanAccessResolver.Resolve(Claude, opus, null));
        Assert.Equal(PlanAccess.None, PlanAccessResolver.Resolve(Claude, M("GPT-5", "OpenAI"), null));

        var o = new Dictionary<string, PlanAccess> { ["Claude Opus"] = PlanAccess.Limited, ["GPT-5"] = PlanAccess.Full };
        Assert.Equal(PlanAccess.Limited, PlanAccessResolver.Resolve(Claude, opus, o));
        Assert.Equal(PlanAccess.Full, PlanAccessResolver.Resolve(Claude, M("GPT-5", "OpenAI"), o));
    }

    [Fact]
    public void Best_TakesMostFreeAccessAcrossPlans()
    {
        var gpt = new PlanDef { Id = "gpt", Vendors = ["OpenAI"] };
        var overrides = new Dictionary<string, Dictionary<string, PlanAccess>>
        {
            ["claude_pro"] = new() { ["Claude Opus"] = PlanAccess.Limited },
        };

        Assert.Equal(PlanAccess.Limited, PlanAccessResolver.Best([Claude, gpt], M("Claude Opus", "Anthropic"), overrides));
        Assert.Equal(PlanAccess.Full, PlanAccessResolver.Best([Claude, gpt], M("GPT-5", "OpenAI"), overrides));
        Assert.Equal(PlanAccess.None, PlanAccessResolver.Best([Claude, gpt], M("Gemini", "Google"), overrides));
    }

    [Fact]
    public void Overrides_RoundTripAndClear()
    {
        _store.SetPlanOverride("claude_pro", "Claude Opus", PlanAccess.Limited);
        _store.SetPlanOverride("claude_pro", "Claude Opus", PlanAccess.None); // 갱신
        _store.SetPlanOverride("claude_pro", "Other", PlanAccess.Full);

        var loaded = _store.LoadPlanOverrides();
        Assert.Equal(PlanAccess.None, loaded["claude_pro"]["Claude Opus"]);
        Assert.Equal(2, loaded["claude_pro"].Count);

        _store.SetPlanOverride("claude_pro", "Claude Opus", null);
        Assert.False(_store.LoadPlanOverrides()["claude_pro"].ContainsKey("Claude Opus"));
    }

    [Theory]
    [InlineData("5h", "2026-11-19T10:00:00", "2026-11-19T14:59:00", false)]
    [InlineData("5h", "2026-11-19T10:00:00", "2026-11-19T15:00:00", true)]
    [InlineData("day", "2026-11-19T23:00:00", "2026-11-19T23:59:00", false)]
    [InlineData("day", "2026-11-19T23:00:00", "2026-11-20T00:01:00", true)]
    [InlineData("week", "2026-11-22T12:00:00", "2026-11-23T00:01:00", true)]  // 일요일 → 월요일
    [InlineData("week", "2026-11-23T12:00:00", "2026-11-29T23:00:00", false)] // 월~일
    [InlineData("month", "2026-11-30T12:00:00", "2026-12-01T00:01:00", true)]
    [InlineData("month", "2026-11-01T00:30:00", "2026-11-30T23:00:00", false)]
    public void IsExpired_ByPeriod(string period, string start, string now, bool expected) =>
        Assert.Equal(expected, PlanLimit.IsExpired(period, Local(start), Local(now)));

    // 기간 경계는 PC 의 로컬 달력 기준이다. 오프셋 없이 로컬 시각으로 만들어 러너 시간대(CI 는 UTC)와 무관하게 한다.
    private static DateTimeOffset Local(string s) =>
        new(DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void IsExpired_NeverWhenNotStarted() =>
        Assert.False(PlanLimit.IsExpired("day", null, DateTimeOffset.UtcNow));

    [Fact]
    public void Usage_CountsResetsAndExpires()
    {
        var t0 = DateTimeOffset.Parse("2026-11-19T10:00:00+09:00");
        Assert.Null(_store.GetPlanLimit("cursor", t0));
        Assert.Null(_store.AddPlanUsage("cursor", 1, t0)); // 한도 행이 없으면 아무 일도 없다

        _store.SavePlanLimit("cursor", 500, "day", "fast requests");
        _store.AddPlanUsage("cursor", 3, t0);
        _store.AddPlanUsage("cursor", -10, t0); // 0 아래로 내려가지 않음
        Assert.Equal(0, _store.GetPlanLimit("cursor", t0)!.Used);

        _store.AddPlanUsage("cursor", 4, t0);
        var l = _store.GetPlanLimit("cursor", t0.AddHours(1))!;
        Assert.Equal(4, l.Used);
        Assert.Equal(500, l.Cap);
        Assert.Equal("fast requests", l.Note);
        Assert.Equal(4 / 500.0, l.Ratio);

        // 다음 날이 되면 자동으로 0
        Assert.Equal(0, _store.GetPlanLimit("cursor", t0.AddDays(1))!.Used);

        // 저장된 값 수정: 기간 종류가 같으면 사용량 유지, 바뀌면 초기화
        _store.SavePlanLimit("cursor", 600, "day", "x");
        Assert.Equal(4, _store.GetPlanLimit("cursor", t0.AddHours(1))!.Used);
        _store.SavePlanLimit("cursor", 600, "month", "x");
        Assert.Equal(0, _store.GetPlanLimit("cursor", t0.AddHours(1))!.Used);

        _store.AddPlanUsage("cursor", 2, t0);
        _store.ResetPlanUsage("cursor");
        Assert.Equal(0, _store.GetPlanLimit("cursor", t0)!.Used);
    }

    [Fact]
    public void ScoringService_PlanOnlyRestrictsRecommendationButNotTopChange()
    {
        LLMPulse.Core.DemoData.Seed(_store, DateTimeOffset.UtcNow);
        var svc = new ScoringService(_store, LLMPulse.Core.Catalog.LoadEmbedded());
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var vendor = _store.LoadModelInputs().First(m => m.Vendor is not null).Vendor!;
        PlanAccess Access(ModelInput m) => m.Vendor == vendor ? PlanAccess.Full : PlanAccess.None;

        var all = svc.Build(today);
        var only = svc.Build(today, planAccess: Access, planOnly: true);
        var shown = svc.Build(today, planAccess: Access, planOnly: false);

        foreach (var c in only.Categories.Where(c => c.Recommendation.Best is not null))
            Assert.Equal(vendor, only.Models[c.Recommendation.Best!.ModelId].Vendor);

        // 제한 보기가 꺼져 있으면 전체 추천은 그대로, 구독 추천만 따로 채워진다
        for (var i = 0; i < all.Categories.Count; i++)
        {
            Assert.Equal(all.Categories[i].Recommendation.Best?.ModelId, shown.Categories[i].Recommendation.Best?.ModelId);
            Assert.Equal(all.Categories[i].TopChanged, only.Categories[i].TopChanged);
            if (shown.Categories[i].PlanBest is { } pb) Assert.Equal(vendor, shown.Models[pb.ModelId].Vendor);
        }
    }

    [Fact]
    public void Limit_WithoutCapHasNoRatio() =>
        Assert.Null(new PlanLimit("p", null, "month", 5, null, "").Ratio);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

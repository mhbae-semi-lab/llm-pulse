using LLMPulse.Core.Plans;
using LLMPulse.Core.Scoring;

namespace LLMPulse.Tests;

public class PlanTests
{
    private static ModelInput M(long id, string name, string? vendor) =>
        new() { Id = id, Name = name, Vendor = vendor, Metrics = new(), BlendedPrice = 1 };

    [Fact]
    public void BuiltInPlans_LoadWithLocalizedNamesAndUniqueIds()
    {
        var c = PlanCatalog.Load();
        Assert.NotEmpty(c.Plans);
        Assert.Equal(c.Plans.Count, c.Plans.Select(p => p.Id).Distinct().Count());
        foreach (var p in c.Plans)
        {
            foreach (var lang in new[] { "en", "ko", "zh", "fr" })
                Assert.False(string.IsNullOrWhiteSpace(p.Name.Get(lang)), $"{p.Id} name {lang}");
            Assert.False(string.IsNullOrWhiteSpace(p.Reviewed), p.Id);
        }
    }

    [Fact]
    public void Matches_ByVendorAndName()
    {
        var plan = new PlanDef { Id = "x", Vendors = ["Anthropic"], Exclude = ["Haiku"] };
        Assert.True(PlanCatalog.Matches(plan, M(1, "Claude 4.5 Sonnet", "Anthropic")));
        Assert.False(PlanCatalog.Matches(plan, M(2, "Claude 4.5 Haiku", "Anthropic")));
        Assert.False(PlanCatalog.Matches(plan, M(3, "GPT-5", "OpenAI")));
        Assert.False(PlanCatalog.Matches(plan, M(4, "Mystery", null)));
    }

    [Fact]
    public void Matches_AllMatchesEverything_ButEmptyRulesMatchNothing()
    {
        Assert.True(PlanCatalog.Matches(new PlanDef { All = true }, M(1, "Any", null)));
        Assert.False(PlanCatalog.Matches(new PlanDef { Id = "empty" }, M(1, "Any", "OpenAI")));
    }

    [Fact]
    public void Filter_NullWhenNothingSelected_UnionWhenSeveral()
    {
        var c = PlanCatalog.Load();
        Assert.Null(c.Filter([]));
        Assert.Null(c.Filter(["does-not-exist"]));

        var f = c.Filter(["claude_pro", "chatgpt_plus"])!;
        Assert.True(f(M(1, "Claude Sonnet", "Anthropic")));
        Assert.True(f(M(2, "GPT-5", "OpenAI")));
        Assert.False(f(M(3, "Gemini", "Google")));
    }

    [Fact]
    public void UserFile_ReplacesBuiltInAndBadFileFallsBackWithWarning()
    {
        var dir = Path.Combine(Path.GetTempPath(), "llmpulse-plans-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var good = Path.Combine(dir, "good.json");
            File.WriteAllText(good, """[ { "id": "mine", "vendors": ["Acme"], "name": { "en": "Mine" } } ]""");
            var c = PlanCatalog.Load(good);
            Assert.True(c.IsUserDefined);
            Assert.Equal("mine", c.Plans.Single().Id);

            var bad = Path.Combine(dir, "bad.json");
            File.WriteAllText(bad, "{ not json");
            var fallback = PlanCatalog.Load(bad);
            Assert.False(fallback.IsUserDefined);
            Assert.NotNull(fallback.LoadWarning);
            Assert.True(fallback.Plans.Count > 1);
        }
        finally { Directory.Delete(dir, true); }
    }
}
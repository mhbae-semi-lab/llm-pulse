using LLMPulse.Core;

namespace LLMPulse.Tests;

public class CoverageGapsTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);

    [Fact]
    public void BaseName_StripsTrailingEffortParenthetical() =>
        Assert.Equal("Claude Fable 5.2", CoverageGaps.BaseName("Claude Fable 5.2 (Max, Default Fallback)"));

    [Fact]
    public void RecentUnscored_GroupsVariants_AndIgnoresScoredOrOld()
    {
        var models = new (long, string, string?)[]
        {
            (1, "GPT-6.2 Astra (Low)", "2026-11-17"), (2, "GPT-6.2 Astra (High)", "2026-11-17"),   // 최근, 점수 없음 → 1건
            (3, "Gemini 4 Neon (Medium)", "2026-11-18"),                                          // 최근, 점수 있음
            (4, "Old Model", "2025-01-01"),                                                       // 오래됨
            (5, "Undated", null),                                                                  // 출시일 모름
        };

        var gaps = CoverageGaps.RecentUnscored(models, new HashSet<long> { 3 }, Today, 30);

        Assert.Equal(["GPT-6.2 Astra"], gaps);
    }

    [Fact]
    public void RecentUnscored_OneScoredVariantCoversTheWholeModel()
    {
        var models = new (long, string, string?)[] { (1, "X (Low)", "2026-11-17"), (2, "X (High)", "2026-11-17") };
        Assert.Empty(CoverageGaps.RecentUnscored(models, new HashSet<long> { 2 }, Today, 30));
    }
}
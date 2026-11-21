using LLMPulse.Core.Sources;

namespace LLMPulse.Tests;

public class ArtificialAnalysisMappingTests
{
    [Fact]
    public void Map_FreeSample_ExtractsMetricsPriceAndBlended()
    {
        var row = new AaModel
        {
            Name = "GLM-5.3 (Max)",
            Slug = "glm-5-3",
            ReleaseDate = "2026-08-18",
            ModelCreator = new AaCreator { Name = "Z AI" },
            Evaluations = new AaEvaluations
            {
                IntelligenceIndex = 31.7,
                CodingIndex = null,
                AgenticIndex = 12.0,
            },
            Pricing = new AaPricing
            {
                Price1MInputTokens = 1.4,
                Price1MOutputTokens = 4.4,
            },
            PerformanceV3 = new AaPerformanceV3
            {
                MeanOutputTokensPerSecond = 62.36,
                MeanTimeToFirstTokenSeconds = 3.45,
            },
        };

        var m = ArtificialAnalysisAdapter.Map(row);
        Assert.NotNull(m);
        Assert.Equal("glm-5-3", m!.SourceName);
        Assert.Equal("Z AI", m.Vendor);
        Assert.Equal(31.7, m.Metrics.Single(x => x.MetricId == MetricIds.IntelligenceIndex).Value);
        Assert.DoesNotContain(m.Metrics, x => x.MetricId == MetricIds.CodingIndex);
        Assert.Equal(12.0, m.Metrics.Single(x => x.MetricId == MetricIds.AgenticIndex).Value);
        Assert.Equal(62.36, m.Metrics.Single(x => x.MetricId == MetricIds.OutputTokensPerSecond).Value);
        Assert.Equal(3.45, m.Metrics.Single(x => x.MetricId == MetricIds.TimeToFirstToken).Value);
        Assert.Equal(1.4, m.InputPerM);
        Assert.Equal(4.4, m.OutputPerM);
        Assert.Equal((3 * 1.4 + 4.4) / 4.0, m.BlendedPerM);
    }

    [Fact]
    public void Map_FallsBackToMedianPerformance_WhenV3Missing()
    {
        var row = new AaModel
        {
            Name = "X",
            Slug = "x",
            Performance = new AaPerformance
            {
                MedianOutputTokensPerSecond = 10,
                MedianTimeToFirstTokenSeconds = 1.5,
            },
        };

        var m = ArtificialAnalysisAdapter.Map(row)!;
        Assert.Equal(10, m.Metrics.Single(x => x.MetricId == MetricIds.OutputTokensPerSecond).Value);
        Assert.Equal(1.5, m.Metrics.Single(x => x.MetricId == MetricIds.TimeToFirstToken).Value);
    }
}

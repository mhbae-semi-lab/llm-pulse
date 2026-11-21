using LLMPulse.Core.Models;
using LLMPulse.Core.Sources;
using Parquet.Serialization;

namespace LLMPulse.Tests;

public class LmArenaTests
{
    private static LmArenaAdapter.ArenaRow Row(string model, string category, double? rating) =>
        new() { ModelName = model, Category = category, Rating = rating };

    private static async Task<List<FetchedModel>> Parse(params LmArenaAdapter.ArenaRow[] rows)
    {
        using var ms = new MemoryStream();
        await ParquetSerializer.SerializeAsync(rows, ms);
        ms.Position = 0;
        return await LmArenaAdapter.ParseAsync(ms);
    }

    [Fact]
    public async Task Parse_MapsCategoriesToMetrics_AndIgnoresOthers()
    {
        var models = await Parse(
            Row("Model A", "overall", 1500.04),
            Row("Model A", "coding", 1490),
            Row("Model A", "korean", 1480),
            Row("Model A", "math", 1700),
            Row("Model A", "creative_writing", 1650), // 매핑 없는 카테고리
            Row("Model B", "overall", 1400));

        Assert.Equal(["Model A", "Model B"], models.Select(m => m.SourceName));
        var a = models[0].Metrics.ToDictionary(m => m.MetricId, m => m.Value);
        Assert.Equal(1500.0, a[MetricIds.LmArenaOverall]);   // 소수 1자리로 반올림
        Assert.Equal(1490, a[MetricIds.LmArenaCoding]);
        Assert.Equal(1480, a[MetricIds.LmArenaKorean]);
        Assert.Equal(1700, a[MetricIds.LmArenaMath]);
        Assert.Equal(4, a.Count);
        Assert.Single(models[1].Metrics);
    }

    [Fact]
    public async Task Parse_DuplicatesKeepHighest_NullRatingsSkipped()
    {
        var models = await Parse(
            Row("Model A", "overall", 1400),
            Row("Model A", "overall", 1450),
            Row("Model A", "coding", null),
            Row("", "overall", 1600));

        var m = Assert.Single(models);
        var metric = Assert.Single(m.Metrics);
        Assert.Equal(1450, metric.Value);
    }

    [Fact]
    public void Categories_PointToDefinedMetrics()
    {
        var catalog = LLMPulse.Core.Catalog.LoadEmbedded();
        foreach (var metricId in LmArenaAdapter.CategoryMetrics.Values)
            Assert.Contains(catalog.Metrics, x => x.Id == metricId);
    }
}
using System.Reflection;
using System.Text.Json;
using LLMPulse.Core.Models;

namespace LLMPulse.Core;

/// <summary>data/metrics.json, data/categories.json (EmbeddedResource) 로더.</summary>
public sealed class Catalog
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public IReadOnlyList<MetricDef> Metrics { get; }
    public IReadOnlyList<CategoryDef> Categories { get; }
    public IReadOnlyDictionary<string, MetricDef> MetricsById { get; }

    public Catalog(IReadOnlyList<MetricDef> metrics, IReadOnlyList<CategoryDef> categories)
    {
        Metrics = metrics;
        Categories = categories;
        MetricsById = metrics.ToDictionary(m => m.Id);
    }

    public static Catalog LoadEmbedded()
    {
        var metrics = Read<List<MetricDef>>("data.metrics.json");
        var categories = Read<List<CategoryDef>>("data.categories.json");
        return new Catalog(metrics, categories);
    }

    private static T Read<T>(string resource)
    {
        var asm = typeof(Catalog).Assembly;
        using var s = asm.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded resource '{resource}' not found.");
        return JsonSerializer.Deserialize<T>(s, Json)
            ?? throw new InvalidOperationException($"Resource '{resource}' is empty.");
    }
}

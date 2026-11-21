using System.Text.Json;
using System.Text.Json.Serialization;
using LLMPulse.Core.Models;
using LLMPulse.Core.Storage;
using Microsoft.Extensions.Logging;

namespace LLMPulse.Core.Sources;

/// <summary>
/// Artificial Analysis Free language models API.
/// Legacy <c>/api/v2/data/*</c> 은 2026-11-04 폐기 → <c>/api/v2/language/models/free</c>.
/// </summary>
public sealed class ArtificialAnalysisAdapter : ISourceAdapter
{
    public const string Source = "artificial_analysis";
    private static readonly Uri BaseUri = new("https://artificialanalysis.ai/api/v2/language/models/free");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;
    private readonly IApiKeyStore _keys;
    private readonly ILogger<ArtificialAnalysisAdapter> _log;

    public string SourceId => Source;

    private readonly ResponseCache? _cache;

    public ArtificialAnalysisAdapter(HttpClient http, IApiKeyStore keys, ILogger<ArtificialAnalysisAdapter> log, ResponseCache? cache = null)
    {
        _http = http;
        _keys = keys;
        _log = log;
        _cache = cache;
    }

    public async Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var key = _keys.Get(Source)
            ?? Environment.GetEnvironmentVariable("AA_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                "Artificial Analysis API key missing. Run with --set-api-key or set AA_API_KEY.");

        var bodies = _cache?.TryRead();
        var fromCache = bodies is not null;
        if (bodies is not null)
        {
            _log.LogInformation("AA: using cached response ({Pages} pages), no API calls", bodies.Count);
        }
        else
        {
            var fetched = new List<string>();
            var page = 1;
            while (true)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUri}?page={page}");
                req.Headers.TryAddWithoutValidation("x-api-key", key);

                using var res = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
                var body = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"AA API {(int)res.StatusCode} {res.ReasonPhrase}: {Truncate(body, 200)}");

                fetched.Add(body);
                var more = JsonSerializer.Deserialize<AaFreeResponse>(body, JsonOptions)?.Pagination?.HasMore ?? false;
                if (!more) break;
                page++;
            }
            _cache?.Write(fetched); // 전부 성공했을 때만 보관
            bodies = fetched;
        }

        var models = new List<FetchedModel>();
        foreach (var body in bodies)
        {
            var payload = JsonSerializer.Deserialize<AaFreeResponse>(body, JsonOptions)
                ?? throw new InvalidOperationException("AA API returned empty body.");
            foreach (var row in payload.Data ?? [])
            {
                var mapped = Map(row);
                if (mapped is not null) models.Add(mapped);
            }
            _log.LogInformation("AA page {Page}/{Total}: +{Count} models (running {TotalModels})",
                payload.Pagination?.Page, payload.Pagination?.TotalPages, payload.Data?.Count ?? 0, models.Count);
        }

        return new SourceFetchResult { SourceId = Source, Models = models, Note = fromCache ? "cached" : null };
    }
    /// <summary>단위 테스트용 공개 매핑.</summary>
    public static FetchedModel? Map(AaModel row)
    {
        if (string.IsNullOrWhiteSpace(row.Slug) || string.IsNullOrWhiteSpace(row.Name))
            return null;

        var metrics = new List<RawMetric>();
        Add(metrics, MetricIds.IntelligenceIndex, row.Evaluations?.IntelligenceIndex);
        Add(metrics, MetricIds.CodingIndex, row.Evaluations?.CodingIndex);
        Add(metrics, MetricIds.AgenticIndex, row.Evaluations?.AgenticIndex);
        Add(metrics, MetricIds.FinanceIndex, row.Evaluations?.FinanceIndex);
        Add(metrics, MetricIds.StrategyIndex, row.Evaluations?.StrategyIndex);
        Add(metrics, MetricIds.LegalIndex, row.Evaluations?.LegalIndex);
        Add(metrics, MetricIds.HealthcareIndex, row.Evaluations?.HealthcareIndex);
        Add(metrics, MetricIds.EngineeringIndex, row.Evaluations?.EngineeringIndex);
        Add(metrics, MetricIds.EconomicsIndex, row.Evaluations?.EconomicsIndex);

        var tok = row.PerformanceV3?.MeanOutputTokensPerSecond
            ?? row.Performance?.MedianOutputTokensPerSecond;
        var ttft = row.PerformanceV3?.MeanTimeToFirstTokenSeconds
            ?? row.Performance?.MedianTimeToFirstTokenSeconds;
        Add(metrics, MetricIds.OutputTokensPerSecond, tok);
        Add(metrics, MetricIds.TimeToFirstToken, ttft);

        return new FetchedModel
        {
            SourceName = row.Slug!,
            CanonicalName = row.Name!,
            Vendor = row.ModelCreator?.Name,
            ReleaseDate = row.ReleaseDate,
            Metrics = metrics,
            InputPerM = row.Pricing?.Price1MInputTokens,
            OutputPerM = row.Pricing?.Price1MOutputTokens,
        };
    }

    private static void Add(List<RawMetric> list, string id, double? value)
    {
        if (value is { } v && !double.IsNaN(v) && !double.IsInfinity(v))
            list.Add(new RawMetric(id, v));
    }

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";
}

#region AA Free JSON DTOs

public sealed class AaFreeResponse
{
    public AaPagination? Pagination { get; set; }
    public List<AaModel>? Data { get; set; }
}

public sealed class AaPagination
{
    public int Page { get; set; }
    [JsonPropertyName("page_size")] public int PageSize { get; set; }
    [JsonPropertyName("total_pages")] public int TotalPages { get; set; }
    [JsonPropertyName("has_more")] public bool HasMore { get; set; }
}

public sealed class AaModel
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Slug { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("model_creator")] public AaCreator? ModelCreator { get; set; }
    public AaEvaluations? Evaluations { get; set; }
    public AaPricing? Pricing { get; set; }
    public AaPerformance? Performance { get; set; }
    [JsonPropertyName("performance_v3")]
    public AaPerformanceV3? PerformanceV3 { get; set; }
}

public sealed class AaCreator
{
    public string? Name { get; set; }
}

public sealed class AaEvaluations
{
    [JsonPropertyName("artificial_analysis_intelligence_index")]
    public double? IntelligenceIndex { get; set; }
    [JsonPropertyName("artificial_analysis_coding_index")]
    public double? CodingIndex { get; set; }
    [JsonPropertyName("artificial_analysis_agentic_index")]
    public double? AgenticIndex { get; set; }
    [JsonPropertyName("artificial_analysis_finance_and_accounting_index")]
    public double? FinanceIndex { get; set; }
    [JsonPropertyName("artificial_analysis_strategy_and_ops_index")]
    public double? StrategyIndex { get; set; }
    [JsonPropertyName("artificial_analysis_legal_index")]
    public double? LegalIndex { get; set; }
    [JsonPropertyName("artificial_analysis_healthcare_and_medical_index")]
    public double? HealthcareIndex { get; set; }
    [JsonPropertyName("artificial_analysis_engineering_index")]
    public double? EngineeringIndex { get; set; }
    [JsonPropertyName("artificial_analysis_economics_index")]
    public double? EconomicsIndex { get; set; }
}

public sealed class AaPricing
{
    [JsonPropertyName("price_1m_input_tokens")]
    public double? Price1MInputTokens { get; set; }
    [JsonPropertyName("price_1m_output_tokens")]
    public double? Price1MOutputTokens { get; set; }
}

public sealed class AaPerformance
{
    [JsonPropertyName("median_output_tokens_per_second")]
    public double? MedianOutputTokensPerSecond { get; set; }
    [JsonPropertyName("median_time_to_first_token_seconds")]
    public double? MedianTimeToFirstTokenSeconds { get; set; }
}

public sealed class AaPerformanceV3
{
    [JsonPropertyName("mean_output_tokens_per_second")]
    public double? MeanOutputTokensPerSecond { get; set; }
    [JsonPropertyName("mean_time_to_first_token_seconds")]
    public double? MeanTimeToFirstTokenSeconds { get; set; }
}

#endregion

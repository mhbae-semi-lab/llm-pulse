using System.Text.Json;
using System.Text.Json.Serialization;
using LLMPulse.Core.Models;
using Microsoft.Extensions.Logging;

namespace LLMPulse.Core.Sources;

/// <summary>
/// OpenRouter 모델 목록(키 불필요). 새 모델을 만들지 않고 이미 알려진 모델에만 컨텍스트 길이/가격을 보강한다 (MatchOnly).
/// </summary>
public sealed class OpenRouterAdapter : ISourceAdapter
{
    public const string Source = "openrouter";
    private static readonly Uri ModelsUri = new("https://openrouter.ai/api/v1/models");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;
    private readonly ILogger<OpenRouterAdapter> _log;

    public string SourceId => Source;

    public OpenRouterAdapter(HttpClient http, ILogger<OpenRouterAdapter> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        using var res = await _http.GetAsync(ModelsUri, cancellationToken).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenRouter API {(int)res.StatusCode} {res.ReasonPhrase}");

        var models = ParseModels(body);
        _log.LogInformation("OpenRouter: {Count} models", models.Count);
        return new SourceFetchResult { SourceId = Source, Models = models, MatchOnly = true };
    }

    /// <summary>단위 테스트용 공개 파서.</summary>
    public static List<FetchedModel> ParseModels(string json)
    {
        var payload = JsonSerializer.Deserialize<OrResponse>(json, JsonOptions);
        var list = new List<FetchedModel>();
        foreach (var m in payload?.Data ?? [])
        {
            if (string.IsNullOrWhiteSpace(m.Id) || string.IsNullOrWhiteSpace(m.Name)) continue;
            if (m.Id.EndsWith(":free", StringComparison.Ordinal)) continue; // 무료 변형은 본 모델과 중복

            var prompt = ToPerMillion(m.Pricing?.Prompt);
            var completion = ToPerMillion(m.Pricing?.Completion);
            var metrics = new List<RawMetric>();
            if (m.ContextLength is > 0)
                metrics.Add(new RawMetric(MetricIds.ContextK, m.ContextLength.Value / 1000.0));

            list.Add(new FetchedModel
            {
                SourceName = m.Id,
                CanonicalName = m.Name,
                Vendor = m.Id.Contains('/') ? m.Id[..m.Id.IndexOf('/')] : null,
                Metrics = metrics,
                InputPerM = prompt,
                OutputPerM = completion,
            });
        }

        return list;
    }

    // OpenRouter 가격은 "토큰당 USD" 문자열. 음수(-1: 동적 가격 라우터)나 파싱 불가는 없음 처리.
    private static double? ToPerMillion(string? perToken) =>
        double.TryParse(perToken, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0
            ? Math.Round(v * 1_000_000, 6)
            : null;

    private sealed class OrResponse { public List<OrModel>? Data { get; set; } }

    private sealed class OrModel
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        [JsonPropertyName("context_length")] public double? ContextLength { get; set; }
        public OrPricing? Pricing { get; set; }
    }

    private sealed class OrPricing
    {
        public string? Prompt { get; set; }
        public string? Completion { get; set; }
    }
}
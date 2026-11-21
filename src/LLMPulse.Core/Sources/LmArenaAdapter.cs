using System.Text.Json.Serialization;
using LLMPulse.Core.Models;
using Microsoft.Extensions.Logging;
using Parquet.Serialization;

namespace LLMPulse.Core.Sources;

/// <summary>
/// Arena(사람 선호 투표 기반 Elo) 공개 리더보드. HuggingFace 의 lmarena-ai/leaderboard-dataset 중 text/latest(약 0.6MB parquet)를
/// 내려받아 종합·코딩·한국어 점수를 읽는다. 키 불필요. 새 모델은 만들지 않고 이미 알려진 모델에만 붙인다 (MatchOnly).
/// </summary>
public sealed class LmArenaAdapter : ISourceAdapter
{
    public const string Source = "lmarena";

    private static readonly Uri DataUri = new(
        "https://huggingface.co/datasets/lmarena-ai/leaderboard-dataset/resolve/main/text/latest-00000-of-00001.parquet");

    /// <summary>리더보드의 category 값 → 우리 지표 id.</summary>
    public static readonly IReadOnlyDictionary<string, string> CategoryMetrics = new Dictionary<string, string>
    {
        ["overall"] = MetricIds.LmArenaOverall,
        ["coding"] = MetricIds.LmArenaCoding,
        ["korean"] = MetricIds.LmArenaKorean,
        ["math"] = MetricIds.LmArenaMath,
        ["hard_prompts"] = MetricIds.LmArenaHard,
        ["instruction_following"] = MetricIds.LmArenaInstruction,
    };

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6)];

    private readonly HttpClient _http;
    private readonly ILogger<LmArenaAdapter> _log;

    public string SourceId => Source;

    public LmArenaAdapter(HttpClient http, ILogger<LmArenaAdapter> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var bytes = await DownloadAsync(cancellationToken).ConfigureAwait(false);
        using var ms = new MemoryStream(bytes);
        var (models, asOf) = await ParseWithDateAsync(ms, cancellationToken).ConfigureAwait(false);
        _log.LogInformation("Arena: {Count} models, published {AsOf}", models.Count, asOf);
        return new SourceFetchResult { SourceId = Source, Models = models, MatchOnly = true, DataAsOf = asOf };
    }

    private async Task<byte[]> DownloadAsync(CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var res = await _http.GetAsync(DataUri, ct).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                    return await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                // 호스트가 일시적으로 거절/오류인 경우만 재시도
                if ((int)res.StatusCode < 500 && (int)res.StatusCode != 429 || attempt >= RetryDelays.Length)
                    throw new HttpRequestException($"Arena {(int)res.StatusCode} {res.ReasonPhrase}");
            }
            catch (HttpRequestException) when (attempt < RetryDelays.Length) { }

            await Task.Delay(RetryDelays[attempt], ct).ConfigureAwait(false);
        }
    }

    /// <summary>parquet 스트림을 읽어 모델별 Elo 지표 목록으로 만든다 (같은 모델·카테고리가 여러 번이면 최고값).</summary>
    public static async Task<List<FetchedModel>> ParseAsync(Stream parquet, CancellationToken ct = default) =>
        (await ParseWithDateAsync(parquet, ct).ConfigureAwait(false)).Models;

    /// <summary>ParseAsync + 리더보드 게시일(leaderboard_publish_date 최댓값) = 소스 데이터 기준일.</summary>
    public static async Task<(List<FetchedModel> Models, DateOnly? AsOf)> ParseWithDateAsync(Stream parquet, CancellationToken ct = default)
    {
        var rows = (await ParquetSerializer.DeserializeAsync<ArenaRow>(parquet, cancellationToken: ct).ConfigureAwait(false)).Data;

        var best = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.ModelName) || r.Rating is not { } rating || double.IsNaN(rating)) continue;
            if (r.Category is null || !CategoryMetrics.TryGetValue(r.Category, out var metric)) continue;

            if (!best.TryGetValue(r.ModelName, out var perMetric))
                best[r.ModelName] = perMetric = [];
            if (!perMetric.TryGetValue(metric, out var old) || rating > old)
                perMetric[metric] = rating;
        }

        var models = best.Select(kv => new FetchedModel
        {
            SourceName = kv.Key,
            CanonicalName = kv.Key,
            Metrics = kv.Value.Select(m => new RawMetric(m.Key, Math.Round(m.Value, 1))).ToList(),
        }).OrderBy(m => m.SourceName, StringComparer.OrdinalIgnoreCase).ToList();
        return (models, SourceFreshness.Latest(rows.Select(r => r.PublishDate)));
    }

    /// <summary>parquet 열 이름과 맞춘 행 모델 (읽기/테스트 공용).</summary>
    public sealed class ArenaRow
    {
        [JsonPropertyName("model_name")] public string? ModelName { get; set; }
        [JsonPropertyName("rating")] public double? Rating { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("leaderboard_publish_date")] public string? PublishDate { get; set; }
    }
}
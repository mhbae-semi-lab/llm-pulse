using System.Globalization;
using LLMPulse.Core.Models;
using Microsoft.Extensions.Logging;

namespace LLMPulse.Core.Sources;

/// <summary>
/// Aider 폴리글랏 코딩 리더보드 (공개 YAML). 새 모델을 만들지 않고 알려진 모델에만 pass_rate_2 를 붙인다 (MatchOnly).
/// 이 리더보드는 갱신 주기가 불규칙하므로 최신 모델이 없을 수 있다.
/// </summary>
public sealed class AiderAdapter : ISourceAdapter
{
    public const string Source = "aider";
    private static readonly Uri DataUri =
        new("https://raw.githubusercontent.com/Aider-AI/aider/main/aider/website/_data/polyglot_leaderboard.yml");

    private readonly HttpClient _http;
    private readonly ILogger<AiderAdapter> _log;

    public string SourceId => Source;

    public AiderAdapter(HttpClient http, ILogger<AiderAdapter> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<SourceFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        using var res = await _http.GetAsync(DataUri, cancellationToken).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Aider leaderboard {(int)res.StatusCode} {res.ReasonPhrase}");

        var yaml = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var models = ParseLeaderboard(yaml);
        _log.LogInformation("Aider: {Count} models", models.Count);
        return new SourceFetchResult { SourceId = Source, Models = models, MatchOnly = true, DataAsOf = LatestDate(yaml) };
    }

    /// <summary>항목들의 <c>date:</c> 중 가장 늦은 날 = 리더보드 데이터 기준일 (2026-11 확인: 2025-10-03 이후 새 항목 없음).</summary>
    public static DateOnly? LatestDate(string yaml) =>
        SourceFreshness.Latest(yaml.Split('\n')
            .Select(l => l.Trim().TrimStart('-', ' '))
            .Where(l => l.StartsWith("date:", StringComparison.Ordinal))
            .Select(l => l[5..].Trim().Trim('"', '\'')));

    /// <summary>
    /// 최상위 리스트 항목(<c>- key: value</c>)의 평면 스칼라만 읽는 최소 파서 (이 파일 형식 전용, YAML 라이브러리 불필요).
    /// 같은 모델이 편집 형식/실행별로 여러 번 나오면 가장 높은 pass_rate_2 를 쓴다.
    /// </summary>
    public static List<FetchedModel> ParseLeaderboard(string yaml)
    {
        var best = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        string? model = null;
        double? rate = null;

        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(model) && rate is { } r
                && (!best.TryGetValue(model, out var old) || r > old))
                best[model] = r;
            model = null;
            rate = null;
        }

        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("- ", StringComparison.Ordinal)) Flush();

            var body = line.TrimStart(' ', '-');
            var colon = body.IndexOf(':');
            if (colon <= 0) continue;
            var key = body[..colon].Trim();
            var value = body[(colon + 1)..].Trim().Trim('"', '\'');

            if (key == "model") model = value;
            else if (key == "pass_rate_2"
                     && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                rate = d;
        }

        Flush();

        return best.Select(kv => new FetchedModel
        {
            SourceName = kv.Key,
            CanonicalName = kv.Key,
            Metrics = [new RawMetric(MetricIds.AiderPolyglot, kv.Value)],
        }).OrderBy(m => m.SourceName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
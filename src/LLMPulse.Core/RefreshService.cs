using LLMPulse.Core.Scoring;
using LLMPulse.Core.Sources;
using LLMPulse.Core.Storage;
using Microsoft.Extensions.Logging;

namespace LLMPulse.Core;

/// <summary>DESIGN §5 갱신 파이프라인 (M1: fetch → upsert → snapshot → log). 점수/추천은 M2.</summary>
public sealed class RefreshService
{
    private readonly IEnumerable<ISourceAdapter> _adapters;
    private readonly SqliteStore _store;
    private readonly ScoringService _scoring;
    private readonly ILogger<RefreshService> _log;
    private readonly Plans.PlanCatalogUpdater? _plans;

    public RefreshService(
        IEnumerable<ISourceAdapter> adapters,
        SqliteStore store,
        ScoringService scoring,
        ILogger<RefreshService> log,
        Plans.PlanCatalogUpdater? plans = null)
    {
        _adapters = adapters;
        _store = store;
        _scoring = scoring;
        _log = log;
        _plans = plans;
    }

    public async Task<RefreshSummary> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var results = new List<SourceRefreshResult>();

        // 소스별 실패 격리: 병렬 fetch 후 개별 upsert
        var tasks = _adapters.Select(a => FetchOneAsync(a, cancellationToken)).ToArray();
        var fetches = await Task.WhenAll(tasks).ConfigureAwait(false);

        // 보강(MatchOnly) 소스는 주 소스 모델이 먼저 저장된 뒤에 매칭되어야 한다
        foreach (var (adapter, outcome) in _adapters.Zip(fetches).OrderBy(p => p.Second.Result?.MatchOnly ?? false))
        {
            var logId = _store.BeginRefreshLog(adapter.SourceId, started);
            try
            {
                if (outcome.Error is not null)
                {
                    _store.FinishRefreshLog(logId, "error", outcome.Error, DateTimeOffset.UtcNow);
                    results.Add(new SourceRefreshResult(adapter.SourceId, 0, outcome.Error));
                    _log.LogWarning("Refresh {Source} failed: {Error}", adapter.SourceId, outcome.Error);
                    continue;
                }

                _store.UpsertFetch(outcome.Result!, DateTimeOffset.UtcNow);
                if (outcome.Result!.DataAsOf is { } asOf)
                    _store.SetSetting(SourceFreshness.SettingKey(adapter.SourceId), asOf.ToString("yyyy-MM-dd"));
                var msg = $"{outcome.Result!.Models.Count} models" + (outcome.Result.Note is { Length: > 0 } note ? $" ({note})" : "");
                _store.FinishRefreshLog(logId, "ok", msg, DateTimeOffset.UtcNow);
                results.Add(new SourceRefreshResult(adapter.SourceId, outcome.Result.Models.Count, null, outcome.Result.MatchOnly));
                _log.LogInformation("Refresh {Source}: {Msg}", adapter.SourceId, msg);
            }
            catch (Exception ex)
            {
                _store.FinishRefreshLog(logId, "error", ex.Message, DateTimeOffset.UtcNow);
                results.Add(new SourceRefreshResult(adapter.SourceId, 0, ex.Message));
                _log.LogError(ex, "Refresh {Source} upsert failed", adapter.SourceId);
            }
        }

        // 보강(MatchOnly) 소스만 성공한 경우엔 "갱신됨" 으로 치지 않는다 (주 소스 실패를 가리지 않도록)
        if (results.Any(r => r.Error is null && !r.MatchOnly))
        {
            // 분야 점수 재계산 + 저장 (전일 대비 변동 감지의 기준)
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            _scoring.Persist(_scoring.Build(today), today);
            _store.SetSetting("last_refresh_at", DateTimeOffset.UtcNow.ToString("O"));
        }

        // 구독 플랜 목록 사본 갱신 (실패해도 갱신 전체는 실패로 보지 않는다: 기존 사본 또는 내장본을 쓴다)
        if (_plans is not null)
        {
            var error = await _plans.UpdateAsync(Path.Combine(AppPaths.DataDirectory, Plans.PlanCatalog.RemoteFileName), cancellationToken).ConfigureAwait(false);
            _store.SetSetting(Plans.PlanCatalogUpdater.SettingStatus, error is null ? $"ok|{DateTimeOffset.UtcNow:O}" : $"error|{error}");
            if (error is not null) _log.LogInformation("Plans remote update skipped: {Error}", error);
        }

        var counts = _store.Counts();
        return new RefreshSummary(results, counts.Models, counts.Metrics, counts.Prices);
    }

    private static async Task<FetchOutcome> FetchOneAsync(ISourceAdapter adapter, CancellationToken ct)
    {
        try
        {
            var result = await adapter.FetchAsync(ct).ConfigureAwait(false);
            return new FetchOutcome(result, null);
        }
        catch (Exception ex)
        {
            return new FetchOutcome(null, ex.Message);
        }
    }

    private sealed record FetchOutcome(Models.SourceFetchResult? Result, string? Error);
}

public sealed record SourceRefreshResult(string SourceId, int ModelCount, string? Error, bool MatchOnly = false);
public sealed record RefreshSummary(
    IReadOnlyList<SourceRefreshResult> Sources,
    int TotalModels,
    int TotalMetrics,
    int TotalPrices)
{
    public bool AllSucceeded => Sources.All(s => s.Error is null);

    /// <summary>주 소스(모델을 만드는 소스)가 하나라도 성공했는가.</summary>
    public bool PrimaryOk => Sources.Any(s => s.Error is null && !s.MatchOnly);
}

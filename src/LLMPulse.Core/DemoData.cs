using LLMPulse.Core.Models;
using LLMPulse.Core.Sources;
using LLMPulse.Core.Storage;

namespace LLMPulse.Core;

/// <summary>
/// API 키 없이 UI 를 확인하기 위한 가짜 데이터(--demo). 실제 데이터가 아니며 source="demo" 로 표시된다.
/// 결정적 난수라 매번 같은 결과.
/// </summary>
public static class DemoData
{
    public const string Source = "demo";

    public static void Seed(SqliteStore store, DateTimeOffset now)
    {
        var rnd = new Random(42);
        var vendors = new[] { "Acme AI", "Borealis", "Cobalt Labs", "Delta Mind", "Echo Research" };
        var models = new List<FetchedModel>();

        for (var i = 1; i <= 48; i++)
        {
            var vendor = vendors[i % vendors.Length];
            var tier = rnd.NextDouble(); // 0 약함 … 1 강함
            var intelligence = 12 + tier * 48 + rnd.NextDouble() * 4;
            double? coding = rnd.NextDouble() < 0.85 ? Math.Max(5, intelligence - 8 + rnd.NextDouble() * 12) : null;
            double? agentic = rnd.NextDouble() < 0.6 ? Math.Max(3, intelligence - 14 + rnd.NextDouble() * 14) : null;
            var price = Math.Round(0.1 + Math.Pow(tier, 2.2) * 40 * (0.5 + rnd.NextDouble()), 2);
            var inputPrice = Math.Round(price * 0.6, 3);
            var outputPrice = Math.Round(price * 1.8, 3);
            var tokS = 20 + (1 - tier) * 180 * (0.5 + rnd.NextDouble());
            var ttft = 0.3 + tier * 8 * rnd.NextDouble();

            var metrics = new List<RawMetric>
            {
                new(MetricIds.IntelligenceIndex, Math.Round(intelligence, 1)),
                new(MetricIds.OutputTokensPerSecond, Math.Round(tokS, 1)),
                new(MetricIds.TimeToFirstToken, Math.Round(ttft, 2)),
            };
            if (coding is { } c) metrics.Add(new RawMetric(MetricIds.CodingIndex, Math.Round(c, 1)));
            if (agentic is { } a) metrics.Add(new RawMetric(MetricIds.AgenticIndex, Math.Round(a, 1)));

            // 분야별 전문 지수: 별도 난수열을 써서 위의 기존 데모 값이 바뀌지 않게 한다 (AA 실제 응답처럼 일부 모델에만 값이 있다)
            var domainRnd = new Random(1000 + i);
            if (domainRnd.NextDouble() < 0.7)
                foreach (var domainId in new[] { MetricIds.FinanceIndex, MetricIds.StrategyIndex, MetricIds.LegalIndex,
                                           MetricIds.HealthcareIndex, MetricIds.EngineeringIndex, MetricIds.EconomicsIndex })
                    metrics.Add(new RawMetric(domainId, Math.Round(Math.Clamp(intelligence + domainRnd.NextDouble() * 10 - 5, 5, 99), 1)));

            models.Add(new FetchedModel
            {
                SourceName = $"demo-{i:00}",
                CanonicalName = $"{vendor.Split(' ')[0]} Demo {i:00}",
                Vendor = vendor,
                ReleaseDate = now.AddDays(-rnd.Next(10, 400)).ToString("yyyy-MM-dd"),
                Metrics = metrics,
                InputPerM = inputPrice,
                OutputPerM = outputPrice,
            });
        }

        // 지난 14일 이력(추이 차트 확인용): 일별로 값이 조금씩 움직인 스냅샷을 먼저 넣고 오늘 값을 마지막에 덮어쓴다.
        for (var back = 14; back >= 1; back--)
        {
            var past = models.Select(m => new FetchedModel
            {
                SourceName = m.SourceName,
                CanonicalName = m.CanonicalName,
                Vendor = m.Vendor,
                ReleaseDate = m.ReleaseDate,
                InputPerM = m.InputPerM,
                OutputPerM = m.OutputPerM,
                Metrics = m.Metrics.Select(x => x.MetricId is MetricIds.IntelligenceIndex or MetricIds.CodingIndex or MetricIds.AgenticIndex
                    ? new RawMetric(x.MetricId, Math.Round(x.Value * (1 - back * 0.004 * (0.5 + rnd.NextDouble())), 1))
                    : x).ToList(),
            }).ToList();
            store.UpsertFetch(new SourceFetchResult { SourceId = Source, Models = past }, now.AddDays(-back));
        }

        store.UpsertFetch(new SourceFetchResult { SourceId = Source, Models = models }, now);

        // OpenRouter 보강 데모: 대부분 이름으로 자동 매칭되고, 일부는 매칭 필요 목록에 남는다.
        var ctxChoices = new[] { 32.0, 64, 128, 200, 1000 };
        var orModels = new List<FetchedModel>();
        for (var i = 1; i <= 30; i++)
        {
            var vendor = vendors[i % vendors.Length];
            var first = vendor.Split(' ')[0];
            // 대부분 주 소스와 같은 가격, 9번째마다 2.2배: 소스 불일치(가격) 경고가 보이게 한다
            var aa = models[i - 1];
            var factor = i % 9 == 0 ? 2.2 : 1.0;
            orModels.Add(new FetchedModel
            {
                SourceName = $"{first.ToLowerInvariant()}/demo-{i:00}",
                CanonicalName = $"{vendor}: {first} Demo {i:00}",
                Vendor = first.ToLowerInvariant(),
                Metrics = [new RawMetric(MetricIds.ContextK, ctxChoices[(i * 7) % ctxChoices.Length])],
                InputPerM = aa.InputPerM * factor,
                OutputPerM = aa.OutputPerM * factor,
            });
        }

        orModels.Add(new FetchedModel
        {
            SourceName = "acme/demo-ultra-preview",
            CanonicalName = "Acme AI: Acme Demo Ultra Preview",
            Vendor = "acme",
            Metrics = [new RawMetric(MetricIds.ContextK, 256)],
        });
        orModels.Add(new FetchedModel
        {
            SourceName = "echo/demo-44-instruct",
            CanonicalName = "Echo Research: Echo Demo 44 Instruct",
            Vendor = "echo",
            Metrics = [new RawMetric(MetricIds.ContextK, 128)],
        });
        store.UpsertFetch(new SourceFetchResult { SourceId = OpenRouterAdapter.Source, Models = orModels, MatchOnly = true }, now.AddDays(-1));
        store.UpsertFetch(new SourceFetchResult { SourceId = OpenRouterAdapter.Source, Models = orModels, MatchOnly = true }, now);

        // Aider 데모: 상위 일부 모델에만 값이 있고 하나는 매칭 필요
        var aider = models.Where((_, i) => i % 3 == 0)
            .Select(m => new FetchedModel
            {
                SourceName = m.CanonicalName,
                CanonicalName = m.CanonicalName,
                Metrics = [new RawMetric(MetricIds.AiderPolyglot, Math.Round(m.Metrics.First(x => x.MetricId == MetricIds.IntelligenceIndex).Value * 1.2, 1))],
            }).ToList();
        aider.Add(new FetchedModel
        {
            SourceName = "demo-legacy-model (high)",
            CanonicalName = "demo-legacy-model (high)",
            Metrics = [new RawMetric(MetricIds.AiderPolyglot, 41.3)],
        });
        store.UpsertFetch(new SourceFetchResult { SourceId = AiderAdapter.Source, Models = aider, MatchOnly = true }, now);
        // Arena 데모: 짝수 번째 모델에 Elo, 한국어는 일부만. 하나는 매칭 필요.
        var arena = models.Where((_, i) => i % 2 == 0).Select((m, i) =>
        {
            var ii = m.Metrics.First(x => x.MetricId == MetricIds.IntelligenceIndex).Value;
            // i == 5, 17: 벤치마크(지능)와 사람 선호(Arena) 순위가 크게 어긋나는 모델 (소스 불일치 경고 시연)
            var overall = i is 5 or 17 ? 1110 + (i % 5) * 4 : 1100 + ii * 6 + (i % 5) * 4;
            var metrics = new List<RawMetric>
            {
                new(MetricIds.LmArenaOverall, Math.Round(overall, 1)),
                new(MetricIds.LmArenaCoding, Math.Round(1090 + ii * 6.4 - (i % 4) * 5, 1)),
            };
            metrics.Add(new RawMetric(MetricIds.LmArenaMath, Math.Round(1070 + ii * 7.1 - (i % 6) * 4, 1)));
            metrics.Add(new RawMetric(MetricIds.LmArenaHard, Math.Round(1060 + ii * 6.9 + (i % 4) * 5, 1)));
            if (i % 4 != 0) metrics.Add(new RawMetric(MetricIds.LmArenaInstruction, Math.Round(1095 + ii * 5.5 + (i % 5) * 3, 1)));
            if (i % 3 != 0) metrics.Add(new RawMetric(MetricIds.LmArenaKorean, Math.Round(1085 + ii * 5.8 + (i % 7) * 3, 1)));
            return new FetchedModel { SourceName = m.CanonicalName, CanonicalName = m.CanonicalName, Metrics = metrics };
        }).ToList();
        arena.Add(new FetchedModel
        {
            SourceName = "demo-legacy-model (max)",
            CanonicalName = "demo-legacy-model (max)",
            Metrics = [new RawMetric(MetricIds.LmArenaOverall, 1312.4)],
        });
        // 어제 값도 남겨 둔다 ("1위가 바뀐 이유"는 어제와 오늘 스냅샷을 비교한다)
        store.UpsertFetch(new SourceFetchResult { SourceId = LmArenaAdapter.Source, Models = arena, MatchOnly = true }, now.AddDays(-1));
        store.UpsertFetch(new SourceFetchResult { SourceId = LmArenaAdapter.Source, Models = arena, MatchOnly = true }, now);

        // 소스 상태 · 신선도: Aider 는 실제 공개 리더보드처럼 마지막 항목이 2025-10-03 (90일 넘음 → 추천 계산에서 제외)
        foreach (var src in new[] { OpenRouterAdapter.Source, AiderAdapter.Source, LmArenaAdapter.Source })
        {
            var logId = store.BeginRefreshLog(src, now);
            store.FinishRefreshLog(logId, "ok", src == AiderAdapter.Source ? $"{aider.Count} models" : src == LmArenaAdapter.Source ? $"{arena.Count} models" : $"{orModels.Count} models", now);
        }

        store.SetSetting(SourceFreshness.SettingKey(AiderAdapter.Source), "2025-10-03");
        store.SetSetting(SourceFreshness.SettingKey(LmArenaAdapter.Source), now.AddDays(-2).ToString("yyyy-MM-dd"));
        var id = store.BeginRefreshLog(Source, now);
        store.FinishRefreshLog(id, "ok", $"{models.Count} demo models", now);
        store.SetSetting("last_refresh_at", now.ToString("O"));
    }
}

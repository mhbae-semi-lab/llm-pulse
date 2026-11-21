using Dapper;
using LLMPulse.Core.Matching;
using LLMPulse.Core.Models;
using LLMPulse.Core.Plans;
using LLMPulse.Core.Sources;
using LLMPulse.Core.Scoring;
using Microsoft.Data.Sqlite;

namespace LLMPulse.Core.Storage;

public sealed record SourceItemRow(string Source, string SourceName, string DisplayName, string? Vendor, IReadOnlyDictionary<string, double> Metrics, double? InputPerM, double? OutputPerM, long? ModelId, string? ModelName);

public sealed record SourceStatus(string Source, string? LastSuccessAt, string? LastStatus, string? LastMessage);

public sealed class SqliteStore
{
    // 레코드 생성자 매핑은 NULL 컬럼을 byte[] 로 추론해 실패하므로 속성 setter 클래스로 받는다
    private sealed class StatusRow
    {
        public string Source { get; set; } = "";
        public string? LastSuccessAt { get; set; }
        public string? LastStatus { get; set; }
        public string? LastMessage { get; set; }
    }

    private sealed class ItemListRow
    {
        public string SourceName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? Vendor { get; set; }
        public double? InputPerM { get; set; }
        public double? OutputPerM { get; set; }
        public string? MetricsJson { get; set; }
        public double? ContextK { get; set; }
        public long? ModelId { get; set; }
        public string? ModelName { get; set; }
    }

    private sealed class ItemRec
    {
        public string Display { get; set; } = "";
        public string? Vendor { get; set; }
        public double? Ctx { get; set; }
        public double? InPerM { get; set; }
        public double? OutPerM { get; set; }
        public string At { get; set; } = "";
        public string? Json { get; set; }
    }

    private readonly Database _db;

    public SqliteStore(Database db) => _db = db;

    public void UpsertFetch(SourceFetchResult fetch, DateTimeOffset fetchedAt)
    {
        var at = fetchedAt.UtcDateTime.ToString("O");
        var date = fetchedAt.UtcDateTime.ToString("yyyy-MM-dd");

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        List<(long Id, string Name)>? candidates = null;
        if (fetch.MatchOnly)
        {
            candidates = conn.Query<(long Id, string Name)>("SELECT id, canonical_name FROM model;", transaction: tx).ToList();
            conn.Execute("DELETE FROM source_item WHERE source = @source;", new { source = fetch.SourceId }, tx);
        }

        foreach (var m in fetch.Models)
        {
            long modelId;
            if (fetch.MatchOnly)
            {
                SaveSourceItem(conn, tx, fetch.SourceId, m, at);

                var alias = conn.QuerySingleOrDefault<long?>(
                    "SELECT model_id FROM model_alias WHERE source = @source AND source_name = @name;",
                    new { source = fetch.SourceId, name = m.SourceName }, tx);
                alias ??= AliasResolver.FindBest(m.CanonicalName, candidates!);
                if (alias is null) continue; // "매칭 필요" 목록에 남는다

                conn.Execute("""
                    INSERT OR IGNORE INTO model_alias (source, source_name, model_id) VALUES (@source, @name, @id);
                    """, new { source = fetch.SourceId, name = m.SourceName, id = alias.Value }, tx);
                modelId = alias.Value;
            }
            else
            {
                modelId = ResolveOrInsertModel(conn, tx, fetch.SourceId, m);
            }

            ApplyMetrics(conn, tx, fetch.SourceId, modelId, m, at, date, fetch.MatchOnly);
        }

        tx.Commit();
    }

    private static void SaveSourceItem(SqliteConnection conn, SqliteTransaction tx, string source, FetchedModel m, string at) =>
        conn.Execute("""
            INSERT INTO source_item (source, source_name, display_name, vendor, context_k, input_per_m, output_per_m, fetched_at, metrics_json)
            VALUES (@source, @name, @display, @vendor, @ctx, @input, @output, @at, @json)
            ON CONFLICT(source, source_name) DO UPDATE SET
                display_name = excluded.display_name, vendor = excluded.vendor, context_k = excluded.context_k,
                input_per_m = excluded.input_per_m, output_per_m = excluded.output_per_m, fetched_at = excluded.fetched_at,
                metrics_json = excluded.metrics_json;
            """, new
        {
            source,
            name = m.SourceName,
            display = m.CanonicalName,
            vendor = m.Vendor,
            ctx = m.Metrics.FirstOrDefault(x => x.MetricId == MetricIds.ContextK)?.Value,
            input = m.InputPerM,
            output = m.OutputPerM,
            at,
            json = System.Text.Json.JsonSerializer.Serialize(m.Metrics.ToDictionary(x => x.MetricId, x => x.Value)),
        }, tx);

    private static void ApplyMetrics(
        SqliteConnection conn, SqliteTransaction tx, string source, long modelId, FetchedModel m,
        string at, string date, bool fillPriceOnly)
    {
        foreach (var metric in m.Metrics)
        {
            conn.Execute("""
                INSERT INTO raw_metric (model_id, metric_id, value, source, fetched_at)
                VALUES (@modelId, @metricId, @value, @source, @at)
                ON CONFLICT(model_id, metric_id, source) DO UPDATE SET
                    value = excluded.value,
                    fetched_at = excluded.fetched_at;
                """, new { modelId, metricId = metric.MetricId, value = metric.Value, source, at }, tx);

            conn.Execute("""
                INSERT INTO snapshot (date, model_id, metric_id, value)
                VALUES (@date, @modelId, @metricId, @value)
                ON CONFLICT(date, model_id, metric_id) DO UPDATE SET value = excluded.value;
                """, new { date, modelId, metricId = metric.MetricId, value = metric.Value }, tx);

            conn.Execute(
                "INSERT OR IGNORE INTO metric_def (id) VALUES (@id);",
                new { id = metric.MetricId }, tx);
        }

        if (m.InputPerM is null && m.OutputPerM is null) return;

        // 보강 소스는 가격이 없는 모델만 채운다 (주 소스 가격을 덮어쓰지 않음). 주 소스는 항상 덮어쓴다.
        conn.Execute($"""
            INSERT INTO price (model_id, input_per_m, output_per_m, blended_per_m, source, fetched_at)
            VALUES (@modelId, @input, @output, @blended, @source, @at)
            ON CONFLICT(model_id) DO UPDATE SET
                input_per_m = excluded.input_per_m,
                output_per_m = excluded.output_per_m,
                blended_per_m = excluded.blended_per_m,
                source = excluded.source,
                fetched_at = excluded.fetched_at
            {(fillPriceOnly ? "WHERE price.source = excluded.source" : "")};
            """, new
        {
            modelId,
            input = m.InputPerM,
            output = m.OutputPerM,
            blended = m.BlendedPerM,
            source,
            at,
        }, tx);

        // 오늘 날짜의 현재 가격을 이력에 남긴다 (같은 날 재실행은 덮어씀)
        conn.Execute("""
            INSERT OR REPLACE INTO price_history (model_id, date, blended_per_m)
            SELECT model_id, @date, blended_per_m FROM price
            WHERE model_id = @modelId AND blended_per_m IS NOT NULL AND blended_per_m > 0;
            """, new { modelId, date }, tx);
    }

    /// <summary>보강 소스 항목 + 현재 매칭 상태 (UI 매칭 화면용).</summary>
    public List<SourceItemRow> SourceItems(string source)
    {
        using var conn = _db.Open();
        return conn.Query<ItemListRow>("""
            SELECT i.source_name AS SourceName, i.display_name AS DisplayName, i.vendor AS Vendor,
                   i.input_per_m AS InputPerM, i.output_per_m AS OutputPerM, i.metrics_json AS MetricsJson,
                   i.context_k AS ContextK,
                   a.model_id AS ModelId, m.canonical_name AS ModelName
            FROM source_item i
            LEFT JOIN model_alias a ON a.source = i.source AND a.source_name = i.source_name
            LEFT JOIN model m ON m.id = a.model_id
            WHERE i.source = @source
            ORDER BY (a.model_id IS NOT NULL), i.display_name COLLATE NOCASE;
            """, new { source })
            .Select(r => new SourceItemRow(
                source, r.SourceName, r.DisplayName, r.Vendor, ParseMetrics(r.MetricsJson, r.ContextK),
                r.InputPerM, r.OutputPerM, r.ModelId, r.ModelName))
            .ToList();
    }

    private static Dictionary<string, double> ParseMetrics(string? json, double? contextK)
    {
        var d = new Dictionary<string, double>();
        if (!string.IsNullOrWhiteSpace(json))
            try { d = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(json) ?? d; }
            catch (System.Text.Json.JsonException) { /* 손상된 값은 무시하고 context_k 로 대체 */ }
        if (d.Count == 0 && contextK is { } c) d[MetricIds.ContextK] = c;
        return d;
    }
    public List<(long Id, string Name)> ModelChoices()
    {
        using var conn = _db.Open();
        return conn.Query<(long Id, string Name)>(
            "SELECT id, canonical_name FROM model ORDER BY canonical_name COLLATE NOCASE;").ToList();
    }

    /// <summary>수동 매칭 (기존 매칭이 있으면 교체). 항목 데이터를 즉시 대상 모델에 반영한다.</summary>
    public void LinkAlias(string source, string sourceName, long modelId)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        RemoveAlias(conn, tx, source, sourceName);

        var item = conn.QuerySingleOrDefault<ItemRec>("""
            SELECT display_name AS Display, vendor AS Vendor, context_k AS Ctx, input_per_m AS InPerM, output_per_m AS OutPerM, fetched_at AS At, metrics_json AS Json
            FROM source_item WHERE source = @source AND source_name = @sourceName;
            """, new { source, sourceName }, tx);
        if (item is null) throw new InvalidOperationException($"Unknown source item: {sourceName}");

        conn.Execute("INSERT INTO model_alias (source, source_name, model_id) VALUES (@source, @sourceName, @modelId);",
            new { source, sourceName, modelId }, tx);

        var metrics = ParseMetrics(item.Json, item.Ctx).Select(kv => new RawMetric(kv.Key, kv.Value)).ToList();
        var fm = new FetchedModel
        {
            SourceName = sourceName,
            CanonicalName = item.Display,
            Vendor = item.Vendor,
            Metrics = metrics,
            InputPerM = item.InPerM,
            OutputPerM = item.OutPerM,
        };
        ApplyMetrics(conn, tx, source, modelId, fm, item.At, item.At[..10], fillPriceOnly: true);
        tx.Commit();
    }

    public void UnlinkAlias(string source, string sourceName)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        RemoveAlias(conn, tx, source, sourceName);
        tx.Commit();
    }

    private static void RemoveAlias(SqliteConnection conn, SqliteTransaction tx, string source, string sourceName)
    {
        var old = conn.QuerySingleOrDefault<long?>(
            "SELECT model_id FROM model_alias WHERE source = @source AND source_name = @sourceName;",
            new { source, sourceName }, tx);
        if (old is null) return;

        conn.Execute("DELETE FROM model_alias WHERE source = @source AND source_name = @sourceName;", new { source, sourceName }, tx);
        conn.Execute("DELETE FROM raw_metric WHERE model_id = @old AND source = @source;", new { old, source }, tx);
        conn.Execute("DELETE FROM price WHERE model_id = @old AND source = @source;", new { old, source }, tx);
    }
    public long BeginRefreshLog(string source, DateTimeOffset startedAt)
    {
        using var conn = _db.Open();
        conn.Execute("""
            INSERT INTO refresh_log (started_at, source, status, message)
            VALUES (@at, @source, 'running', NULL);
            """, new { at = startedAt.UtcDateTime.ToString("O"), source });
        return conn.ExecuteScalar<long>("SELECT last_insert_rowid();");
    }

    public void FinishRefreshLog(long id, string status, string? message, DateTimeOffset finishedAt)
    {
        using var conn = _db.Open();
        conn.Execute("""
            UPDATE refresh_log
            SET finished_at = @at, status = @status, message = @message
            WHERE id = @id;
            """, new { id, at = finishedAt.UtcDateTime.ToString("O"), status, message });
    }

    public void SetSetting(string key, string value)
    {
        using var conn = _db.Open();
        conn.Execute("""
            INSERT INTO setting (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """, new { key, value });
    }

    public string? GetSetting(string key)
    {
        using var conn = _db.Open();
        return conn.QuerySingleOrDefault<string?>("SELECT value FROM setting WHERE key = @key;", new { key });
    }

    /// <summary>紐⑤뜽蹂??먮낯 吏???뚯뒪 媛?理쒖떊 媛??곗꽑) + 釉붾젋?붾뱶 媛寃?</summary>
    public List<ModelInput> LoadModelInputs()
    {
        using var conn = _db.Open();
        var models = conn.Query<(long Id, string Name, string? Vendor, long? Open, string? Release)>(
            "SELECT id, canonical_name, vendor, open_weights, release_date FROM model;").ToList();

        var metricRows = conn.Query<(long ModelId, string MetricId, double Value)>("""
            SELECT model_id, metric_id, value FROM raw_metric
            ORDER BY fetched_at;   -- ?섏쨷 媛믪씠 ??뼱?
            """).ToList();
        var prices = conn.Query<(long ModelId, double? Blended)>(
            "SELECT model_id, blended_per_m FROM price;").ToDictionary(p => p.ModelId, p => p.Blended);

        var byModel = new Dictionary<long, Dictionary<string, double>>();
        foreach (var (modelId, metricId, value) in metricRows)
        {
            if (!byModel.TryGetValue(modelId, out var d)) byModel[modelId] = d = new();
            d[metricId] = value;
        }

        return models.Select(m => new ModelInput
        {
            Id = m.Id,
            Name = m.Name,
            Vendor = m.Vendor,
            ReleaseDate = m.Release,
            OpenWeights = m.Open is null ? null : m.Open != 0,
            Metrics = byModel.GetValueOrDefault(m.Id) ?? new(),
            BlendedPrice = prices.GetValueOrDefault(m.Id),
        }).ToList();
    }

    public void SaveCategoryScores(string date, IEnumerable<CategoryScore> scores)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var s in scores)
        {
            conn.Execute("""
                INSERT INTO category_score (date, category_id, model_id, score, rank, coverage)
                VALUES (@date, @cat, @model, @score, @rank, @coverage)
                ON CONFLICT(date, category_id, model_id) DO UPDATE SET
                    score = excluded.score, rank = excluded.rank, coverage = excluded.coverage;
                """, new { date, cat = s.CategoryId, model = s.ModelId, score = s.Score, rank = s.Rank, coverage = s.Coverage }, tx);
        }

        tx.Commit();
    }

    /// <summary>date ?댁쟾 媛??理쒓렐 ?ㅻ깄?룹뿉?쒖쓽 遺꾩빞 1??紐⑤뜽 id.</summary>
    public long? PreviousTopModel(string categoryId, string beforeDate)
    {
        using var conn = _db.Open();
        return conn.QuerySingleOrDefault<long?>("""
            SELECT model_id FROM category_score
            WHERE category_id = @categoryId AND rank = 1
              AND date = (SELECT MAX(date) FROM category_score WHERE category_id = @categoryId AND date < @beforeDate)
            ORDER BY score DESC LIMIT 1;
            """, new { categoryId, beforeDate });
    }

    /// <summary>date ?댁쟾???대? ?곗씠?곌? ?덉뿀?붾뜲 date ??泥섏쓬 ?섑???紐⑤뜽 ??(泥??ㅽ뻾? 0).</summary>
    public int CountNewModels(string date)
    {
        using var conn = _db.Open();
        var hadEarlier = conn.ExecuteScalar<int>("SELECT count(*) FROM snapshot WHERE date < @date;", new { date }) > 0;
        if (!hadEarlier) return 0;
        return conn.ExecuteScalar<int>($"""
            SELECT count(*) FROM (
                SELECT s.model_id FROM snapshot s JOIN model m ON m.id = s.model_id
                GROUP BY s.model_id HAVING MIN(s.date) >= @date AND {RecentRelease}
            );
            """, new { date });
    }

    /// <summary>?뚯뒪蹂?留덉?留??깃났/留덉?留??쒕룄 ?뺣낫 (UI 諛곗???.</summary>
    public List<SourceStatus> SourceStatuses()
    {
        using var conn = _db.Open();
        return conn.Query<StatusRow>("""
            SELECT s.source AS Source,
                   CAST((SELECT MAX(finished_at) FROM refresh_log WHERE source = s.source AND status = 'ok') AS TEXT) AS LastSuccessAt,
                   CAST((SELECT status FROM refresh_log WHERE source = s.source ORDER BY id DESC LIMIT 1) AS TEXT) AS LastStatus,
                   CAST((SELECT message FROM refresh_log WHERE source = s.source ORDER BY id DESC LIMIT 1) AS TEXT) AS LastMessage
            FROM (SELECT DISTINCT source FROM refresh_log) s;
            """).Select(r => new SourceStatus(r.Source, r.LastSuccessAt, r.LastStatus, r.LastMessage)).ToList();
    }

    /// <summary>일별 스냅샷 이력 (추이 차트용). 날짜 오름차순.</summary>
    public List<(DateOnly Date, double Value)> LoadHistory(long modelId, string metricId)
    {
        using var conn = _db.Open();
        return conn.Query<(string Date, double Value)>(
                "SELECT date, value FROM snapshot WHERE model_id = @modelId AND metric_id = @metricId ORDER BY date;",
                new { modelId, metricId })
            .Select(r => (DateOnly.ParseExact(r.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), r.Value))
            .ToList();
    }

    // ---- 변경 내역 ----

    /// <summary>점수가 저장된 가장 최근 날짜와 그 직전 날짜.</summary>
    public (string? Latest, string? Previous) ScoreDates()
    {
        using var conn = _db.Open();
        var latest = conn.ExecuteScalar<string?>("SELECT MAX(date) FROM category_score;");
        if (latest is null) return (null, null);
        var prev = conn.ExecuteScalar<string?>("SELECT MAX(date) FROM category_score WHERE date < @latest;", new { latest });
        return (latest, prev);
    }

    public Dictionary<string, Dictionary<long, int>> LoadRanks(string date)
    {
        using var conn = _db.Open();
        var result = new Dictionary<string, Dictionary<long, int>>(StringComparer.Ordinal);
        foreach (var (cat, model, rank) in conn.Query<(string Cat, long Model, long Rank)>(
                     "SELECT category_id, model_id, rank FROM category_score WHERE date = @date AND rank IS NOT NULL;", new { date }))
        {
            if (!result.TryGetValue(cat, out var d)) result[cat] = d = [];
            d[model] = (int)rank;
        }

        return result;
    }

    /// <summary>모델의 분야 점수 최근 추이 (오래된 순, 최대 days 개). 점수가 없는 날은 건너뛴다.</summary>
    public List<double> LoadScoreTrend(string categoryId, long modelId, int days = 7)
    {
        using var conn = _db.Open();
        var rows = conn.Query<double>("""
            SELECT score FROM category_score
            WHERE category_id = @categoryId AND model_id = @modelId AND score IS NOT NULL
            ORDER BY date DESC LIMIT @days;
            """, new { categoryId, modelId, days }).ToList();
        rows.Reverse();
        return rows;
    }

    /// <summary>점수가 저장된 모든 날짜 (최신순).</summary>
    public List<string> ScoreDateList()
    {
        using var conn = _db.Open();
        return conn.Query<string>("SELECT DISTINCT date FROM category_score ORDER BY date DESC;").ToList();
    }

    /// <summary>특정 날짜의 분야별 점수/순위.</summary>
    public Dictionary<long, (double? Score, int? Rank)> LoadCategoryScoresOn(string categoryId, string date)
    {
        using var conn = _db.Open();
        return conn.Query<(long Model, double? Score, long? Rank)>(
                "SELECT model_id, score, rank FROM category_score WHERE category_id = @categoryId AND date = @date;",
                new { categoryId, date })
            .ToDictionary(r => r.Model, r => (r.Score, r.Rank is { } k ? (int?)k : null));
    }

    /// <summary>date 시점(그 날 이전 가장 최근 기록)의 모델별 가격.</summary>
    public Dictionary<long, double> LoadPricesAsOf(string date)
    {
        using var conn = _db.Open();
        return conn.Query<(long Model, double Price)>("""
            SELECT p.model_id, p.blended_per_m FROM price_history p
            WHERE p.date = (SELECT MAX(date) FROM price_history WHERE model_id = p.model_id AND date <= @date);
            """, new { date }).ToDictionary(r => r.Model, r => r.Price);
    }

    /// <summary>
    /// "신규" 는 처음 관측됐고 출시도 최근(60일)인 모델만. 소스가 갑자기 더 많은 옛 모델을 주기 시작한 경우(예: 페이지 넘김 수정)를
    /// 신규로 세지 않기 위함. 출시일 모르면 신규로 본다.
    /// </summary>
    private const string RecentRelease = "(m.release_date IS NULL OR m.release_date >= date(@date, '-60 days'))";

    /// <summary>date 에 처음 관측된 모델 (그 이전 스냅샷이 전혀 없으면 비어 있음).</summary>
    public List<long> NewModelIds(string date)
    {
        using var conn = _db.Open();
        if (conn.ExecuteScalar<int>("SELECT count(*) FROM snapshot WHERE date < @date;", new { date }) == 0) return [];
        return conn.Query<long>($"""
            SELECT s.model_id FROM snapshot s JOIN model m ON m.id = s.model_id
            GROUP BY s.model_id HAVING MIN(s.date) >= @date AND {RecentRelease};
            """, new { date }).ToList();
    }

    // ---- 구독 보정/한도 ----

    public Dictionary<string, Dictionary<string, PlanAccess>> LoadPlanOverrides()
    {
        using var conn = _db.Open();
        var result = new Dictionary<string, Dictionary<string, PlanAccess>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (plan, model, access) in conn.Query<(string Plan, string Model, long Access)>(
                     "SELECT plan_id, model_name, access FROM plan_override;"))
        {
            if (!result.TryGetValue(plan, out var d)) result[plan] = d = new(StringComparer.Ordinal);
            d[model] = (PlanAccess)Math.Clamp((int)access, 0, 2);
        }

        return result;
    }

    /// <summary>access 가 null 이면 보정을 지워 플랜 기본 규칙으로 되돌린다.</summary>
    public void SetPlanOverride(string planId, string modelName, PlanAccess? access)
    {
        using var conn = _db.Open();
        if (access is null)
        {
            conn.Execute("DELETE FROM plan_override WHERE plan_id = @planId AND model_name = @modelName;",
                new { planId, modelName });
            return;
        }

        conn.Execute("""
            INSERT INTO plan_override (plan_id, model_name, access) VALUES (@planId, @modelName, @access)
            ON CONFLICT(plan_id, model_name) DO UPDATE SET access = excluded.access;
            """, new { planId, modelName, access = (int)access.Value });
    }

    private sealed class LimitRow
    {
        public string PlanId { get; set; } = "";
        public long? Cap { get; set; }
        public string Period { get; set; } = "month";
        public long Used { get; set; }
        public string? PeriodStart { get; set; }
        public string? Note { get; set; }
    }

    public PlanLimit? GetPlanLimit(string planId, DateTimeOffset now)
    {
        using var conn = _db.Open();
        var r = conn.QuerySingleOrDefault<LimitRow>("""
            SELECT plan_id AS PlanId, cap AS Cap, period AS Period, used AS Used, period_start AS PeriodStart, note AS Note
            FROM plan_limit WHERE plan_id = @planId;
            """, new { planId });
        if (r is null) return null;

        var limit = new PlanLimit(r.PlanId, r.Cap is { } c ? (int)c : null, r.Period, (int)r.Used,
            DateTimeOffset.TryParse(r.PeriodStart, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var ps) ? ps : null,
            r.Note ?? "");
        return limit.Effective(now);
    }

    public void SavePlanLimit(string planId, int? cap, string period, string note)
    {
        if (!PlanLimit.Periods.Contains(period)) period = "month";
        using var conn = _db.Open();
        conn.Execute("""
            INSERT INTO plan_limit (plan_id, cap, period, used, period_start, note)
            VALUES (@planId, @cap, @period, 0, NULL, @note)
            ON CONFLICT(plan_id) DO UPDATE SET cap = excluded.cap, note = excluded.note,
                -- 기간 종류가 바뀌면 이전 카운트는 의미가 없으므로 초기화
                used = CASE WHEN plan_limit.period = excluded.period THEN plan_limit.used ELSE 0 END,
                period_start = CASE WHEN plan_limit.period = excluded.period THEN plan_limit.period_start ELSE NULL END,
                period = excluded.period;
            """, new { planId, cap, period, note });
    }

    /// <summary>사용 횟수 증감 (기간이 지났으면 먼저 초기화). 0 아래로는 내려가지 않는다.</summary>
    public PlanLimit? AddPlanUsage(string planId, int delta, DateTimeOffset now)
    {
        var current = GetPlanLimit(planId, now);
        if (current is null) return null;

        var used = Math.Max(0, current.Used + delta);
        var start = current.PeriodStart ?? (used > 0 ? now : null);
        using var conn = _db.Open();
        conn.Execute("UPDATE plan_limit SET used = @used, period_start = @start WHERE plan_id = @planId;",
            new { planId, used, start = start?.UtcDateTime.ToString("O") });
        return current with { Used = used, PeriodStart = start };
    }

    /// <summary>자동 집계 결과로 사용 횟수와 기간 시작을 직접 설정한다 (한도 행이 있을 때만).</summary>
    public void SetPlanUsage(string planId, int used, DateTimeOffset? start)
    {
        using var conn = _db.Open();
        conn.Execute("UPDATE plan_limit SET used = @used, period_start = @start WHERE plan_id = @planId;",
            new { planId, used, start = start?.UtcDateTime.ToString("O") });
    }

    public void ResetPlanUsage(string planId)
    {
        using var conn = _db.Open();
        conn.Execute("UPDATE plan_limit SET used = 0, period_start = NULL WHERE plan_id = @planId;", new { planId });
    }

    /// <summary>모델별 가장 최근 두 이력 값을 비교해 minPct% 이상 달라진 모델을 변동폭 큰 순으로 돌려준다.</summary>
    public List<PriceChange> LoadPriceChanges(double minPct = 5)
    {
        using var conn = _db.Open();
        var rows = conn.Query<(long ModelId, string Date, double Price)>(
            "SELECT model_id, date, blended_per_m FROM price_history ORDER BY model_id, date DESC;").ToList();
        var result = new List<PriceChange>();
        foreach (var g in rows.GroupBy(r => r.ModelId))
        {
            var two = g.Take(2).ToList();
            if (two.Count < 2) continue;
            var change = PriceChange.Create(g.Key, two[1].Price, two[0].Price, minPct);
            if (change is not null) result.Add(change);
        }

        return result.OrderByDescending(c => Math.Abs(c.Pct)).ToList();
    }

    /// <summary>모델의 최근 가격 이력 (오래된 순, 최대 days 개).</summary>
    public List<double> LoadPriceHistory(long modelId, int days = 30)
    {
        using var conn = _db.Open();
        var rows = conn.Query<double>(
            "SELECT blended_per_m FROM price_history WHERE model_id = @modelId ORDER BY date DESC LIMIT @days;",
            new { modelId, days }).ToList();
        rows.Reverse();
        return rows;
    }

    /// <summary>특정 날짜의 가격 이력을 직접 기록한다 (데모/테스트용).</summary>
    public void SavePriceHistory(string date, long modelId, double blendedPerM)
    {
        using var conn = _db.Open();
        conn.Execute("INSERT OR REPLACE INTO price_history (model_id, date, blended_per_m) VALUES (@modelId, @date, @blendedPerM);",
            new { modelId, date, blendedPerM });
    }

    // ---- 소스 신선도 · 불일치 · 과거 시점 입력 ----

    /// <summary>beforeDate 이전에 이 분야 점수가 저장된 가장 최근 날짜 (PreviousTopModel 과 같은 기준).</summary>
    public string? PreviousScoreDate(string categoryId, string beforeDate)
    {
        using var conn = _db.Open();
        return conn.ExecuteScalar<string?>(
            "SELECT MAX(date) FROM category_score WHERE category_id = @categoryId AND date < @beforeDate;",
            new { categoryId, beforeDate });
    }

    /// <summary>갱신 기록이 있는 소스마다 신선도 (소스 기준일 설정 → 없으면 마지막 성공일).</summary>
    public List<SourceFreshnessInfo> SourceFreshnessAll(DateOnly today) =>
        SourceStatuses().Select(s => SourceFreshness.Evaluate(
            s.Source,
            SourceFreshness.Latest([GetSetting(SourceFreshness.SettingKey(s.Source))]),
            SourceFreshness.Latest([s.LastSuccessAt]),
            today)).ToList();

    /// <summary>스냅샷 한 칸을 직접 쓴다 (데모 시나리오용).</summary>
    public void SaveSnapshotValue(string date, long modelId, string metricId, double value)
    {
        using var conn = _db.Open();
        conn.Execute("""
            INSERT INTO snapshot (date, model_id, metric_id, value) VALUES (@date, @modelId, @metricId, @value)
            ON CONFLICT(date, model_id, metric_id) DO UPDATE SET value = excluded.value;
            """, new { date, modelId, metricId, value });
    }

    /// <summary>값이 오직 주어진 소스들에서만 온 지표 id (그 소스를 빼면 사라지는 지표).</summary>
    public HashSet<string> MetricsOnlyFrom(IReadOnlyCollection<string> sources)
    {
        if (sources.Count == 0) return [];
        using var conn = _db.Open();
        return conn.Query<string>("""
            SELECT metric_id FROM raw_metric GROUP BY metric_id
            HAVING SUM(CASE WHEN source IN @sources THEN 0 ELSE 1 END) = 0;
            """, new { sources }).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>date 시점(그 날 이전 가장 최근 스냅샷)의 모델 입력. date 이전에 관측된 적 없는 모델은 빠진다.</summary>
    public List<ModelInput> LoadModelInputsAsOf(string date)
    {
        var current = LoadModelInputs().ToDictionary(m => m.Id);
        var prices = LoadPricesAsOf(date);
        using var conn = _db.Open();
        var rows = conn.Query<(long ModelId, string MetricId, double Value)>("""
            SELECT s.model_id, s.metric_id, s.value FROM snapshot s
            JOIN (SELECT model_id, metric_id, MAX(date) AS d FROM snapshot WHERE date <= @date GROUP BY model_id, metric_id) last
              ON last.model_id = s.model_id AND last.metric_id = s.metric_id AND last.d = s.date;
            """, new { date });
        var byModel = new Dictionary<long, Dictionary<string, double>>();
        foreach (var (modelId, metricId, value) in rows)
        {
            if (!byModel.TryGetValue(modelId, out var d)) byModel[modelId] = d = new();
            d[metricId] = value;
        }

        return byModel.Where(kv => current.ContainsKey(kv.Key)).Select(kv =>
        {
            var m = current[kv.Key];
            return new ModelInput
            {
                Id = m.Id, Name = m.Name, Vendor = m.Vendor, ReleaseDate = m.ReleaseDate, OpenWeights = m.OpenWeights,
                Metrics = kv.Value,
                BlendedPrice = prices.TryGetValue(m.Id, out var p) ? p : null,
            };
        }).ToList();
    }

    /// <summary>
    /// 주 소스(AA) 가격과, 같은 모델에 연결된 OpenRouter 기본 항목 가격(":" 변종 제외, 여러 개면 최저가)을 나란히. 둘 다 있는 모델만.
    /// </summary>
    public Dictionary<long, (double Primary, double OpenRouter)> LoadPricePairs()
    {
        using var conn = _db.Open();
        return conn.Query<(long Model, double Primary, double Or)>("""
            SELECT p.model_id, p.blended_per_m, MIN((3 * i.input_per_m + i.output_per_m) / 4.0)
            FROM price p
            JOIN model_alias a ON a.model_id = p.model_id AND a.source = 'openrouter'
            JOIN source_item i ON i.source = a.source AND i.source_name = a.source_name
            WHERE p.source <> 'openrouter' AND p.blended_per_m > 0
              AND instr(a.source_name, ':') = 0   -- :free, :batch, :nitro 같은 변종 가격은 비교하지 않는다
              AND i.input_per_m IS NOT NULL AND i.output_per_m IS NOT NULL AND (3 * i.input_per_m + i.output_per_m) > 0
            GROUP BY p.model_id;
            """).ToDictionary(r => r.Model, r => (r.Primary, r.Or));
    }

    public (int Models, int Metrics, int Prices) Counts()
    {
        using var conn = _db.Open();
        return (
            conn.ExecuteScalar<int>("SELECT count(*) FROM model;"),
            conn.ExecuteScalar<int>("SELECT count(*) FROM raw_metric;"),
            conn.ExecuteScalar<int>("SELECT count(*) FROM price;"));
    }

    private static long ResolveOrInsertModel(SqliteConnection conn, SqliteTransaction tx, string source, FetchedModel m)
    {
        var existing = conn.QuerySingleOrDefault<long?>(
            "SELECT model_id FROM model_alias WHERE source = @source AND source_name = @name;",
            new { source, name = m.SourceName }, tx);

        if (existing is { } id)
        {
            conn.Execute("""
                UPDATE model SET
                    canonical_name = @name,
                    vendor = @vendor,
                    release_date = @release
                WHERE id = @id;
                """, new { id, name = m.CanonicalName, vendor = m.Vendor, release = m.ReleaseDate }, tx);
            return id;
        }

        conn.Execute("""
            INSERT INTO model (canonical_name, vendor, release_date, open_weights, context_len)
            VALUES (@name, @vendor, @release, NULL, NULL);
            """, new { name = m.CanonicalName, vendor = m.Vendor, release = m.ReleaseDate }, tx);
        id = conn.ExecuteScalar<long>("SELECT last_insert_rowid();", transaction: tx);

        conn.Execute("""
            INSERT INTO model_alias (source, source_name, model_id)
            VALUES (@source, @name, @id);
            """, new { source, name = m.SourceName, id }, tx);

        return id;
    }
}

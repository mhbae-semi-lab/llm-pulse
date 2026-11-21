using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LLMPulse.Core.Storage;

/// <summary>앱 데이터 경로 (DB 파일 등). 설정 파일/DB 는 저장소에 커밋하지 않는다.</summary>
public static class AppPaths
{
    /// <summary>환경 변수 LLMPULSE_DATA_DIR 로 재정의 가능 (테스트/데모/휴대용).</summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("LLMPULSE_DATA_DIR") is { Length: > 0 } overrideDir
            ? overrideDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LLMPulse");

    public static string DatabasePath => Path.Combine(DataDirectory, "llmpulse.db");
}

/// <summary>SQLite 연결 팩토리 + 단순 버전 기반 마이그레이션 (PRAGMA user_version).</summary>
public sealed class Database
{
    private readonly string _connectionString;
    private readonly ILogger<Database> _log;

    public string Path { get; }

    public Database(string path, ILogger<Database> log)
    {
        Path = path;
        _log = log;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>DB 파일과 폴더를 만들고 미적용 마이그레이션을 순서대로 적용한다.</summary>
    public void Migrate()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var conn = Open();

        var current = Scalar(conn, "PRAGMA user_version;");
        for (var v = (int)current; v < Migrations.Steps.Count; v++)
        {
            using var tx = conn.BeginTransaction();
            Exec(conn, tx, Migrations.Steps[v]);
            Exec(conn, tx, $"PRAGMA user_version = {v + 1};");
            tx.Commit();
            _log.LogInformation("DB migrated to v{Version}", v + 1);
        }
    }

    /// <summary>현재 DB 를 한 파일로 백업한다 (사용 중에도 일관된 사본, VACUUM INTO). API 키는 DB 에 없으므로 들어가지 않는다.</summary>
    public void BackupTo(string file)
    {
        if (File.Exists(file)) File.Delete(file);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO @file;";
        cmd.Parameters.AddWithValue("@file", file);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 백업 파일로 현재 DB 를 바꾼다. 이 앱의 DB 가 아니거나 더 새 버전 앱의 DB 면 거절하고 사유를 돌려준다 (현재 DB 는 그대로).
    /// 바꾸기 전 현재 DB 는 llmpulse.db.before-restore 로 남긴다.
    /// </summary>
    public string? RestoreFrom(string file)
    {
        try
        {
            using (var src = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                src.Open();
                var version = Scalar(src, "PRAGMA user_version;");
                if (version > Migrations.Steps.Count) return $"schema v{version} is newer than this app (v{Migrations.Steps.Count}).";
                if (Scalar(src, "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('model', 'raw_metric', 'setting');") < 3)
                    return "not an LLM Pulse database.";
            }
        }
        catch (SqliteException ex)
        {
            return ex.Message;
        }

        SqliteConnection.ClearAllPools();
        File.Copy(Path, Path + ".before-restore", overwrite: true);
        File.Copy(file, Path, overwrite: true);
        Migrate(); // 옛 버전 백업이면 최신 스키마로 올린다
        return null;
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

internal static class Migrations
{
    /// <summary>인덱스 i 의 항목이 user_version i → i+1 마이그레이션. 이미 배포된 항목은 수정하지 말고 추가만 한다.</summary>
    public static readonly IReadOnlyList<string> Steps =
    [
        // v1: DESIGN.md §7 초기 스키마
        """
        CREATE TABLE model (
            id             INTEGER PRIMARY KEY,
            canonical_name TEXT NOT NULL UNIQUE,
            vendor         TEXT,
            release_date   TEXT,
            open_weights   INTEGER,
            context_len    INTEGER
        );
        CREATE TABLE model_alias (
            source      TEXT NOT NULL,
            source_name TEXT NOT NULL,
            model_id    INTEGER NOT NULL REFERENCES model(id) ON DELETE CASCADE,
            PRIMARY KEY (source, source_name)
        );
        CREATE TABLE metric_def (
            id TEXT PRIMARY KEY
        );
        CREATE TABLE raw_metric (
            model_id   INTEGER NOT NULL REFERENCES model(id) ON DELETE CASCADE,
            metric_id  TEXT NOT NULL,
            value      REAL NOT NULL,
            source     TEXT NOT NULL,
            fetched_at TEXT NOT NULL,
            PRIMARY KEY (model_id, metric_id, source)
        );
        CREATE TABLE snapshot (
            date      TEXT NOT NULL,
            model_id  INTEGER NOT NULL REFERENCES model(id) ON DELETE CASCADE,
            metric_id TEXT NOT NULL,
            value     REAL NOT NULL,
            PRIMARY KEY (date, model_id, metric_id)
        );
        CREATE TABLE category_score (
            date        TEXT NOT NULL,
            category_id TEXT NOT NULL,
            model_id    INTEGER NOT NULL REFERENCES model(id) ON DELETE CASCADE,
            score       REAL,
            rank        INTEGER,
            coverage    REAL,
            PRIMARY KEY (date, category_id, model_id)
        );
        CREATE TABLE price (
            model_id       INTEGER PRIMARY KEY REFERENCES model(id) ON DELETE CASCADE,
            input_per_m    REAL,
            output_per_m   REAL,
            blended_per_m  REAL,
            source         TEXT NOT NULL,
            fetched_at     TEXT NOT NULL
        );
        CREATE TABLE refresh_log (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            started_at  TEXT NOT NULL,
            finished_at TEXT,
            source      TEXT NOT NULL,
            status      TEXT NOT NULL,
            message     TEXT
        );
        CREATE TABLE setting (
            key   TEXT PRIMARY KEY,
            value TEXT
        );
        """,
        // v2: 보강 소스(MatchOnly) 원본 항목 + 매칭 상태는 model_alias 존재 여부로 판단
        """
        CREATE TABLE source_item (
            source       TEXT NOT NULL,
            source_name  TEXT NOT NULL,
            display_name TEXT NOT NULL,
            vendor       TEXT,
            context_k    REAL,
            input_per_m  REAL,
            output_per_m REAL,
            fetched_at   TEXT NOT NULL,
            PRIMARY KEY (source, source_name)
        );
        """,
        // v3: 보강 소스의 모든 지표를 보존 (수동 매칭 시 그대로 반영)
        "ALTER TABLE source_item ADD COLUMN metrics_json TEXT;",
        // v4: 구독별 모델 접근 수동 보정(2=포함, 1=한도 있음, 0=불가)과 사용량 한도 메모/카운터
        """
        CREATE TABLE plan_override (
            plan_id    TEXT NOT NULL,
            model_name TEXT NOT NULL,
            access     INTEGER NOT NULL,
            PRIMARY KEY (plan_id, model_name)
        );
        CREATE TABLE plan_limit (
            plan_id      TEXT PRIMARY KEY,
            cap          INTEGER,
            period       TEXT NOT NULL,
            used         INTEGER NOT NULL DEFAULT 0,
            period_start TEXT,
            note         TEXT
        );
        """,
        // v5: 모델별 일자별 블렌디드 가격 이력 (가격 변동 피드용)
        """
        CREATE TABLE price_history (
            model_id      INTEGER NOT NULL REFERENCES model(id) ON DELETE CASCADE,
            date          TEXT NOT NULL,
            blended_per_m REAL NOT NULL,
            PRIMARY KEY (model_id, date)
        );
        """,
    ];
}

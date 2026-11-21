using Dapper;
using LLMPulse.Core.Models;
using LLMPulse.Core.Sources;
using LLMPulse.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMPulse.Tests;

public class SqliteStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "llmpulse-store-" + Guid.NewGuid().ToString("N"));
    private readonly Database _db;
    private readonly SqliteStore _store;

    public SqliteStoreTests()
    {
        _db = new Database(Path.Combine(_dir, "t.db"), NullLogger<Database>.Instance);
        _db.Migrate();
        _store = new SqliteStore(_db);
    }

    [Fact]
    public void UpsertFetch_InsertsAndIsIdempotent()
    {
        var fetch = new SourceFetchResult
        {
            SourceId = ArtificialAnalysisAdapter.Source,
            Models =
            [
                new FetchedModel
                {
                    SourceName = "glm-5-3",
                    CanonicalName = "GLM-5.3 (Max)",
                    Vendor = "Z AI",
                    ReleaseDate = "2026-08-18",
                    Metrics =
                    [
                        new RawMetric(MetricIds.IntelligenceIndex, 31.7),
                        new RawMetric(MetricIds.OutputTokensPerSecond, 62.36),
                    ],
                    InputPerM = 1.4,
                    OutputPerM = 4.4,
                },
            ],
        };

        _store.UpsertFetch(fetch, DateTimeOffset.Parse("2026-11-19T01:00:00Z"));
        _store.UpsertFetch(fetch, DateTimeOffset.Parse("2026-11-19T02:00:00Z"));

        var counts = _store.Counts();
        Assert.Equal(1, counts.Models);
        Assert.Equal(2, counts.Metrics);
        Assert.Equal(1, counts.Prices);

        using var conn = _db.Open();
        var blended = conn.QuerySingle<double>("SELECT blended_per_m FROM price;");
        Assert.Equal((3 * 1.4 + 4.4) / 4.0, blended, precision: 5);
        var snaps = conn.QuerySingle<int>("SELECT count(*) FROM snapshot;");
        Assert.Equal(2, snaps);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}

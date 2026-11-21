using LLMPulse.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMPulse.Tests;

public class DatabaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "llmpulse-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Migrate_CreatesFileAndTables_AndIsIdempotent()
    {
        var db = new Database(Path.Combine(_dir, "t.db"), NullLogger<Database>.Instance);
        db.Migrate();
        db.Migrate();

        Assert.True(File.Exists(db.Path));
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('model','raw_metric','snapshot','category_score','price','refresh_log','setting','model_alias','metric_def')";
        Assert.Equal(9L, (long)cmd.ExecuteScalar()!);
        SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}

using Microsoft.Data.Sqlite;

namespace AITool.IntegrationTests.Developer;

public class PostCompletionUsageMigrationTests
{
    private static string ReadScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AiTool.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, "sql-migrations", "20261104_01_settle_post_completion_canceled_usage.sql"));
    }

    [Fact]
    public void OnlyIndividuallyVerifiedAffectedRowsAreUpdated()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, """
            CREATE TABLE ProxyUsageLogs (
                Id TEXT PRIMARY KEY, Status TEXT, ErrorMessage TEXT, ErrorCategory TEXT,
                IsStreamInterrupted INTEGER, ProtocolType TEXT, ForwardingMode TEXT,
                IsStreaming INTEGER, IsFinalResult INTEGER, RequestedAt TEXT,
                InputTokens INTEGER, CachedTokens INTEGER, OutputTokens INTEGER
            );
            INSERT INTO ProxyUsageLogs VALUES
                ('verified', 'canceled', 'aborted', 'timeout', 1, 'OpenAI', 'bridge', 1, 1, '2026-11-04 13:00:00', 1, 0, 1),
                ('unverified', 'canceled', 'aborted', 'timeout', 1, 'OpenAI', 'bridge', 1, 1, '2026-11-04 13:00:00', 1, 0, 1),
                ('direct', 'canceled', 'aborted', 'timeout', 1, 'OpenAI', 'direct', 1, 1, '2026-11-04 13:00:00', 1, 0, 1),
                ('other-protocol', 'canceled', 'aborted', 'timeout', 1, 'Anthropic', 'bridge', 1, 1, '2026-11-04 13:00:00', 1, 0, 1),
                ('non-stream', 'canceled', 'aborted', 'timeout', 1, 'OpenAI', 'bridge', 0, 1, '2026-11-04 13:00:00', 1, 0, 1),
                ('non-final', 'canceled', 'aborted', 'timeout', 1, 'OpenAI', 'bridge', 1, 0, '2026-11-04 13:00:00', 1, 0, 1),
                ('too-early', 'canceled', 'aborted', 'timeout', 1, 'OpenAI', 'bridge', 1, 1, '2026-11-03 23:59:59', 1, 0, 1),
                ('too-late', 'canceled', 'aborted', 'timeout', 1, 'OpenAI', 'bridge', 1, 1, '2026-11-04 14:00:00', 1, 0, 1),
                ('failed', 'fail', 'aborted', 'timeout', 1, 'OpenAI', 'bridge', 1, 1, '2026-11-04 13:00:00', 1, 0, 1);
            """);

        var script = ReadScript();
        Execute(connection, script);
        Assert.Equal(0L, Count(connection, "Status = 'success'"));

        var ids = "SELECT 'verified' UNION ALL SELECT 'direct' UNION ALL SELECT 'other-protocol' UNION ALL SELECT 'non-stream' UNION ALL SELECT 'non-final' UNION ALL SELECT 'too-early' UNION ALL SELECT 'too-late' UNION ALL SELECT 'failed'";
        Assert.Contains("SELECT NULL WHERE 0", script);
        script = script.Replace("SELECT NULL WHERE 0", ids);
        Execute(connection, script);
        Assert.Equal(1L, Count(connection, "Status = 'success' AND Id = 'verified' AND ErrorMessage = '' AND ErrorCategory IS NULL AND IsStreamInterrupted = 0"));
        Assert.Equal(8L, Count(connection, "Id <> 'verified' AND ErrorMessage = 'aborted' AND ErrorCategory = 'timeout' AND IsStreamInterrupted = 1"));
        Execute(connection, script);
        Assert.Equal(1L, Count(connection, "Status = 'success'"));
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Count(SqliteConnection connection, string condition)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM ProxyUsageLogs WHERE {condition}";
        return (long)command.ExecuteScalar()!;
    }
}

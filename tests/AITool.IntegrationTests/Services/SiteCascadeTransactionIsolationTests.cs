using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using SqlSugar;

namespace AITool.IntegrationTests.Services;

public sealed class SiteCascadeTransactionIsolationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Cascade_completion_does_not_complete_parallel_writer_transaction(bool cascadeFails, bool writerCommits)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cascade-isolation-{Guid.NewGuid():N}.db");
        using var client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={path}", DbType = DbType.Sqlite, IsAutoCloseConnection = true
        });
        try
        {
            SqlSugarSetup.InitializeDatabase(client);
            await client.Ado.ExecuteCommandAsync("PRAGMA journal_mode=WAL;");
            var db = new AppDbContext(client, new SemaphoreSlim(1, 1));
            var deleter = new SiteCascadeDeleter(db);
            var siteId = Guid.NewGuid();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var cascade = deleter.ExecuteGraphMutationAsync(async transactionDb =>
            {
                transactionDb.Client.Should().NotBeSameAs(client);
                entered.SetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (cascadeFails) throw new InvalidOperationException("cascade failure");
                return true;
            }, default);

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writer = Task.Run(async () =>
            {
                writerStarted.SetResult();
                await client.Ado.BeginTranAsync();
                await client.Insertable(new Site { Id = siteId, Name = "parallel", BaseUrl = "https://example.invalid", ApiKey = "key" }).ExecuteCommandAsync();
            });
            await writerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            release.TrySetResult();

            if (cascadeFails)
                await FluentActions.Awaiting(async () => await cascade).Should().ThrowAsync<InvalidOperationException>().WithMessage("cascade failure");
            else
                (await cascade).Should().BeTrue();

            await writer.WaitAsync(TimeSpan.FromSeconds(15));
            client.Ado.Transaction.Should().NotBeNull("cascade completion must not commit or roll back the writer");
            if (writerCommits) await client.Ado.CommitTranAsync();
            else await client.Ado.RollbackTranAsync();
            (await db.Sites.AnyAsync(s => s.Id == siteId)).Should().Be(writerCommits);
        }
        finally
        {
            if (client.Ado.Transaction != null) await client.Ado.RollbackTranAsync();
            client.Dispose();
            try { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); } catch { }
        }
    }

    [Fact]
    public async Task Cascade_failure_rolls_back_mutation_on_transaction_context()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cascade-rollback-{Guid.NewGuid():N}.db");
        using var client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={path}", DbType = DbType.Sqlite, IsAutoCloseConnection = true
        });
        try
        {
            SqlSugarSetup.InitializeDatabase(client);
            var db = new AppDbContext(client, new SemaphoreSlim(1, 1));
            var deleter = new SiteCascadeDeleter(db);
            var siteId = Guid.NewGuid();
            await FluentActions.Awaiting(() => deleter.ExecuteGraphMutationAsync<bool>(async transactionDb =>
            {
                await transactionDb.Client.Insertable(new Site { Id = siteId, Name = "rollback", BaseUrl = "https://example.invalid", ApiKey = "key" }).ExecuteCommandAsync();
                throw new InvalidOperationException("cascade failure");
            }, default)).Should().ThrowAsync<InvalidOperationException>().WithMessage("cascade failure");
            (await db.Sites.AnyAsync(s => s.Id == siteId)).Should().BeFalse();
        }
        finally
        {
            client.Dispose();
            try { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); } catch { }
        }
    }
}

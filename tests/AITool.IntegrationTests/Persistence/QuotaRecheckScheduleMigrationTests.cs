using AITool.Domain.Codex;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using Xunit;

namespace AITool.IntegrationTests.Persistence;

public sealed class QuotaRecheckScheduleMigrationTests
{
    [Fact]
    public async Task Existing_database_gains_schedule_table_without_changing_account_or_success_cache()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aitool-recheck-migration-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddSqlSugar($"Data Source={path};Pooling=False");
        using var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<ISqlSugarClient>();
        try
        {
            // Pre-feature database has real account rows but no schedule table.
            db.CodeFirst.InitTables<CodexAccount>();
            var account = new CodexAccount
            {
                DisplayName = "legacy disabled account",
                IsEnabled = false,
                AccessToken = "isolated-test-access",
                RefreshToken = "isolated-test-refresh",
                LastQuotaRawJson = "{\"remaining\":0}",
                LastQuotaCheckedAt = new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.Zero),
                AutoDisableThreshold = 10,
                LinkedSiteId = Guid.NewGuid()
            };
            await db.Insertable(account).ExecuteCommandAsync();
            var before = db.Ado.GetString("SELECT json_object('id', Id, 'enabled', IsEnabled, 'access', AccessToken, 'refresh', RefreshToken, 'raw', LastQuotaRawJson, 'checked', LastQuotaCheckedAt, 'created', CreatedAt) FROM CodexAccounts");

            SqlSugarSetup.InitializeDatabase(db);
            using var scope = provider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await context.QuotaRecheckSchedules.CountAsync()).Should().Be(0);
            var store = new QuotaRecheckScheduleStore(provider.GetRequiredService<IServiceScopeFactory>(),
                TimeProvider.System, new QuotaRecheckIntervalSource());
            await store.EnsureScheduledAsync("codex", account.Id, null, default);
            await store.EnsureScheduledAsync("google", account.Id, null, default);
            (await context.QuotaRecheckSchedules.CountAsync()).Should().Be(2, "provider and account form the composite key");
            var persisted = await context.QuotaRecheckSchedules.ToListAsync();
            persisted.Should().OnlyContain(s => s.NextCheckAtUtcTicks > 0 && s.CreatedAtUtcTicks > 0);

            // Startup migration is repeatable and cannot reset persisted due dates.
            SqlSugarSetup.InitializeDatabase(db);
            var after = await context.QuotaRecheckSchedules.ToListAsync();
            after.Should().BeEquivalentTo(persisted);
            db.Ado.GetString("SELECT json_object('id', Id, 'enabled', IsEnabled, 'access', AccessToken, 'refresh', RefreshToken, 'raw', LastQuotaRawJson, 'checked', LastQuotaCheckedAt, 'created', CreatedAt) FROM CodexAccounts")
                .Should().Be(before);
            var indexes = db.Ado.SqlQuery<IndexName>("SELECT name AS Name FROM sqlite_master WHERE type='index' AND tbl_name='QuotaRecheckSchedules'");
            indexes.Should().Contain(i => i.Name == "IX_QuotaRecheckSchedules_NextCheckAtUtcTicks");
            db.Ado.GetString("SELECT typeof(NextCheckAtUtcTicks) FROM QuotaRecheckSchedules LIMIT 1").Should().Be("integer");
        }
        finally
        {
            // AddSqlSugar registers a pre-created instance; the fixture owns its disposal.
            // Pooling is disabled so cleanup never closes another test's memory database.
            db.Dispose();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(path + suffix); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Existing_schedule_table_gains_generation_without_resetting_due_or_lease()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aitool-recheck-generation-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddSqlSugar($"Data Source={path};Pooling=False");
        using var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<ISqlSugarClient>();
        var id = Guid.NewGuid();
        var due = DateTimeOffset.UtcNow.AddMinutes(10).UtcTicks;
        var lease = Guid.NewGuid().ToString("D");
        try
        {
            db.Ado.ExecuteCommand("""
                CREATE TABLE QuotaRecheckSchedules (
                    ProviderKey TEXT NOT NULL,
                    AccountId TEXT NOT NULL,
                    NextCheckAtUtcTicks BIGINT NOT NULL,
                    CreatedAtUtcTicks BIGINT NOT NULL,
                    UpdatedAtUtcTicks BIGINT NOT NULL,
                    LeaseToken TEXT NULL,
                    LeaseExpiresAtUtcTicks BIGINT NULL,
                    PRIMARY KEY (ProviderKey, AccountId)
                );
                """);
            db.Ado.ExecuteCommand("""
                INSERT INTO QuotaRecheckSchedules
                    (ProviderKey, AccountId, NextCheckAtUtcTicks, CreatedAtUtcTicks, UpdatedAtUtcTicks, LeaseToken, LeaseExpiresAtUtcTicks)
                VALUES ('p', @id, @due, @due, @due, @lease, @due);
                """, new { id, due, lease });
            SqlSugarSetup.InitializeDatabase(db);
            using var scope = provider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = (await context.QuotaRecheckSchedules.ToListAsync()).Single();
            row.Generation.Should().BeEmpty("existing rows receive a safe migration default");
            row.NextCheckAtUtcTicks.Should().Be(due);
            row.LeaseToken.Should().Be(lease);
            row.LeaseExpiresAtUtcTicks.Should().Be(due);
            var store = new QuotaRecheckScheduleStore(provider.GetRequiredService<IServiceScopeFactory>(),
                TimeProvider.System, new QuotaRecheckIntervalSource());
            var observation = await store.BeginReconciliationAsync("p", default);
            await store.ReconcileAsync("p", [], observation, default);
            (await context.QuotaRecheckSchedules.CountAsync()).Should().Be(1, "migration-default generation never overrides active lease protection");
            await store.EnsureScheduledAsync("p", id, null, default);
            row = (await context.QuotaRecheckSchedules.ToListAsync()).Single();
            row.Generation.Should().NotBeEmpty();
            row.NextCheckAtUtcTicks.Should().Be(due);
            row.LeaseToken.Should().Be(lease);
        }
        finally
        {
            // AddSqlSugar registers a pre-created instance; the fixture owns its disposal.
            // Pooling is disabled so cleanup never closes another test's memory database.
            db.Dispose();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(path + suffix); } catch (IOException) { }
        }
    }

    public sealed class IndexName
    {
        public string Name { get; set; } = string.Empty;
    }
}

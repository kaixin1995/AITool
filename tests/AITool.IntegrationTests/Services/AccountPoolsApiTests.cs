using System.Text.Json;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Web.Controllers.Admin;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AITool.IntegrationTests.Services;

public sealed class AccountPoolsApiTests
{
    [Fact]
    public async Task GetPools_counts_only_entries_owned_by_each_pool_in_one_grouped_query()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aitool-pool-api-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddSqlSugar($"Data Source={path}");
            using var provider = services.BuildServiceProvider();
            var client = provider.GetRequiredService<SqlSugar.ISqlSugarClient>();
            SqlSugarSetup.InitializeDatabase(client);
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var a = new AccountPool { Name = "a", VendorKey = "codex" };
            var b = new AccountPool { Name = "b", VendorKey = "codex" };
            var empty = new AccountPool { Name = "empty", VendorKey = "codex" };
            await db.Client.Insertable(new[] { a, b, empty }).ExecuteCommandAsync();
            await db.Client.Insertable(new[]
            {
                new ProxyRouteEntry { EntryName = "a1", ManagedByPoolId = a.Id },
                new ProxyRouteEntry { EntryName = "a2", ManagedByPoolId = a.Id },
                new ProxyRouteEntry { EntryName = "b1", ManagedByPoolId = b.Id },
                new ProxyRouteEntry { EntryName = "manual" },
                new ProxyRouteEntry { EntryName = "orphan", ManagedByPoolId = Guid.NewGuid() },
            }).ExecuteCommandAsync();
            var sql = new List<string>();
            db.Client.Aop.OnLogExecuting = (query, _) => sql.Add(query);
            var controller = new AccountPoolsApiController(db, null!, new DeviceOAuthVendorRegistry([]));
            var result = (OkObjectResult)await controller.GetPools(default);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            var counts = json.RootElement.GetProperty("Data").EnumerateArray()
                .ToDictionary(x => x.GetProperty("id").GetGuid(), x => x.GetProperty("managedEntryCount").GetInt32());
            counts.Should().BeEquivalentTo(new Dictionary<Guid, int> { [a.Id] = 2, [b.Id] = 1, [empty.Id] = 0 });
            var entryQueries = sql.Where(s => s.Contains("ProxyRouteEntries", StringComparison.OrdinalIgnoreCase)).ToList();
            entryQueries.Should().ContainSingle();
            entryQueries[0].Should().ContainEquivalentOf("GROUP BY").And.ContainEquivalentOf("COUNT(").And.ContainEquivalentOf("WHERE");
        }
        finally
        {
            try { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); } catch { }
        }
    }
}

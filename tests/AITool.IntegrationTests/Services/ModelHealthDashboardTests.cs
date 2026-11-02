using System.Text.Json;
using AITool.Domain.Models;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Controllers.Admin;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using SqlSugar;

namespace AITool.IntegrationTests.Services;

public sealed class ModelHealthDashboardTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public async Task Dashboard_retains_history_for_every_enabled_state_combination(
        bool siteEnabled, bool mappingEnabled, bool ruleEnabled)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aitool-model-health-{Guid.NewGuid():N}.db");
        using var client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={path};Pooling=False", DbType = DbType.Sqlite, IsAutoCloseConnection = true
        });
        try
        {
            SqlSugarSetup.InitializeDatabase(client);
            using var db = new AppDbContext(client, new SemaphoreSlim(1, 1));
            var modelId = Guid.NewGuid();
            var siteId = Guid.NewGuid();
            db.ModelLibraryItems.Add(new ModelLibraryItem { Id = modelId, ModelName = "health-model" });
            db.ModelHealthMonitors.Add(new ModelHealthMonitor { ModelLibraryItemId = modelId });
            db.Sites.Add(new Site { Id = siteId, Name = "Health site", BaseUrl = "https://example.invalid", ApiKey = "test", IsEnabled = siteEnabled });
            db.SiteModelMappings.Add(new SiteModelMapping
            {
                SiteId = siteId, ModelLibraryItemId = modelId, RemoteModelName = "remote-model",
                IsEnabled = mappingEnabled, LastStatus = "fail"
            });
            db.ProxyRouteRules.Add(new ProxyRouteRule
            {
                ExternalModelName = "health-model", UpstreamModelName = "upstream-alias",
                SiteId = siteId, SiteModelName = "remote-model", IsEnabled = ruleEnabled
            });
            var now = DateTimeOffset.UtcNow;
            db.ProxyUsageLogs.AddRange(
                Log("other-request", "upstream-alias", siteId, "success", now.AddHours(-1), 120),
                Log("other-request", "remote-model", Guid.Empty, "fail", now.AddHours(-2), 200),
                Log("health-model", "remote-model", siteId, "fail", now.AddDays(-3), 300),
                Log("other-request", "unrelated", siteId, "fail", now.AddHours(-1), 400));

            var result = await new ModelHealthApiController(db).GetDashboard("1d", CancellationToken.None);
            var ok = result.Should().BeOfType<OkObjectResult>().Subject;
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var data = json.RootElement.GetProperty("data");
            var model = data.GetProperty("monitoredModels")[0];
            var sites = data.GetProperty("healthData").GetProperty(modelId.ToString());
            sites.GetArrayLength().Should().Be(1);
            var site = sites[0];
            site.GetProperty("siteName").GetString().Should().Be("Health site");
            site.GetProperty("remoteModelName").GetString().Should().Be("remote-model");
            site.GetProperty("lastStatus").GetString().Should().Be("success");
            site.GetProperty("lastDurationMs").GetInt32().Should().Be(120);
            site.GetProperty("successCount").GetInt32().Should().Be(1);
            site.GetProperty("failureCount").GetInt32().Should().Be(1);
            site.GetProperty("successRate").GetDouble().Should().Be(0.5);
            site.GetProperty("timelineSegments").EnumerateArray().Sum(x => x.GetProperty("count").GetInt32()).Should().Be(2);
            model.GetProperty("siteCount").GetInt32().Should().Be(1);
            model.GetProperty("totalRequestCount").GetInt32().Should().Be(2);
            model.GetProperty("timelineSegments").EnumerateArray().Sum(x => x.GetProperty("count").GetInt32()).Should().Be(2);
        }
        finally
        {
            client.Dispose();
            DeleteDatabaseFiles(path);
        }
    }

    [Fact]
    public async Task Dashboard_does_not_count_one_site_call_for_each_mapping()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aitool-model-health-{Guid.NewGuid():N}.db");
        using var client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={path};Pooling=False", DbType = DbType.Sqlite, IsAutoCloseConnection = true
        });
        try
        {
            SqlSugarSetup.InitializeDatabase(client);
            using var db = new AppDbContext(client, new SemaphoreSlim(1, 1));
            var model = new ModelLibraryItem { ModelName = "shared" };
            var site = new Site { Name = "site", BaseUrl = "https://example.invalid", ApiKey = "test", IsEnabled = false };
            db.ModelLibraryItems.Add(model);
            db.ModelHealthMonitors.Add(new ModelHealthMonitor { ModelLibraryItemId = model.Id });
            db.Sites.Add(site);
            db.SiteModelMappings.AddRange(
                new SiteModelMapping { SiteId = site.Id, ModelLibraryItemId = model.Id, RemoteModelName = "alpha" },
                new SiteModelMapping { SiteId = site.Id, ModelLibraryItemId = model.Id, RemoteModelName = "beta" });
            db.ProxyRouteRules.AddRange(
                new ProxyRouteRule { ExternalModelName = "shared", UpstreamModelName = "alias", SiteId = site.Id, SiteModelName = "alpha" },
                new ProxyRouteRule { ExternalModelName = "shared", UpstreamModelName = "alias", SiteId = site.Id, SiteModelName = "beta" });
            db.ProxyUsageLogs.AddRange(
                Log("shared", "alpha", site.Id, "success", DateTimeOffset.UtcNow.AddMinutes(-3), 100),
                Log("shared", "beta", site.Id, "fail", DateTimeOffset.UtcNow.AddMinutes(-2), 200),
                Log("shared", "alias", site.Id, "success", DateTimeOffset.UtcNow.AddMinutes(-1), 150),
                Log("shared", "removed-alias", site.Id, "fail", DateTimeOffset.UtcNow, 250));

            var result = await new ModelHealthApiController(db).GetDashboard("1d", CancellationToken.None);
            var ok = result.Should().BeOfType<OkObjectResult>().Subject;
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var data = json.RootElement.GetProperty("data");
            var sites = data.GetProperty("healthData").GetProperty(model.Id.ToString()).EnumerateArray().ToArray();
            sites.Should().HaveCount(2);
            var alpha = sites.Single(x => x.GetProperty("remoteModelName").GetString() == "alpha");
            var beta = sites.Single(x => x.GetProperty("remoteModelName").GetString() == "beta");
            alpha.GetProperty("successCount").GetInt32().Should().Be(2);
            alpha.GetProperty("failureCount").GetInt32().Should().Be(1);
            beta.GetProperty("successCount").GetInt32().Should().Be(0);
            beta.GetProperty("failureCount").GetInt32().Should().Be(1);
            data.GetProperty("monitoredModels")[0].GetProperty("totalRequestCount").GetInt32().Should().Be(4);
        }
        finally
        {
            client.Dispose();
            DeleteDatabaseFiles(path);
        }
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            for (var attempt = 0; ; attempt++)
            {
                try { File.Delete(path + suffix); break; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(50); }
                catch (UnauthorizedAccessException) when (attempt < 20) { Thread.Sleep(50); }
            }
        }
    }

    private static ProxyUsageLog Log(string request, string attempted, Guid siteId, string status, DateTimeOffset time, int duration) => new()
    {
        RequestId = Guid.NewGuid(), AccessKeyId = Guid.NewGuid(), ProtocolType = "OpenAI",
        RequestModel = request, AttemptedModel = attempted, TargetSiteId = siteId,
        Status = status, RequestedAt = time, TotalDurationMs = duration
    };
}

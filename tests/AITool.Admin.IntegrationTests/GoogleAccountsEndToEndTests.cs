using Xunit;
using System.Net;
using System.Text;
using AITool.Domain.Models;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net.Http.Headers;

namespace AITool.Admin.IntegrationTests.GoogleAccounts;

/// <summary>
/// Google（Antigravity）账号管理端到端（额度刷新窗口 + 模型导入联动）。
/// split 双宿主：google-accounts 控制器在 Admin 宿主，从 master 的 Gemini 端到端文件拆出。
/// </summary>
public sealed class GoogleAccountsEndToEndTests
{
    private const string GeminiUpstreamJson =
        "{\"response\":{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"你好，来自 Gemini\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":50,\"cachedContentTokenCount\":10,\"candidatesTokenCount\":6,\"thoughtsTokenCount\":2}}}";
    private static readonly Guid SiteId = Guid.Parse("21212121-2121-2121-2121-212121212121");
    private static readonly Guid GoogleAccountId = Guid.Parse("23232323-2323-2323-2323-232323232323");

        [Fact]
        public async Task Refresh_quota_for_antigravity_account_returns_model_windows()
    {
        // Antigravity 额度链路：refresh-quota → fetchAvailableModels → 每模型剩余比例窗口持久化并在账号列表回显。
        const string quotaJson =
            "{\"models\":{\"gemini-3-pro-preview\":{\"quotaInfo\":{\"remainingFraction\":0.85,\"resetTime\":\"2026-08-20T02:30:00Z\"}},\"claude-sonnet-4-6\":{\"quotaInfo\":{\"remainingFraction\":0.05,\"resetTime\":\"2026-08-19T10:00:00Z\"}}}}";
        await using var factory = new GeminiProxyWebApplicationFactory(
            request => request.RequestUri!.AbsolutePath.Contains("fetchAvailableModels")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(quotaJson, Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(GeminiUpstreamJson, Encoding.UTF8, "application/json")
                },
            accountKind: "Antigravity",
            baseUrl: "https://daily-cloudcode-pa.googleapis.com");
        using var client = factory.CreateClient();

        using var refreshResponse = await client.PostAsync(
            $"/api/admin/google-accounts/accounts/{GoogleAccountId}/refresh-quota",
            content: null);
        var refreshBody = await refreshResponse.Content.ReadAsStringAsync();
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK, refreshBody);
        refreshBody.Should().Contain("gemini-3-pro-preview", "额度响应应包含模型窗口");

        using var listResponse = await client.GetAsync("/api/admin/google-accounts/accounts");
        var listBody = await listResponse.Content.ReadAsStringAsync();
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK, listBody);
        using var listDoc = System.Text.Json.JsonDocument.Parse(listBody);
        var account = listDoc.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == GoogleAccountId.ToString());
        var windows = account.GetProperty("windows");
        windows.GetArrayLength().Should().Be(2, "额度结果应持久化并在账号列表解析为窗口");
        var geminiWindow = windows.EnumerateArray().Single(w => w.GetProperty("id").GetString() == "gemini-3-pro-preview");
        geminiWindow.GetProperty("usedPercent").GetDouble().Should().BeApproximately(15d, 0.01);
        account.GetProperty("lastQuotaCheckedAt").GetString().Should().NotBeNullOrEmpty();
    }


        [Fact]
        public async Task Import_selected_antigravity_models_disables_unselected_and_stale_mappings()
    {
        await using var factory = new GeminiProxyWebApplicationFactory(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(GeminiUpstreamJson, Encoding.UTF8, "application/json")
            },
            accountKind: "Antigravity",
            baseUrl: "https://daily-cloudcode-pa.googleapis.com");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var staleModel = new ModelLibraryItem
            {
                ModelName = "stale-antigravity-model",
                DisplayName = "Stale Antigravity Model"
            };
            db.ModelLibraryItems.Add(staleModel);
            db.SiteModelMappings.Add(new SiteModelMapping
            {
                SiteId = SiteId,
                ModelLibraryItemId = staleModel.Id,
                RemoteModelName = staleModel.ModelName,
                IsEnabled = true
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        using var response = await client.PostAsync(
            $"/api/admin/google-accounts/accounts/{GoogleAccountId}/import-selected-models",
            new StringContent(
                """
                {"models":[
                  {"remoteModelName":"gemini-2.5-pro","displayName":"Gemini 2.5 Pro","selected":false},
                  {"remoteModelName":"new-antigravity-model","displayName":"New Antigravity Model","selected":true}
                ]}
                """,
                Encoding.UTF8,
                "application/json"));
        var responseBody = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, responseBody);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var siteId = SiteId;
        var mappings = await verifyDb.SiteModelMappings
            .Where(mapping => mapping.SiteId == siteId)
            .ToListAsync();

        mappings.Single(mapping => mapping.RemoteModelName == "gemini-2.5-pro").IsEnabled.Should().BeFalse();
        mappings.Single(mapping => mapping.RemoteModelName == "stale-antigravity-model").IsEnabled.Should().BeFalse();
        mappings.Single(mapping => mapping.RemoteModelName == "new-antigravity-model").IsEnabled.Should().BeTrue();
    }


    private sealed class GeminiProxyWebApplicationFactory : WebApplicationFactory<AITool.Admin.AdminProgramMarker>
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        private readonly string _accountKind;
        private readonly string _baseUrl;
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-google-e2e-{Guid.NewGuid():N}.db");

        public GeminiProxyWebApplicationFactory(
            Func<HttpRequestMessage, HttpResponseMessage> responder,
            string accountKind = "Antigravity",
            string baseUrl = "https://daily-cloudcode-pa.googleapis.com")
        {
            _responder = responder;
            _accountKind = accountKind;
            _baseUrl = baseUrl;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);
                var handler = new StubHandler(_responder);
                var httpClient = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
                services.RemoveAll<System.Net.Http.IHttpClientFactory>();
                services.AddSingleton<System.Net.Http.IHttpClientFactory>(new SingletonHttpClientFactory(httpClient));
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            SeedAsync().GetAwaiter().GetResult();
        }

        private async Task SeedAsync()
        {
            await IntegrationTestDbHelper.InitializeDatabaseAsync(Services);
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Sites.Add(new Site
            {
                Id = SiteId,
                Name = "Gemini Site",
                BaseUrl = _baseUrl,
                ApiKey = "ya29-upstream-token",
                ProtocolType = "Gemini",
                SupportsOpenAi = false,
                SupportsAnthropic = false,
                SupportsResponses = false,
                ManagedSource = "Google",
                IsEnabled = true
            });
            db.GoogleAccounts.Add(new Domain.Google.GoogleAccount
            {
                Id = GoogleAccountId,
                DisplayName = "E2E Antigravity",
                AccountKind = "Antigravity",
                ProjectId = "e2e-project-123",
                AccessToken = "ya29-upstream-token",
                RefreshToken = "ref-token",
                TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                LinkedSiteId = SiteId,
                IsEnabled = true
            });
            // master 原版种子：额度窗口按「已勾选模型」去重展示（0ef754b），
            // 必须为额度 JSON 中的模型建映射，否则列表 windows 恒为空。
            var baseModelId = Guid.Parse("a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1");
            db.ModelLibraryItems.Add(new ModelLibraryItem
            {
                Id = baseModelId,
                ModelName = "gemini-2.5-pro",
                DisplayName = "Gemini 2.5 Pro",
                IsEnabled = true
            });
            db.SiteModelMappings.Add(new SiteModelMapping
            {
                Id = Guid.Parse("b2b2b2b2-b2b2-b2b2-b2b2-b2b2b2b2b2b2"),
                SiteId = SiteId,
                ModelLibraryItemId = baseModelId,
                RemoteModelName = "gemini-2.5-pro",
                IsEnabled = true,
                MaxConcurrency = 0
            });
            foreach (var (mid, mname) in new[]
            {
                (Guid.Parse("c3c3c3c3-c3c3-c3c3-c3c3-c3c3c3c3c3c3"), "gemini-3-pro-preview"),
                (Guid.Parse("d4d4d4d4-d4d4-d4d4-d4d4-d4d4d4d4d4d4"), "claude-sonnet-4-6")
            })
            {
                db.ModelLibraryItems.Add(new ModelLibraryItem { Id = mid, ModelName = mname, DisplayName = mname, IsEnabled = true });
                db.SiteModelMappings.Add(new SiteModelMapping
                {
                    Id = Guid.NewGuid(),
                    SiteId = SiteId,
                    ModelLibraryItemId = mid,
                    RemoteModelName = mname,
                    IsEnabled = true,
                    MaxConcurrency = 0
                });
            }
            // OAuth 总开关默认关闭（OAuthFeatureToggle 会 404），测试需显式开启。
            var settings = await db.SystemRuntimeSettings.FirstAsync(x => x.Id == 1);
            if (settings is null)
            {
                settings = new AITool.Domain.Operations.SystemRuntimeSettings { Id = 1, OAuthFeaturesEnabled = true };
                db.SystemRuntimeSettings.Add(settings);
            }
            else
            {
                settings.OAuthFeaturesEnabled = true;
                await db.UpdateAsync(settings);
            }
            await db.SaveChangesAsync();
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_responder(request));
    }

    private sealed class SingletonHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingletonHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }
}

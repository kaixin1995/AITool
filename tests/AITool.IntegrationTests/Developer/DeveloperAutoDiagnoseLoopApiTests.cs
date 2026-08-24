using System.Net;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Domain.Models;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Controllers.Admin;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.Developer;

public sealed class DeveloperAutoDiagnoseLoopApiTests
{
    private const string Endpoint = "/api/admin/developer/invocations/auto-diagnose-loop";

    [Fact]
    public async Task Auto_diagnose_loop_fails_when_developer_features_disabled()
    {
        await using var factory = new AutoDiagnoseLoopTestWebApplicationFactory(developerFeaturesEnabled: false);
        using var client = factory.CreateClient();

        using var content = JsonContent(new DeveloperAutoDiagnoseLoopRequest
        {
            DiagnosticModelId = Guid.NewGuid(),
            TargetModelName = "gemini-3.7-flash",
            InitialErrorResponse = "test error"
        });

        var response = await client.PostAsync(Endpoint, content);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Auto_diagnose_loop_validates_empty_diagnostic_model()
    {
        await using var factory = new AutoDiagnoseLoopTestWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();

        using var content = JsonContent(new DeveloperAutoDiagnoseLoopRequest
        {
            DiagnosticModelId = Guid.Empty,
            TargetModelName = "gemini-3.7-flash",
            InitialErrorResponse = "test error"
        });

        var response = await client.PostAsync(Endpoint, content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Auto_diagnose_loop_executes_multiround_trial_and_converges_to_success()
    {
        var fakeForwardService = new FakeMultiRoundForwardService();
        await using var factory = new AutoDiagnoseLoopTestWebApplicationFactory(developerFeaturesEnabled: true, fakeForwardService);
        using var client = factory.CreateClient();

        var modelId = factory.SeedModelId;
        var mappingId = factory.SeedMappingId;
        var siteId = factory.SeedSiteId;

        using var content = JsonContent(new DeveloperAutoDiagnoseLoopRequest
        {
            DiagnosticModelId = modelId,
            DiagnosticMappingId = mappingId,
            TargetSiteId = siteId,
            TargetModelName = "gemini-3.7-flash",
            SourceProtocol = "OpenAI",
            TargetProtocol = "Gemini",
            OriginalRequestBody = "{\"model\":\"gemini-3.7\",\"messages\":[]}",
            InitialPreparedRequestBody = "{\"contents\":[],\"invalid_arg\":true}",
            InitialErrorResponse = "HTTP 400: Invalid argument invalid_arg",
            InitialStatusCode = 400,
            MaxRounds = 3
        });

        var response = await client.PostAsync(Endpoint, content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");

        data.GetProperty("success").GetBoolean().Should().BeTrue();
        data.GetProperty("totalRounds").GetInt32().Should().BeGreaterThan(0);
        data.GetProperty("rounds").GetArrayLength().Should().BeGreaterThan(0);
        data.GetProperty("workingPayload").GetString().Should().NotBeNullOrWhiteSpace();
    }

    private static StringContent JsonContent(object obj)
    {
        return new StringContent(
            JsonSerializer.Serialize(obj),
            global::System.Text.Encoding.UTF8,
            "application/json");
    }

    private sealed class FakeMultiRoundForwardService : IProxyForwardService
    {
        private int _upstreamCallCount = 0;

        public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
        {
            // If request is from diagnostic LLM (asking for analysis / summary)
            if (request.TargetBaseUrl.Contains("diagnostic-llm.com"))
            {
                // If it's a round prompt (asking for hypothesis & adjustedPayload)
                if (request.RequestBody.Contains("第 1 轮自动试错") || request.RequestBody.Contains("核心假设"))
                {
                    var roundJson = "```json\n{\n  \"hypothesis\": \"Gemini 不支持 invalid_arg 参数\",\n  \"explanation\": \"移除了 invalid_arg 字段\",\n  \"adjustedPayload\": {\"contents\": [{\"parts\": [{\"text\": \"hello\"}]}]}\n}\n```";
                    var aiRoundOutput = JsonSerializer.Serialize(new
                    {
                        choices = new[]
                        {
                            new
                            {
                                message = new
                                {
                                    content = roundJson
                                }
                            }
                        }
                    });

                    return Task.FromResult(new ProxyForwardResult
                    {
                        Success = true,
                        StatusCode = 200,
                        ResponseBody = aiRoundOutput,
                        TotalDurationMs = 150
                    });
                }

                // If it's final summary prompt
                var summaryJson = "```json\n{\n  \"summary\": \"自愈成功：已剔除非法参数 invalid_arg\",\n  \"rootCause\": \"上游 Gemini 协议严格校验根层字段\",\n  \"suggestedAction\": \"配置剔除规则或使用最新网关桥接\",\n  \"rules\": [\n    {\n      \"op\": \"strip\",\n      \"key\": \"invalid_arg\",\n      \"scope\": \"bridge\"\n    }\n  ]\n}\n```";
                var aiSummaryOutput = JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new
                        {
                            message = new
                            {
                                content = summaryJson
                            }
                        }
                    }
                });

                return Task.FromResult(new ProxyForwardResult
                {
                    Success = true,
                    StatusCode = 200,
                    ResponseBody = aiSummaryOutput,
                    TotalDurationMs = 150
                });
            }

            // Real upstream trial request simulation:
            _upstreamCallCount++;
            if (_upstreamCallCount == 1)
            {
                // Round 1 trial succeeds with 200
                return Task.FromResult(new ProxyForwardResult
                {
                    Success = true,
                    StatusCode = 200,
                    ResponseBody = "{\"candidates\": [{\"content\": {\"parts\": [{\"text\": \"Hi there!\"}]}}]}",
                    TotalDurationMs = 200
                });
            }

            return Task.FromResult(new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = "{\"candidates\": []}",
                TotalDurationMs = 100
            });
        }

        public Task<ProxyForwardResult> ForwardStreamingAsync(ProxyForwardRequest request, Func<string, CancellationToken, Task> onChunkReceived, CancellationToken cancellationToken = default)
        {
            return ForwardAsync(request, cancellationToken);
        }
    }

    private sealed class AutoDiagnoseLoopTestWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-auto-diagnose-{Guid.NewGuid():N}.db");
        private readonly bool _developerFeaturesEnabled;
        private readonly IProxyForwardService? _customForwardService;

        public Guid SeedModelId { get; private set; }
        public Guid SeedMappingId { get; private set; }
        public Guid SeedSiteId { get; private set; }

        public AutoDiagnoseLoopTestWebApplicationFactory(bool developerFeaturesEnabled, IProxyForwardService? customForwardService = null)
        {
            _developerFeaturesEnabled = developerFeaturesEnabled;
            _customForwardService = customForwardService;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Server:Port"] = "0"
                });
            });
            builder.ConfigureServices(services =>
            {
                IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);
                if (_customForwardService != null)
                {
                    services.RemoveAll<IProxyForwardService>();
                    services.AddSingleton(_customForwardService);
                }
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            Seed();
        }

        private void Seed()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            SqlSugarSetup.InitializeDatabase(db.Client);
            db.Client.Deleteable<SystemRuntimeSettings>().Where(x => x.Id == 1).ExecuteCommand();
            db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
            {
                Id = 1,
                DeveloperFeaturesEnabled = _developerFeaturesEnabled,
                ProxyRequestTimeoutSeconds = 30
            });

            var site = new Site
            {
                Id = Guid.NewGuid(),
                Name = "Diagnostic LLM Site",
                BaseUrl = "https://diagnostic-llm.com",
                ApiKey = "diag-key",
                ProtocolType = "OpenAI",
                IsEnabled = true
            };
            db.Sites.Add(site);

            var model = new ModelLibraryItem
            {
                Id = Guid.NewGuid(),
                ModelName = "gpt-4o-diag",
                DisplayName = "GPT-4o Diagnostic",
                IsEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.ModelLibraryItems.Add(model);

            var mapping = new SiteModelMapping
            {
                Id = Guid.NewGuid(),
                SiteId = site.Id,
                ModelLibraryItemId = model.Id,
                RemoteModelName = "gpt-4o-diag",
                IsEnabled = true
            };
            db.SiteModelMappings.Add(mapping);

            db.ProxyRouteEntries.Add(new ProxyRouteEntry { EntryName = "gpt-4o-diag" });
            db.ProxyRouteRules.Add(new ProxyRouteRule
            {
                Id = Guid.NewGuid(),
                ExternalModelName = "gpt-4o-diag",
                UpstreamModelName = "gpt-4o-diag",
                SiteId = site.Id,
                SiteModelName = "gpt-4o-diag",
                Priority = 0,
                ModelPriority = 0,
                InstancePriority = 0,
                IsEnabled = true
            });

            SeedModelId = model.Id;
            SeedMappingId = mapping.Id;
            SeedSiteId = site.Id;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (File.Exists(_databasePath))
            {
                try { File.Delete(_databasePath); } catch { }
            }
        }
    }
}

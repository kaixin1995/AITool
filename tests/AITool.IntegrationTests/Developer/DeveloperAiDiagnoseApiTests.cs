using System.Net;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Domain.Models;
using AITool.Domain.Operations;
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

public sealed class DeveloperAiDiagnoseApiTests
{
    private const string Endpoint = "/api/admin/developer/invocations/ai-diagnose";

    [Fact]
    public async Task Ai_diagnose_fails_when_developer_features_disabled()
    {
        await using var factory = new AiDiagnoseTestWebApplicationFactory(developerFeaturesEnabled: false);
        using var client = factory.CreateClient();

        using var content = JsonContent(new DeveloperAiDiagnoseRequest
        {
            ModelId = Guid.NewGuid(),
            ErrorMessage = "test error"
        });

        var response = await client.PostAsync(Endpoint, content);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Ai_diagnose_validates_empty_model()
    {
        await using var factory = new AiDiagnoseTestWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();

        using var content = JsonContent(new DeveloperAiDiagnoseRequest
        {
            ModelId = Guid.Empty,
            ErrorMessage = "test error"
        });

        var response = await client.PostAsync(Endpoint, content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static StringContent JsonContent(object obj)
    {
        return new StringContent(
            JsonSerializer.Serialize(obj),
            global::System.Text.Encoding.UTF8,
            "application/json");
    }

    private sealed class AiDiagnoseTestWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-ai-diagnose-{Guid.NewGuid():N}.db");
        private readonly bool _developerFeaturesEnabled;

        public AiDiagnoseTestWebApplicationFactory(bool developerFeaturesEnabled)
        {
            _developerFeaturesEnabled = developerFeaturesEnabled;
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
                DeveloperFeaturesEnabled = _developerFeaturesEnabled
            });
        }
    }
}

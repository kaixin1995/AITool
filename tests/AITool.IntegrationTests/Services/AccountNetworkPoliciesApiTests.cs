using System.Text.Json;
using AITool.Application.Networking;
using AITool.Domain.Codex;
using AITool.Domain.Google;
using AITool.Domain.Kimi;
using AITool.Domain.OAuth;
using AITool.Domain.Operations;
using AITool.Domain.Sites;
using AITool.Domain.Xai;
using AITool.Infrastructure.Persistence;
using AITool.Web.Controllers.Admin;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using Xunit;

namespace AITool.IntegrationTests.Services;

/// <summary>Isolated SQLite/controller integration; no application startup or external network.</summary>
public sealed class AccountNetworkPoliciesApiTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aitool-network-api-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;
    private readonly ISqlSugarClient _client;
    private readonly AccountNetworkPoliciesApiController _controller;
    private readonly ProxyProfileSecretService _secrets = new(new EphemeralDataProtectionProvider());
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AccountNetworkPoliciesApiTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlSugar($"Data Source={_path}");
        _services = services.BuildServiceProvider();
        _scope = _services.CreateScope();
        var db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _client = _scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
        _client.CodeFirst.InitTables(typeof(AccountNetworkPolicy), typeof(ProxyProfile), typeof(SystemRuntimeSettings),
            typeof(CodexAccount), typeof(GoogleAccount), typeof(KimiAccount), typeof(XaiAccount), typeof(DeviceOAuthAccount));
        _client.Insertable(new SystemRuntimeSettings { Id = 1, OAuthFeaturesEnabled = true, DeveloperFeaturesEnabled = false, DeveloperProxyProfilesEnabled = true }).ExecuteCommand();
        var store = new AccountNetworkPolicyStore(_services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, _secrets);
        _controller = new AccountNetworkPoliciesApiController(db, store, store);
    }

    [Fact]
    public async Task ControllerRequiresAuthenticationAndOAuthFeatureGate()
    {
        typeof(AccountNetworkPoliciesApiController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Should().NotBeEmpty();
        typeof(AccountNetworkPoliciesApiController).GetCustomAttributes(typeof(ServiceFilterAttribute), true)
            .Cast<ServiceFilterAttribute>().Should().Contain(a => a.ServiceType == typeof(OAuthFeatureToggleAttribute));
        Data(await _controller.Capabilities(default)).GetProperty("ready").GetBoolean().Should().Be(AccountNetworkPoliciesApiController.PipelineReady);
        // The egress selector is part of the proxy-pool feature surface: capabilities must hide it while the toggle is off.
        _client.Updateable(new SystemRuntimeSettings { Id = 1, OAuthFeaturesEnabled = true, DeveloperFeaturesEnabled = false, DeveloperProxyProfilesEnabled = false }).ExecuteCommand();
        Data(await _controller.Capabilities(default)).GetProperty("ready").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task DiscoveryIsIndependentOfDeveloperUiAndNeverReturnsEndpointOrSecrets()
    {
        var profile = AddProfile();
        var options = Data(await _controller.ProxyOptions(default));
        options.GetArrayLength().Should().Be(1);
        options[0].GetProperty("id").GetGuid().Should().Be(profile.Id);
        options[0].GetProperty("hasCredentials").GetBoolean().Should().BeTrue();
        options.GetRawText().Should().NotContain("private-user").And.NotContain("private-password")
            .And.NotContain("proxyUrl").And.NotContain("protectedPassword");
        profile.IsEnabled = false;
        _client.Updateable(profile).ExecuteCommand();
        Data(await _controller.ProxyOptions(default))[0].GetProperty("isEnabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task NumericSelectionRoundTripsAndSummaryShowsDisabledBindingWithoutFallback()
    {
        var id = AddAccount();
        var profile = AddProfile();
        var payload = JsonSerializer.Serialize(new { providerKey = "codex", accountId = id, networkSelection = new { mode = 2, proxyProfileId = profile.Id } }, JsonOptions);
        var request = JsonSerializer.Deserialize<AccountNetworkPolicyRequest>(payload, JsonOptions)!;
        var saved = Data(await _controller.Set(request, default));
        saved.GetProperty("networkSelection").GetProperty("mode").GetInt32().Should().Be(2);
        var revision = saved.GetProperty("revision").GetString();
        profile.IsEnabled = false;
        _client.Updateable(profile).ExecuteCommand();
        var summaries = Data(await _controller.Summary(new() { Accounts = [new("codex", id)] }, default));
        summaries[0].GetProperty("blockReason").GetString().Should().Be("proxy_profile_disabled");
        summaries[0].GetProperty("networkSelection").GetProperty("proxyProfileId").GetGuid().Should().Be(profile.Id);
        (await _controller.Set(request, default)).Should().BeOfType<BadRequestObjectResult>();
        (await _controller.Set(new() { ProviderKey = "codex", AccountId = id, NetworkSelection = new(NetworkMode.Direct), ExpectedRevision = revision }, default))
            .Should().BeOfType<OkObjectResult>();
        (await _controller.Set(new() { ProviderKey = "codex", AccountId = id, NetworkSelection = new(NetworkMode.Legacy), ExpectedRevision = revision }, default))
            .Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task MissingSelectionDoesNotClearAndBatchReportsPartialSuccess()
    {
        var id = AddAccount();
        await _controller.Set(new() { ProviderKey = "codex", AccountId = id, NetworkSelection = new(NetworkMode.Direct) }, default);
        (await _controller.Set(new() { ProviderKey = "codex", AccountId = id }, default)).Should().BeOfType<BadRequestObjectResult>();
        Data(await _controller.List("codex", id, default))[0].GetProperty("networkSelection").GetProperty("mode").GetInt32().Should().Be(1);
        var result = Data(await _controller.Batch(new()
        {
            Accounts = [new("codex", id), new("codex", Guid.NewGuid())], NetworkSelection = new(NetworkMode.Legacy)
        }, default));
        result.GetProperty("succeeded").GetInt32().Should().Be(1);
        result.GetProperty("failed").GetInt32().Should().Be(1);
        result.GetProperty("results")[1].GetProperty("errorCode").GetString().Should().Be("account_not_found");
        _client.Queryable<CodexAccount>().InSingle(id).AccessToken.Should().Be("account-secret");
    }

    [Fact]
    public async Task ProviderIdentityScopesSameGuidAndInvalidRequestsDoNotWrite()
    {
        var id = AddAccount();
        _client.Insertable(new GoogleAccount { Id = id }).ExecuteCommand();
        var result = Data(await _controller.Batch(new()
        {
            Accounts = [new("codex", id), new("google", id)], NetworkSelection = new(NetworkMode.Direct)
        }, default));
        result.GetProperty("succeeded").GetInt32().Should().Be(2);
        (await _controller.Set(new() { ProviderKey = "codex", AccountId = id, NetworkSelection = new((NetworkMode)99) }, default)).Should().BeOfType<BadRequestObjectResult>();
        (await _controller.Set(new() { ProviderKey = "unknown", AccountId = id, NetworkSelection = new(NetworkMode.Direct) }, default)).Should().BeOfType<BadRequestObjectResult>();
        _client.Queryable<AccountNetworkPolicy>().Count().Should().Be(2);
    }

    private Guid AddAccount()
    {
        var account = new CodexAccount { AccessToken = "account-secret" };
        _client.Insertable(account).ExecuteCommand();
        return account.Id;
    }
    private ProxyProfile AddProfile()
    {
        var profile = new ProxyProfile { Key = "test", Name = "Test profile", Revision = 1 };
        _secrets.TryApplyForSave(profile, "http://127.0.0.1:12345", "Replace", "private-user", "private-password", out _).Should().BeTrue();
        _client.Insertable(profile).ExecuteCommand();
        return profile;
    }
    private static JsonElement Data(IActionResult result) => JsonSerializer.SerializeToElement(
        result.Should().BeOfType<OkObjectResult>().Which.Value, JsonOptions).GetProperty("data");
    public void Dispose()
    {
        _scope.Dispose();
        _services.Dispose();
        try { File.Delete(_path); } catch (IOException) { }
    }
}

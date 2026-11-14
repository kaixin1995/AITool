using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.IntegrationTests.Proxy;

public sealed class GatewayConfigurationReferenceTests
{
    private static readonly Guid ValidKey = Guid.Parse("c3c3c3c3-c3c3-c3c3-c3c3-c3c3c3c3c3c3");

    [Fact]
    public async Task Save_returns_the_revision_of_its_own_snapshot()
    {
        await using var factory = new ResponsesWebApplicationFactory(new ResponsesFakeProxyForwardService());
        using var client = factory.CreateClient();
        var settings = factory.Services.GetRequiredService<GatewayEnhancementSettings>();
        var first = settings.Current;
        first.SearchApiKey = "first-secret";
        var firstSnapshot = await settings.SaveAsync(first, CancellationToken.None, first.Revision);
        var firstRevision = firstSnapshot.Revision;
        var second = settings.Current;
        second.SearchApiKey = "second-secret";
        var secondRevision = (await settings.SaveAsync(second, CancellationToken.None, second.Revision)).Revision;

        firstRevision.Should().Be(first.Revision).And.NotBe(secondRevision);
        firstSnapshot.SearchApiKey.Should().Be("first-secret");
        firstSnapshot.SearchApiKey = "changed-return-value";
        settings.Current.SearchApiKey.Should().Be("second-secret");
        settings.Current.Revision.Should().Be(secondRevision);
    }

    [Fact]
    public async Task Saving_settings_removes_inherited_deleted_keys_and_preserves_valid_configuration()
    {
        await using var factory = new ResponsesWebApplicationFactory(new ResponsesFakeProxyForwardService());
        using var client = factory.CreateClient();
        var settings = factory.Services.GetRequiredService<GatewayEnhancementSettings>();
        var deletedKey = Guid.NewGuid();
        await settings.SaveAsync(new GatewayEnhancementOptions
        {
            SearchEndpoint = "https://search.example/search", SearchApiKey = "search-secret",
            ToolAllowedKeyIds = [deletedKey, ValidKey],
            Budgets = [new() { KeyId = deletedKey, TokenLimit = 100 }, new() { KeyId = ValidKey, TokenLimit = 200 }],
            McpServers = [new() { Name = "docs", Url = "https://mcp.example", ApiKey = "mcp-secret", AllowedKeyIds = [deletedKey, ValidKey] }]
        }, CancellationToken.None);

        var options = (await client.GetFromJsonAsync<GatewayEnhancementOptions>("/api/admin/gateway-enhancements"))!;
        options.KeepAliveEnabled = true;
        options.Budgets.Single(b => b.KeyId == ValidKey).TokenLimit = 300;
        using var response = await client.PostAsJsonAsync("/api/admin/gateway-enhancements", options);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = settings.Current;
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        result["saved"]!.GetValue<bool>().Should().BeTrue();
        result["revision"]!.GetValue<string>().Should().Be(saved.Revision).And.NotBe(options.Revision);
        var returned = result["configuration"]!;
        returned["revision"]!.GetValue<string>().Should().Be(saved.Revision);
        returned["budgets"]!.AsArray().Should().ContainSingle();
        returned["toolAllowedKeyIds"]!.AsArray().Should().ContainSingle();
        returned["searchApiKey"]!.GetValue<string>().Should().Be("********");
        returned["mcpServers"]![0]!["apiKey"]!.GetValue<string>().Should().Be("********");
        result.ToJsonString().Should().NotContain("search-secret").And.NotContain("mcp-secret");
        saved.KeepAliveEnabled.Should().BeTrue();
        saved.ToolAllowedKeyIds.Should().Equal(ValidKey);
        saved.Budgets.Should().ContainSingle(b => b.KeyId == ValidKey && b.TokenLimit == 300);
        saved.McpServers.Should().ContainSingle();
        saved.McpServers[0].AllowedKeyIds.Should().Equal(ValidKey);
        saved.SearchApiKey.Should().Be("search-secret");
        saved.McpServers[0].ApiKey.Should().Be("mcp-secret");
    }

    [Theory]
    [InlineData("budget")]
    [InlineData("tools")]
    [InlineData("mcp")]
    [InlineData("moved-to-tools")]
    [InlineData("moved-to-mcp")]
    public async Task Saving_settings_rejects_new_invalid_references_without_changing_saved_configuration(string destination)
    {
        await using var factory = new ResponsesWebApplicationFactory(new ResponsesFakeProxyForwardService());
        using var client = factory.CreateClient();
        var settings = factory.Services.GetRequiredService<GatewayEnhancementSettings>();
        var inheritedDeletedKey = Guid.NewGuid();
        await settings.SaveAsync(new GatewayEnhancementOptions
        {
            Budgets = [new() { KeyId = inheritedDeletedKey, TokenLimit = 100 }],
            McpServers = [new() { Name = "docs", Url = "https://mcp.example", AllowedKeyIds = [ValidKey] }]
        }, CancellationToken.None);
        var options = settings.Current;
        var revision = options.Revision;
        var invalidKey = destination.StartsWith("moved-", StringComparison.Ordinal) ? inheritedDeletedKey : Guid.NewGuid();
        switch (destination)
        {
            case "budget": options.Budgets.Add(new() { KeyId = invalidKey, TokenLimit = 10 }); break;
            case "tools":
            case "moved-to-tools": options.ToolAllowedKeyIds.Add(invalidKey); break;
            case "mcp":
            case "moved-to-mcp": options.McpServers[0].AllowedKeyIds.Add(invalidKey); break;
        }

        using var response = await client.PostAsJsonAsync("/api/admin/gateway-enhancements", options);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("不存在的访问密钥");
        settings.Current.Revision.Should().Be(revision);
        settings.Current.Budgets.Should().ContainSingle(b => b.KeyId == inheritedDeletedKey);
        settings.Current.ToolAllowedKeyIds.Should().BeEmpty();
        settings.Current.McpServers[0].AllowedKeyIds.Should().Equal(ValidKey);
    }
}

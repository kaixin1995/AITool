using System.Net;
using System.Text;
using AITool.Application.OAuth;
using AITool.Infrastructure.OAuth;
using FluentAssertions;
using Xunit;

namespace AITool.IntegrationTests.OAuth;

public sealed class WorkBuddyModelWhitelistTests
{
    private sealed class Handler(string? enterprise, string? v3) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath == "/v3/config" ? v3 : enterprise;
            return Task.FromResult(new HttpResponseMessage(body is null ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private const string Enterprise = """
        {"code":0,"data":{"agents":[{"name":"CLI","models":["allowed","enterprise-only","image","tiny"]}],"models":[
          {"id":"allowed","name":"Enterprise name"},{"id":"enterprise-only"},
          {"id":"image","tags":["text-to-image"]},{"id":"tiny","maxOutputTokens":256},
          {"id":"not-allowed"}]}}
        """;
    private const string V3 = """
        {"code":0,"data":{"models":[{"id":"allowed","name":"V3 name"},
          {"id":"not-allowed"},{"id":"image"},{"id":"tiny"},{"id":"v3-only"}]}}
        """;

    [Fact]
    public async Task China_applies_enterprise_whitelist_and_non_chat_filters_to_both_sources()
    {
        var vendor = new WorkBuddyOAuthVendor(new HttpClient(new Handler(Enterprise, V3)));
        var models = await vendor.FetchModelsAsync(new DeviceOAuthTokenSet { AccessToken = "token" }, null, default);
        models.Select(m => m.Slug).Should().Equal("allowed", "enterprise-only");
        models[0].DisplayName.Should().Be("V3 name");
    }

    [Fact]
    public async Task China_uses_v3_whitelist_when_enterprise_fails()
    {
        const string v3 = """
            {"code":0,"data":{"agents":[{"name":"cli","models":["ALLOWED"]}],
            "models":[{"id":"allowed"},{"id":"not-allowed"}]}}
            """;
        var vendor = new WorkBuddyOAuthVendor(new HttpClient(new Handler(null, v3)));
        var models = await vendor.FetchModelsAsync(new DeviceOAuthTokenSet { AccessToken = "token" }, null, default);
        models.Select(m => m.Slug).Should().Equal("allowed");
    }

    [Fact]
    public async Task China_filters_string_catalog_and_preserves_empty_whitelist_fallback()
    {
        const string enterprise = """{"code":0,"data":{"agents":[{"name":"cli","models":[]}],"models":[{"id":"chat"}]}}""";
        const string v3 = """{"code":0,"data":["chat","NES-test","completion-test","codewise-test","other"]}""";
        var vendor = new WorkBuddyOAuthVendor(new HttpClient(new Handler(enterprise, v3)));
        var models = await vendor.FetchModelsAsync(new DeviceOAuthTokenSet { AccessToken = "token" }, null, default);
        models.Select(m => m.Slug).Should().Equal("chat", "other");
    }

    [Fact]
    public async Task Global_keeps_models_outside_cli_whitelist_and_non_chat_models()
    {
        var vendor = new WorkBuddyOAuthVendor(new HttpClient(new Handler(Enterprise, V3)), global: true);
        var models = await vendor.FetchModelsAsync(new DeviceOAuthTokenSet { AccessToken = "token" }, null, default);
        models.Select(m => m.Slug).Should().Equal("allowed", "not-allowed", "image", "tiny", "v3-only", "enterprise-only");
    }
}

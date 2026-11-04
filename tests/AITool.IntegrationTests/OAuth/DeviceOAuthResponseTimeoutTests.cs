using System.Net;
using System.Net.Http;
using AITool.Application.OAuth;
using AITool.Infrastructure.OAuth;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.OAuth;

public sealed class DeviceOAuthResponseTimeoutTests
{
    private static readonly DeviceOAuthTokenSet Token = new() { AccessToken = "access", RefreshToken = "refresh" };

    [Theory]
    [InlineData("copilot")]
    [InlineData("kiro")]
    [InlineData("minimax")]
    [InlineData("trae")]
    [InlineData("zcode")]
    public async Task Model_body_is_bounded_by_client_timeout_without_network_binding(string provider)
    {
        using var client = new HttpClient(new StalledHandler()) { Timeout = TimeSpan.FromMilliseconds(100) };
        var vendor = CreateVendor(provider, client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await vendor.FetchModelsAsync(Token, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("kiro")]
    [InlineData("minimax")]
    [InlineData("qoder")]
    [InlineData("zcode")]
    public async Task Quota_body_timeout_is_not_reported_as_invalid_credentials(string provider)
    {
        using var client = new HttpClient(new StalledHandler()) { Timeout = TimeSpan.FromMilliseconds(100) };
        var result = await CreateVendor(provider, client).QueryQuotaAsync(Token, null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.CredentialInvalid);
        Assert.False(result.UpstreamUnauthorized);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("kiro")]
    [InlineData("minimax")]
    [InlineData("qoder")]
    [InlineData("zcode")]
    public async Task Stalled_unauthorized_quota_body_marks_credentials_invalid(string provider)
    {
        using var client = new HttpClient(new StalledHandler(HttpStatusCode.Unauthorized)) { Timeout = TimeSpan.FromSeconds(30) };
        var result = await CreateVendor(provider, client).QueryQuotaAsync(Token, null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.CredentialInvalid);
        Assert.True(result.UpstreamUnauthorized);
    }

    [Fact]
    public async Task WorkBuddy_stalled_unauthorized_quota_body_marks_credentials_invalid()
    {
        using var client = new HttpClient(new StalledHandler(HttpStatusCode.Unauthorized)) { Timeout = TimeSpan.FromSeconds(30) };
        var result = await new WorkBuddyOAuthVendor(client).QueryQuotaAsync(Token, null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.CredentialInvalid);
        Assert.True(result.UpstreamUnauthorized);
    }

    [Fact]
    public async Task WorkBuddy_quota_body_is_bounded_by_client_timeout()
    {
        using var client = new HttpClient(new StalledHandler()) { Timeout = TimeSpan.FromMilliseconds(100) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new WorkBuddyOAuthVendor(client).QueryQuotaAsync(Token, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WorkBuddy_call_body_is_bounded_by_client_timeout()
    {
        using var client = new HttpClient(new StalledHandler()) { Timeout = TimeSpan.FromMilliseconds(100) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new WorkBuddyOAuthVendor(client).StartSignInAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Infinite_client_timeout_still_honors_caller_cancellation()
    {
        using var client = new HttpClient(new StalledHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new CopilotOAuthVendor(client).FetchModelsAsync(Token, null, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static IDeviceOAuthVendor CreateVendor(string provider, HttpClient client) => provider switch
    {
        "copilot" => new CopilotOAuthVendor(client),
        "kiro" => new KiroOAuthVendor(client),
        "minimax" => new MiniMaxOAuthVendor(client, NullLogger<MiniMaxOAuthVendor>.Instance),
        "qoder" => new QoderOAuthVendor(client),
        "trae" => new TraeOAuthVendor(client),
        "zcode" => new ZCodeOAuthVendor(client),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private sealed class StalledHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StalledContent() });
    }

    private sealed class StalledContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.Delay(Timeout.Infinite);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);
    }
}

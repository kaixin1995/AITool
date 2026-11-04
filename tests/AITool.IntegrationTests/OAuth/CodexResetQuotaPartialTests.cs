using System.Text.Json;
using AITool.Application.Codex;
using AITool.Web.Controllers.Admin;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.IntegrationTests.OAuth;

public sealed class CodexResetQuotaPartialTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_returns_completed_status_and_actionable_message(bool completed)
    {
        var reset = completed
            ? CodexQuotaResetResult.Success()
            : CodexQuotaResetResult.Unconfirmed(true, "恢复站点路由");
        var controller = new CodexApiController(null!, null!, null!, null!, null!, null!,
            new StubCooldownService(reset), null!, null!, NullLogger<CodexApiController>.Instance, null!);

        var response = (await controller.ResetQuota(Guid.NewGuid(), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject;
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(response.Value));
        body.RootElement.GetProperty("completed").GetBoolean().Should().Be(completed);
        var message = body.RootElement.GetProperty("message").GetString();
        if (completed)
            message.Should().Be("已重置");
        else
        {
            message.Should().Contain("重试");
            body.RootElement.GetProperty("coolingCleared").GetBoolean().Should().BeTrue();
            body.RootElement.GetProperty("unconfirmedStep").GetString().Should().Be("恢复站点路由");
        }
    }

    private sealed class StubCooldownService(CodexQuotaResetResult result) : ICodexQuotaCooldownService
    {
        public Task<CodexQuotaResetResult> ResetAsync(Guid codexAccountId, CancellationToken cancellationToken)
            => Task.FromResult(result);

        public Task<bool> TryApplyCooldownFromErrorAsync(int httpStatus, string? responseBody, Guid linkedSiteId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}

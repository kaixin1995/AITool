using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Proxy;
using FluentAssertions;
using Xunit;

namespace AITool.ApplicationTests.Proxy;

/// <summary>
/// 请求头目录升级与存量回填单元测试。
/// 重点验证 EnsureBuiltInHeaderPresent 对 WorkBuddy 存量档案的补全行为：
/// 仅补缺失键、空字典/非法 JSON/空 HeadersJson 不动且不抛、显式 false 保留。
/// </summary>
public sealed class HeaderProfileCatalogUpgradeTests
{
    [Fact]
    public void EnsureBuiltInHeaderPresent_Adds_Key_When_Missing_And_Preserves_Other_Headers()
    {
        var original = new Dictionary<string, string>
        {
            ["User-Agent"] = "WorkBuddy/Custom-UA",
            ["X-IDE-Name"] = "WorkBuddy"
        };
        var profile = new HeaderProfile
        {
            Key = ClientEmulationConstants.WorkBuddy,
            IsBuiltIn = true,
            HeadersJson = JsonSerializer.Serialize(original)
        };

        var modified = HeaderProfileCatalogService.EnsureBuiltInHeaderPresent(
            profile,
            ProxyForwardService.ForceStreamHeader,
            "true");

        modified.Should().BeTrue();
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(profile.HeadersJson);
        parsed.Should().NotBeNull();
        parsed![ProxyForwardService.ForceStreamHeader].Should().Be("true");
        parsed["User-Agent"].Should().Be("WorkBuddy/Custom-UA");
        parsed["X-IDE-Name"].Should().Be("WorkBuddy");
    }

    [Fact]
    public void EnsureBuiltInHeaderPresent_Does_Not_Modify_When_Key_Already_Exists_With_Explicit_False()
    {
        var original = new Dictionary<string, string>
        {
            ["User-Agent"] = "WorkBuddy/Custom-UA",
            [ProxyForwardService.ForceStreamHeader] = "false"
        };
        var originalJson = JsonSerializer.Serialize(original);
        var profile = new HeaderProfile
        {
            Key = ClientEmulationConstants.WorkBuddy,
            IsBuiltIn = true,
            HeadersJson = originalJson
        };

        var modified = HeaderProfileCatalogService.EnsureBuiltInHeaderPresent(
            profile,
            ProxyForwardService.ForceStreamHeader,
            "true");

        modified.Should().BeFalse();
        profile.HeadersJson.Should().Be(originalJson);
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(profile.HeadersJson);
        parsed![ProxyForwardService.ForceStreamHeader].Should().Be("false");
    }

    [Fact]
    public void EnsureBuiltInHeaderPresent_Ignores_Empty_Dictionary_Without_Throwing()
    {
        var profile = new HeaderProfile
        {
            Key = ClientEmulationConstants.WorkBuddy,
            IsBuiltIn = true,
            HeadersJson = "{}"
        };

        var modified = HeaderProfileCatalogService.EnsureBuiltInHeaderPresent(
            profile,
            ProxyForwardService.ForceStreamHeader,
            "true");

        modified.Should().BeFalse();
        profile.HeadersJson.Should().Be("{}");
    }

    [Fact]
    public void EnsureBuiltInHeaderPresent_Ignores_Invalid_Json_Without_Throwing()
    {
        const string badJson = "not a valid json {";
        var profile = new HeaderProfile
        {
            Key = ClientEmulationConstants.WorkBuddy,
            IsBuiltIn = true,
            HeadersJson = badJson
        };

        var modified = HeaderProfileCatalogService.EnsureBuiltInHeaderPresent(
            profile,
            ProxyForwardService.ForceStreamHeader,
            "true");

        modified.Should().BeFalse();
        profile.HeadersJson.Should().Be(badJson);
    }

    [Fact]
    public void EnsureBuiltInHeaderPresent_Ignores_Null_Or_Empty_HeadersJson()
    {
        var profileNull = new HeaderProfile { Key = ClientEmulationConstants.WorkBuddy, HeadersJson = null };
        var modifiedNull = HeaderProfileCatalogService.EnsureBuiltInHeaderPresent(
            profileNull,
            ProxyForwardService.ForceStreamHeader,
            "true");
        modifiedNull.Should().BeFalse();

        var profileEmpty = new HeaderProfile { Key = ClientEmulationConstants.WorkBuddy, HeadersJson = "   " };
        var modifiedEmpty = HeaderProfileCatalogService.EnsureBuiltInHeaderPresent(
            profileEmpty,
            ProxyForwardService.ForceStreamHeader,
            "true");
        modifiedEmpty.Should().BeFalse();
    }
}

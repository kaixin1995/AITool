using AITool.Infrastructure.Proxy;
using Xunit;

namespace AITool.ApplicationTests.Proxy;

public class EgressProxyValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://127.0.0.1:7890")]
    [InlineData("https://proxy.example.com:8443")]
    [InlineData("socks5://127.0.0.1:10808")]
    [InlineData("socks4://10.0.0.1:1080")]
    [InlineData("socks4a://10.0.0.1:1080")]
    public void TryValidate_ValidProxies_ReturnsTrue(string? proxyUrl)
    {
        var isValid = EgressProxyValidator.TryValidate(proxyUrl, out var error);
        Assert.True(isValid);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("ftp://127.0.0.1:21")]
    [InlineData("ws://127.0.0.1:8080")]
    [InlineData("127.0.0.1:10808")]
    [InlineData("not a valid uri")]
    [InlineData("http://:8080")]
    [InlineData("socks5://127.0.0.1:70000")]
    public void TryValidate_InvalidProxies_ReturnsFalse(string proxyUrl)
    {
        var isValid = EgressProxyValidator.TryValidate(proxyUrl, out var error);
        Assert.False(isValid);
        Assert.NotNull(error);
    }
}

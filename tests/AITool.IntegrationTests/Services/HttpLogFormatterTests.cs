using AITool.Web.Services;
using Microsoft.AspNetCore.Http;

namespace AITool.IntegrationTests.Services;

public sealed class HttpLogFormatterTests
{
    [Theory]
    [InlineData("/api/admin/developer/proxy-profiles")]
    [InlineData("/api/admin/developer/proxy-profiles/example/test")]
    public async Task Proxy_configuration_body_is_omitted_even_when_not_valid_json(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{password:secret,proxyUrl:http://user:secret@localhost"));

        var preview = await HttpLogFormatter.ReadRequestBodyPreviewAsync(context.Request, CancellationToken.None);

        Assert.Equal("<proxy configuration omitted>", preview);
        Assert.Equal(0, context.Request.Body.Position);
        Assert.True(HttpLogFormatter.IsSensitiveNetworkConfiguration(context.Request));
    }

    [Fact]
    public async Task ReadRequestBodyPreviewAsync_does_not_read_unbounded_body()
    {
        var body = new string('x', HttpLogFormatter.DefaultMaxBodyLength * 4);
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));

        var preview = await HttpLogFormatter.ReadRequestBodyPreviewAsync(context.Request, CancellationToken.None);

        Assert.True(preview.Length <= HttpLogFormatter.DefaultMaxBodyLength);
        Assert.Contains("request body preview truncated", preview, StringComparison.Ordinal);
        Assert.Equal(0, context.Request.Body.Position);
    }
}

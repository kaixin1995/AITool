using AITool.Infrastructure.Hosting;
using AITool.Admin.Services;
using Microsoft.AspNetCore.Http;

namespace AITool.Admin.IntegrationTests.Services;

public sealed class HttpLogFormatterTests
{
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

using System.Text;

namespace AITool.Web.Services;

public static class GatewayToolHttp
{
    /// <summary>Read a bounded body rather than buffering an arbitrary upstream response.</summary>
    public static async Task<string> ReadAsync(HttpContent content, int maximumBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximumBytes) throw new HttpRequestException("上游返回内容过大。");
        await using var source = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (output.Length + read > maximumBytes) throw new HttpRequestException("上游返回内容过大。");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    public static Uri LocalEndpoint(HttpContext http, string path)
    {
        var ip = http.Connection.LocalIpAddress;
        var host = ip?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[::1]" : "127.0.0.1";
        if (http.Connection.LocalPort <= 0) throw new InvalidOperationException("当前宿主不支持内部工具调用。");
        return new Uri($"{http.Request.Scheme}://{host}:{http.Connection.LocalPort}{path}");
    }
}

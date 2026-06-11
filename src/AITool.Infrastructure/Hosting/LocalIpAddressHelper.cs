using System.Net;
using System.Net.Sockets;

namespace AITool.Infrastructure.Hosting;

/// <summary>
/// 提供本机 IPv4 地址查询功能，用于启动日志输出。
/// </summary>
public static class LocalIpAddressHelper
{
    /// <summary>
    /// 获取本机非回环 IPv4 地址。查询失败时回退到 127.0.0.1。
    /// </summary>
    public static string GetLocalIpAddress()
    {
        try
        {
            var addresses = Dns.GetHostAddresses(Dns.GetHostName());
            var ipv4 = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x));
            return ipv4?.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}

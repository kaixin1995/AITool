using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AITool.ApplicationTests.Networking;

/// <summary>Only loopback listeners/connections; no DNS, external proxies, or Internet access.</summary>
internal sealed class LoopbackNetworkHarness : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly Func<Stream, CancellationToken, Task> _serve;
    private readonly X509Certificate2? _tlsCertificate;
    private readonly Task _accept;
    private int _accepted;

    public LoopbackNetworkHarness(Func<Stream, CancellationToken, Task> serve, X509Certificate2? tlsCertificate = null)
    {
        _serve = serve;
        _tlsCertificate = tlsCertificate;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptAsync();
    }

    public int Port { get; }
    public Uri Url => new($"{(_tlsCertificate is null ? "http" : "https")}://127.0.0.1:{Port}");
    public int Accepted => Volatile.Read(ref _accepted);
    public ConcurrentQueue<Exception> Errors { get; } = new();

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                Interlocked.Increment(ref _accepted);
                _connections.Add(ServeAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                if (_tlsCertificate is null)
                    await _serve(stream, _stop.Token);
                else
                {
                    await using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _tlsCertificate
                    }, _stop.Token);
                    await _serve(tls, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) { /* Client cancellation/rejected TLS certificates close the socket. */ }
            catch (System.Security.Authentication.AuthenticationException) { }
            catch (Exception error) { Errors.Enqueue(error); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_connections);
        _stop.Dispose();
    }

    public static async Task<HttpCapture> ReadHttpAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var next = new byte[1];
        while (bytes.Count < 32768)
        {
            if (await stream.ReadAsync(next, ct) == 0) throw new IOException("Peer closed before HTTP headers.");
            bytes.Add(next[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
            {
                var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n");
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1).Where(s => s.Contains(':')))
                {
                    var split = line.IndexOf(':');
                    headers[line[..split]] = line[(split + 1)..].Trim();
                }
                return new HttpCapture(lines[0], headers);
            }
        }
        throw new IOException("HTTP headers too large.");
    }

    public static async Task ReplyAsync(Stream stream, string body, CancellationToken ct,
        string status = "200 OK", string extraHeaders = "")
    {
        var content = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {content.Length}\r\nConnection: close\r\n{extraHeaders}\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(content, ct);
        await stream.FlushAsync(ct);
    }

    public static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return new X509Certificate2(certificate.Export(X509ContentType.Pfx));
    }

    public sealed record HttpCapture(string RequestLine, IReadOnlyDictionary<string, string> Headers);
}

internal sealed class LoopbackHttpProxy : IAsyncDisposable
{
    private readonly LoopbackNetworkHarness _server;
    private readonly string _label;
    private readonly string? _expectedAuthorization;

    public LoopbackHttpProxy(string label, NetworkCredential? credentials = null, X509Certificate2? tlsCertificate = null)
    {
        _label = label;
        if (credentials is not null)
            _expectedAuthorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.UserName + ":" + credentials.Password));
        _server = new LoopbackNetworkHarness(ServeAsync, tlsCertificate);
    }

    public Uri Url => _server.Url;
    public int Accepted => _server.Accepted;
    public ConcurrentQueue<LoopbackNetworkHarness.HttpCapture> Requests { get; } = new();
    public ConcurrentQueue<Exception> Errors => _server.Errors;

    private async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        var request = await LoopbackNetworkHarness.ReadHttpAsync(stream, ct);
        Requests.Enqueue(request);
        if (_expectedAuthorization is not null &&
            (!request.Headers.TryGetValue("Proxy-Authorization", out var authorization) || authorization != _expectedAuthorization))
        {
            await LoopbackNetworkHarness.ReplyAsync(stream, "proxy authentication required", ct,
                "407 Proxy Authentication Required", "Proxy-Authenticate: Basic realm=\"loopback\"\r\n");
            return;
        }

        if (!request.RequestLine.StartsWith("CONNECT ", StringComparison.Ordinal))
        {
            await LoopbackNetworkHarness.ReplyAsync(stream, _label, ct);
            return;
        }

        var authority = request.RequestLine.Split(' ')[1];
        var destination = new Uri("http://" + authority);
        // The proxy cannot be abused to connect outside this harness.
        if (!IPAddress.TryParse(destination.Host, out var ip) || !IPAddress.IsLoopback(ip))
            throw new InvalidOperationException("CONNECT target must be a literal loopback address.");
        using var upstream = new TcpClient();
        await upstream.ConnectAsync(IPAddress.Loopback, destination.Port, ct);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), ct);
        await stream.FlushAsync(ct);
        await using var target = upstream.GetStream();
        using var copying = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var upload = stream.CopyToAsync(target, copying.Token);
        var download = target.CopyToAsync(stream, copying.Token);
        await Task.WhenAny(upload, download);
        copying.Cancel();
        try { await Task.WhenAll(upload, download); }
        catch (OperationCanceledException) { }
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

internal sealed class LoopbackSocksProxy : IAsyncDisposable
{
    private readonly LoopbackNetworkHarness _server;
    private readonly string _label;
    private readonly NetworkCredential? _credentials;
    public ConcurrentQueue<string> Destinations { get; } = new();
    public ConcurrentQueue<string> UserNames { get; } = new();
    public ConcurrentQueue<Exception> Errors => _server.Errors;

    public LoopbackSocksProxy(string label, NetworkCredential? credentials = null)
    {
        _label = label;
        _credentials = credentials;
        _server = new LoopbackNetworkHarness(ServeAsync);
    }

    public Uri Url(string scheme) => new($"{scheme}://127.0.0.1:{_server.Port}");

    private async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        var version = (await ReadAsync(stream, 1, ct))[0];
        if (version == 5) await Socks5Async(stream, ct);
        else if (version == 4) await Socks4Async(stream, ct);
        else throw new IOException("Unexpected SOCKS version.");
        await LoopbackNetworkHarness.ReadHttpAsync(stream, ct);
        await LoopbackNetworkHarness.ReplyAsync(stream, _label, ct);
    }

    private async Task Socks5Async(Stream stream, CancellationToken ct)
    {
        var count = (await ReadAsync(stream, 1, ct))[0];
        var methods = await ReadAsync(stream, count, ct);
        byte method = _credentials is null ? (byte)0 : (byte)2;
        if (!methods.Contains(method)) throw new IOException("Expected SOCKS authentication method absent.");
        await stream.WriteAsync(new byte[] { 5, method }, ct);
        if (method == 2)
        {
            var authentication = await ReadAsync(stream, 2, ct);
            var user = Encoding.UTF8.GetString(await ReadAsync(stream, authentication[1], ct));
            var passwordLength = (await ReadAsync(stream, 1, ct))[0];
            var password = Encoding.UTF8.GetString(await ReadAsync(stream, passwordLength, ct));
            UserNames.Enqueue(user);
            var success = user == _credentials!.UserName && password == _credentials.Password;
            await stream.WriteAsync(new byte[] { 1, success ? (byte)0 : (byte)1 }, ct);
            if (!success) throw new IOException("SOCKS credentials rejected.");
        }
        var command = await ReadAsync(stream, 4, ct);
        if (command[0] != 5 || command[1] != 1) throw new IOException("Expected SOCKS CONNECT.");
        var host = command[3] switch
        {
            1 => new IPAddress(await ReadAsync(stream, 4, ct)).ToString(),
            4 => new IPAddress(await ReadAsync(stream, 16, ct)).ToString(),
            3 => Encoding.ASCII.GetString(await ReadAsync(stream, (await ReadAsync(stream, 1, ct))[0], ct)),
            _ => throw new IOException("Unexpected SOCKS address kind.")
        };
        var port = await ReadAsync(stream, 2, ct);
        Destinations.Enqueue($"{host}:{(port[0] << 8) | port[1]}");
        await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, ct);
    }

    private async Task Socks4Async(Stream stream, CancellationToken ct)
    {
        var command = await ReadAsync(stream, 7, ct);
        if (command[0] != 1) throw new IOException("Expected SOCKS4 CONNECT.");
        var user = await ReadTerminatedAsync(stream, ct);
        UserNames.Enqueue(user);
        var remoteDns = command[3] == 0 && command[4] == 0 && command[5] == 0 && command[6] != 0;
        var host = remoteDns ? await ReadTerminatedAsync(stream, ct) : new IPAddress(command.AsSpan(3, 4)).ToString();
        Destinations.Enqueue($"{host}:{(command[1] << 8) | command[2]}");
        await stream.WriteAsync(new byte[] { 0, 90, 0, 80, 127, 0, 0, 1 }, ct);
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int length, CancellationToken ct)
    {
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct);
        return bytes;
    }

    private static async Task<string> ReadTerminatedAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        while (bytes.Count < 1024)
        {
            var value = (await ReadAsync(stream, 1, ct))[0];
            if (value == 0) return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(value);
        }
        throw new IOException("SOCKS field too long.");
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

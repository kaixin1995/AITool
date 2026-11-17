using System.Diagnostics;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;

namespace AITool.Web.Services;

public sealed class GatewayEnhancementMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, GatewayEnhancementSettings settings, ProxyRequestMetadataCache metadata,
        GatewayBudgetGate gate, GatewayRequestContext context, AppDbContext db, ILogger<GatewayEnhancementMiddleware> logger)
    {
        if (!http.Request.Path.StartsWithSegments("/v1") && !http.Request.Path.StartsWithSegments("/mcp")) { await next(http); return; }
        if (settings.LoadError is { } loadError)
        {
            http.Response.StatusCode = 503;
            await http.Response.WriteAsJsonAsync(new { error = new { code = "enhancement_configuration_unavailable", message = loadError } });
            return;
        }
        var options = settings.Current;
        if (http.Request.Path.Equals(new PathString("/v1/tools/describe-image"))) { await next(http); return; }
        // MCP has no model token use of its own. Built-in vision re-enters the model
        // gateway, where admission and settlement occur. Never hold this key's lock
        // around a call that will acquire it again, including chunked MCP requests.
        if (http.Request.Path.StartsWithSegments("/mcp")) { await next(http); return; }
        if (http.WebSockets.IsWebSocketRequest) { await next(http); return; }
        if (!options.KeepAliveEnabled && options.Budgets.Count == 0) { await next(http); return; }
        var bearer = http.Request.Headers.Authorization.ToString();
        var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer[7..] : http.Request.Headers["x-api-key"].ToString();
        var key = await metadata.ValidateAccessKeyAsync(token, http.RequestAborted);
        if (key is null) { await next(http); return; }
        var budgets = options.Budgets.Where(b => b.KeyId == key.Id && (b.TokenLimit > 0 || b.CostLimitUsd > 0)).ToList();
        using var budgetLock = budgets.Count > 0 ? await gate.EnterAsync(key.Id, http.RequestAborted) : null;
        if (budgets.Count > 0 && gate.HasUnsettled(key.Id))
        {
            http.Response.StatusCode = 503;
            await http.Response.WriteAsJsonAsync(new { error = new { code = "budget_settlement_failed", message = "先前请求用量未能结算，请检查数据库后重启服务。" } });
            return;
        }
        if (budgets.Count > 0 && await GatewayBudgetGate.CheckAsync(db, key.Id, budgets, http.RequestAborted) is { } error)
        {
            http.Response.StatusCode = 429;
            await http.Response.WriteAsJsonAsync(new { error = new { code = "budget_exceeded", message = error } });
            return;
        }
        context.Enabled = budgets.Count > 0;
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var original = http.Response.Body;
        await using var measured = new GatewayResponseStream(original, http, options);
        http.Response.Body = measured;
        var completed = false;
        try { await next(http); completed = true; }
        finally
        {
            await measured.StopAsync();
            http.Response.Body = original;
            if (budgets.Count > 0)
            {
                // Admission ledger must settle even when the client has disconnected.
                var row = new GatewayRequestMetric { AccessKeyId = key.Id, StartedUtcTicks = started.UtcTicks,
                    RequestBytes = http.Request.ContentLength ?? 0, ResponseBytes = measured.BytesWritten,
                    DurationMs = watch.ElapsedMilliseconds, StatusCode = http.Response.StatusCode,
                    Outcome = http.RequestAborted.IsCancellationRequested ? "canceled" : !completed || http.Response.StatusCode >= 400 || context.Failed ? "failed" : "completed",
                    Tokens = context.Tokens, CostUsd = context.CostUsd, Unpriced = context.Unpriced };
                try { await db.Client.Insertable(row).ExecuteCommandAsync(); }
                catch (Exception ex) { logger.LogError(ex, "网关预算记录写入失败"); if (budgets.Count > 0) gate.MarkUnsettled(key.Id); }
            }
        }
    }
}

/// <summary>Only send SSE comments after actual output; serialize heartbeats with writes, never inject into a partial event.</summary>
public sealed class GatewayResponseStream : Stream
{
    private readonly Stream _inner;
    private readonly HttpContext _http;
    private readonly SemaphoreSlim _write = new(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _heartbeat;
    private byte _previous, _last;
    private uint _tail;
    public long BytesWritten { get; private set; }
    public GatewayResponseStream(Stream inner, HttpContext http, GatewayEnhancementOptions options)
    {
        _inner = inner; _http = http;
        _heartbeat = options.KeepAliveEnabled ? Heartbeat(options.KeepAliveSeconds) : Task.CompletedTask;
    }
    private async Task Heartbeat(int seconds)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                await _write.WaitAsync(_stop.Token);
                try
                {
                    if (BytesWritten > 0 && ((_previous == '\n' && _last == '\n') || _tail == 0x0d0a0d0a)
                        && _http.Response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
                    { var bytes = ": keepalive\n\n"u8.ToArray(); await _inner.WriteAsync(bytes, _stop.Token); await _inner.FlushAsync(_stop.Token); BytesWritten += bytes.Length; }
                }
                finally { _write.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
    public async Task StopAsync() { _stop.Cancel(); await _heartbeat; }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await _write.WaitAsync(ct);
        try
        {
            await _inner.WriteAsync(buffer, ct); BytesWritten += buffer.Length;
            if (buffer.Length > 0) { _previous = buffer.Length > 1 ? buffer.Span[^2] : _last; _last = buffer.Span[^1]; }
            UpdateTail(buffer);
        }
        finally { _write.Release(); }
    }
    private void UpdateTail(ReadOnlyMemory<byte> buffer)
    {
        foreach (var value in buffer.Span) _tail = (_tail << 8) | value;
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override void Flush() => _inner.Flush();
    public override async Task FlushAsync(CancellationToken ct) { await _write.WaitAsync(ct); try { await _inner.FlushAsync(ct); } finally { _write.Release(); } }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override async ValueTask DisposeAsync() { await StopAsync(); _stop.Dispose(); _write.Dispose(); GC.SuppressFinalize(this); }
}

using System.Runtime.CompilerServices;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// SSE 心跳注入流：包装响应体输出流，静默超过阈值时写入 SSE 注释帧 `: ping\n\n`，
/// 防止中间设备（NAT/QoS/反代）因长空闲回收连接——推理模型思考间隙常达数十秒。
/// <para>
/// 规则：
/// - 任何真实数据写（非空字节）都会刷新空闲基准；心跳帧直接写内层流，不参与计数；
/// - 响应完成/连接关闭后，写入抛异常即静默退出（注释帧不破坏 SSE 帧语义，标准客户端忽略）；
/// - 秒级轮询，无数据时不产生任何额外系统调用开销。
/// </para>
/// </summary>
public sealed class SseHeartbeatStream : Stream
{
    private static readonly byte[] PingFrame = ": ping\n\n"u8.ToArray();

    private readonly Stream _inner;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    // 写串行化：心跳泵与转发数据写共享内层流，必须互斥，避免帧交错。
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _lastWriteTicks = Environment.TickCount64;

    public SseHeartbeatStream(Stream inner, int intervalSeconds)
    {
        _inner = inner;
        _interval = TimeSpan.FromSeconds(Math.Max(1, intervalSeconds));
        _ = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token).ConfigureAwait(false);
                var idleMs = Environment.TickCount64 - Interlocked.Read(ref _lastWriteTicks);
                if (idleMs < _interval.TotalMilliseconds)
                {
                    continue;
                }

                // 静默超阈值：注入注释帧并冲刷到网络（与数据写互斥，防帧交错）。
                await _writeGate.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    await _inner.WriteAsync(PingFrame, _cts.Token).ConfigureAwait(false);
                    await _inner.FlushAsync(_cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出。
        }
        catch (Exception)
        {
            // 客户端已断开/响应已结束：停止心跳。
        }
    }

    private void MarkWrite(int byteCount)
    {
        if (byteCount > 0)
        {
            Interlocked.Exchange(ref _lastWriteTicks, Environment.TickCount64);
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        MarkWrite(count);
        _writeGate.Wait();
        try
        {
            _inner.Write(buffer, offset, count);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        MarkWrite(buffer.Length);
        _writeGate.Wait();
        try
        {
            _inner.Write(buffer);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        MarkWrite(buffer.Length);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        MarkWrite(count);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
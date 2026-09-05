namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 流恢复捕获流：透传所有写入字节的同时按 SSE 帧边界切分并存入 <see cref="StreamResumeStore"/>，
/// 供断线方以 X-Stream-Resume 重入后重放。经由此流的写字节与内层流完全一致（截断/增加零开销语义）。
/// 心跳注释帧同样会被缓冲——注释帧重放无害且有利于对齐心跳节奏。
/// </summary>
public sealed class StreamResumeCaptureStream : Stream
{
    private readonly Stream _inner;
    private readonly StreamResumeStore _store;
    private readonly string _requestId;
    private byte[]? _pending;
    private bool _completed;

    public StreamResumeCaptureStream(Stream inner, StreamResumeStore store, string requestId)
    {
        _inner = inner;
        _store = store;
        _requestId = requestId;
    }

    private void Capture(byte[] buffer, int offset, int count)
    {
        if (_completed || count <= 0)
        {
            return;
        }

        var frames = SseFrameSplitter.Split(buffer.AsMemory(offset, count), ref _pending);
        foreach (var frame in frames)
        {
            if (!_store.Append(_requestId, frame))
            {
                // 超限/未知：停止缓冲（转发不受影响）。
                _completed = true;
                break;
            }
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Capture(buffer, offset, count);
        _inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Capture(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer).ToArray(), 0, buffer.Length);
        _inner.Write(buffer);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Capture(buffer.ToArray(), 0, buffer.Length);
        return _inner.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Capture(buffer, offset, count);
        return _inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _completed = true;
            _store.MarkCompleted(_requestId);
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            _completed = true;
            _store.MarkCompleted(_requestId);
        }
        await base.DisposeAsync();
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
using System.Text;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace AITool.IntegrationTests.Proxy;

public class SseTerminatorWriterTests
{
    [Fact]
    public async Task TryWriteTerminatorAsync_WhenStreamBlocks_AbortsWithinBudgetWithoutThrowing()
    {
        var context = new DefaultHttpContext();
        var blockingStream = new BlockingStream();
        context.Response.Body = blockingStream;

        var budget = TimeSpan.FromMilliseconds(100);
        using var requestAbortedCts = new CancellationTokenSource();

        var writeTask = context.Response.TryWriteTerminatorAsync("data: [DONE]\n\n", requestAbortedCts.Token, budget);
        var completedTask = await Task.WhenAny(writeTask, Task.Delay(TimeSpan.FromSeconds(3)));

        completedTask.Should().BeSameAs(writeTask, "Terminator write should complete boundedly via budget cancellation");
        await writeTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryWriteTerminatorAsync_WhenWriteOrFlushIgnoresCancellation_AbortsAndReturns(bool blockFlush)
    {
        var context = new DefaultHttpContext();
        var lifetime = new RecordingLifetimeFeature();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var stream = new IgnoringCancellationStream(blockFlush);
        context.Response.Body = stream;

        var task = context.Response.TryWriteTerminatorAsync("data: [DONE]\n\n", CancellationToken.None, TimeSpan.FromMilliseconds(300));
        try
        {
            await stream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            lifetime.AbortCount.Should().Be(1);
            stream.FlushCount.Should().Be(blockFlush ? 1 : 0);
        }
        finally
        {
            // 故意在调用方返回之后才结束在途操作，并以异常结束以覆盖迟到的失败。
            stream.Release.TrySetException(new IOException("late failure"));
        }
    }

    [Fact]
    public async Task TryWriteTerminatorAsync_WhenRequestAbortedDuringIgnoredWrite_AbortsAndReturns()
    {
        var context = new DefaultHttpContext();
        var lifetime = new RecordingLifetimeFeature();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var stream = new IgnoringCancellationStream(false);
        context.Response.Body = stream;
        using var aborted = new CancellationTokenSource();

        var task = context.Response.TryWriteTerminatorAsync("data: [DONE]\n\n", aborted.Token, TimeSpan.FromSeconds(30));
        try
        {
            await stream.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
            aborted.Cancel();
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            lifetime.AbortCount.Should().Be(1);
            stream.FlushCount.Should().Be(0);
        }
        finally
        {
            stream.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task TryWriteTerminatorAsync_WhenRequestAbortedAlreadyCanceled_ReturnsImmediatelyWithoutWriting()
    {
        var context = new DefaultHttpContext();
        using var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        using var preCanceledCts = new CancellationTokenSource();
        preCanceledCts.Cancel();

        await context.Response.TryWriteTerminatorAsync("data: [DONE]\n\n", preCanceledCts.Token);

        memoryStream.Length.Should().Be(0);
    }

    [Fact]
    public async Task TryWriteTerminatorAsync_WhenClientHealthy_WritesAndFlushesPayload()
    {
        var context = new DefaultHttpContext();
        using var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        const string payload = "data: [DONE]\n\n";
        await context.Response.TryWriteTerminatorAsync(payload, CancellationToken.None);

        var written = Encoding.UTF8.GetString(memoryStream.ToArray());
        written.Should().Be(payload);
    }

    private sealed class RecordingLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        public int AbortCount { get; private set; }
        public void Abort() => AbortCount++;
    }

    private sealed class IgnoringCancellationStream(bool blockFlush) : Stream
    {
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FlushCount { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            if (!blockFlush) return Task.CompletedTask;
            Blocked.TrySetResult();
            return Release.Task;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (blockFlush) return ValueTask.CompletedTask;
            Blocked.TrySetResult();
            return new ValueTask(Release.Task);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}

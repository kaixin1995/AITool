using System.Text;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
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

        completedTask.Should().BeSameAs(writeTask, "Terminator write should complete boundedly via budget cancelation");
        await writeTask; // Ensure no exception escaped
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

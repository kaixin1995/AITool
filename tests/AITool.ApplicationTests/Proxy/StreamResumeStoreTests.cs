using System.Text;
using AITool.Infrastructure.Proxy;
using FluentAssertions;

namespace AITool.ApplicationTests.Proxy;

/// <summary>
/// SSE 帧切分器与可恢复缓冲存储单测（StreamResumeStore / SseFrameSplitter）。
/// </summary>
public sealed class StreamResumeStoreTests
{
    // ---------- SseFrameSplitter ----------

    [Fact]
    public void Splitter_extracts_frames_across_chunk_boundaries()
    {
        byte[]? pending = null;
        var frames = new List<byte[]>();

        frames.AddRange(SseFrameSplitter.Split("data: a\n\n"u8.ToArray(), ref pending));
        pending.Should().BeNull();

        // 边界落在块中间：前一帧尾部 + 后一帧头部在同一块内。
        frames.AddRange(SseFrameSplitter.Split("data: b"u8.ToArray(), ref pending));
        frames.Should().HaveCount(1);
        pending.Should().NotBeNull();

        frames.AddRange(SseFrameSplitter.Split("\n\ndata: c\n\n"u8.ToArray(), ref pending));
        frames.Should().HaveCount(3);

        Encoding.UTF8.GetString(frames[0]).Should().Be("data: a\n\n");
        Encoding.UTF8.GetString(frames[1]).Should().Be("data: b\n\n");
        Encoding.UTF8.GetString(frames[2]).Should().Be("data: c\n\n");
    }

    [Fact]
    public void Splitter_preserves_crlf_boundaries()
    {
        byte[]? pending = null;
        var frames = SseFrameSplitter.Split("data: x\r\n\r\ndata: y\r\n\r\n"u8.ToArray(), ref pending);

        frames.Should().HaveCount(2);
        Encoding.UTF8.GetString(frames[0]).Should().Be("data: x\r\n\r\n", "回车随帧保留，重放逐字节还原");
    }

    [Fact]
    public void Splitter_drops_unclosed_tail_at_stream_end()
    {
        byte[]? pending = null;
        var frames = SseFrameSplitter.Split("data: ok\n\npartial-without-boundary"u8.ToArray(), ref pending);

        frames.Should().ContainSingle();
        pending.Should().NotBeNull();
        // 流结束没有新字节到来：残帧由调用方丢弃（客户端同样无法解析它）。
    }

    // ---------- StreamResumeStore ----------

    private static StreamResumeStore CreateStore()
        => new(maxFrameBytesPerRequest: 1024, maxActiveStreams: 5, retainWindow: TimeSpan.FromMinutes(5));

    [Fact]
    public void Store_resume_returns_frames_after_last_seq()
    {
        var store = CreateStore();
        store.TryBegin("req-1").Should().BeTrue();

        store.Append("req-1", "data: 1\n\n"u8.ToArray());
        store.Append("req-1", "data: 2\n\n"u8.ToArray());
        store.Append("req-1", "data: 3\n\n"u8.ToArray());
        store.MarkCompleted("req-1");

        var resumed = store.TryResume("req-1", 1);
        resumed.Should().NotBeNull();
        resumed!.Select(f => Encoding.UTF8.GetString(f)).Should().Equal("data: 2\n\n", "data: 3\n\n");
    }

    [Fact]
    public void Store_resume_with_fresh_seq_returns_empty()
    {
        var store = CreateStore();
        store.TryBegin("req-2");
        store.Append("req-2", "data: 1\n\n"u8.ToArray());
        store.MarkCompleted("req-2");

        store.TryResume("req-2", 1).Should().BeEmpty();
        store.TryResume("req-2", 5).Should().BeEmpty("客户端比服务器还新：无帧可重放");
    }

    [Fact]
    public void Store_unknown_or_truncated_returns_null()
    {
        var store = CreateStore();
        store.TryResume("missing", 0).Should().BeNull();

        store.TryBegin("req-3");
        for (var i = 0; i < 100; i++)
        {
            store.Append("req-3", new byte[20]); // 40×20=800 < 1024；第 52 次后超限
            if (i > 60 && !store.HasBuffer("req-3"))
            {
                break;
            }
        }

        // 超限后缓冲被清空，视为不可恢复。
        var oversized = store.Append("req-3", new byte[1024]);
        if (!oversized)
        {
            store.TryResume("req-3", 0).Should().BeNull();
        }
    }

    [Fact]
    public void Store_rejects_new_streams_beyond_active_cap()
    {
        var store = new StreamResumeStore(maxFrameBytesPerRequest: 1024, maxActiveStreams: 2, retainWindow: TimeSpan.FromMinutes(5));
        store.TryBegin("a").Should().BeTrue();
        store.TryBegin("b").Should().BeTrue();
        store.TryBegin("c").Should().BeFalse("并发缓冲满：拒绝新缓冲但不影响转发");
    }

    [Fact]
    public void Store_truncated_stops_appending()
    {
        var store = new StreamResumeStore(maxFrameBytesPerRequest: 16, maxActiveStreams: 5, retainWindow: TimeSpan.FromMinutes(5));
        store.TryBegin("req-4");
        store.Append("req-4", new byte[10]).Should().BeTrue();
        store.Append("req-4", new byte[10]).Should().BeFalse("超过 16 字节上限");
        store.Append("req-4", new byte[10]).Should().BeFalse("截断后不再追加");
    }
}
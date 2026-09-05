using System.Collections.Concurrent;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// SSE 帧切片工具：将上游字节流传入的原始字节按 `\n\n` 边界切分为完整帧（保留原始字节，
/// 兼容 `\r\n\r\n`——回车随帧保留于帧尾，重放时逐字节还原）。
/// 不完整残帧挂起等待后续字节补全；流结束时未闭合残帧丢弃（客户端同样无法解析它）。
/// </summary>
public static class SseFrameSplitter
{
    private const string FrameBoundary = "\n\n";
    private const int PendingCapacity = 64 * 1024; // 残帧挂起上限，防畸形流无限累积。

    public static List<byte[]> Split(ReadOnlyMemory<byte> chunk, ref byte[]? pending)
    {
        var frames = new List<byte[]>();

        // 合并挂起残帧与新块。
        byte[] buffer;
        if (pending is { Length: > 0 } head)
        {
            buffer = new byte[head.Length + chunk.Length];
            Array.Copy(head, buffer, head.Length);
            chunk.Span.CopyTo(buffer.AsSpan(head.Length));
        }
        else
        {
            buffer = chunk.ToArray();
        }

        var start = 0;
        while (true)
        {
            var boundaryEnd = IndexOfBoundaryEnd(buffer, start);
            if (boundaryEnd < 0)
            {
                break;
            }

            // 帧内容 = start..boundaryEnd（闭区间，含结束边界；\n\n 结束在 i+1，\r\n\r\n 结束在 i+3）。
            var frameLength = boundaryEnd - start + 1;
            var frame = new byte[frameLength];
            Array.Copy(buffer, start, frame, 0, frameLength);
            frames.Add(frame);

            start = boundaryEnd + 1;
        }

        // 剩余残帧。
        var leftoverLength = buffer.Length - start;
        if (leftoverLength > 0)
        {
            if (leftoverLength > PendingCapacity)
            {
                // 畸形流（无边界）：只保留尾部，避免无界增长，并标记需要重切（调用方应放弃该流）。
                var tail = new byte[PendingCapacity];
                Array.Copy(buffer, buffer.Length - PendingCapacity, tail, 0, PendingCapacity);
                pending = tail;
            }
            else
            {
                pending = new byte[leftoverLength];
                Array.Copy(buffer, start, pending, 0, leftoverLength);
            }
        }
        else
        {
            pending = null;
        }

        return frames;
    }

    /// <summary>
    /// 查找从 start 开始的下一个 SSE 帧结束边界（"\n\n" 或 "\r\n\r\n"），返回边界的最后字节下标；未找到返回 -1。
    /// </summary>
    private static int IndexOfBoundaryEnd(byte[] buffer, int start)
    {
        for (var i = start; i < buffer.Length; i++)
        {
            if (buffer[i] == (byte)'\n')
            {
                if (i + 1 < buffer.Length && buffer[i + 1] == (byte)'\n')
                {
                    return i + 1;
                }

                // "\r\n\r\n"：当前位置的 '\n' 是第一个 \r\n 的结尾（前一个字节为 \r 时）。
                if (i > start && buffer[i - 1] == (byte)'\r'
                    && i + 2 < buffer.Length && buffer[i + 1] == (byte)'\r' && buffer[i + 2] == (byte)'\n')
                {
                    return i + 2;
                }
            }
        }

        return -1;
    }
}
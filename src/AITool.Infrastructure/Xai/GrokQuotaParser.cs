using System.Text;

namespace AITool.Infrastructure.Xai;

/// <summary>
/// Grok 账单 gRPC-web 响应解析器（移植自 cc-switch subscription_grok.rs，后者移植自 CodexBar）。
/// <para>
/// 端点 <c>https://grok.com/grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig</c> 无公开 .proto，
/// 用通用 protobuf 扫描按字段路径启发式提取已用百分比与重置时间：
/// - 百分比：wire-type 5 (fixed32/float) 中路径末段为 1、值域 [0,100] 的字段，取路径最浅、出现最早；
/// - 重置时间：varint 中值落在合理 Unix 秒区间 [1.7e9, 2.1e9] 且晚于当前时刻，优先精确路径 [1,5,1]；
/// - 零用量特判：proto3 省略值为 0 的 percent 字段，此时若存在重置时间和用量周期标记
///   （路径 [1,6,*] 或 [1,8,1]=1/2），按 0% 处理。
/// </para>
/// </summary>
public static class GrokQuotaParser
{
    /// <summary>账单快照：已用百分比（0-100）与重置时间（Unix 秒，可能为 null）。</summary>
    public sealed record BillingSnapshot(double UsedPercent, long? ResetsAtSeconds);

    /// <summary>
    /// 解析 gRPC-web 响应字节。无法定位用量百分比时抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public static BillingSnapshot Parse(byte[] data, DateTimeOffset now)
        => Parse(data, now.ToUnixTimeSeconds());

    /// <summary>内部重载：以 Unix 秒为基准时间（测试可注入固定时钟）。</summary>
    public static BillingSnapshot Parse(byte[] data, long nowSeconds)
    {
        var payloads = GrpcWebDataFrames(data);
        if (payloads.Count == 0 && LooksLikeProtobufPayload(data))
        {
            payloads = [data];
        }

        if (payloads.Count == 0)
        {
            throw new InvalidOperationException("Grok billing response contained no protobuf payload");
        }

        var scan = new ProtobufScan();
        var order = 0;
        foreach (var payload in payloads)
        {
            // 与 CodexBar 一致：fixed32 序号在每个顶层 data 帧内独立从 0 计数。
            order = ScanProtobuf(payload, 0, [], order, scan);
        }

        var parsedPercent = scan.Fixed32Fields
            .Where(f => f.Path[^1] == 1 && float.IsFinite(f.Value) && f.Value >= 0f && f.Value <= 100f)
            .OrderBy(f => f.Path.Count)
            .ThenBy(f => f.Order)
            .Select(f => (double?)f.Value)
            .FirstOrDefault();

        var resetCandidates = scan.VarintFields
            .Where(v => v.Value is >= 1_700_000_000 and <= 2_100_000_000)
            .Select(v => (Path: v.Path, Ts: (long)v.Value))
            .Where(c => c.Ts > nowSeconds)
            .ToList();

        // 优先精确路径 [1,5,1]（嵌套消息 1 → 5 → 1），否则取最近的未来时间。
        long? reset = resetCandidates
            .Where(c => c.Path.Count == 3 && c.Path[0] == 1 && c.Path[1] == 5 && c.Path[2] == 1)
            .Select(c => c.Ts)
            .DefaultIfEmpty(0)
            .Min() is var pathReset && pathReset > 0
                ? pathReset
                : resetCandidates.Count > 0 ? resetCandidates.Min(c => c.Ts) : null;

        var hasUsagePeriod = scan.VarintFields.Any(v =>
            (v.Path.Count >= 2 && v.Path[0] == 1 && v.Path[1] == 6)
            || (v.Path.Count == 3 && v.Path[0] == 1 && v.Path[1] == 8 && v.Path[2] == 1 && (v.Value == 1 || v.Value == 2)));

        var noUsageYet = parsedPercent is null
            && scan.Fixed32Fields.Count == 0
            && reset is not null
            && hasUsagePeriod;

        var usedPercent = parsedPercent ?? (noUsageYet ? 0.0 : (double?)null)
            ?? throw new InvalidOperationException("Could not locate usage percent in Grok billing response");

        return new BillingSnapshot(Math.Clamp(usedPercent, 0d, 100d), reset);
    }

    /// <summary>按重置距离命名窗口（cc-switch tier_name_for_reset 的中文口径）。</summary>
    public static string DescribeWindow(long? resetsAtSeconds, long nowSeconds)
    {
        if (resetsAtSeconds is { } ts)
        {
            var days = (int)Math.Round((ts - nowSeconds) / 86400.0);
            if (days is >= 4 and <= 12)
            {
                return "每周额度";
            }
            if (days is >= 20 and <= 45)
            {
                return "月度额度";
            }
        }
        return "Credits 额度";
    }

    private sealed record Fixed32Field(IReadOnlyList<ulong> Path, float Value, int Order);

    private sealed record VarintField(IReadOnlyList<ulong> Path, ulong Value);

    private sealed class ProtobufScan
    {
        public List<Fixed32Field> Fixed32Fields { get; } = [];
        public List<VarintField> VarintFields { get; } = [];
    }

    /// <summary>读取 varint（LEB128）；越界或截断返回 null。</summary>
    private static ulong? ReadVarint(byte[] bytes, ref int index)
    {
        ulong value = 0;
        var shift = 0;
        while (index < bytes.Length && shift < 64)
        {
            var byteValue = bytes[index];
            index++;
            value |= (ulong)(byteValue & 0x7F) << shift;
            if ((byteValue & 0x80) == 0)
            {
                return value;
            }
            shift += 7;
        }
        return null;
    }

    /// <summary>
    /// 递归扫描 protobuf 消息，收集 varint 与 fixed32 字段。
    /// 无 .proto 定义，length-delimited 字段一律当嵌套消息试扫（深度 ≤4）；
    /// 无法解析的字节从字段起点 +1 重新同步。返回下一个 fixed32 序号。
    /// </summary>
    private static int ScanProtobuf(byte[] bytes, int depth, ulong[] path, int order, ProtobufScan scan)
    {
        var index = 0;
        var nextOrder = order;

        while (index < bytes.Length)
        {
            var fieldStart = index;
            var key = ReadVarint(bytes, ref index);
            if (key is null || key == 0)
            {
                index = fieldStart + 1;
                continue;
            }

            var fieldNumber = key.Value >> 3;
            var wireType = key.Value & 0x07;
            var fieldPath = new ulong[path.Length + 1];
            Array.Copy(path, fieldPath, path.Length);
            fieldPath[path.Length] = fieldNumber;

            switch (wireType)
            {
                case 0:
                    {
                        var value = ReadVarint(bytes, ref index);
                        if (value is not null)
                        {
                            scan.VarintFields.Add(new VarintField(fieldPath, value.Value));
                        }
                        else
                        {
                            index = fieldStart + 1;
                        }
                        break;
                    }
                case 1:
                    if (index + 8 > bytes.Length)
                    {
                        return nextOrder;
                    }
                    index += 8;
                    break;
                case 2:
                    {
                        var length = ReadVarint(bytes, ref index);
                        if (length is null || length.Value > (ulong)(bytes.Length - index))
                        {
                            index = fieldStart + 1;
                            continue;
                        }
                        var end = index + (int)length.Value;
                        if (depth < 4)
                        {
                            nextOrder = ScanProtobuf(bytes.AsSpan(index, end - index).ToArray(), depth + 1, fieldPath, nextOrder, scan);
                        }
                        index = end;
                        break;
                    }
                case 5:
                    if (index + 4 > bytes.Length)
                    {
                        return nextOrder;
                    }
                    var bits = BitConverter.ToUInt32([bytes[index], bytes[index + 1], bytes[index + 2], bytes[index + 3]], 0);
                    scan.Fixed32Fields.Add(new Fixed32Field(fieldPath, BitConverter.UInt32BitsToSingle(bits), nextOrder));
                    nextOrder++;
                    index += 4;
                    break;
                default:
                    index = fieldStart + 1;
                    break;
            }
        }

        return nextOrder;
    }

    /// <summary>拆出 gRPC-web data 帧（flags 高位 0x80 的 trailer 帧跳过）；任一帧非法返回空。</summary>
    private static List<byte[]> GrpcWebDataFrames(byte[] data)
    {
        var frames = new List<byte[]>();
        var index = 0;
        while (index < data.Length)
        {
            if (index + 5 > data.Length)
            {
                return [];
            }
            var flags = data[index];
            var length = (data[index + 1] << 24) | (data[index + 2] << 16) | (data[index + 3] << 8) | data[index + 4];
            var start = index + 5;
            var end = start + length;
            if (end > data.Length)
            {
                return [];
            }
            if ((flags & 0x80) == 0)
            {
                frames.Add(data[start..end]);
            }
            index = end;
        }
        return frames;
    }

    /// <summary>响应体没有帧头时，看首字节是否像合法 protobuf tag（部分成功请求直接返回裸 protobuf）。</summary>
    private static bool LooksLikeProtobufPayload(byte[] data)
    {
        if (data.Length == 0)
        {
            return false;
        }
        var first = data[0];
        var fieldNumber = first >> 3;
        var wireType = first & 0x07;
        return fieldNumber > 0 && wireType is 0 or 1 or 2 or 5;
    }

    /// <summary>从 trailer 帧（flags &amp; 0x80）解析 grpc-status / grpc-message 字段。</summary>
    public static Dictionary<string, string> ParseGrpcWebTrailers(byte[] data)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        while (index + 5 <= data.Length)
        {
            var flags = data[index];
            var length = (data[index + 1] << 24) | (data[index + 2] << 16) | (data[index + 3] << 8) | data[index + 4];
            var start = index + 5;
            var end = start + length;
            if (end > data.Length)
            {
                break;
            }
            if ((flags & 0x80) != 0)
            {
                var text = Encoding.UTF8.GetString(data[start..end]);
                foreach (var line in text.Split('\n').Where(l => l.Length > 0))
                {
                    var separator = line.IndexOf(':');
                    if (separator > 0)
                    {
                        fields[line[..separator].Trim()] = PercentDecode(line[(separator + 1)..].Trim());
                    }
                }
            }
            index = end;
        }
        return fields;
    }

    /// <summary>gRPC message 使用 percent-encoding；解码失败的原样保留。</summary>
    public static string PercentDecode(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var output = new List<byte>(bytes.Length);
        var i = 0;
        while (i < bytes.Length)
        {
            if (bytes[i] == 0x25 && i + 2 < bytes.Length)
            {
                var hex = Encoding.UTF8.GetString(bytes, i + 1, 2);
                if (byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var decoded))
                {
                    output.Add(decoded);
                    i += 3;
                    continue;
                }
            }
            output.Add(bytes[i]);
            i++;
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}

using AITool.Infrastructure.Xai;
using FluentAssertions;
using Xunit;

namespace AITool.ApplicationTests.Xai;

/// <summary>
/// Grok 账单 gRPC-web protobuf 启发式解析测试。
/// 用例场景对齐 cc-switch subscription_grok.rs 的测试（无官方 .proto，靠字段路径启发式）。
/// </summary>
public sealed class GrokQuotaParserTests
{
    private const long Now = 1_750_000_000;

    // ── protobuf 编码辅助（测试内最小实现） ──────────────────────

    private static byte[] EncodeVarint(ulong value)
    {
        var bytes = new List<byte>();
        var v = value;
        while (v >= 0x80)
        {
            bytes.Add((byte)((v & 0x7F) | 0x80));
            v >>= 7;
        }
        bytes.Add((byte)v);
        return [.. bytes];
    }

    private static byte[] Tag(uint field, uint wireType) => EncodeVarint((field << 3) | wireType);

    private static byte[] VarintField(uint field, ulong value)
        => [.. Tag(field, 0), .. EncodeVarint(value)];

    private static byte[] Float32Field(uint field, float value)
        => [.. Tag(field, 5), .. BitConverter.GetBytes(value)];

    private static byte[] LenDelimField(uint field, byte[] payload)
        => [.. Tag(field, 2), .. EncodeVarint((ulong)payload.Length), .. payload];

    private static byte[] GrpcFrame(byte[] payload, byte flags = 0x00)
        => [flags, .. BitConverter.GetBytes((uint)payload.Length).Reverse().ToArray(), .. payload];

    [Fact]
    public void Parse_extracts_percent_and_exact_path_reset_time()
    {
        // 结构：1 → { 1: float 42.0（百分比）, 5 → { 1: varint 重置时间 } }。
        var inner = new List<byte>();
        inner.AddRange(Float32Field(1, 42.0f));
        inner.AddRange(LenDelimField(5, VarintField(1, (ulong)Now + 86_400)));

        var payload = LenDelimField(1, [.. inner]);
        var snapshot = GrokQuotaParser.Parse(GrpcFrame(payload), Now);

        snapshot.UsedPercent.Should().Be(42.0);
        snapshot.ResetsAtSeconds.Should().Be(Now + 86_400);
    }

    [Fact]
    public void Parse_prefers_exact_reset_path_over_earlier_candidate()
    {
        // 两个未来时间候选：[1,5,1]=T+7200（精确路径）与 [2,1]=T+3600（更早但路径不符），
        // 启发式优先精确路径 [1,5,1]。
        var inner = new List<byte>();
        inner.AddRange(Float32Field(1, 10.0f));
        inner.AddRange(LenDelimField(5, VarintField(1, (ulong)Now + 7_200)));
        inner.AddRange(LenDelimField(2, VarintField(1, (ulong)Now + 3_600)));

        var payload = LenDelimField(1, [.. inner]);
        var snapshot = GrokQuotaParser.Parse(GrpcFrame(payload), Now);

        snapshot.ResetsAtSeconds.Should().Be(Now + 7_200);
    }

    [Fact]
    public void Parse_ignores_past_and_out_of_range_timestamps()
    {
        // 过去时间与超范围值（>2.1e9）不作为重置时间候选；无其他候选时 reset 为 null。
        var inner = new List<byte>();
        inner.AddRange(Float32Field(1, 50.0f));
        inner.AddRange(LenDelimField(5, VarintField(1, (ulong)Now - 3_600)));
        inner.AddRange(LenDelimField(2, VarintField(1, 9_999_999_999)));

        var payload = LenDelimField(1, [.. inner]);
        var snapshot = GrokQuotaParser.Parse(GrpcFrame(payload), Now);

        snapshot.UsedPercent.Should().Be(50.0);
        snapshot.ResetsAtSeconds.Should().BeNull();
    }

    [Fact]
    public void Parse_zero_usage_with_period_marker_treats_as_zero_percent()
    {
        // proto3 省略 0 值 float：无 fixed32 字段、有重置时间 + 用量周期标记（[1,6,*]）→ 0%。
        var usage = LenDelimField(6, VarintField(1, 1));
        var reset = LenDelimField(5, VarintField(1, (ulong)Now + 86_400));
        var payload = LenDelimField(1, [.. usage, .. reset]);

        var snapshot = GrokQuotaParser.Parse(GrpcFrame(payload), Now);

        snapshot.UsedPercent.Should().Be(0.0);
        snapshot.ResetsAtSeconds.Should().Be(Now + 86_400);
    }

    [Fact]
    public void Parse_keeps_in_range_percent_as_is()
    {
        // 值域内的百分比忠实提取（夹取只作用于候选筛选，候选必在 0-100）。
        var payload = LenDelimField(1, Float32Field(1, 99.5f));
        var snapshot = GrokQuotaParser.Parse(GrpcFrame(payload), Now);
        snapshot.UsedPercent.Should().Be(99.5);
    }

    [Fact]
    public void Parse_out_of_range_floats_are_not_percent_candidates()
    {
        // 值域外的 float（150/-5）不算候选；仅有它们时应报「无法定位」。
        var payload = LenDelimField(1, Float32Field(1, 150.0f));
        var act = () => GrokQuotaParser.Parse(GrpcFrame(payload), Now);
        act.Should().Throw<InvalidOperationException>().WithMessage("*usage percent*");
    }

    [Fact]
    public void Parse_throws_on_non_protobuf_payload()
    {
        var act = () => GrokQuotaParser.Parse("not protobuf at all"u8.ToArray(), Now);
        act.Should().Throw<InvalidOperationException>().WithMessage("*protobuf*");
    }

    [Fact]
    public void Parse_scans_multiple_data_frames_with_independent_order()
    {
        // 两个 data 帧：序号计数在每帧内独立；取最浅路径的百分比（第二帧的 [1]=30%）。
        var frame1 = GrpcFrame(LenDelimField(1, LenDelimField(1, Float32Field(1, 42.0f))));
        var frame2 = GrpcFrame(Float32Field(1, 30.0f));

        var bytes = new List<byte>();
        bytes.AddRange(frame1);
        bytes.AddRange(frame2);

        var snapshot = GrokQuotaParser.Parse([.. bytes], Now);
        snapshot.UsedPercent.Should().Be(30.0);
    }

    [Fact]
    public void ParseGrpcWebTrailers_extracts_status_and_percent_decoded_message()
    {
        // trailer 帧（flags=0x80）：grpc-status: 5、grpc-message: %20hello%22（percent 解码）。
        var trailerText = "grpc-status: 5\r\ngrpc-message: %20hello%22";
        var bytes = new List<byte>();
        bytes.AddRange(GrpcFrame([0x01], flags: 0x00));
        bytes.Add(0x80);
        bytes.AddRange(BitConverter.GetBytes((uint)System.Text.Encoding.UTF8.GetByteCount(trailerText)).Reverse().ToArray());
        bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(trailerText));

        var trailers = GrokQuotaParser.ParseGrpcWebTrailers([.. bytes]);
        trailers["grpc-status"].Should().Be("5");
        trailers["grpc-message"].Should().Be(" hello\"");
    }

    [Fact]
    public void DescribeWindow_maps_reset_distance_to_labels()
    {
        GrokQuotaParser.DescribeWindow(Now + 7 * 86_400, Now).Should().Be("每周额度");
        GrokQuotaParser.DescribeWindow(Now + 30 * 86_400, Now).Should().Be("月度额度");
        GrokQuotaParser.DescribeWindow(Now + 86_400, Now).Should().Be("Credits 额度");
        GrokQuotaParser.DescribeWindow(null, Now).Should().Be("Credits 额度");
    }
}

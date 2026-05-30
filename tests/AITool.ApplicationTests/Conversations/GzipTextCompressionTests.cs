using AITool.Infrastructure.Conversations;
using FluentAssertions;

namespace AITool.ApplicationTests.Conversations;

/// <summary>
/// 验证 GZip 文本压缩和解压逻辑。
/// </summary>
public sealed class GzipTextCompressionTests
{
    [Fact]
    public void Short_text_is_not_compressed()
    {
        var text = "短文本不压缩";
        GzipTextCompression.Compress(text).Should().Be(text);
    }

    [Fact]
    public void Long_text_is_compressed_and_decompresses_to_original()
    {
        var text = new string('A', 2000) + new string('B', 2000);
        var compressed = GzipTextCompression.Compress(text);

        // 压缩后应该比原文短
        compressed.Length.Should().BeLessThan(text.Length);

        // 压缩后应该有 gzip 前缀
        compressed.Should().StartWith("gzip:");

        // 解压后应该和原文一致
        GzipTextCompression.Decompress(compressed).Should().Be(text);
    }

    [Fact]
    public void Compress_decompress_preserves_chinese_content()
    {
        var text = string.Join("\n", Enumerable.Repeat("这是一段用于测试压缩的中文文本，包含代码片段：```csharp\nConsole.WriteLine(\"hello\");\n```", 20));
        var compressed = GzipTextCompression.Compress(text);
        GzipTextCompression.Decompress(compressed).Should().Be(text);
    }

    [Fact]
    public void Null_and_empty_are_passed_through()
    {
        GzipTextCompression.Compress(null!).Should().BeNull();
        GzipTextCompression.Compress(string.Empty).Should().BeEmpty();
        GzipTextCompression.Decompress(null!).Should().BeNull();
        GzipTextCompression.Decompress(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Decompress_plain_text_returns_as_is()
    {
        var plain = "这是明文，没有压缩过";
        GzipTextCompression.Decompress(plain).Should().Be(plain);
    }

    [Fact]
    public void Compressed_output_is_much_smaller_for_repetitive_content()
    {
        var text = new string('X', 10000);
        var compressed = GzipTextCompression.Compress(text);
        // 10000 个相同字符压缩后应该非常小
        compressed.Length.Should().BeLessThan(200);
    }
}

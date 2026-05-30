using System.IO.Compression;
using System.Text;

namespace AITool.Infrastructure.Conversations;

/// <summary>
/// 对长文本做 GZip 压缩和解压，减少 SQLite 大文本字段的存储压力。
/// 压缩后以 Base64 编码存入数据库，解压时自动检测是否为压缩数据。
/// </summary>
public static class GzipTextCompression
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    /// <summary>
    /// 压缩阈值（字节），低于此长度的文本不值得压缩，直接存原文。
    /// </summary>
    private const int CompressThresholdBytes = 512;

    /// <summary>
    /// 压缩前缀标记，用于区分压缩数据和明文。
    /// </summary>
    private const string CompressedPrefix = "gzip:";

    /// <summary>
    /// 压缩文本。短文本直接返回原文，长文本返回 "gzip:" + Base64 编码的压缩数据。
    /// </summary>
    public static string Compress(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var rawBytes = Utf8NoBom.GetBytes(text);
        if (rawBytes.Length < CompressThresholdBytes)
        {
            return text;
        }

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(rawBytes);
        }

        return CompressedPrefix + Convert.ToBase64String(output.ToArray());
    }

    /// <summary>
    /// 解压文本。如果数据以 "gzip:" 开头则解压，否则直接返回原文。
    /// </summary>
    public static string Decompress(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.StartsWith(CompressedPrefix, StringComparison.Ordinal))
        {
            return text;
        }

        var base64 = text[CompressedPrefix.Length..];
        var compressedBytes = Convert.FromBase64String(base64);
        using var input = new MemoryStream(compressedBytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Utf8NoBom);
        return reader.ReadToEnd();
    }
}

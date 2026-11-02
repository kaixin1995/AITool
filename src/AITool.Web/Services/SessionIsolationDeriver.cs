using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AITool.Domain.Operations;

namespace AITool.Web.Services;

/// <summary>
/// Versioned HMAC session identifiers, scoped by client and provider/account identity.
/// No token, route, circuit, workspace, or request state participates in derivation.
/// This is a foundation only; it does not rewrite or isolate any forwarded request.
/// </summary>
public sealed class SessionIsolationDeriver(SessionIsolationKeyStore keyStore)
{
    // Bound work and memory for hostile inputs, without truncation/normalization collisions.
    public const int MaxFieldUtf8Bytes = 1024 * 1024;
    private static readonly UTF8Encoding ExactUtf8 = new(false, true);

    /// <summary>Returns a fixed 64-character lowercase hexadecimal HMAC-SHA256.</summary>
    public async Task<string> DeriveAsync(string clientScope, string providerKey, Guid accountId,
        string fieldDomain, string seed, CancellationToken ct = default)
    {
        var digest = await DeriveDigestAsync(clientScope, providerKey, accountId, fieldDomain, seed, ct);
        try
        {
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    /// <summary>Google-compatible invariant decimal in [1, Int64.MaxValue], at most 19 digits.</summary>
    public async Task<string> DerivePositiveInt63Async(string clientScope, string providerKey, Guid accountId,
        string fieldDomain, string seed, CancellationToken ct = default)
    {
        var digest = await DeriveDigestAsync(clientScope, providerKey, accountId, fieldDomain, seed, ct);
        try
        {
            return FormatPositiveInt63(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private async Task<byte[]> DeriveDigestAsync(string clientScope, string providerKey, Guid accountId,
        string fieldDomain, string seed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(clientScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldDomain);
        ArgumentNullException.ThrowIfNull(seed);
        if (accountId == Guid.Empty)
            throw new ArgumentException("A concrete account identity is required.", nameof(accountId));

        // v1 wire encoding: six ordered fields, each a 4-byte unsigned big-endian byte
        // length followed by exact strict UTF-8. Version is decimal invariant; GUID is D.
        // Strings are NOT trimmed, case-folded, normalized, truncated, or concatenated.
        string[] fields = [SessionIsolationKey.CurrentVersion.ToString(CultureInfo.InvariantCulture),
            clientScope, providerKey, accountId.ToString("D"), fieldDomain, seed];
        // Six fields: version, clientScope, accountProvider, accountGuid, fieldDomain, originalSeed.
        foreach (var field in fields)
        {
            if (field.Length > MaxFieldUtf8Bytes || ExactUtf8.GetByteCount(field) > MaxFieldUtf8Bytes)
                throw new ArgumentException("A session isolation field exceeds the UTF-8 size limit.");
        }

        var key = await keyStore.ReadKeyAsync(ct);
        try
        {
            return ComputeDigest(key, fields, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] ComputeDigest(byte[] key, string[] fields, CancellationToken ct)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        Span<byte> length = stackalloc byte[4];
        Span<byte> buffer = stackalloc byte[4096];
        try
        {
            foreach (var field in fields)
            {
                ct.ThrowIfCancellationRequested();
                BinaryPrimitives.WriteUInt32BigEndian(length, (uint)ExactUtf8.GetByteCount(field));
                hmac.AppendData(length);
                var encoder = ExactUtf8.GetEncoder();
                var remaining = field.AsSpan();
                bool completed;
                do
                {
                    ct.ThrowIfCancellationRequested();
                    encoder.Convert(remaining, buffer, true, out var charsUsed, out var bytesUsed, out completed);
                    hmac.AppendData(buffer[..bytesUsed]);
                    remaining = remaining[charsUsed..];
                } while (!completed);
            }
            ct.ThrowIfCancellationRequested();
            return hmac.GetHashAndReset();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static string FormatPositiveInt63(byte[] digest)
    {
        var value = BinaryPrimitives.ReadUInt64BigEndian(digest) & 0x7fff_ffff_ffff_ffffUL;
        return (value == 0 ? 1UL : value).ToString(CultureInfo.InvariantCulture);
    }
}

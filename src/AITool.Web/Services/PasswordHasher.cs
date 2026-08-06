using System.Security.Cryptography;
using System.Text;

namespace AITool.Web.Services;

/// <summary>
/// 后台登录密码的加盐哈希与校验。
/// <para>
/// 采用 PBKDF2-HMAC-SHA256（.NET 内置 <see cref="System.Security.Cryptography.Rfc2898DeriveBytes"/>），
/// 替代原无盐 MD5。哈希字符串格式：<c>pbkdf2${iterations}${saltBase64}${hashBase64}</c>。
/// </para>
/// <para>
/// 校验时双格式兼容：识别 <c>pbkdf2$</c> 前缀走新算法；否则按旧 MD5（小写 hex）校验，
/// 用于平滑迁移老配置。旧 MD5 校验成功后由调用方触发透明升级。
/// </para>
/// </summary>
public static class PasswordHasher
{
    /// <summary>
    /// 新哈希格式的前缀，用于与旧 MD5（32 位 hex，无分隔符）区分。
    /// </summary>
    public const string Prefix = "pbkdf2$";

    /// <summary>
    /// 默认迭代次数（OWASP 2023 推荐 PBKDF2-HMAC-SHA256 ≥ 600000，这里取 100000 平衡桌面部署的 CPU 开销）。
    /// </summary>
    private const int DefaultIterations = 100_000;

    /// <summary>
    /// 盐长度（字节）。
    /// </summary>
    private const int SaltBytes = 16;

    /// <summary>
    /// 派生哈希长度（字节），256 位。
    /// </summary>
    private const int HashBytes = 32;

    /// <summary>
    /// 计算密码的 PBKDF2 加盐哈希字符串。
    /// </summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            DefaultIterations,
            HashAlgorithmName.SHA256,
            HashBytes);

        return $"{Prefix}{DefaultIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// 校验密码是否与存储的哈希匹配。
    /// 自动识别新旧两种格式：pbkdf2$ 走新算法，否则按旧 MD5 处理。
    /// </summary>
    /// <param name="password">用户输入的明文密码。</param>
    /// <param name="storedHash">存储的哈希字符串（新 pbkdf2$ 格式或旧 MD5 hex）。</param>
    /// <param name="needsUpgrade">输出：当且仅当存储的是旧 MD5 且校验通过时为 true，提示调用方透明升级。</param>
    /// <returns>校验是否通过。</returns>
    public static bool Verify(string password, string storedHash, out bool needsUpgrade)
    {
        needsUpgrade = false;
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        if (storedHash.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return VerifyPbkdf2(password, storedHash);
        }

        // 旧格式：无盐 MD5（小写 hex，32 位）。
        if (VerifyLegacyMd5(password, storedHash))
        {
            needsUpgrade = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 校验 pbkdf2$ 格式哈希。格式非法时返回 false（不抛异常，避免泄漏细节）。
    /// </summary>
    private static bool VerifyPbkdf2(string password, string storedHash)
    {
        var parts = storedHash[Prefix.Length..].Split('$');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var iterations)
            || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expectedHash = Convert.FromBase64String(parts[2]);
        }
        catch
        {
            return false;
        }

        if (expectedHash.Length == 0)
        {
            return false;
        }

        var actualHash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expectedHash.Length);

        // 定长比较，防时序攻击。
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    /// <summary>
    /// 校验旧 MD5（小写 hex）。保留与原 AdminAuthService.ComputeMd5 一致的算法。
    /// </summary>
    private static bool VerifyLegacyMd5(string password, string storedHash)
    {
        var actual = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();
        var expectedBytes = Encoding.UTF8.GetBytes(storedHash.ToLowerInvariant());
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}

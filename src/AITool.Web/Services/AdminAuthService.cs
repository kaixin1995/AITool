using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace AITool.Web.Services;

/// <summary>
/// 提供后台登录密码的读取、校验和初始化写入能力。
/// </summary>
public sealed class AdminAuthService
{
    /// <summary>
    /// 当前应用配置对象，用于读取密码哈希并支持热重载。
    /// </summary>
    private readonly IConfiguration _configuration;

    /// <summary>
    /// appsettings.json 文件路径，首次设置密码时会直接写回此文件。
    /// </summary>
    private readonly string _appSettingsPath;

    /// <summary>
    /// 初始化后台认证服务。
    /// </summary>
    public AdminAuthService(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _configuration = configuration;
        _appSettingsPath = Path.Combine(environment.ContentRootPath, "appsettings.json");
    }

    /// <summary>
    /// 判断当前是否已经配置后台登录密码。
    /// </summary>
    public bool HasPasswordConfigured()
    {
        // 直接读取当前配置中的密码哈希，便于配合配置热重载即时生效。
        return !string.IsNullOrWhiteSpace(GetPasswordHash());
    }

    /// <summary>
    /// 校验输入密码是否与配置中的哈希值一致。
    /// </summary>
    public bool VerifyPassword(string password)
    {
        var passwordHash = GetPasswordHash();
        if (string.IsNullOrWhiteSpace(passwordHash) || string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        return string.Equals(passwordHash, ComputeMd5(password), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 首次设置后台密码，并将哈希值写入配置文件。
    /// </summary>
    public async Task SetPasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("密码不能为空");
        }

        // 这里直接修改 appsettings.json，方便桌面部署场景下立即生效。
        var rootNode = JsonNode.Parse(await File.ReadAllTextAsync(_appSettingsPath, cancellationToken))?.AsObject()
            ?? new JsonObject();
        var authNode = rootNode[AdminAuthOptions.SectionName] as JsonObject ?? new JsonObject();
        authNode[nameof(AdminAuthOptions.PasswordHash)] = ComputeMd5(password);
        rootNode[AdminAuthOptions.SectionName] = authNode;

        var json = rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_appSettingsPath, json, Encoding.UTF8, cancellationToken);

        if (_configuration is IConfigurationRoot configurationRoot)
        {
            configurationRoot.Reload();
        }
    }

    /// <summary>
    /// 从当前配置中读取后台密码哈希。
    /// </summary>
    private string GetPasswordHash()
    {
        return _configuration.GetSection(AdminAuthOptions.SectionName)[nameof(AdminAuthOptions.PasswordHash)]?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// 计算字符串的 MD5 值，并按小写十六进制输出。
    /// </summary>
    private static string ComputeMd5(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hashBytes = MD5.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AITool.Application.Common;
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
    /// 自动兼容新旧两种格式：pbkdf2$（新）和无盐 MD5（旧）。
    /// </summary>
    public bool VerifyPassword(string password)
    {
        return VerifyPassword(password, out _);
    }

    /// <summary>
    /// 校验输入密码是否与配置中的哈希值一致，并指示是否需要透明升级（旧 MD5 → PBKDF2）。
    /// 调用方（如 AuthApiController）在校验通过且 <paramref name="needsUpgrade"/> 为 true 时，
    /// 应调用 <see cref="UpgradePasswordAsync"/> 用同一明文密码重算 PBKDF2 写回。
    /// </summary>
    public bool VerifyPassword(string password, out bool needsUpgrade)
    {
        var passwordHash = GetPasswordHash();
        if (string.IsNullOrWhiteSpace(passwordHash) || string.IsNullOrWhiteSpace(password))
        {
            needsUpgrade = false;
            return false;
        }

        return PasswordHasher.Verify(password, passwordHash, out needsUpgrade);
    }

    /// <summary>
    /// 用明文密码重新计算 PBKDF2 哈希并写回配置（透明升级旧 MD5）。
    /// 仅在 <see cref="VerifyPassword(string, out bool)"/> 返回 true 且 needsUpgrade=true 时调用。
    /// </summary>
    public Task UpgradePasswordAsync(string password, CancellationToken cancellationToken = default)
        => SetPasswordAsync(password, cancellationToken);

    /// <summary>
    /// 首次设置后台密码，并将哈希值写入配置文件（PBKDF2 加盐格式）。
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
        authNode[nameof(AdminAuthOptions.PasswordHash)] = PasswordHasher.Hash(password);
        rootNode[AdminAuthOptions.SectionName] = authNode;

        var json = rootNode.ToJsonString(JsonSerializerPresets.WriteIndented);
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
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace AITool.Web.Services;

public sealed class AdminAuthService
{
    private readonly IConfiguration _configuration;
    private readonly string _appSettingsPath;

    public AdminAuthService(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _configuration = configuration;
        _appSettingsPath = Path.Combine(environment.ContentRootPath, "appsettings.json");
    }

    // 直接读取当前配置中的密码哈希，便于配合配置热重载即时生效。
    public bool HasPasswordConfigured()
    {
        return !string.IsNullOrWhiteSpace(GetPasswordHash());
    }

    public bool VerifyPassword(string password)
    {
        var passwordHash = GetPasswordHash();
        if (string.IsNullOrWhiteSpace(passwordHash) || string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        return string.Equals(passwordHash, ComputeMd5(password), StringComparison.OrdinalIgnoreCase);
    }

    // 首次设置密码时把 MD5 字符串直接写回 appsettings.json。
    public async Task SetPasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("密码不能为空");
        }

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

    private string GetPasswordHash()
    {
        return _configuration.GetSection(AdminAuthOptions.SectionName)[nameof(AdminAuthOptions.PasswordHash)]?.Trim() ?? string.Empty;
    }

    private static string ComputeMd5(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hashBytes = MD5.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}

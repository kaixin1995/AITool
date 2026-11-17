using System.Text.Json;

namespace AITool.Web.Services;

public sealed class GatewayEnhancementOptions
{
    public string Revision { get; set; } = "";
    public bool KeepAliveEnabled { get; set; }
    public int KeepAliveSeconds { get; set; } = 15;
    public bool LoopDetectionEnabled { get; set; }
    public bool ExtendedHeadBufferEnabled { get; set; }
    public int MetricsRetentionDays { get; set; } = 30;
    public List<GatewayKeyBudget> Budgets { get; set; } = [];
    public string SearchEndpoint { get; set; } = "";
    public string SearchApiKey { get; set; } = "";
    public string VisionRoute { get; set; } = "";
    public List<Guid> ToolAllowedKeyIds { get; set; } = [];
    public List<GatewayMcpServer> McpServers { get; set; } = [];
}

public sealed class GatewayKeyBudget
{
    public Guid KeyId { get; set; }
    public string Period { get; set; } = "day";
    public long TokenLimit { get; set; }
    public decimal CostLimitUsd { get; set; }
}

public sealed class GatewayMcpServer
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<Guid> AllowedKeyIds { get; set; } = [];
}

/// <summary>Atomic configuration snapshots; secrets are protected with the deployment's key ring.</summary>
public sealed class GatewayEnhancementSettings
{
    private readonly string _path;
    private readonly Microsoft.AspNetCore.DataProtection.IDataProtector _protector;
    private readonly SemaphoreSlim _gate = new(1);
    private GatewayEnhancementOptions _current;
    public string? LoadError { get; private set; }
    public GatewayEnhancementOptions Current => JsonSerializer.Deserialize<GatewayEnhancementOptions>(JsonSerializer.Serialize(_current))!;

    public GatewayEnhancementSettings(IWebHostEnvironment environment, Microsoft.AspNetCore.DataProtection.IDataProtectionProvider provider)
    {
        _path = environment.IsEnvironment("Testing") ? Path.Combine(Path.GetTempPath(), $"aitool-enhancements-{Guid.NewGuid():N}.json")
            : Path.Combine(environment.ContentRootPath, "gateway-enhancements.json");
        _protector = provider.CreateProtector("AITool.GatewayEnhancements.v1");
        try
        {
            _current = File.Exists(_path) ? JsonSerializer.Deserialize<GatewayEnhancementOptions>(Unprotect(File.ReadAllText(_path))) ?? throw new JsonException("Configuration is null") : new();
            Validate(_current);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or IOException or ArgumentException)
        {
            // Preserve the file for recovery. Unknown budget rules must not be silently disabled.
            LoadError = "网关增强配置无法读取，请恢复匹配的密钥圈或重新保存配置。";
            _current = new();
        }
    }

    private string Unprotect(string value) => Microsoft.AspNetCore.DataProtection.DataProtectionCommonExtensions.Unprotect(_protector, value);
    private static void Validate(GatewayEnhancementOptions options)
    {
        if (options.Budgets is null || options.Budgets.Any(b => b is null) || options.McpServers is null || options.McpServers.Any(s => s is null || s.AllowedKeyIds is null)
            || options.ToolAllowedKeyIds is null || options.SearchEndpoint is null || options.SearchApiKey is null || options.VisionRoute is null) throw new ArgumentException("配置字段不能为 null。");
        if (options.KeepAliveSeconds is < 5 or > 60 || options.MetricsRetentionDays is < 1 or > 365) throw new ArgumentException("保活间隔应为 5–60 秒，保留天数应为 1–365。");
        if (options.Budgets.Count > 256 || options.McpServers.Count > 32 || options.ToolAllowedKeyIds.Count > 256) throw new ArgumentException("最多配置 256 项预算、32 个 MCP 服务和 256 个工具访问密钥。");
        if (options.Budgets.Any(b => b.KeyId == Guid.Empty || b.TokenLimit < 0 || b.CostLimitUsd < 0 || b.Period is not ("day" or "week" or "month"))
            || options.Budgets.GroupBy(b => (b.KeyId, b.Period)).Any(g => g.Count() > 1)) throw new ArgumentException("预算配置无效或重复。");
        if (options.McpServers.Any(s => s.Url is null || s.ApiKey is null || string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 64 || s.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            || options.McpServers.GroupBy(s => s.Name).Any(g => g.Count() > 1)) throw new ArgumentException("MCP 名称仅允许字母、数字和下划线且不能重复。");
        if (options.McpServers.Any(s => s.Name.Contains("__", StringComparison.Ordinal))) throw new ArgumentException("MCP 服务名不能包含连续下划线。");
        if (options.McpServers.Any(s => s.Url.Length == 0)) throw new ArgumentException("MCP 服务地址不能为空。");
        foreach (var address in options.McpServers.Select(s => s.Url).Append(options.SearchEndpoint).Where(s => s.Length > 0))
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) throw new ArgumentException("服务地址必须是 HTTP 或 HTTPS，不能携带用户信息或片段。");
    }
    public async Task<GatewayEnhancementOptions> SaveAsync(GatewayEnhancementOptions options, CancellationToken ct, string? expectedRevision = null)
    {
        Validate(options);
        await _gate.WaitAsync(ct);
        try
        {
            if (expectedRevision is not null && expectedRevision != _current.Revision)
                throw new InvalidOperationException("配置已被其他页面修改，请刷新后重试。");
            options.Revision = Guid.NewGuid().ToString("N");
            var snapshot = JsonSerializer.Serialize(options);
            await File.WriteAllTextAsync(_path + ".tmp", Microsoft.AspNetCore.DataProtection.DataProtectionCommonExtensions.Protect(_protector, snapshot), ct);
            File.Move(_path + ".tmp", _path, true);
            _current = JsonSerializer.Deserialize<GatewayEnhancementOptions>(snapshot)!;
            LoadError = null;
            // Return this operation's snapshot while holding the lock, independently of later saves.
            return JsonSerializer.Deserialize<GatewayEnhancementOptions>(snapshot)!;
        }
        finally { _gate.Release(); }
    }
}

using System.Text.Json;
using AITool.Application.Codex;
using AITool.Application.Proxy;
using AITool.Infrastructure.Proxy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AITool.Infrastructure.Codex;

/// <summary>
/// Codex 客户端版本解析器实现。
/// <para>
/// 单一事实源是「请求头模板库」中 Key=CodexCli 档案的 User-Agent：转发链路的伪装头本来就取自
/// 该档案（BuildEffectiveExtraHeaders → CodexCli 模板 → 站点 ExtraHeaders 叠加），拉模型与查额度
/// 也从这里解析版本，保证三路版本一致。档案缺失/禁用/格式无法识别时回落
/// <see cref="CodexUpstreamOptions.ClientVersion"/> 配置兜底。
/// </para>
/// </summary>
public sealed class CodexClientVersionResolver : ICodexClientVersionResolver
{
    /// <summary>模板库中 Codex Desktop 官方客户端档案的 Key（与 ClientEmulationConstants.CodexCli 一致）。</summary>
    private const string CodexCliProfileKey = "CodexCli";

    /// <summary>兜底 User-Agent 模板（版本号取配置）。与 ClientEmulationEngine 的 CodexCli 预设保持同构。</summary>
    private const string FallbackUserAgentTemplate =
        "Codex Desktop/{0} (Windows 10.0.19045; x86_64) unknown (Codex Desktop; 26.818.61809)";

    private readonly IHeaderProfileCatalogService _catalogService;
    private readonly CodexUpstreamOptions _options;
    private readonly ILogger<CodexClientVersionResolver> _logger;

    public CodexClientVersionResolver(
        IHeaderProfileCatalogService catalogService,
        IOptions<CodexUpstreamOptions> options,
        ILogger<CodexClientVersionResolver> logger)
    {
        _catalogService = catalogService;
        _options = options?.Value ?? new CodexUpstreamOptions();
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CodexClientVersionInfo> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var fallbackVersion = string.IsNullOrWhiteSpace(_options.ClientVersion)
            ? "0.153.3"
            : _options.ClientVersion.Trim();

        try
        {
            var profile = await _catalogService.GetByKeyAsync(CodexCliProfileKey, cancellationToken);
            if (profile is { IsEnabled: true } && !string.IsNullOrWhiteSpace(profile.HeadersJson)
                && JsonSerializer.Deserialize<Dictionary<string, string>>(profile.HeadersJson) is { Count: > 0 } headers
                && headers.TryGetValue("User-Agent", out var userAgent)
                && !string.IsNullOrWhiteSpace(userAgent)
                && ClientVersionText.ExtractVersion(userAgent) is { Length: > 0 } version)
            {
                return new CodexClientVersionInfo
                {
                    ClientVersion = version,
                    UserAgent = userAgent.Trim(),
                    Source = $"profile:{profile.Key}"
                };
            }
        }
        catch (Exception ex)
        {
            // 模板库是用户可编辑的本地 JSON，坏数据不应影响拉模型/查额度，静默回落。
            _logger.LogWarning(ex, "Resolve Codex client version from header profile failed, falling back to config");
        }

        return new CodexClientVersionInfo
        {
            ClientVersion = fallbackVersion,
            UserAgent = string.Format(FallbackUserAgentTemplate, fallbackVersion),
            Source = "fallback"
        };
    }
}

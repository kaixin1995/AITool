using AITool.Application.CoreRuntime;
using AITool.Application.Networking;
using AITool.Application.Proxy;
using AITool.Domain.Operations;

namespace AITool.Core.Services;

/// <summary>
/// Core 快照形态的发送边界凭证复验（master 同步的双宿主适配）。
/// Admin/AllInOne 用 DB 形态的 <see cref="AITool.Admin.Services.ForwardCredentialResolver"/>；
/// Core 无数据库，站点所有权/托管凭证从配置快照复验，行为对齐 DB 版：
/// 无站点目标必须显式 AllowNonSiteTarget；托管站点要求 AccountIdentity 与快照凭证一致。
/// </summary>
public sealed class SnapshotForwardCredentialResolver(ICoreRuntimeConfigProvider configProvider)
    : IForwardCredentialResolver
{
    public Task<ForwardCredentialLease> ResolveAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
        if (request.SiteId is not { } siteId || siteId == Guid.Empty)
        {
            if (!request.AllowNonSiteTarget || request.AccountIdentity is not null
                || !string.IsNullOrWhiteSpace(request.ManagedSource))
                throw new NetworkPolicyException("forward_target_blocked", "非站点诊断目标未被显式允许。");
            return Task.FromResult(new ForwardCredentialLease(request.TargetApiKey));
        }

        var snapshot = configProvider.GetCurrent();
        var site = snapshot?.Sites.FirstOrDefault(s => s.Id == siteId);
        if (site is null || !site.IsEnabled)
            throw new NetworkPolicyException("forward_target_blocked", "目标站点不存在于当前配置快照。");
        if (!string.Equals(site.ManagedSource?.Trim() ?? string.Empty, request.ManagedSource?.Trim() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase))
            throw new NetworkPolicyException("forward_target_blocked", "目标站点的托管来源与请求不一致。");
        if (string.IsNullOrWhiteSpace(site.ManagedSource))
        {
            if (request.AccountIdentity is not null)
                throw new NetworkPolicyException("forward_target_blocked", "非托管站点不允许携带账号身份。");
            return Task.FromResult(new ForwardCredentialLease(request.TargetApiKey));
        }

        // 托管站点：要求账号身份与快照凭证段一致，取账号当前 access token（比路由候选捕获的 key 更新）。
        var identity = request.AccountIdentity;
        if (identity is null || identity.AccountId == Guid.Empty
            || !string.Equals(identity.ProviderKey, ProviderKeyFor(site.ManagedSource), StringComparison.OrdinalIgnoreCase))
            throw new NetworkPolicyException("forward_target_blocked", "托管站点转发缺少匹配的账号身份。");
        var credential = snapshot?.AccountCredentials.FirstOrDefault(c =>
            c.AccountId == identity.AccountId
            && string.Equals(c.Provider, ProviderKeyFor(site.ManagedSource), StringComparison.OrdinalIgnoreCase));
        if (credential is null || credential.LinkedSiteId != siteId || !credential.IsEnabled)
            throw new NetworkPolicyException("forward_target_blocked", "账号凭证与目标站点不匹配或未启用。");
        // 快照凭证段只带 refresh token；access token 随隐藏站点 ApiKey 下发（轮换时同步更新）。
        var accessToken = !string.IsNullOrWhiteSpace(site.ApiKey) ? site.ApiKey : request.TargetApiKey;
        return Task.FromResult(new ForwardCredentialLease(accessToken));
    }

    private static string ProviderKeyFor(string managedSource) => managedSource.Trim().ToLowerInvariant() switch
    {
        "codex" => "codex",
        "google" => "google",
        "kimi" or "kimi_oauth" => "kimi",
        "xai" => "xai",
        "device-oauth" or "device_oauth" => "device-oauth",
        _ => managedSource.Trim()
    };
}

/// <summary>
/// Core 快照形态的发送边界账号网络策略解析（master 同步的双宿主适配）。
/// 从快照 AccountNetworkPolicies 段读取三态；无策略/禁用按 Legacy。
/// ProxyProfile 模式使用 Admin 预解析的明文代理地址（带认证的代理凭证不进快照，属已知边界）。
/// </summary>
public sealed class SnapshotForwardNetworkPolicyResolver(ICoreRuntimeConfigProvider configProvider)
    : IForwardNetworkPolicyResolver
{
    public Task<ForwardNetworkPolicyResolution> ResolveAsync(ProxyForwardRequest request,
        CancellationToken cancellationToken = default)
    {
        var account = request.AccountIdentity;
        var context = request.NetworkContext;
        if (account is null)
        {
            // 对齐 master 语义：普通转发（无账号/网络上下文）走系统默认客户端（UseDefaultClient=true），
            // 客户端由 IHttpClientFactory 提供——测试可整体替换，生产沿用宿主默认处理器链。
            if (context is null)
                return Task.FromResult(new ForwardNetworkPolicyResolution(
                    ResolvedNetworkConfiguration.Legacy, "ordinary-forward", UseDefaultClient: true));
            return Task.FromResult(new ForwardNetworkPolicyResolution(ResolvedNetworkConfiguration.Legacy,
                context.IsolationKey ?? "ordinary-forward"));
        }
        var isolation = $"account:{account.ProviderKey}:{account.AccountId:N}";

        var policy = configProvider.GetCurrent()?.AccountNetworkPolicies.FirstOrDefault(p =>
            p.AccountId == account.AccountId
            && string.Equals(p.ProviderKey, account.ProviderKey, StringComparison.OrdinalIgnoreCase)
            && p.IsEnabled);
        if (policy is null)
            return Task.FromResult(new ForwardNetworkPolicyResolution(ResolvedNetworkConfiguration.Legacy, isolation));

        if (string.Equals(policy.Mode, nameof(NetworkMode.Direct), StringComparison.Ordinal))
            return Task.FromResult(new ForwardNetworkPolicyResolution(ResolvedNetworkConfiguration.Direct, isolation));

        if (string.Equals(policy.Mode, nameof(NetworkMode.ProxyProfile), StringComparison.Ordinal)
            && Uri.TryCreate(policy.ProxyUrl, UriKind.Absolute, out var endpoint))
            return Task.FromResult(new ForwardNetworkPolicyResolution(
                new ResolvedNetworkConfiguration { Mode = NetworkMode.ProxyProfile, ProxyEndpoint = endpoint },
                isolation));

        // ProxyProfile 解析失败（档案禁用/地址缺失）回退 Legacy，保持转发可用。
        return Task.FromResult(new ForwardNetworkPolicyResolution(ResolvedNetworkConfiguration.Legacy, isolation));
    }
}

/// <summary>
/// 设备 OAuth 托管来源常量的 Core 侧占位（与 Admin 的 DeviceOAuthAccountProvisioner.ManagedSource 同值）。
/// Core 不引用 Admin 程序集；控制器路由分流仅依赖该常量字符串。
/// </summary>
public static class DeviceOAuthAccountProvisioner
{
    public const string ManagedSource = "device_oauth";
}

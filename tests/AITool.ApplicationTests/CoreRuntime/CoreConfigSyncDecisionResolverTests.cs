using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;

namespace AITool.ApplicationTests.CoreRuntime;

/// <summary>
/// 验证 Core 配置同步决策推导器的最小行为。
/// </summary>
public sealed class CoreConfigSyncDecisionResolverTests
{
    /// <summary>
    /// 当 Core 尚未加载任何配置时，Admin 应被告知执行 full sync。
    /// </summary>
    [Fact]
    public void Resolve_returns_full_sync_required_when_core_has_no_snapshot()
    {
        var request = new CoreAdminHandshakeRequest
        {
            CurrentConfigVersion = 5,
            CurrentConfigHash = "sha256:test"
        };

        CoreConfigSyncDecisionResolver.Resolve(request, null)
            .Should().Be("full-sync-required");
    }

    /// <summary>
    /// 当版本和哈希都一致时，应直接返回 noop，避免 Admin 重启造成无意义的配置切换。
    /// </summary>
    [Fact]
    public void Resolve_returns_noop_when_version_and_hash_match()
    {
        var snapshot = new CoreRuntimeConfigSnapshot
        {
            ConfigVersion = 7,
            ConfigHash = "sha256:same"
        };
        var request = new CoreAdminHandshakeRequest
        {
            CurrentConfigVersion = 7,
            CurrentConfigHash = "sha256:same"
        };

        CoreConfigSyncDecisionResolver.Resolve(request, snapshot)
            .Should().Be("noop");
    }

    /// <summary>
    /// 当版本相同但哈希不同时，说明配置已漂移，当前阶段应强制要求全量同步。
    /// </summary>
    [Fact]
    public void Resolve_returns_full_sync_required_when_hash_differs_on_same_version()
    {
        var snapshot = new CoreRuntimeConfigSnapshot
        {
            ConfigVersion = 7,
            ConfigHash = "sha256:core"
        };
        var request = new CoreAdminHandshakeRequest
        {
            CurrentConfigVersion = 7,
            CurrentConfigHash = "sha256:admin"
        };

        CoreConfigSyncDecisionResolver.Resolve(request, snapshot)
            .Should().Be("full-sync-required");
    }

    /// <summary>
    /// 当 Admin 拥有更高版本配置时，当前阶段先统一要求执行 full sync。
    /// </summary>
    [Fact]
    public void Resolve_returns_full_sync_required_when_admin_version_is_newer()
    {
        var snapshot = new CoreRuntimeConfigSnapshot
        {
            ConfigVersion = 7,
            ConfigHash = "sha256:core"
        };
        var request = new CoreAdminHandshakeRequest
        {
            CurrentConfigVersion = 8,
            CurrentConfigHash = "sha256:admin"
        };

        CoreConfigSyncDecisionResolver.Resolve(request, snapshot)
            .Should().Be("full-sync-required");
    }
}

using System.Net;
using System.Net.Http;
using AITool.Application.Codex;
using AITool.Infrastructure.Codex;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.ApplicationTests.Codex;

/// <summary>
/// CodexModelCatalog 远端刷新的行为契约：
/// 超时/网络失败换下一个 URL、全部失败不抛异常、坏数据拒绝、成功后原子替换分层快照。
/// 注意：目录快照是进程级 static 状态，涉及变更的用例结束时把快照恢复为内置分层，
/// 避免影响同进程内其他依赖目录内容的测试。
/// </summary>
public sealed class CodexModelCatalogTests
{
    private const string Marker = "marker-catalog-test";

    /// <summary>按 URL 脚本化的 HttpMessageHandler：值可为响应体字符串或待抛异常。</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, object> _script;

        public ScriptedHandler(Dictionary<string, object> script)
        {
            _script = script;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            var action = _script.FirstOrDefault(kv => url.Contains(kv.Key, StringComparison.Ordinal)).Value;
            if (action is Exception ex)
            {
                throw ex;
            }
            var body = action as string ?? "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static string TierJson(IEnumerable<string> free, IEnumerable<string> team, IEnumerable<string> plus, IEnumerable<string> pro)
        => $$"""
        {
          "codex-free": [{{string.Join(",", free.Select(x => $"\"{x}\""))}}],
          "codex-team": [{{string.Join(",", team.Select(x => $"\"{x}\""))}}],
          "codex-plus": [{{string.Join(",", plus.Select(x => $"\"{x}\""))}}],
          "codex-pro": [{{string.Join(",", pro.Select(x => $"\"{x}\""))}}]
        }
        """;

    private static CodexModelCatalog CreateCatalog(Dictionary<string, object> script)
        => new(new HttpClient(new ScriptedHandler(script)), NullLogger<CodexModelCatalog>.Instance);

    private static readonly string[] BuiltinFree = ["gpt-5.5", "gpt-5.6-terra", "gpt-5.6-luna", "codex-auto-review"];
    private static readonly string[] BuiltinTeam = ["gpt-5.5", "gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "codex-auto-review"];
    private static readonly string[] BuiltinPlus = ["gpt-5.3-codex-spark", "gpt-5.5", "gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "codex-auto-review"];

    [Fact]
    public async Task Timeout_on_first_url_falls_through_to_second_and_replaces_snapshot()
    {
        var markerTiers = new[] { "gpt-5.5", Marker };
        var catalog = CreateCatalog(new Dictionary<string, object>
        {
            // HttpClient 超时抛 TaskCanceledException(OCE 子类)，且调用方 token 未取消——
            // 修复前该异常会穿透「不抛异常」契约；修复后应按失败换下一个 URL。
            ["router-for.me"] = new TaskCanceledException("simulated timeout"),
            ["githubusercontent.com"] = TierJson(markerTiers, markerTiers, markerTiers, markerTiers)
        });

        var result = await catalog.TryRefreshFromRemoteAsync();

        result.Success.Should().BeTrue("超时只应淘汰第一个 URL，而不是让整个刷新抛异常");
        result.Source.Should().Contain("githubusercontent.com");
        catalog.GetModelsForPlan("free").Should().Contain(Marker);
        catalog.GetModelsForPlan("plus").Should().Contain(Marker);

        // 恢复内置分层，避免 static 快照影响同进程其他测试。
        var restore = CreateCatalog(new Dictionary<string, object>
        {
            ["router-for.me"] = TierJson(BuiltinFree, BuiltinTeam, BuiltinPlus, BuiltinPlus)
        });
        var restored = await restore.TryRefreshFromRemoteAsync();
        restored.Success.Should().BeTrue();
        catalog.GetModelsForPlan("free").Should().NotContain(Marker);
    }

    [Fact]
    public async Task All_sources_unreachable_returns_failure_without_throwing()
    {
        var catalog = CreateCatalog(new Dictionary<string, object>
        {
            ["router-for.me"] = new HttpRequestException("unreachable"),
            ["githubusercontent.com"] = new TaskCanceledException("simulated timeout")
        });

        var act = async () => await catalog.TryRefreshFromRemoteAsync();

        (await act.Should().NotThrowAsync()).Which.Success.Should().BeFalse();
        catalog.GetModelsForPlan("pro").Should().NotBeEmpty("失败时必须保留本地目录兜底");
    }

    [Fact]
    public async Task Payload_missing_baseline_model_is_rejected()
    {
        // 缺 gpt-5.5 基线模型：疑似非 Codex 目录或损坏数据，应拒绝并沿用本地。
        var bogus = new[] { "totally-unknown-model" };
        var catalog = CreateCatalog(new Dictionary<string, object>
        {
            ["router-for.me"] = TierJson(bogus, bogus, bogus, bogus),
            ["githubusercontent.com"] = TierJson(bogus, bogus, bogus, bogus)
        });

        var result = await catalog.TryRefreshFromRemoteAsync();

        result.Success.Should().BeFalse();
        catalog.GetModelsForPlan("free").Should().Contain("gpt-5.5");
    }
}

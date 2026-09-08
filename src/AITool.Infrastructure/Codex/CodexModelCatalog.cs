using System.Collections.Concurrent;
using System.Text.Json;
using AITool.Application.Codex;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Codex;

/// <summary>
/// Codex 模型目录实现。分层列表数据移植自 CPA
/// （reference-projects/CLIProxyAPI/internal/registry/models/models.json 的 codex-free/-team/-plus/-pro 键）。
/// <para>
/// 进程内可刷新：内置静态分层仅作启动兜底；<see cref="TryRefreshFromRemoteAsync"/> 按需从 CPA 同源远端
/// （router-for-me/models）拉取最新分层，校验通过后原子替换内存快照，失败保留现状。
/// 「拉取模型」入口触发刷新（无后台服务），使新账号供给的默认映射能跟上上游新模型。
/// </para>
/// </summary>
public sealed class CodexModelCatalog : ICodexModelCatalog
{
    // —— 远端目录来源（CPA model_updater.go 同源，双 URL 回退）——
    private static readonly string[] RemoteUrls =
    [
        "https://models.router-for.me/models.json",
        "https://raw.githubusercontent.com/router-for-me/models/refs/heads/main/models.json"
    ];

    /// <summary>内置静态分层（快照自 CPA models.json，2026-09 校对）。</summary>
    private static readonly IReadOnlyList<string> Free =
        ["gpt-5.5", "gpt-5.6-terra", "gpt-5.6-luna", "codex-auto-review"];

    private static readonly IReadOnlyList<string> Team =
        ["gpt-5.5", "gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "codex-auto-review"];

    private static readonly IReadOnlyList<string> Plus =
        ["gpt-5.3-codex-spark", "gpt-5.5", "gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "codex-auto-review"];

    private static readonly IReadOnlyList<string> Pro =
        ["gpt-5.3-codex-spark", "gpt-5.5", "gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "codex-auto-review"];

    // —— builtin 图片模型（CPA WithCodexBuiltins 注入逻辑）——
    private static readonly IReadOnlyList<string> Builtins =
        ["gpt-image-1.5", "gpt-image-2"];

    /// <summary>不可变分层快照；刷新时整体替换引用（volatile 读），读侧无锁。</summary>
    private sealed record CatalogSnapshot(
        IReadOnlyList<string> Free, IReadOnlyList<string> Team,
        IReadOnlyList<string> Plus, IReadOnlyList<string> Pro);

    private static readonly CatalogSnapshot InitialSnapshot = new(Free, Team, Plus, Pro);

    // 目录状态必须为 static：本类经 AddHttpClient 注册为瞬态（typed client），
    // 刷新结果要进程级留存就不能放在实例字段上（与旧版 static 分层设计一致）。
    // 快照为不可变对象，刷新时整体替换引用（volatile 写），读侧取局部引用无锁。
    private static volatile CatalogSnapshot _snapshot = InitialSnapshot;
    private static volatile ConcurrentDictionary<string, IReadOnlyList<string>> _planCache = new();

    private readonly HttpClient _httpClient;
    private readonly ILogger<CodexModelCatalog> _logger;

    public CodexModelCatalog(HttpClient httpClient, ILogger<CodexModelCatalog> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetModelsForPlan(string? planType)
    {
        var key = (planType ?? string.Empty).ToLowerInvariant();
        var cache = _planCache;
        return cache.GetOrAdd(key, ComputeModels);
    }

    /// <inheritdoc />
    public async Task<CodexCatalogRefreshResult> TryRefreshFromRemoteAsync(CancellationToken cancellationToken = default)
    {
        foreach (var url in RemoteUrls)
        {
            try
            {
                var parsed = await FetchAndParseAsync(url, cancellationToken);
                if (parsed is null) continue;

                var snapshot = _snapshot;
                var changed = !TiersEqual(parsed.Free, snapshot.Free)
                    || !TiersEqual(parsed.Team, snapshot.Team)
                    || !TiersEqual(parsed.Plus, snapshot.Plus)
                    || !TiersEqual(parsed.Pro, snapshot.Pro);

                if (changed)
                {
                    _snapshot = parsed;
                    // 换新字典使旧的按 plan 缓存整体失效；读侧每次调用取当前引用，无锁竞争。
                    _planCache = new ConcurrentDictionary<string, IReadOnlyList<string>>();
                    _logger.LogInformation(
                        "Codex model catalog refreshed from {Url}: free={Free}, team={Team}, plus={Plus}, pro={Pro}",
                        url, string.Join(",", parsed.Free), string.Join(",", parsed.Team),
                        string.Join(",", parsed.Plus), string.Join(",", parsed.Pro));
                }
                else
                {
                    _logger.LogInformation("Codex model catalog refreshed from {Url}, no changes detected", url);
                }

                return new CodexCatalogRefreshResult { Success = true, Changed = changed, Source = url };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 调用方主动取消才上抛；HttpClient 超时也抛 TaskCanceledException(OCE 子类)，
                // 但此时调用方 token 未取消，须按「该来源失败」落入下方 catch 换下一个 URL。
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Codex model catalog fetch failed from {Url}", url);
            }
        }

        return new CodexCatalogRefreshResult { Success = false, Error = "远端模型目录拉取失败（所有来源均不可达或数据无效），沿用本地目录" };
    }

    // —— 私有 ——

    private async Task<CatalogSnapshot?> FetchAndParseAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var free = ParseTier(root, "codex-free");
        var team = ParseTier(root, "codex-team");
        var plus = ParseTier(root, "codex-plus");
        var pro = ParseTier(root, "codex-pro");

        // 校验（照 CPA validateModelsCatalog 的关键约束）：四层非空、层内无重复、
        // 且至少一层含 gpt-5.5（证明这确实是一份 Codex 客户端目录而非损坏数据）。
        if (free.Count == 0 || team.Count == 0 || plus.Count == 0 || pro.Count == 0)
        {
            _logger.LogWarning("Codex remote catalog from {Url} has empty tier, rejected", url);
            return null;
        }
        if (new[] { free, team, plus, pro }.Any(tier => tier.Distinct(StringComparer.Ordinal).Count() != tier.Count))
        {
            _logger.LogWarning("Codex remote catalog from {Url} contains duplicate ids, rejected", url);
            return null;
        }
        if (!free.Concat(team).Concat(plus).Concat(pro).Any(id => string.Equals(id, "gpt-5.5", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Codex remote catalog from {Url} is missing expected baseline model gpt-5.5, rejected", url);
            return null;
        }

        return new CatalogSnapshot(free, team, plus, pro);
    }

    private static IReadOnlyList<string> ParseTier(JsonElement root, string section)
    {
        if (!root.TryGetProperty(section, out var arrayEl) || arrayEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var ids = new List<string>();
        foreach (var item in arrayEl.EnumerateArray())
        {
            string? id = null;
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            {
                id = idEl.GetString();
            }
            else if (item.ValueKind == JsonValueKind.String)
            {
                id = item.GetString();
            }
            if (!string.IsNullOrWhiteSpace(id))
            {
                ids.Add(id.Trim());
            }
        }
        return ids;
    }

    private static bool TiersEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
        => a.Count == b.Count && a.SequenceEqual(b, StringComparer.Ordinal);

    private IReadOnlyList<string> ComputeModels(string key)
    {
        // 分层选择规则照搬 CPA sdk/cliproxy/service.go
        var snapshot = _snapshot;
        var tier = key switch
        {
            "pro" => snapshot.Pro,
            "plus" => snapshot.Plus,
            "team" or "business" or "go" => snapshot.Team,
            "free" => snapshot.Free,
            _ => snapshot.Pro, // 未知/空 default = pro
        };
        return tier.Concat(Builtins).Distinct().ToList();
    }
}

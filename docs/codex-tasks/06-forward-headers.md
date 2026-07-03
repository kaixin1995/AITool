# T06 — 转发链路请求头接入

> 状态：已完成 ✅
> 前置依赖：T01（`Site.ExtraHeadersJson` 列）
> 关联总览章节：横切性能原则 P1 / P10

## 实施记录

- `CachedProxyRouteTarget` 加 `ExtraHeaders` 字段（大小写不敏感 Dictionary），位于 ApiKey 之后。
- `ProxyRequestMetadataCache.GetRouteTargetsAsync` join 中填充 `ExtraHeaders = TryParseExtraHeaders(site.ExtraHeadersJson)`；新增私有 `TryParseExtraHeaders`（容错：空/坏 JSON 返回空字典），仅在缓存构建期(5s)调用。
- 接入 3 处 `new ProxyForwardRequest`：`OpenAiProxyController.cs:604`（chat/completions/completions/embeddings）、`OpenAiProxyController.Responses.cs:190`（Responses HTTP）、`:488`（Responses WebSocket）—— 全部加 `ForwardHeaders = MergeExtraHeaders(route.ExtraHeaders)`。
- 新增静态 helper `MergeExtraHeaders` 于 `OpenAiProxyController.Helpers.cs`（空字典返回新空字典，非空浅拷贝，防御性避免共享缓存实例被改）。
- `BuildRequestMessage` 既有 `TryAddWithoutValidation` 循环无需改动（User-Agent 等自定义头直接生效）。
- 编译通过。覆盖了 OpenAI 入口的全部转发点（流式 helper 接收已构造好的 forwardRequest，无需改）。

## 目标

让转发链路能为「带 `ExtraHeadersJson` 的 Site」自动注入自定义请求头（Codex 的 `Originator` / `Chatgpt-Account-Id` / `User-Agent`）。**通用机制，非 Codex 专属**——任何未来需要特殊头的站点都受益。

已核查事实：
- `ProxyForwardRequest.ForwardHeaders`（Dictionary）→ `ProxyForwardService.BuildRequestMessage` 的 `TryAddWithoutValidation`（line 496-504）**管线已存在**。
- 但 `OpenAiProxyController.cs`（line 604-621）、`OpenAiProxyController.Responses.cs`（line 190-203、485-495）构造 `ProxyForwardRequest` 时**当前不设置 `ForwardHeaders`**（仅 Anthropic 控制器设置）。

---

## 涉及文件

| 文件 | 操作 |
| --- | --- |
| `src/AITool.Web/Services/ProxyRequestMetadataCache.cs` | `CachedProxyRouteTarget` 加字段；join 填充 |
| `src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs` | 构造 `ProxyForwardRequest` 时合并 ForwardHeaders |
| `src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs` | 同上（HTTP + WebSocket 两处） |

参考：`AnthropicProxyController.cs:240-256`（已有的 ForwardHeaders 设置范式）、`ProxyForwardService.cs:467-507`（`BuildRequestMessage` 合并逻辑）。

---

## 详细步骤

### 1. `CachedProxyRouteTarget` 加字段

`ProxyRequestMetadataCache.cs` 的 `CachedProxyRouteTarget` 类（line 1417-1507）增加：

```csharp
/// 从 Site.ExtraHeadersJson 反序列化的自定义请求头；空则不注入。
public Dictionary<string, string> ExtraHeaders { get; set; } = new();
```

### 2. `GetRouteTargetsAsync` join 时填充

`GetRouteTargetsAsync`（line 1022-1067）的 join select 中：

```csharp
ExtraHeaders = string.IsNullOrWhiteSpace(site.ExtraHeadersJson)
    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    : TryParseHeaders(site.ExtraHeadersJson),
```

其中 `TryParseHeaders`：

```csharp
private static Dictionary<string,string> TryParseHeaders(string json)
{
    try {
        var dict = JsonSerializer.Deserialize<Dictionary<string,string>>(json);
        return dict != null
            ? new Dictionary<string,string>(dict, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    } catch {
        return new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);  // 容错：坏 JSON 不影响转发
    }
}
```

> **大小写不敏感**：HTTP 头不区分大小写，用 `StringComparer.OrdinalIgnoreCase`，与现有 `ForwardHeaders` 一致。

### 3. OpenAI/Responses 控制器注入 ForwardHeaders

**`OpenAiProxyController.cs:604-621`**（chat/completions、completions、embeddings）构造 `ProxyForwardRequest` 处：

```csharp
var forwardRequest = new ProxyForwardRequest {
    TargetBaseUrl = route.BaseUrl,
    TargetEndpointPathMode = route.EndpointPathMode,
    TargetApiKey = route.ApiKey,
    ProtocolType = actualProtocolType,
    TargetModelName = route.SiteModelName,
    // ... 现有字段 ...
    ForwardHeaders = MergeExtraHeaders(route.ExtraHeaders),  // 新增
};
```

**`OpenAiProxyController.Responses.cs:190-203`（HTTP）与 `:485-495`（WebSocket）** 同样加 `ForwardHeaders`。

`MergeExtraHeaders` helper（放控制器 Helpers partial 或静态方法）：

```csharp
private static Dictionary<string,string> MergeExtraHeaders(Dictionary<string,string> extra)
{
    if (extra == null || extra.Count == 0)
        return new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    // 直接用 ExtraHeaders（OpenAI 入口当前不携带需透传的客户端头）
    return new Dictionary<string,string>(extra, StringComparer.OrdinalIgnoreCase);
}
```

> **注意 User-Agent**：`BuildRequestMessage` 用 `TryAddWithoutValidation("User-Agent", ...)` 在请求级覆盖 HttpClient 默认 UA，可正常工作。无需特殊处理。

> **与 Anthropic 控制器的差异**：Anthropic 控制器额外透传客户端的 `anthropic-version`/`anthropic-beta`（`CollectAnthropicForwardHeaders`）。OpenAI 入口本期只注入 Site 配置的 ExtraHeaders，不透传客户端头（保持现状）。若未来需要透传，再扩展合并逻辑。

### 4. 验证 BuildRequestMessage 合并

`ProxyForwardService.BuildRequestMessage`（line 496-504）已有：

```csharp
foreach (var header in request.ForwardHeaders) {
    if (string.Equals(header.Key, "anthropic-version", ...)) continue;
    httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
}
```

- `Originator` / `Chatgpt-Account-Id` 是自定义头，`TryAddWithoutValidation` 可直接加。
- `User-Agent` 同样可加（覆盖客户端默认）。
- **无需改动 `ProxyForwardService`**。

---

## 性能考量

### 引用原则
- **P1 缓存**：`ExtraHeadersJson` 反序列化在 `GetRouteTargetsAsync`（缓存构建期，5s 一次）完成，结果随 `CachedProxyRouteTarget` 缓存。**不是每请求反序列化**。
- **P10 热路径**：正常转发每请求只多一次 Dictionary 拷贝（MergeExtraHeaders），开销极小。

### 本任务特有
- **JSON 反序列化频率**：每 5s 缓存重建时，对每个带 ExtraHeadersJson 的 Site 反序列化一次。Codex 账号量级小（几十），开销可忽略。
- **空字典跳过**：`ExtraHeadersJson` 为 null/空 → `ExtraHeaders` 是空字典，`MergeExtraHeaders` 返回空字典，`BuildRequestMessage` 的 foreach 不迭代。对非 Codex 站点零额外开销。
- **字典拷贝**：`new Dictionary(extra, ...)` 是浅拷贝，避免共享缓存实例被修改（防御性）。每次转发拷贝一个小字典（2-3 项），开销可忽略。若profile 显示热点，可改为只读包装，但优先正确性。
- **容错**：`TryParseHeaders` 捕获异常返回空字典，坏 JSON 不阻断转发（降级为不带额外头，可能上游返回错误，但不影响其它站点）。

---

## 验收标准

1. 带 `ExtraHeadersJson` 的 Site（Codex 隐藏 Site）转发时，上游收到 `Originator`、`Chatgpt-Account-Id`、`User-Agent` 三个头。
2. 普通 Site（无 ExtraHeadersJson）转发行为不变，无额外头。
3. `ExtraHeadersJson` 是坏 JSON → 不抛异常，该 Site 转发时不带额外头（其它 Site 不受影响）。
4. 现有 OpenAI/Responses 功能回归通过（chat/completions、responses HTTP、responses WebSocket）。
5. 缓存重建期反序列化只发生一次/5s/Site。

---

## 风险

- **User-Agent 覆盖**：某些上游可能依赖 UA。Codex 上游要求 `codex_cli_rs/...`，照搬 CPA。对普通 Site，ExtraHeaders 空，UA 不变（用 HttpClient 默认或客户端透传）。**无回归风险**。
- **Chatgpt-Account-Id 为空**：若 Codex 账号 JWT 解析失败导致 AccountId 空，`ExtraHeadersJson` 里 `Chatgpt-Account-Id=""`。上游可能拒绝。T04 供给时应保证 AccountId 非空（解析失败则拒绝建账号）。
- **缓存字段增加体积**：`CachedProxyRouteTarget` 多一个小字典，内存增量可忽略（每路由目标 +1 字典引用）。
- **多入口一致性**：必须在 OpenAI 控制器的**所有**构造 `ProxyForwardRequest` 的点都加 ForwardHeaders（chat/completions、completions、embeddings、responses HTTP、responses WebSocket）。漏一处会导致部分入口不带 Codex 头。实现时全局搜索 `new ProxyForwardRequest` 确认覆盖完整。

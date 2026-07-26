# T05 — Codex 模型目录

> 状态：已完成 ✅
> 前置依赖：T01（数据模型）
> 关联总览章节：横切性能原则 P5 / P6

## 实施记录

- 已从 CPA `models.json` 提取 4 档模型快照（2026-07-03 校对）：free=3、team=4、plus=5、pro=5；含 gpt-5.4/gpt-5.4-mini/gpt-5.5/gpt-5.3-codex-spark/codex-auto-review。
- 新建 `src/AITool.Application/Codex/ICodexModelCatalog.cs`、`CodexRemoteModel.cs`、`ICodexModelFetcher.cs`。
- 新建 `src/AITool.Infrastructure/Codex/CodexModelCatalog.cs`：static readonly 分层 + Builtins(gpt-image-1.5/2)，按 plan `ConcurrentDictionary` 缓存拼接结果（default=pro）。
- 新建 `src/AITool.Infrastructure/Codex/CodexModelFetcher.cs`：GET chatgpt.com/backend-api/codex/models，含全部 5 个必需头（Originator/UA/Chatgpt-Account-Id/Bearer/Accept）；响应兼容数组/{models}/{data} 三种形态。
- Program.cs 注册 Catalog(singleton) + Fetcher(HttpClient,30s)。
- 编译通过。
- 决策：builtin 图片模型保留注入（gpt-image-1.5/2），动态拉取的 context_window 本期不落地（仅留 DTO 字段）。

## 目标

提供 Codex 模型目录能力：① 静态分层目录（free/team/plus/pro，含 builtin 图片模型）；② PlanType → 分层映射；③ 动态拉取上游模型目录（`backend-api/codex/models`）。供 T04（供给工厂建映射）与 T11（拉取按钮）使用。

对应 CPA：
- 静态目录：`internal/registry/models/models.json` 的 `codex-free`/`codex-team`/`codex-plus`/`codex-pro` 键。
- 访问器：`internal/registry/model_definitions.go:53-71`（`GetCodexFreeModels` 等）。
- builtin 注入：`model_definitions.go:116-118`（`WithCodexBuiltins` 注入 `gpt-image-1.5`/`gpt-image-2`）。
- 动态拉取：`cmd/fetch_codex_models/main.go`。
- Plan 映射：`sdk/cliproxy/service.go:1984-2009`。

---

## 涉及文件

| 文件 | 操作 |
| --- | --- |
| `src/AITool.Application/Codex/ICodexModelCatalog.cs` | 新建接口 |
| `src/AITool.Infrastructure/Codex/CodexModelCatalog.cs` | 新建实现（静态目录） |
| `src/AITool.Infrastructure/Codex/CodexModelFetcher.cs` | 新建动态拉取（HttpClient） |
| `src/AITool.Web/Program.cs` | 注册 |

参考：CPA `models.json`（移植静态数据）、`fetch_codex_models/main.go`（动态拉取请求格式）。

---

## 详细步骤

### 1. 静态分层目录

`CodexModelCatalog`（singleton，进程内只读缓存）：

```csharp
public sealed class CodexModelCatalog
{
    private static readonly IReadOnlyList<string> Free = new[]{ /* 从 CPA codex-free 移植 */ };
    private static readonly IReadOnlyList<string> Team = new[]{ /* codex-team */ };
    private static readonly IReadOnlyList<string> Plus = new[]{ /* codex-plus */ };
    private static readonly IReadOnlyList<string> Pro  = new[]{ /* codex-pro */ };

    private static readonly IReadOnlyList<string> Builtins = new[]{ "gpt-image-1.5", "gpt-image-2" };

    public IReadOnlyList<string> GetModelsForPlan(string? planType)
    {
        var base_ = (planType?.ToLowerInvariant()) switch {
            "pro"   => Pro,
            "plus"  => Plus,
            "team" or "business" or "go" => Team,
            "free"  => Free,
            _       => Pro,     // default = pro（对应 CPA service.go:2007）
        };
        return base_.Concat(Builtins).Distinct().ToList();
    }
}
```

> **移植数据**：实现时打开 `reference-projects/CLIProxyAPI/internal/registry/models/models.json`，提取 `codex-free`/`-team`/`-plus`/`-pro` 四个键的模型 id 列表，硬编码为 C# `string[]`。这些是相对稳定的静态目录。

### 2. PlanType → 分层映射规则（照搬 CPA）

| PlanType | 分层 |
| --- | --- |
| `pro` | codex-pro |
| `plus` | codex-plus |
| `team` / `business` / `go` | codex-team |
| `free` | codex-free |
| 其它/null/未知 | **codex-pro（default）** |

`gpt-image-1.5` / `gpt-image-2` 对所有 plan 都注入。

### 3. 动态拉取 `CodexModelFetcher`

注入 HttpClient（`AddHttpClient<CodexModelFetcher>` 或复用额度/OAuth 客户端，但建议独立，因请求头与超时不同）。

```csharp
public async Task<IReadOnlyList<CodexRemoteModel>> FetchAsync(string accessToken, string accountId, CancellationToken ct)
```

请求（对应 `fetch_codex_models/main.go:231-295`）：

- URL：`https://chatgpt.com/backend-api/codex/models?client_version=0.133.0`
- Method：GET
- Headers：
  ```
  Accept: application/json
  Authorization: Bearer <accessToken>
  Originator: codex_cli_rs
  User-Agent: codex_cli_rs/0.133.0 (Mac OS 26.3.1; arm64) iTerm.app/3.6.9
  Chatgpt-Account-Id: <accountId>
  ```
- 超时：30s。

响应字段（`codex_client_models.json` 结构）：
- `slug`（模型名，用作 RemoteModelName，如 `gpt-5.5`）
- `display_name`
- `context_window`
- `default_reasoning_level` / `supported_reasoning_levels`
- `prefer_websockets` / `truncation_policy` / `visibility`

`CodexRemoteModel` DTO 取 `slug` + `display_name`（+ 可选 context_window 留作模型元数据）。

### 4. 拉取后入库

动态拉取的入口在 T11（`pull-models` 按钮）调用 Fetcher 拿列表后，**复用 T04 Provisioner 的批量 upsert 模式**，把 slug 作为 RemoteModelName 追加到该账号隐藏 Site 的映射（已存在则跳过）。本任务只负责「拉取 → 返回模型列表」，入库逻辑放 T11（或 Provisioner 加 `UpsertModelsAsync(siteId, models)` 方法供 T11 调）。

### 5. 接口设计

```csharp
public interface ICodexModelCatalog
{
    IReadOnlyList<string> GetModelsForPlan(string? planType);
}

public interface ICodexModelFetcher
{
    Task<IReadOnlyList<CodexRemoteModel>> FetchAsync(string accessToken, string accountId, CancellationToken ct);
}
```

---

## 性能考量

### 引用原则
- **P5 HttpClient 复用**：Fetcher 经 `AddHttpClient` 注册。
- **P6 批量**：拉取结果 upsert 批量。

### 本任务特有
- **静态目录进程内只读**：`static readonly` 数组，零分配、零 IO，`GetModelsForPlan` 是 O(n) 拼接（n = 模型数，几十），可忽略。**每次调用都 Concat 新建 List**——若担心，可按 plan 缓存到 `ConcurrentDictionary<plan, List>`，但收益微小，优先可读性。
- **动态拉取限频**：上游对 `codex/models` 可能有频率限制。**T11 拉取按钮加前端节流 + 后端单账号最小间隔**（如 60s 内重复请求直接返回上次结果或 429）。Fetcher 本身无状态，限频在 T11/服务层。
- **拉取失败降级**：动态拉取失败不应影响账号可用性（静态目录已保证基础模型）。T11 捕获异常返回提示，账号仍用静态映射。
- **User-Agent 字符串**：硬编码 CPA 的 UA（含平台信息）。上游可能校验，照搬。
- **client_version**：`0.133.0` 硬编码，后续上游升级可能需调整。

---

## 验收标准

1. `GetModelsForPlan("free"/"plus"/"team"/"pro")` 返回对应分层 + 两个 builtin 图片模型。
2. 未知 plan → 返回 pro 分层。
3. `FetchAsync` 用真实 token/accountId 能拿到上游模型列表（手动验证）。
4. 拉取请求头含全部 5 个必需头。
5. 静态目录编译期常量，无运行时 IO。

---

## 风险

- **静态目录时效性**：CPA `models.json` 的 codex 模型列表会随上游变化。移植的是**快照**，后续需定期对照 CPA 上游同步（参考 `docs/protocol-url-reference.md` 的维护约定）。本期以能跑通为准。
- **动态拉取端点可达性**：`chatgpt.com/backend-api/codex/models` 需要有效 token 且可能地区限制。本地实测确认。若不可达，本期退化为纯静态目录。
- **模型名与现有 vendor 归类**：Codex 模型名（gpt-5.x）进 `ModelLibraryItem` 后，`ModelVendorCatalogService.ResolveVendor` 需能归类到 OpenAI/Codex vendor。实现时检查 vendor 目录（`model-vendor-catalog.json` 或类似）是否需补条目；若归类错只影响 Models 页面分组展示，不影响路由/调用。
- **context_window 未用**：动态拉取返回的 context_window 可作为模型元数据展示，但当前 `ModelLibraryItem` 无此字段（参考 `protocol-url-reference.md` 第 190-219 行，new-api 也只是前端推断）。本期不落地 context_window，仅留 DTO 字段。

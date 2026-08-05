# T04 — 账号供给工厂（隐藏 Site）

> 状态：已完成 ✅
> 前置依赖：T01（数据模型）、T02（OAuth 客户端）、T03（凭证导入，复用 DTO）、T05（模型目录）
> 关联总览章节：横切性能原则 P1 / P2 / P6

## 实施记录

- 新建 `src/AITool.Web/Services/SiteCascadeDeleter.cs`：**提取共享级联删除服务**（比文档原计划「复制逻辑」更好），含 `RemoveSitesAsync` + `CleanupEmptyRouteEntriesAsync`，逻辑取自 Sites/Index.cshtml.cs。后续可让 Sites 页面也改用它消除重复（本期未改 Sites 页，降低回归风险）。
- 新建 `src/AITool.Application/Codex/CodexProvisionInput.cs`。
- 新建 `src/AITool.Web/Services/CodexAccountProvisioner.cs`：
  - `ProvisionFromTokensAsync`（去重 by AccountId→Email；新建/更新隐藏 Site；ExtraHeadersJson 含三头；按 plan 批量 upsert 模型+映射；末尾一次性失效缓存）。
  - `DeprovisionAsync`（调 SiteCascadeDeleter + 删 CodexAccount）。
  - `UpdateAsync`（DisplayName/Threshold，同步 Site.Name）。
  - `UpsertRemoteModelsAsync`（动态拉取结果复用，供 T11）。
- 修正 SqlSugar 用法：单记录查 `InSingleAsync(pk)` / `.Where().ToListAsync().FirstOrDefault()`（无 FirstOrDefaultAsync 扩展）。
- Program.cs 注册 `SiteCascadeDeleter` + `CodexAccountProvisioner`（Scoped）。
- 编译通过。
- 决策：隐藏 Site 配置 BaseUrl=`https://chatgpt.com/backend-api/codex`、EndpointPathMode=`versioned-base`、SupportsOpenAi/Anthropic=false→Responses；User-Agent=`codex_cli_rs/0.133.0 (...)`。Sites 页面本期未改用共享删除器（降风险，留作后续重构）。

## 目标

实现 `CodexAccountProvisioner`：把 OAuth 拿到的 token / 导入的凭证，转换为一个**隐藏 Site + CodexAccount + 模型映射**的完整供给，并支持级联删除。这是后端核心枢纽——T11 控制器的所有写操作都经它执行。

采用「隐藏 Site 复用」方案后，Models/Routes/Chat 经 `SiteId` 自动联动，本任务是建立这条关联的工厂。

---

## 涉及文件

| 文件 | 操作 |
| --- | --- |
| `src/AITool.Web/Services/CodexAccountProvisioner.cs` | 新建服务 |
| `src/AITool.Application/Codex/ICodexAccountProvisioner.cs` | 新建接口（可选，便于测试 mock） |

依赖注入：`AppDbContext`、`ProxyRequestMetadataCache`、`CodexModelCatalog`（T05）。

参考：`Pages/Admin/Sites/Index.cshtml.cs` 的 `RemoveSitesAsync`（级联删除逻辑，line 159-204）；`SiteCatalogApiController.ImportSelected`（模型 upsert，line 277+）。

---

## 详细步骤

### 1. 供给入口 `ProvisionFromTokensAsync(input)`

统一入参（OAuth 完成与凭证导入共用）：

```csharp
public sealed class CodexProvisionInput
{
    public string DisplayName { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string IdToken { get; set; } = "";
    public string? AccountId { get; set; }
    public string? Email { get; set; }
    public string? PlanType { get; set; }          // null → 模型目录 default(pro)
    public DateTimeOffset? TokenExpiresAt { get; set; }
}
```

流程：

1. **解析 id_token 兜底**（若 AccountId/Email/PlanType 缺失）：调 `CodexJwtParser.Parse(input.IdToken)` 补全。
2. **去重判定**：按 `AccountId` 查 `CodexAccounts`，若已存在则视为「重新授权/更新」（更新 token + 刷新 Site.ApiKey），不重复建 Site。若 `AccountId` 为空则按 `Email` 兜底，再不行按 DisplayName（应用层提示用户）。
3. **建/更新隐藏 Site**：
   ```csharp
   var site = new Site {
       Name = input.DisplayName,
       BaseUrl = "https://chatgpt.com/backend-api/codex",
       EndpointPathMode = "versioned-base",
       ApiKey = input.AccessToken,
       SupportsOpenAi = false,
       SupportsAnthropic = false,        // → ResolveSiteProtocolType 返回 "Responses"
       ManagedSource = "Codex",
       ExtraHeadersJson = JsonSerializer.Serialize(new Dictionary<string,string> {
           ["Originator"] = "codex_cli_rs",
           ["Chatgpt-Account-Id"] = input.AccountId ?? "",
           ["User-Agent"] = "codex_cli_rs/0.133.0"
       }),
       IsEnabled = true
   };
   ```
   - 新建 → `InsertAsync(site)`；已存在 → `UpdateAsync`。
4. **建/更新 CodexAccount**：填 `LinkedSiteId = site.Id`、token 字段、`TokenExpiresAt`、`IsEnabled=true`、`IsQuotaCooling=false`。
5. **模型映射**：调 `CodexModelCatalog.GetModelsForPlan(planType)` 拿模型名列表 → 批量 upsert（见下方，复用 P6 批量模式）。
6. **失效缓存**：`_metadataCache.InvalidateRouteTargets()` + `InvalidateModelMetadata()`。
7. 返回 `CodexAccount`（含 Id、LinkedSiteId）。

### 2. 模型批量 upsert（复用 P6 模式，对应 `SiteCatalogApiController.ImportSelected`）

```csharp
// 1) 取该 plan 的模型名列表（T05）
var modelNames = _catalog.GetModelsForPlan(input.PlanType);

// 2) 内存求差集
var existingModels = await _dbContext.ModelLibraryItems.ToListAsync(ct);
var existingMappings = await _dbContext.SiteModelMappings
    .Where(m => m.SiteId == site.Id).ToListAsync(ct);

var toAddModels = modelNames.Except(existingModels.Select(x => x.ModelName)).ToList();
var toAddMappings = modelNames
    .Where(name => !existingMappings.Any(m => m.RemoteModelName == name))
    .Select(name => {
        var libItem = existingModels.FirstOrDefault(x => x.ModelName == name)
                   ?? new ModelLibraryItem { ModelName = name, DisplayName = name, IsEnabled = true };
        return new SiteModelMapping {
            SiteId = site.Id,
            ModelLibraryItemId = libItem.Id,
            RemoteModelName = name,
            IsEnabled = true
        };
    }).ToList();

// 3) 批量写
if (toAddModels.Any()) await _dbContext.InsertRangeAsync(toAddModels, ct);
// 注意：先写 ModelLibraryItem 拿到 Id，再建 mapping（见 P6 与现有 ImportSelected 的顺序）
if (toAddMappings.Any()) await _dbContext.InsertRangeAsync(toAddMappings, ct);
```

> **重要顺序**：`ModelLibraryItem` 是全局去重的（by ModelName）。先 upsert ModelLibraryItem 拿到稳定 Id，再建指向它的 SiteModelMapping。完全照搬 `SiteCatalogApiController.ImportSelected` 的现有写法（实现时 Read 该方法对齐）。

### 3. 删除 `DeprovisionAsync(codexAccountId)`

```csharp
public async Task DeprovisionAsync(Guid codexAccountId, CancellationToken ct)
{
    var account = await _dbContext.CodexAccounts
        .FirstAsync(a => a.Id == codexAccountId, ct);   // 找不到抛异常→控制器返回 404
    // 复用 Sites 的级联删除：删 SiteModelMapping + ProxyRouteRule + 清空 ProxyRouteEntry + 删 Site
    // 直接调 Sites/Index.cshtml.cs 的 RemoveSitesAsync 等效逻辑（提取为共享方法或复制）
    await RemoveSiteCascadeAsync(account.LinkedSiteId, ct);
    await _dbContext.DeleteAsync<CodexAccount>(a => a.Id == codexAccountId, ct);
    _metadataCache.InvalidateRouteTargets();
    _metadataCache.InvalidateModelMetadata();
}
```

**级联删除逻辑**（对应 `Sites/Index.cshtml.cs:159-204`）：
- 删 `SiteModelMappings` where SiteId
- 删 `ProxyRouteRules` where SiteId
- `CleanupEmptyRouteEntriesAsync`：删掉因规则被清空而孤立的外部入口 `ProxyRouteEntry`
- 删 `Sites` where Id

> 建议把 `Sites/Index.cshtml.cs` 里的级联逻辑**提取为共享方法**（如 `SiteCascadeDeleter` 服务），供 Sites 页面与 Provisioner 共用，避免逻辑重复。本期可在 Provisioner 内复制实现，后续重构提取。

### 4. 更新账号（编辑 DisplayName / AutoDisableThreshold）

`UpdateAsync(codexAccountId, displayName, threshold)`：更新 CodexAccount + 同步 `LinkedSite.Name = displayName` + invalidate。

---

## 接口设计

```csharp
public interface ICodexAccountProvisioner
{
    Task<CodexAccount> ProvisionFromTokensAsync(CodexProvisionInput input, CancellationToken ct);
    Task DeprovisionAsync(Guid codexAccountId, CancellationToken ct);
    Task UpdateAsync(Guid codexAccountId, string displayName, decimal? autoDisableThreshold, CancellationToken ct);
}
```

---

## 性能考量

### 引用原则
- **P1 缓存失效**：所有写后 `InvalidateRouteTargets()` + `InvalidateModelMetadata()`，不手动重建。
- **P2 内存 join**：批量 upsert 前把 `ModelLibraryItems` / `SiteModelMappings` 载入内存求差集（量级小）。
- **P6 批量**：`InsertRangeAsync` 批量写模型与映射，禁止逐条。

### 本任务特有
- **一次性失效**：单个供给流程内可能多次写库（Site + Account + 模型 + 映射），但**只在末尾失效一次缓存**（中间不需要缓存生效）。
- **去重查询**：按 AccountId 查重，单次查询（Account 表量级 = Codex 账号数，很小）。
- **模型库全局去重**：多个 Codex 账号共享同一 `ModelLibraryItem`（by ModelName），避免重复模型条目。映射按 (SiteId, RemoteModelName) 区分到各账号的隐藏 Site。
- **事务**：SqlSugar 默认每语句自动提交。供给流程若需原子性，可用 `asTransaction` 包裹（`_client.Ado.UseTranAsync`）。建议包事务，避免中途失败留下半成品（如 Site 建了但 Account 没建）。
- **DisplayName 唯一性**：应用层校验（可选），不强制 DB 唯一索引（允许同名，由用户负责区分）。

---

## 验收标准

1. OAuth 完成或凭证导入 → 调 `ProvisionFromTokensAsync` → 产出 CodexAccount + 隐藏 Site（`ManagedSource="Codex"`、`SupportsOpenAi/Anthropic=false`、`ExtraHeadersJson` 含三头）+ 模型映射。
2. 隐藏 Site 不出现在 `Admin/Sites` 列表（T07 过滤生效）。
3. 模型出现在 `Admin/Models`，可在 `Admin/Routes` 选为目标，出现在对话测试下拉。
4. 同一 AccountId 二次供给 → 更新 token，不重复建 Site/账号。
5. `DeprovisionAsync` → CodexAccount + 隐藏 Site + 映射 + 路由规则 + 孤立入口 全部清除；缓存失效。
6. 写后 5s 内缓存重建，新账号可被转发链路命中。

---

## 风险

- **级联删除逻辑重复**：若不提取共享方法，Provisioner 与 Sites 页面各一份，易漂移。**建议提取 `SiteCascadeDeleter`**（小重构），或至少在 Provisioner 内严格复制现有逻辑并加注释指向来源。
- **模型库污染**：Codex 模型名若与现有站点模型同名（如 `gpt-5`），会共享同一 `ModelLibraryItem`——这是期望行为（路由可跨站点负载）。但要确认 `ModelVendorCatalogService.ResolveVendor` 能正确归类 Codex 模型（可能需在 vendor 目录补映射，T05 处理）。
- **AccountId 为空**：导入文件若 JWT 解析失败且无顶层 account_id，去重无依据。策略：拒绝供给并提示用户（凭证不完整）。
- **事务支持**：SqlSugar `UseTranAsync` 需确认与 `AppDbContext` 包装兼容；若不支持，降级为「尽力写 + 失败补偿删除」，但优先用事务。

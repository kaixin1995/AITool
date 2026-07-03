# T07 — 站点列表过滤托管 Site

> 状态：已完成 ✅
> 前置依赖：T01（`Site.ManagedSource` 列）、T04（开始产生托管 Site）
> 关联总览章节：横切性能原则 P2

## 实施记录

- `Sites/Index.cshtml.cs` `OnGetAsync`：加 `Where(x => string.IsNullOrEmpty(x.ManagedSource))`。
- 批量删除 `OnPostBulkDeleteAsync`：查询加 `&& string.IsNullOrEmpty(x.ManagedSource)`，托管 Site 不被批量删。
- 单删除 `OnPostDeleteAsync`：加防护，若 `ManagedSource` 非空返回提示「请到对应账号管理页删除」。
- `Export.cshtml.cs` `OnGetAsync`：加 `Where(s => string.IsNullOrEmpty(s.ManagedSource))`，不导出托管 Site。
- `SiteCatalogApiController.FetchAllModels`：查询加 `&& string.IsNullOrEmpty(s.ManagedSource)`，避免对 Codex 隐藏 Site 调 OpenAI catalog 接口误报。
- 决策：用 `string.IsNullOrEmpty(x.ManagedSource)` 而非 `== null`，更稳（兼容空字符串）。
- 编译通过。

## 目标

在站点管理页面（`Admin/Sites`）的列表、导入、导出中过滤掉 `ManagedSource="Codex"` 的托管 Site，避免 Codex 自动创建的隐藏 Site 污染用户的站点视图。

---

## 涉及文件

| 文件 | 操作 |
| --- | --- |
| `src/AITool.Web/Pages/Admin/Sites/Index.cshtml.cs` | `OnGetAsync` 列表过滤 |
| `src/AITool.Web/Pages/Admin/Sites/Import.cshtml.cs` | 导入过滤（如涉及遍历现有 Site） |
| `src/AITool.Web/Pages/Admin/Sites/Export.cshtml.cs` | 导出过滤 |

参考：`Sites/Index.cshtml.cs:66`（`OnGetAsync` 现有查询）、`:100-204`（批量删除/级联）。

---

## 详细步骤

### 1. 列表查询过滤

`Index.cshtml.cs` 的 `OnGetAsync`（line 66 附近）现有查询形如：

```csharp
Sites = await _dbContext.Sites.OrderBy(s => s.Name).ToListAsync();
```

改为：

```csharp
Sites = await _dbContext.Sites
    .Where(s => s.ManagedSource == null)   // 过滤掉托管 Site（Codex 等）
    .OrderBy(s => s.Name)
    .ToListAsync();
```

> **SqlSugar 表达式支持** `Where` 字段 == null。确认 SqlSugar 对 nullable 字段的 null 比较正确翻译（SQLite `IS NULL`）。若 SqlSugar 对 `== null` 翻译有问题，用 `.Where(s => string.IsNullOrEmpty(s.ManagedSource))`（更安全）。

### 2. 批量操作防护

`OnPostBulkDeleteAsync`（line 100）与 `OnPostDeleteAsync`（line 135）：当前按 siteId 删除。**无需额外过滤**——用户在列表上看到的都是非托管 Site，勾选/删除的都是普通 Site。但为防恶意请求（直接 POST 托管 SiteId），加防护：

```csharp
// 在 RemoveSitesAsync 内，删除前校验 ManagedSource
var sitesToRemove = await _dbContext.Sites
    .Where(s => selectedIds.Contains(s.Id) && s.ManagedSource == null)
    .ToListAsync();
```

> 托管 Site 的删除只能走 Codex 账号的 `DeprovisionAsync`（T04/T11），不允许从 Sites 页面误删（否则 CodexAccount 成孤儿）。

### 3. 导入/导出过滤

- **导出**：`Export.cshtml.cs` 导出 Site 列表时加 `Where(s => s.ManagedSource == null)`，不导出托管 Site。
- **导入**：`Import.cshtml.cs` 若在导入前去重（查现有 Site），同样只比对非托管 Site；导入的 Site 默认 `ManagedSource=null`。

### 4. 一键拉取全部

`Index.cshtml` 的「一键拉取全部」按钮调 `SiteCatalogApiController.FetchAllModels`。该接口遍历所有 Site 拉模型——**Codex 隐藏 Site 不应走 OpenAI catalog 拉取**（它的 BaseUrl 是 codex backend，catalog 接口不适用）。需在 `FetchAllModels` 或其查询 Site 处过滤 `ManagedSource==null`：

- 文件：`src/AITool.Web/Controllers/Admin/SiteCatalogApiController.cs:219`（FetchAllModels）。
- 改：遍历 Site 前加 `.Where(s => s.ManagedSource == null)`。

> Codex 账号的模型拉取走 T05 的 `CodexModelFetcher`（独立按钮），不复用 SiteCatalog。

---

## 性能考量

### 引用原则
- **P2 内存 join**：Sites 表量级小（用户自建 + Codex 托管），全表载入后 Where 过滤开销可忽略。

### 本任务特有
- **过滤条件下推**：用 `.Where(...)` 在 SQL 层过滤（SqlSugar 翻译成 `WHERE ManagedSource IS NULL`），而非 `.ToListAsync()` 后内存过滤。减少传输。
- **索引**：`ManagedSource` 列无索引，但 Site 表行数小（百级），全表扫描过滤可忽略。若未来托管类型增多（多种 ManagedSource），可加索引，当前不必要。
- **批量删除防护查询**：增加一个 `ManagedSource==null` 条件，不增加显著开销。

---

## 验收标准

1. 创建 Codex 账号后，`Admin/Sites` 列表**不显示**对应隐藏 Site。
2. 现有用户自建 Site 正常显示。
3. 导出文件不含托管 Site。
4. 「一键拉取全部」不尝试拉取 Codex 隐藏 Site（不报错、不浪费时间）。
5. 直接 POST 托管 SiteId 到 Sites 删除接口 → 不删除（被防护），需走 Codex 删除接口。

---

## 风险

- **SqlSugar null 翻译**：`== null` 在某些 ORM 翻译为 `= NULL`（永远假）而非 `IS NULL`。**优先用 `string.IsNullOrEmpty(s.ManagedSource)`** 更稳。实现时用实际数据验证。
- **FetchAllModels 过滤遗漏**：若漏改，Codex Site 会尝试 OpenAI catalog 拉取并失败（BaseUrl 是 codex backend，`/v1/models` 不存在），导致「一键拉取全部」报错或卡顿。务必过滤。
- **其它遍历 Site 的地方**：全局搜索 `_dbContext.Sites` 的所有使用点，确认是否有其它接口需要过滤托管 Site（如 dashboard 统计 Site 数量、健康检查等）。统计类若把托管 Site 算进去可能误导，按需过滤。

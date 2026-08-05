# T13 — 集成验证与性能验证

> 状态：编译与回归验证已完成 ✅；真实上游端到端验证待运行环境
> 前置依赖：T01～T12 全部完成
> 关联总览章节：横切性能原则全部

## 已完成的验证

### 编译验证
- 全解决方案 `dotnet build` 通过：0 警告 0 错误（Domain / Application / Infrastructure / Web）。
- 启动期 CodeFirst 会自动建 `CodexAccounts` 表并为 `Sites` 补 `ManagedSource`/`ExtraHeadersJson` 两列（nullable，兼容老库）。

### 回归验证（关键，确保现有功能不受影响）
- **集成测试 177/177 全通过**（含代理转发、协议桥接、SSE、用量日志、对话等核心链路）。
- ApplicationTests 87/88 通过；唯一失败的 `RouteCircuitStateStoreTests.Block_does_not_refresh_expiration_for_already_blocked_site` 是**预存的 flaky 时间测试**（与 Codex 改动无关：我没改 `RouteCircuitStateStore`）：单独跑该类 3 次全过（6/6），仅在完整套件高负载下偶发。

### Models / Routes / Chat 自动联动（核心零改动验证，待真实数据确认）
- 设计上这三处业务代码未改：Codex 模型经 Provisioner 写为 `ModelLibraryItem` + `SiteModelMapping(指向隐藏Site)`；`ProxyRouteRule.SiteId` 指向隐藏 Site；`/api/admin/chat/targets` 经 `ProxyRequestMetadataCache` 自动包含。
- 隐藏 Site 的 `ProtocolType="Responses"`（SupportsOpenAi/Anthropic=false），经现有 `ProxyProtocolBridge` 桥接，对话测试可调用。
- `ForwardHeaders` 注入对普通 Site（ExtraHeaders 空）零开销，回归通过。

### 性能验证（设计层面，待运行环境实测）
| 项 | 设计 | 状态 |
| --- | --- | --- |
| 缓存命中 | 写后 InvalidateRouteTargets，5s 重建 | ✅ 全部写路径已调用 |
| ExtraHeaders 反序列化 | 缓存构建期(5s)一次，非每请求 | ✅ TryParseExtraHeaders 在 GetRouteTargetsAsync 内 |
| Token 刷新 single-flight | OAuth 客户端 SemaphoreSlim | ✅ |
| 额度查询防抖 | 30s IMemoryCache + single-flight | ✅ |
| 后台服务节流 | 刷新 5min/恢复 2min，错峰 500ms | ✅ |
| 转发热路径零退化 | 额度/冷却判定不进正常转发；ExtraHeaders 空时空字典 | ✅ |
| accounts 列表无 N+1 | 单次 ToListAsync | ✅ |
| 连接池复用 | OAuth/额度/模型均 AddHttpClient | ✅ |

## 待真实上游环境验证（需有效 Codex token）

以下需用真实账号在运行环境验证，无法纯代码确认：

1. **OAuth 登录闭环**：start-oauth → 浏览器登录 → 粘贴回调 URL → complete-oauth → 账号建库 → 模型进 Models/Routes/Chat。
2. **凭证导入**：上传真实 CPA codex JSON，确认 account_id/email/plan_type 解析正确。
3. **上游额度端点**：`chatgpt.com/backend-api/codex/usage` 是否返回可读额度数字。
   - 若有：`CodexQuotaService.TryParseQuota` 按实测字段名补全；自动禁用阈值生效。
   - 若无：RemainingQuota 留 null，自动禁用改由 T10 被动冷却兜底。
4. **被动冷却触发**：耗尽额度触发 usage_limit_reached，确认错误结构（`error.type`/`resets_at`）。
   - ⚠️ 转发错误路径接入（即时冷却）当前**未接线**（见 T10 待办），现由「恢复服务 + 手动重置 + 主动查询」兜底。
5. **token 有效期**：实测 `expires_in`，据此调整 `CodexTokenRefreshService.RefreshLead`（现 1h）与 `ScanInterval`（现 5min）。
6. **Codex 模型 vendor 归类**：gpt-5.x 等在 Models 页面分组是否正确，必要时补 `ModelVendorCatalogService` 映射。

## 待办（运行验证后补做）
- **T10 转发错误路径接入**：基础闭环验证通过后，在 OpenAiProxyController/Responses 控制器错误分支调用 `TryApplyCooldownFromErrorAsync`，实现 usage_limit_reached 即时冷却（当前为兜底模式）。
- 额度端点确认后补全 `TryParseQuota` 解析。
- 若 token 有效期 < 1h，调小 RefreshLead/ScanInterval。

## 目标

端到端验证 Codex 功能闭环，确认 Models/Routes/Chat 自动联动（零业务改动），并执行性能验证清单与回归测试。这是交付前的最终验收关卡。

---

## 涉及文件

无新建。验证以下现有功能未受影响：
- `src/AITool.Web/Pages/Admin/Models/` —— 模型库
- `src/AITool.Web/Pages/Admin/Routes/` —— 路由规则
- `src/AITool.Web/Pages/Admin/Chat/` —— 对话测试
- `src/AITool.Web/Pages/Admin/Sites/` —— 站点管理
- `src/AITool.Web/Controllers/Proxy/` —— 转发链路

---

## 详细步骤

### 1. 端到端闭环验证

按顺序执行：

| # | 操作 | 预期 |
| --- | --- | --- |
| 1 | OAuth 登录一个 Codex 账号 | 账号出现在 Codex 面板 |
| 2 | 导入一个 CPA 凭证文件 | 账号出现，token 解析正确（account_id/email/plan_type） |
| 3 | 检查 `Admin/Models` | Codex 模型（按 plan 分层）出现在模型库，SiteCount ≥ 1 |
| 4 | 检查 `Admin/Routes` | Codex 隐藏 Site 作为可发现站点出现，可为目标模型添加路由规则 |
| 5 | 配置一条路由：外部模型名 → Codex Site 的某模型 | 规则保存成功 |
| 6 | 对话测试选择该模型发送消息 | 转发到 Codex 上游，返回结果（验证 Responses 链路 + Codex 头注入） |
| 7 | 检查 `Admin/Sites` 列表 | **不显示** Codex 隐藏 Site |
| 8 | Codex 面板「刷新额度」 | 额度数字更新（若上游支持） |
| 9 | 设置 AutoDisableThreshold，额度低于阈值 | 账号自动禁用，转发绕开 |
| 10 | 触发上游 usage_limit_reached（耗尽额度测试） | 账号进入冷却，状态显示冷却 + 恢复时间 |
| 11 | 冷却到期 | 自动恢复（若账号未手动禁用） |
| 12 | 「重置额度」（confirm 后） | 清冷却、刷新 token、恢复，账号可重新调用 |
| 13 | 禁用账号 | 转发绕开 |
| 14 | 删除账号（confirm 后） | CodexAccount + 隐藏 Site + 映射 + 路由规则 + 孤立入口 全部清除 |
| 15 | 检查 token 自动刷新 | 临期账号被后台服务刷新，Site.ApiKey 更新 |

### 2. Models / Routes / Chat 自动联动确认（核心零改动验证）

重点确认：**这三处的业务代码未被改动**，但 Codex 模型/路由/对话自动可用。

- **Models**：`ModelLibraryItem` 由 T04 Provisioner 写入；`SiteModelMapping` 指向隐藏 Site。`Models/Index.cshtml.cs:LoadModelGroupsAsync` 的 join 自动包含（按 SiteId）。确认 SiteCount 正确。
- **Routes**：`ProxyRouteRule.SiteId` 指向隐藏 Site；`RouteRulesApiController.GetSiteInstances` / `DiscoverSites` 经 `ProxyRequestMetadataCache` 自动包含。确认可选为目标。
- **Chat**：`/api/admin/chat/targets`（`ChatApiController.GetTargets`）→ `GetChatTargetsAsync` 自动包含 Codex 模型。确认下拉框出现。`ProtocolType="Responses"` 经 `ProxyProtocolBridge` 正确桥接。

### 3. 性能验证清单

| 项 | 验证方法 | 预期 |
| --- | --- | --- |
| **缓存命中** | 转发链路读 `CachedProxyRouteTarget`，观察 `ProxyRequestMetadataCache` 重建频率 | 5s 一次重建，非每请求 |
| **ExtraHeaders 反序列化** | 观察缓存构建期是否每 Site 反序列化一次 | 缓存重建时反序列化（5s），非每请求 |
| **Token 刷新 single-flight** | 并发触发同账号刷新（手动 + 自动） | OAuth 客户端只一次真实上游请求 |
| **额度查询防抖** | 30s 内多次刷新额度 | 只一次真实上游请求（手动 forceRefresh 除外） |
| **后台服务节流** | 观察刷新/恢复服务的扫描周期与错峰 | 每 5min / 2min，无风暴 |
| **转发热路径** | 正常转发不解析 body、不查冷却字段 | 错误分支才解析；冷却状态走缓存 IsEnabled |
| **内存 join 量级** | accounts 列表查询 | 单次 ToListAsync，无 N+1 |
| **连接池复用** | OAuth/额度/模型拉取 HttpClient | 经 AddHttpClient 注册，连接复用 |
| **批量 upsert** | 多账号导入 / 模型映射写入 | InsertRangeAsync，非逐条 |

### 4. 回归测试点

| 功能 | 验证 |
| --- | --- |
| `Admin/Sites` 列表/增删改/导入导出 | 不受 `ManagedSource` 过滤影响，正常 |
| 「一键拉取全部」 | 不尝试拉取 Codex 隐藏 Site |
| 现有 OpenAI/Anthropic 站点转发 | ForwardHeaders 注入对普通 Site 无影响（ExtraHeaders 空） |
| 现有 Models/Routes/Chat | 现有模型/路由/对话正常 |
| `ProtocolType` 解析 | 普通 Site 仍按 OpenAI/Anthropic 解析；只有隐藏 Site 为 Responses |
| 数据库升级 | 老库启动后 Site 加两列（nullable）、CodexAccounts 新建，现有数据不丢 |

### 5. 边界与异常验证

- **OAuth state 过期**：10 分钟后完成登录 → 拒绝，提示重新开始。
- **回调 URL 格式错误**：粘贴非法 URL → 友好错误。
- **凭证文件非 codex 类型** → 拒绝。
- **额度查询上游失败** → 面板显示失败，账号不禁用。
- **删除正在被路由引用的账号** → 级联清理路由规则，无残留。
- **并发禁用 + 冷却到期** → 手动禁用优先，不自动恢复。

---

## 性能考量

本任务为验证任务，性能考量体现在上述清单的逐项验证。重点：

- **转发热路径零退化**（P10）：Codex 功能不能让普通转发变慢。ExtraHeaders 空时 MergeExtraHeaders 返回空字典，BuildRequestMessage foreach 不迭代。
- **缓存失效不滥用**（P1）：每次写后失效一次，不要在单流程内多次失效导致频繁重建。
- **后台服务不风暴**（P7）：刷新/恢复服务周期合理，错峰。

---

## 验收标准

1. 全部端到端闭环（1-15）通过。
2. Models/Routes/Chat 业务代码零改动前提下自动联动可用。
3. 性能验证清单全部达标（无退化、无风暴、缓存正确）。
4. 回归测试点全部通过（现有功能不受影响）。
5. 边界与异常验证友好处理。

---

## 风险

- **上游依赖**：端到端验证依赖真实 Codex token 与上游可达。若环境受限，部分步骤（额度查询、usage_limit 触发、模型拉取）可能无法完整验证，标注「需真实环境补充验证」。
- **协议桥接兼容**：Codex 上游 Responses 格式与现有 `ProxyProtocolBridge` 的 Responses 转换需兼容。若 Codex 的 Responses 响应有特殊字段，可能需微调桥接（属 T06 范畴的延伸）。验证时重点看对话测试返回是否正常。
- **vendor 归类**：Codex 模型在 Models 页面的分组（vendor）若不正确，需补 `ModelVendorCatalogService` 映射。
- **并发场景**：多账号同时刷新/冷却/恢复的并发正确性难以手动复现，依赖代码审查 + 单元测试（single-flight、列更新）保证。

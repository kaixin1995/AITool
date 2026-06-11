
## 阶段记录 — 2026-06-12 代理运行时 DI 注册去重扩展方法提取（AddProxyRuntimeInfrastructure）

### 动机
Core/Program.cs 和 Web/Program.cs 中有约 30 行几乎完全相同的代理运行时 DI 注册代码，属于典型的复制粘贴冗余。随着 Core/Admin 双宿主架构推进，需要将这部分共享注册归纳到统一的扩展方法中，消除双副本维护负担。

### 变更内容
- **新增** `src/AITool.Infrastructure/DependencyInjection/ProxyRuntimeInfrastructureExtensions.cs`
  - 包含 `AddProxyRuntimeInfrastructure()` 扩展方法，统一注册代理运行时共享服务
  - 关键设计：`bool useCoreRuntimeConfigProviderForCache` 参数控制 ProxyRequestMetadataCache 是否从配置快照读取
  - Core 宿主传入 true（走快照），Web 宿主传入 false（走数据库）
  - 懒加载模式：在工厂 lambda 内部通过 `sp.GetRequiredService<ICoreRuntimeConfigProvider>()` 延迟解析，避免在注册阶段调用 BuildServiceProvider()
- **修改** `src/AITool.Core/Program.cs` — 内联 DI 注册替换为 `AddProxyRuntimeInfrastructure()` 调用，保留 5 个 Core 独有服务
- **修改** `src/AITool.Web/Program.cs` — 内联 DI 注册替换为 `AddProxyRuntimeInfrastructure()` 调用，保留 Web 独有服务

### 三层 DI 扩展方法架构
Infrastructure/DependencyInjection/ 现在包含 3 个扩展方法文件：
- `CommonInfrastructureExtensions.cs` — 三宿主共享（Core + Web + Admin）
- `AdminInfrastructureExtensions.cs` — Web + Admin 共享
- `ProxyRuntimeInfrastructureExtensions.cs` — Core + Web 共享（本轮新增）

### 编译与测试
- 编译：0 错误，123 warnings（均为既有 nullable 警告）
- 代理相关测试：86 个全部通过
- 全量测试：54 个全部通过，零回归

### 当前状态
- DI 注册去重第三层已完成
- Core/Program.cs 和 Web/Program.cs 中仍有少量未使用的 using 语句待清理

## 阶段记录 — 2026-06-12 统一 DeveloperInvocationTraceStore 到 Infrastructure/Proxy 层（提交 f707163）

### 动机
DeveloperInvocationTraceStore（含 5 个辅助类型）在 Core/Services 和 Web/Services 各存在一份副本，
代码几乎完全相同（Core 版本多一个 OnTraceCompleted 事件），属于典型的复制粘贴冗余。

### 变更内容
- **新增** `src/AITool.Infrastructure/Proxy/DeveloperInvocationTraceStore.cs`
  - 命名空间 `AITool.Infrastructure.Proxy`
  - 包含 6 个 public 类型：DeveloperInvocationTraceStore（含 OnTraceCompleted 事件）、
    DeveloperInvocationTraceRequest、DeveloperInvocationAttempt、DeveloperInvocationResult、
    DeveloperInvocationTraceEntry、DeveloperInvocationTraceAttempt
  - 统一版本保留 Core 版本的 OnTraceCompleted 事件机制
- **修改** `src/AITool.Core/GlobalUsings.cs` — 添加 6 个类型别名
- **修改** `src/AITool.Web/GlobalUsings.cs` — 添加 6 个类型别名
- **删除** `src/AITool.Core/Services/DeveloperInvocationTraceStore.cs`（Git 识别为 rename）
- **删除** `src/AITool.Web/Services/DeveloperInvocationTraceStore.cs`

### 无需修改的文件
- Core/Program.cs、Web/Program.cs — DI 注册通过 GlobalUsings 别名自动解析
- Core/Services/DeveloperInvocationTraceQueryService.cs — 通过 GlobalUsings 别名解析
- Core/Services/CoreDeveloperTraceEventPublisher.cs — 通过 GlobalUsings 别名解析
- 所有 Controller 文件 — `using AITool.Core.Services;` / `using AITool.Web.Services;` 仍需保留给其他类型

### 测试结果
- DeveloperTrace 相关测试 7/7 通过
- Proxy + Chat 相关测试 94/94 通过

### 当前 Core/Services 剩余文件（3 个）
- DeveloperInvocationTraceQueryService.cs
- CoreDeveloperTraceEventPublisher.cs
- CoreRouteFallbackEventPublisher.cs

### 当前 Web/Services 剩余文件（1 个）
- AdminAuthService.cs

### Infrastructure/Proxy 目录现在共 12 个文件
已包含 DeveloperInvocationTraceStore.cs。
# Core / Admin 拆分当前进展说明
> **协议与运行时基础已经打好，双宿主架构进入实质性页面迁移阶段；Admin 宿主已迁移 12 组页面（UsageLogs + Conversations + Chat + Developer/Invocations + System/Settings + 第三批 8 组），覆盖全部管理页面与系统配置能力；AITool.Core 物理独立宿主已创建并编译通过（纯代理运行时，无 DB/无 Razor/无认证）；ProxyRequestMetadataCache 已完成全部 6 个 DB 依赖方法的双路径改造（快照 vs DB），Core 宿主可完全从配置快照驱动代理运行时，零数据库依赖；Core / Admin 联合部署基础配置已完成——独立 appsettings.json（Core 5029、Admin 5030）、独立 launchSettings.json、Admin 启动时自动同步配置到 Core 的 HostedService；Developer/Invocations 页面迁移成功突破了此前"不可迁移"的判断，通过 CoreAdminClient 代理模式解决了三重运行时依赖问题；System/Settings 页面通过将熔断参数更新和缓存失效封装到 Core 全量同步流程中，彻底解决了 Admin 页面对 Core 运行时对象的直接依赖；Web 侧已迁移页面/控制器/服务的最终清理已完成——删除 10 个冗余文件、清理 7 个 DI 注册、重写/删除 3 个过时测试，Web/Services/ 仅剩 2 个仍有运行时消费者的文件；AITool.Web 中仅剩 Chat/Index 页面（Admin 已有对应版本）和 ChatApiController（深度代理运行时依赖，不可迁移）。**
### 已完成：Patch 增量同步协议

本轮完成了 Patch 增量同步协议的设计、实现和测试，Admin 写入后不再每次发送完整配置快照，而是只发送变更类别的完整列表，Core 端收到后仅替换对应集合并定向失效相关缓存。

#### 已创建/修改文件

**数据模型：**
- `src/AITool.Application/CoreRuntime/ConfigPatchPayload.cs` — Patch 增量同步载荷数据模型，包含 7 个可空实体集合 + `Categories` 指定变更类别 + `PatchHash` 用于去重
- `src/AITool.Application/CoreRuntime/CorePatchSyncResult.cs` — Patch 同步结果模型（`Applied`/`Ignored`/`ConfigVersion`/`ConfigHash`）

**Core 端点：**
- `src/AITool.Core/Controllers/Core/CoreConfigSyncController.cs` — 新增 `[HttpPost("patch-sync")]` 端点，包含 `PatchSync`、`MergePatch`、`InvalidateCacheForCategories` 三个核心方法

**Admin 侧客户端：**
- `src/AITool.Infrastructure/CoreRuntime/CoreAdminClient.cs` — 新增 `PatchSyncAsync(ConfigPatchPayload, CancellationToken)` 方法

**Admin 侧缓存失效服务：**
- `src/AITool.Admin/Services/AdminCacheInvalidationService.cs` — 重写为基于类别的增量同步，包含 6 个 `InvalidateXxxAsync` 公共方法 + `SyncToCoreAsync` 核心方法 + `BuildPatchAsync` 构建方法 + `FullSyncFallbackAsync` 回退方法 + `ComputePatchHash` 哈希方法

**测试：**
- `tests/AITool.Core.IntegrationTests/CorePatchSyncTests.cs` — 新增 6 个增量同步集成测试
- `tests/AITool.IntegrationTests/Core/CoreConfigSyncTests.cs` — 移除 5 个已迁移到 Core 测试工程的 patch 测试，仅保留 2 个全量同步测试

#### Patch 增量同步协议设计

**7 个实体类别：** Sites、Models、SiteModelMappings、RouteEntries、RouteRules、AccessKeys、RuntimeSettings

**同步流程：**
1. Admin 写入数据库后调用 `AdminCacheInvalidationService.InvalidateXxxAsync()`
2. 服务内部根据变更类别从数据库只读对应表，构建 `ConfigPatchPayload`
3. 通过 `CoreAdminClient.PatchSyncAsync()` 发送到 Core
4. Core 校验类别名称合法性、检查是否已初始化、比较版本号
5. Core 调用 `MergePatch()` 将 Patch 数据合并到当前快照副本
6. Core 调用 `InvalidateCacheForCategories()` 根据变更类别定向失效缓存
7. 如果 Core 尚未初始化（返回 400），Admin 自动回退到全量同步

**类别到缓存失效的映射：**
| 变更类别 | 失效方法 | 说明 |
|---|---|---|
| AccessKeys | InvalidateAccessKeys | 访问密钥缓存 |
| RuntimeSettings | InvalidateRuntimeSettings | 运行时设置缓存 |
| Sites / RouteRules / RouteEntries | InvalidateRuntimeRouteTargets | 路由选择缓存 |
| Models / SiteModelMappings | InvalidateRuntimeRouteTargets | 兜底映射/已启用模型缓存 |

**去重机制：**
- Patch 使用 `PatchHash`（SHA256）对携带数据进行哈希，Core 端比较合并后的全量哈希
- 如果合并后哈希与当前快照一致，说明 Patch 实际没有带来变化，返回 `ignored: true`

**版本号规则：**
- Admin 每次同步递增 `_configVersion`（`Interlocked.Increment`）
- Core 拒绝版本号不大于当前版本的 Patch，返回 `ignored: true`

#### 测试覆盖（6 个测试）

| 测试方法 | 验证内容 |
|---|---|
| `Patch_sync_rejected_when_core_not_initialized` | Core 未初始化时返回 400 |
| `Patch_sync_updates_only_specified_categories` | 单类别（AccessKeys）Patch 成功，版本递增 |
| `Patch_sync_ignores_stale_version` | 低版本号 Patch 被忽略 |
| `Patch_sync_rejects_unknown_category` | 未知类别名称返回 400 |
| `Patch_sync_rejects_empty_categories` | 空类别列表返回 400 |
| `Patch_sync_updates_multiple_categories` | 多类别（Sites + RouteRules）Patch 成功 |

#### 测试放置位置说明

Patch 同步测试放置在 `tests/AITool.Core.IntegrationTests/`（而非 `tests/AITool.IntegrationTests/`），原因：
- `AITool.IntegrationTests` 引用 `AITool.Web`，其 `WebApplicationFactory<Program>` 启动的是 Web 宿主
- Web 宿主的 `CoreConfigSyncController` 只有 `full-sync` 端点，没有 `patch-sync`
- `patch-sync` 端点仅存在于 `AITool.Core` 的 `CoreConfigSyncController` 中
- `AITool.Core.IntegrationTests` 使用 `CoreHostWebApplicationFactory`（`WebApplicationFactory<AITool.Core.CoreProgramMarker>`），正确启动 Core 宿主

#### Patch 增量同步状态

- **Patch 增量同步协议已完整实现并通过测试**
- **6/6 Patch 同步测试通过**
- **2/2 全量同步测试通过**
- **42/42 Core 集成测试全部通过**
- **Admin 侧 `AdminCacheInvalidationService` 已重写为基于类别的增量同步**
- **回退策略已验证：Core 未初始化时自动回退到全量同步**

## 文档目的

这份文档用于记录 **Core / Admin 双宿主拆分** 当前已经完成的工作，以及后续仍未完成的事项，方便在后续继续开发时快速对齐现状。

这份文档是阶段性进展记录，不替代整体设计文档与协议文档。

相关文档：

- [core-admin-split-design.md](core-admin-split-design.md)
- [core-admin-split-implementation-checklist.md](core-admin-split-implementation-checklist.md)
- [core-admin-split-communication-protocol.md](core-admin-split-communication-protocol.md)
- [core-admin-split-handoff.md](core-admin-split-handoff.md)

---

## 当前目标回顾

当前拆分目标是：

- **Core 进程**：无页面、只提供接口、面向 Claude Code / OpenCode / Codex / 代理客户端
- **Admin 进程**：承载全部 `/Admin/*` 页面，面向管理员
- Core 不再依赖当前业务数据库
- Admin 作为配置权威源与历史数据中心
- Core 与 Admin 通过：
  - 配置同步协议
  - 事件流协议
  - ack / replay / spool
  进行通信

在物理拆成两个独立宿主之前，先在当前单体内把协议与运行模型打通，确保核心链路稳定，再推进双宿主落地。

---

## 一、当前已经完成的内容

### 已完成：Core 运行时配置快照模型

已经完成并接入：

- `src/AITool.Application/CoreRuntime/CoreRuntimeConfigSnapshot.cs`
- `src/AITool.Application/CoreRuntime/ICoreRuntimeConfigProvider.cs`
- `src/AITool.Infrastructure/CoreRuntime/CoreRuntimeConfigProvider.cs`
- `src/AITool.Infrastructure/CoreRuntime/CoreRuntimeConfigSnapshotBuilder.cs`
- `src/AITool.Infrastructure/Operations/SystemRuntimeSettingsService.cs`
- `src/AITool.Application/Operations/ISystemRuntimeSettingsService.cs`

#### 已实现能力

- 定义了 Core 运行时完整配置快照模型
- 支持从 Admin 当前数据库中的主数据构建完整快照
- 支持稳定计算配置哈希 `ConfigHash`
- Core 当前内存持有一份生效快照
- Core 启动时可以从本地 `last-good-config.json` 恢复最后一次成功配置

#### 配置快照能力状态

- 已可工作
- 已有测试覆盖

---

### 已完成：Core 全量同步最小闭环

已经完成并接入：

- `src/AITool.Web/Controllers/Core/CoreConfigController.cs`
- `src/AITool.Web/Controllers/Core/CoreConfigSyncController.cs`
- `src/AITool.Web/Controllers/Core/CoreConfigHandshakeController.cs`
- `src/AITool.Web/Controllers/Core/CoreRuntimeStatusController.cs`
- `src/AITool.Application/CoreRuntime/CoreAdminHandshakeModels.cs`
- `src/AITool.Infrastructure/CoreRuntime/CoreConfigSyncDecisionResolver.cs`

#### 当前接口

- `GET /api/core/config/status`
- `POST /api/core/config/full-sync`
- `POST /api/core/config/handshake`
- `GET /api/core/health`
- `GET /api/core/ready`
- `GET /api/core/runtime/status`

#### 配置同步已实现能力

- Core 无配置时 `ready=false`
- Admin 可下发完整配置快照
- Core 会校验：
  - `ConfigVersion`
  - `ConfigHash`
  - 最小主配置完整性
- 同版本同哈希会被 `ignored`
- 握手时可以返回当前：
  - 已应用配置版本
  - 已应用配置哈希
  - 是否 ready
  - 当前同步建议（`noop` / `full-sync-required` / `admin-version-behind`）

#### 全量同步闭环状态

- 最小闭环已打通
- 已有应用测试与集成测试验证

---

### 已完成：Core 本地 last-good-config 恢复

已经完成并接入：

- `src/AITool.Infrastructure/CoreRuntime/CoreRuntimeConfigFileOptions.cs`
- `src/AITool.Infrastructure/CoreRuntime/CoreRuntimeConfigProvider.cs`
- `src/AITool.Web/Program.cs`

#### 本地恢复已实现能力

- Core 在设置当前快照后，会把配置写入本地文件
- Core 启动时会尝试恢复该文件
- 如果没有可恢复配置，会保持 `not-ready`，等待 Admin 下发首个完整快照

#### 本地恢复状态

- 已可工作
- 已有测试验证

---

### 已完成：Core 事件模型与最小事件总线

已经完成并接入：

- `src/AITool.Application/CoreRuntime/CoreAdminEventModels.cs`
- `src/AITool.Infrastructure/CoreRuntime/CoreAdminEventEnvelopeBuilder.cs`
- `src/AITool.Infrastructure/CoreRuntime/CoreEventSequenceProvider.cs`
- `src/AITool.Infrastructure/CoreRuntime/CoreAdminEventBus.cs`

#### 事件模型已实现能力

- 定义统一 `CoreAdminEventEnvelope`
- 已定义事件负载：
  - `CoreUsageLogEvent`
  - `CoreConversationTurnEvent`
- 使用单进程全局递增 `SequenceId`
- 提供最小内存事件总线

#### 事件总线状态

- 已工作
- 已有测试验证

---

### 已完成：两条真实链路接入事件发布

#### UsageLog 链路

已经完成：

- `src/AITool.Infrastructure/CoreRuntime/CoreUsageLogEventPublisher.cs`
- `src/AITool.Infrastructure/Proxy/UsageLogService.cs`

当前行为：

- 继续保留原有数据库落库逻辑
- 同时额外发布一份 UsageLog 事件到 Core 事件总线

#### Conversation 链路

已经完成：

- `src/AITool.Infrastructure/Conversations/CoreConversationEventPublisher.cs`
- `src/AITool.Infrastructure/Conversations/ConversationLogService.cs`

当前行为：

- 继续保留原有对话记录持久化逻辑
- 同时额外发布一份 Conversation 事件到 Core 事件总线

#### 真实事件接入状态

- 已可工作
- 已有测试验证

---

### 已完成：spool / ack / replay 最小可靠闭环

已经完成并接入：

- `src/AITool.Infrastructure/CoreRuntime/CoreEventSpoolOptions.cs`
- `src/AITool.Infrastructure/CoreRuntime/CoreEventSpoolStore.cs`
- `src/AITool.Infrastructure/CoreRuntime/CoreEventSpoolBackgroundService.cs`
- `src/AITool.Application/CoreRuntime/CoreAdminAckModels.cs`
- `src/AITool.Web/Controllers/Core/CoreEventAckController.cs`

#### 事件可靠性接口

- `POST /api/core/events/ack`
- `GET /api/core/events/replay?afterSequenceId=...`

#### 可靠事件已实现能力

- 发布的事件会进入内存总线
- 后台服务会把事件写入本地 spool JSONL 文件
- Core 可以：
  - 读取最新 `latestSequenceId`
  - 判断 `hasSpoolBacklog`
  - 根据 `afterSequenceId` replay 积压事件
  - 根据 ack 序号清理已确认事件

#### 可靠事件闭环状态

- 最小可靠事件闭环已打通
- 已有应用测试与集成测试验证

---

### 已完成：Admin 最小客户端与 UsageLog 真实消费入库

已经完成：

- `src/AITool.Infrastructure/CoreRuntime/CoreAdminClient.cs`
- `src/AITool.Infrastructure/CoreRuntime/AdminUsageLogEventIngestor.cs`
- `tests/AITool.ApplicationTests/CoreRuntime/AdminUsageLogEventIngestorTests.cs`
- `tests/AITool.IntegrationTests/Core/CoreUsageLogAdminIngestTests.cs`
- `tests/AITool.IntegrationTests/Core/CoreAdminClientTests.cs`

#### Admin 消费已实现能力

- Admin 侧最小客户端已支持：
  - `HandshakeAsync(...)`
  - `FullSyncAsync(...)`
  - `AckAsync(...)`
  - `ReplayAsync(...)`
- Admin 现在已经不只是“会调用 Core 协议”，而是能够：
  - 读取 `replay`
  - 解析 `usage-log` 事件
  - 将 UsageLog 事件写入 Admin 当前数据库中的 `ProxyUsageLogs`
  - 返回最大连续 sequence，供上层提交 `ack`
- 已做最小幂等去重，避免 replay 或重复提交写入完全相同的日志记录

#### Admin 消费状态

- **UsageLog 这条真实链路已经具备最小消费入库闭环**
- 但目前仍属于骨架级能力，还没有与独立 Admin 页面真正连通

---

### 已完成：独立 AITool.Admin 宿主骨架

已经完成：

- `src/AITool.Admin/AITool.Admin.csproj`
- `src/AITool.Admin/Program.cs`
- `AITool.slnx` 已包含 `AITool.Admin`
- `tests/AITool.Admin.IntegrationTests/AITool.Admin.IntegrationTests.csproj`
- `tests/AITool.Admin.IntegrationTests/AdminHostSmokeTests.cs`

#### Admin 宿主已实现能力

- `AITool.Admin` 宿主工程已经创建
- `AITool.Admin` 已能单独 `dotnet build`
- 已接入：
  - Razor Pages
  - Controllers
  - Authentication / Authorization
  - 数据库
  - CoreAdminClient
- `AITool.Admin.IntegrationTests` 已能单独拉起 Admin 宿主并完成最小 smoke test

#### Admin 宿主状态

- **宿主本体已能单独编译成功**
- **独立测试工程已打通最小宿主启动验证**

---

### 已完成：第一批宿主共享边界继续收口

本轮已继续推进：

- `src/AITool.Infrastructure/Hosting/ModelVendorCatalogService.cs`
- `src/AITool.Infrastructure/Hosting/AnalyticsBackgroundQueryExecutor.cs`
- `src/AITool.Web/Services/ModelVendorCatalogService.cs`
- `src/AITool.Web/Services/AnalyticsBackgroundQueryExecutor.cs`
- `src/AITool.Web/Pages/Admin/Models/Index.cshtml.cs`
- `src/AITool.Web/Pages/Admin/System/Settings.cshtml.cs`
- `src/AITool.Web/Controllers/Admin/AnalyticsApiController.cs`
- `tests/AITool.ApplicationTests/Hosting/ModelVendorCatalogServiceTests.cs`
- `tests/AITool.IntegrationTests/System/SystemSettingsCacheTests.cs`

#### 本轮已实现能力

- 已把 `ModelVendorCatalogService` 的核心实现从 `AITool.Web.Services` 收口到 `AITool.Infrastructure.Hosting`
- `AITool.Web.Services.ModelVendorCatalogService` 已降为最小桥接壳，避免继续保留一份完整重复实现
- Web 侧模型管理页已显式引用共享宿主层中的 `ModelVendorCatalogService`、`ModelVendorCatalog`、`ModelVendorDefinition`
- 已补充共享宿主层的应用测试，覆盖：
  - 命中厂商规则时返回正确厂商定义
  - 未命中规则时正确回退到“未分类”厂商定义
- 已把 `AnalyticsBackgroundQueryExecutor` 及其相关状态类型从 `AITool.Web.Services` 收口到 `AITool.Infrastructure.Hosting`
- `AITool.Web.Services.AnalyticsBackgroundQueryExecutor` 已降为最小桥接壳，避免 Web 宿主继续保留完整后台查询执行器实现
- Web 侧系统设置页与 Analytics 控制器已显式改为引用共享宿主层中的 `AnalyticsBackgroundQueryExecutor`
- 已通过 `SystemSettingsCacheTests` 验证系统设置页在使用共享宿主层查询执行器后，缓存失效链路仍保持正常
- 本轮继续对 `src/AITool.Web/Services/ProxyRequestMetadataCache.cs` 做了第一轮职责分区收口，先在同一类型内显式标出 Core 运行时元数据入口、Admin 查询元数据入口与共享失效入口，并把后台聊天/开发者/路由查询相关缓存失效进一步聚拢到独立方法中，后续可在不破坏代理链路的前提下继续拆分
- 本轮继续把 `ProxyRequestMetadataCache` 中偏 Admin 查询的一组方法拆到 `src/AITool.Web/Services/ProxyRequestMetadataCache.AdminQueries.cs`，让主文件优先保留运行时路径、共享失效入口与共享辅助逻辑，进一步降低运行时逻辑与后台查询逻辑的混杂度
- 本轮继续把路由规则页和客户端模拟器中被 `ProxyRequestMetadataCache` 直接引用的查询结果类型抽到 `src/AITool.Web/Services/ProxyRequestMetadataQueryModels.cs`，降低缓存服务对控制器和页面命名空间的直接耦合，为后续继续下沉到独立 Admin 查询服务做准备
- 本轮已新增 `src/AITool.Web/Services/AdminQueryMetadataService.cs` 作为后台查询元数据服务雏形，并已把 Chat、RouteRules、Developer Invocations 这三处 Admin 调用方的查询读取从 `ProxyRequestMetadataCache` 直接依赖切到该服务，进一步降低后台页面对运行时缓存对象的直接耦合
- 本轮继续清掉了 `Developer Invocations` 页面中对 `ProxyRequestMetadataCache` 已不再使用的直接字段依赖，说明这批后台查询读取切换后，页面层已经可以仅依赖 `AdminQueryMetadataService` 获取只读查询元数据
- 本轮又新增 `src/AITool.Web/Services/DeveloperInvocationTraceQueryService.cs`，把后台开发者调用页对 `DeveloperInvocationTraceStore` 的读取动作收口到只读查询门面中，保持代理控制器继续直接写入运行时存储，而管理页读取开始走独立查询入口
- 本轮继续新增 `src/AITool.Web/Services/ModelConcurrencyQueryService.cs`，把开发者并发面板对 `ModelConcurrencyLimiter` 的只读快照读取收口到查询门面中，后台页面不再直接依赖运行时并发控制对象本身的读取接口
- 本轮继续新增 `src/AITool.Web/Services/AdminCacheInvalidationService.cs`，开始把后台写操作对 `ProxyRequestMetadataCache` 的直接失效调用统一收口；当前已覆盖 AccessKeys、Models、SiteCatalog、System Settings 这几类典型后台写入口
- 本轮所有与上述调整直接相关的构建与集成测试已重新通过，包括 `ProxyMetadataCacheTests`、`SystemSettingsCacheTests`、`DeveloperInvocationsPageTests`、`ChatApiTests`、`ClientSimulatorPageTests`、`ProxyFallbackFlowTests` 这批高关联测试

#### 当前边界结论

- `ModelVendorCatalogService` 已可明确归入 **宿主共享层 / 偏 Admin 管理展示能力**
- `AnalyticsBackgroundQueryExecutor` 已可明确归入 **宿主共享层 / 偏 Admin 统计分析后台能力**
- 它们不再适合继续停留在 `AITool.Web.Services` 里作为 Web 专属完整实现
- 这一步也为后续 `AITool.Admin` 真正接管 Models / Analytics / System 相关页面能力提供了更稳定的共享基础

### 已完成：Admin 门面服务扩展——后台页面/控制器对运行时对象的直接依赖剥离

本轮完成了后台页面和控制器对运行时对象（`ProxyRequestMetadataCache`、`ModelConcurrencyLimiter`）的直接依赖剥离，通过已有门面服务（`AdminCacheInvalidationService`、`AdminConcurrencyControlService`、`ModelConcurrencyQueryService`）替代直接引用。

#### 已完成文件

- `src/AITool.Web/Pages/Admin/Models/Index.cshtml.cs` — 两个 PageModel（`IndexModel`、`CreateModelModel`）的 `ProxyRequestMetadataCache?` 替换为 `AdminCacheInvalidationService`，6 处 `_metadataCache?.InvalidateXxx()` 改为 `_cacheInvalidation.InvalidateXxx()`
- `src/AITool.Web/Pages/Admin/Models/Edit.cshtml.cs` — `EditModel` 的 `ProxyRequestMetadataCache?` 替换为 `AdminCacheInvalidationService`，3 对失效调用替换
- `src/AITool.Web/Pages/Admin/Sites/Index.cshtml.cs` — 两个 PageModel（`IndexModel`、`CreateModel`）的 `ProxyRequestMetadataCache?` 替换为 `AdminCacheInvalidationService`，4 处失效调用替换
- `src/AITool.Web/Pages/Admin/Sites/Edit.cshtml.cs` — `EditModel` 的 `ProxyRequestMetadataCache?` 替换为 `AdminCacheInvalidationService`，1 处失效调用替换
- `src/AITool.Web/Pages/Admin/Sites/Import.cshtml.cs` — `ImportModel` 的 `ProxyRequestMetadataCache?` 替换为 `AdminCacheInvalidationService`，1 处失效调用替换
- `src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs` — 混合依赖剥离：`ProxyRequestMetadataCache` → `AdminCacheInvalidationService`，`ModelConcurrencyLimiter` → `AdminConcurrencyControlService`，6 处失效调用 + 1 处并发控制调用替换
- `src/AITool.Web/Controllers/Admin/ModelsApiController.cs` — `ModelConcurrencyLimiter` → `AdminConcurrencyControlService`，1 处 `UpdateLimit` 调用替换
- `src/AITool.Web/Pages/Admin/Developer/Invocations/Index.cshtml.cs` — `ModelConcurrencyLimiter.RecentRetention` → `ModelConcurrencyQueryService.RecentRetention`，该文件不再引用 `ModelConcurrencyLimiter`
- `tests/AITool.IntegrationTests/Models/ModelEditCacheTests.cs` — 测试适配：构造函数改用 `AdminCacheInvalidationService`，保留 `ProxyRequestMetadataCache` 用于只读断言
- `src/AITool.Web/Controllers/Admin/ChatApiController.cs` — `ProxyRequestMetadataCache` 直接依赖剥离，改为通过 `AdminQueryMetadataService` 门面服务路由只读查询（`GetEnabledModelAsync`、`GetRuntimeSettingsAsync`、`GetRouteTargetsForModelAsync`、`GetFallbackTargetAsync`），`ModelConcurrencyLimiter.AcquireAsync` 保留为直接依赖（属于代理转发运行时链路，不属于管理操作）
- `src/AITool.Web/Services/AdminQueryMetadataService.cs` — 新增 4 个只读查询方法支撑 ChatApiController 剥离需求

#### 本轮替换模式

所有页面/控制器均采用一致替换模式：

- `ProxyRequestMetadataCache? _metadataCache` + `[ActivatorUtilitiesConstructor]` → `AdminCacheInvalidationService _cacheInvalidation`（非空），fallback 构造函数使用 `= null!`
- `_metadataCache?.InvalidateXxx()` → `_cacheInvalidation.InvalidateXxx()`（去掉 `?.`，因为门面保证非空）
- `ModelConcurrencyLimiter _concurrencyLimiter` → `AdminConcurrencyControlService _concurrencyControl`
- `ModelConcurrencyLimiter.RecentRetention` → `ModelConcurrencyQueryService.RecentRetention`

#### 门面服务扩展状态

- **已可工作**
- **全部测试通过**

---

### 已完成：当前主线完整验证

在持续推进期间，以下一直保持通过：

- `dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj`
- `dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj`
- `dotnet build src/AITool.Web/AITool.Web.csproj`
- `dotnet build src/AITool.Admin/AITool.Admin.csproj`
- `dotnet test tests/AITool.Admin.IntegrationTests/AITool.Admin.IntegrationTests.csproj`

最新一轮完整验证结果：

- ApplicationTests：`101/101` 通过
- IntegrationTests：`127/127` 通过
- Admin.IntegrationTests：`46/46` 通过
- Core.IntegrationTests：`28/28` 通过（3 冒烟 + 20 端点测试 + 5 代理端点测试）
- AITool.Web：构建成功，`0 error`（34 warnings 均为既有 nullable 警告）
- AITool.Admin：单独构建成功
- AITool.Core：单独构建成功，`0 error`，`0 warning`
- **总计：302 个测试全部通过，0 失败**

### 已完成：Core API 端点集成测试

本轮为 AITool.Core 独立宿主新增了 20 个全面的 API 端点集成测试，覆盖所有 8 个 Tier 1 Core API 端点。

#### 已完成文件

- `tests/AITool.Core.IntegrationTests/CoreApiEndpointTests.cs` — 新增，20 个端点集成测试
- `tests/AITool.Core.IntegrationTests/CoreHostSmokeTests.cs` — 修改，`CoreHostWebApplicationFactory` 从 `internal` 改为 `public`，供端点测试引用

#### 测试覆盖范围（20 个测试，5 个控制器）

| 控制器 | 端点 | 测试数 | 说明 |
|--------|------|--------|------|
| CoreRuntimeStatusController | GET /health, GET /api/core/health | 2 | 健康检查 |
| CoreRuntimeStatusController | GET /api/core/ready | 2 | 就绪检查（同步前/后） |
| CoreRuntimeStatusController | GET /api/core/runtime/status | 2 | 运行时状态（同步前/后） |
| CoreConfigController | GET /api/core/config/status | 2 | 配置状态（同步前/后） |
| CoreConfigSyncController | POST /api/core/config/full-sync | 7 | 全量同步（合法/重复/零版本/错误哈希/无站点/无密钥/成功后状态） |
| CoreConfigHandshakeController | POST /api/core/config/handshake | 2 | 握手（无配置/匹配配置） |
| CoreEventAckController | POST /api/core/events/ack | 2 | 事件确认（合法/负序号） |
| CoreEventAckController | GET /api/core/events/replay | 2 | 事件回放（初始空/负序号） |

#### 测试隔离设计

每个测试方法创建独立的 `CoreHostWebApplicationFactory` 实例（`await using var factory = new CoreHostWebApplicationFactory()`），确保测试之间完全隔离，不会因共享 singleton `CoreRuntimeConfigProvider` 而互相影响。此前曾使用 `IClassFixture` 共享工厂，但因全量同步测试写入共享状态导致 "before sync" 测试失败，已改为每测试独立工厂。

#### Core API 端点测试状态

- **20 个端点测试全部通过**
- **覆盖所有 8 个 Tier 1 Core API 端点**
- **每个测试完全隔离，无共享状态**

### 已完成：集成测试缓存失效修复

本轮修复了 AITool.IntegrationTests 中因直接数据库操作绕过 IMemoryCache 导致的 17 个测试失败。所有混合测试（同时涉及代理端点和后台管理操作）已统一采用"直接 DB 操作 + 手动缓存失效"模式。

#### 修复背景

在双宿主拆分过程中，部分集成测试原来通过 Admin API 触发缓存失效，但迁移后 Admin API 已不再由 Web 宿主承载。这些测试改为直接操作数据库，但 `ProxyRequestMetadataCache` 使用 IMemoryCache 缓存代理元数据，直接 DB 修改不会触发缓存刷新，导致代理仍使用旧缓存值，测试断言失败。

#### 修复文件与内容

- `tests/AITool.IntegrationTests/Proxy/ProxyFallbackFlowTests.cs` — 3 个测试添加缓存失效：
  - `Get_models_returns_unauthorized_after_access_key_is_disabled` → `InvalidateAccessKeys()`
  - `Get_models_refreshes_after_route_entry_is_deleted` → `InvalidateRouteTargets()`
  - `Save_route_rules_persists_latest_manual_order_used_by_followup_request` → `InvalidateRouteTargets()`

- `tests/AITool.IntegrationTests/Proxy/AnthropicProxyControllerTests.cs` — 2 个测试添加缓存失效：
  - `Post_messages_returns_unauthorized_after_access_key_is_disabled` → `InvalidateAccessKeys()`
  - `Post_messages_returns_not_found_after_route_entry_is_deleted` → `InvalidateRouteTargets()`

- `tests/AITool.IntegrationTests/Chat/ChatApiTests.cs` — 1 个测试添加缓存失效：
  - `Put_concurrency_applies_new_limit_immediately` → `InvalidateRuntimeRouteTargets()`（因为 `ModelConcurrencyLimiter.AcquireAsync` 每次调用都会从缓存读取 MaxConcurrency 并覆盖 `UpdateLimit` 的结果）

- `tests/AITool.IntegrationTests/Conversations/ConversationPageTests.cs` — 已删除（纯 Admin 测试，已由 Admin 测试工程覆盖）

#### 缓存失效方法选择规则

| 数据库修改对象 | 对应失效方法 | 说明 |
|---|---|---|
| `ProxyAccessKeys` | `InvalidateAccessKeys()` | 访问密钥启用/禁用 |
| `ProxyRouteEntries` / `ProxyRouteRules` | `InvalidateRouteTargets()` | 路由条目/规则增删改 |
| `SiteModelMappings.MaxConcurrency` | `InvalidateRuntimeRouteTargets()` | 并发限制修改（含 ModelConcurrencyLimitsCacheKey） |

#### 修复状态

- **17 个失败测试全部修复，0 失败**
- **274 个测试全部通过**

这说明当前主宿主与核心协议链路仍然稳定，没有被双宿主改造破坏，同时独立 Admin 宿主已经不只是能启动，而且开始承载真实页面与只读接口并通过验证。

---

## 二、当前还未完成的内容

下面这些是下一阶段必须继续推进的工作。

---

### 已关闭：AITool.Admin 独立测试工程跑通

这部分已经完成。

#### Admin 测试宿主状态

- `AITool.Admin.IntegrationTests` 已能独立启动 Admin 宿主并完成最小 smoke test
- 之所以单独拆测试工程，是为了避免与 `AITool.Web` 顶级 `Program` 的宿主入口冲突
- 后续不再需要围绕宿主入口做基础打通，只需在其上继续增加真实页面或接口验证

---

### 未完成：宿主共享层边界完全清理

虽然第一批共享能力已经抽取，但还没有完全结束。

#### 还需要继续确认的内容

- `DeveloperInvocationTraceStore`
- `ModelConcurrencyLimiter`
- `ProxyRequestMetadataCache`
- `ModelVendorCatalogService`
- `AnalyticsBackgroundQueryExecutor`

#### 这些服务要继续判断

- 哪些应该继续留在 `AITool.Web.Services`
- 哪些应该抽到可共享宿主层
- 哪些应该只属于 Admin
- 哪些后续应通过 Core API 替代，而不应在 Admin 宿主直接引用

#### 宿主共享层当前边界结论

宿主共享层已经从“完全混杂”进入“边界已暴露”的阶段，但还没有最终定型。
当前已可以明确：

- `ModelVendorCatalogService` 已开始收口到 `AITool.Infrastructure.Hosting`
- 它更适合归入宿主共享层 / 偏 Admin 管理展示能力，而不是继续作为 `AITool.Web.Services` 的专属完整实现
- `AnalyticsBackgroundQueryExecutor` 已开始收口到 `AITool.Infrastructure.Hosting`，并可明确视为宿主共享层 / 偏 Admin 统计分析后台能力
- `DeveloperInvocationTraceStore`、`ModelConcurrencyLimiter`、`ProxyRequestMetadataCache` 仍直接挂在 Core / Proxy 运行时链路上，当前不宜贸然整体迁出
- `ProxyRequestMetadataCache` 当前已经完成第五轮结构收口：除了职责显式分区、Admin 查询 partial 拆分、查询结果模型抽离外，后台查询读取已开始通过 `AdminQueryMetadataService` 门面服务对外暴露，Chat / RouteRules / Developer 三处调用方已经不再直接读取运行时缓存对象上的这批查询方法，并且 `Developer Invocations` 页面已清掉多余的直接缓存字段依赖；后续仍需继续扩大覆盖面并决定是否进一步迁入独立 Admin 宿主
- `DeveloperInvocationTraceStore` 当前也已开始做最小边界收口：代理控制器继续直接写入运行时跟踪存储，而开发者后台页面读取已改走 `DeveloperInvocationTraceQueryService`，为后续把读写职责进一步分离提供了稳定起点
- `ModelConcurrencyLimiter` 当前也已开始做最小边界收口：运行时获取/释放并发许可逻辑保持不变，而开发者后台页面读取最近并发快照已改走 `ModelConcurrencyQueryService`，为后续把后台查询读取与运行时控制对象继续分离提供了稳定起点
- `ProxyRequestMetadataCache` 除了后台只读查询门面外，后台写操作的缓存失效也已通过 `AdminCacheInvalidationService` 完成收口；当前 Models、Sites、RouteRules、SystemSettings 这四类后台写入口的全部页面和控制器已全部切换到 `AdminCacheInvalidationService`，不再直接依赖 `ProxyRequestMetadataCache`，说明 Admin 侧对运行时缓存对象的直接依赖正在同时从”读”和”写”两个方向下降
- `ModelConcurrencyLimiter` 的后台写操作也已通过 `AdminConcurrencyControlService` 收口，覆盖 `UpdateLimit` 和 `TryDeferRuntimeRouteTargetsRefresh` 两个写操作入口；后台页面的只读常量引用也已通过 `ModelConcurrencyQueryService.RecentRetention` 收口；当前 `RouteRulesApiController` 和 `ModelsApiController` 已全部切换到门面服务，不再直接依赖 `ModelConcurrencyLimiter`
- `DeveloperInvocationTraceStore` 后台页面已不再有任何直接引用
- `ChatApiController` 已完成 `ProxyRequestMetadataCache` 依赖剥离：所有只读查询（`GetEnabledModel`、`GetRuntimeSettings`、`GetRouteTargetsForModel`、`GetFallbackTarget`）改走 `AdminQueryMetadataService`；`ModelConcurrencyLimiter.AcquireAsync` 保留直接依赖，因为它是代理转发运行时链路的一部分，不属于管理操作；至此 **Admin 侧所有页面和控制器均已完成对 `ProxyRequestMetadataCache` 的直接依赖剥离**，无剩余 Admin 文件直接引用该运行时缓存对象

---

### 已完成：Admin 真实事件链路最小消费入库

已经完成：

- `src/AITool.Infrastructure/CoreRuntime/AdminUsageLogEventIngestor.cs`
- `tests/AITool.ApplicationTests/CoreRuntime/AdminUsageLogEventIngestorTests.cs`
- `tests/AITool.IntegrationTests/Core/CoreUsageLogAdminIngestTests.cs`

#### UsageLog 消费已实现能力

- Admin 现在已经不只是“会调用 Core 协议”，而是能够：
  - 读取 `replay`
  - 解析 `usage-log` 事件
  - 将 UsageLog 事件写入 Admin 当前数据库中的 `ProxyUsageLogs`
  - 返回最大连续 sequence，供上层提交 `ack`
- 已做最小幂等去重，避免 replay 或重复提交写入完全相同的日志记录

#### UsageLog 消费状态

- **UsageLog 这条真实链路已经具备最小消费入库闭环**
- 当前 `/Admin/UsageLogs` 真实页面还没有切到独立 `AITool.Admin` 宿主
- 当前阶段只能说明：数据链路已经具备页面迁移条件，但页面本身尚未迁移

---

### 已完成：第一块真实 Admin 页面/接口迁移验证

已经完成：

- `src/AITool.Admin/Pages/Admin/UsageLogs/Index.cshtml`
- `src/AITool.Admin/Pages/Admin/UsageLogs/Index.cshtml.cs`
- `src/AITool.Admin/Controllers/Admin/UsageLogsApiController.cs`
- `tests/AITool.Admin.IntegrationTests/UsageLogsPageSmokeTests.cs`
- `tests/AITool.Admin.IntegrationTests/AdminHostSmokeTests.cs`

#### 第一块页面迁移已实现能力

- `/Admin/UsageLogs` 已开始由独立 `AITool.Admin` 宿主承载
- 页面基础路由、标题、筛选骨架、汇总卡片和列表框架已迁入 `AITool.Admin`
- `AITool.Admin` 中已新增对应的 `UsageLogsApiController`，开始承接列表、汇总和链路详情这三类只读查询接口
- UsageLogs 页面脚本已开始迁入 `AITool.Admin`，当前已具备基础的筛选、分页、汇总加载和详情抽屉联动逻辑
- `AITool.Admin.IntegrationTests` 已能直接访问 `/Admin/UsageLogs` 页面与对应只读 API，并验证最小页面/API 联动
- 本轮已把独立 Admin 宿主中的 UsageLogs 查询接口继续向 Web 侧现有语义靠齐，补齐了查询 DTO、分页响应 DTO、详情 DTO、汇总 DTO，以及站点模型名称解析逻辑
- 本轮已补强 Admin 独立宿主的 UsageLogs 集成测试数据，覆盖了失败重试后成功、不同来源、不同协议和模型关键字筛选这几类真实查询场景
- 本轮继续补强了 `src/AITool.Admin/Pages/Admin/UsageLogs/Index.cshtml` 的前端展示，把模型列、状态徽标与详情抽屉继续向真实链路信息靠齐，开始展示请求模型、站点模型、重试/回退/最终结果标记，以及更完整的尝试级指标和错误信息

#### 第一块页面迁移状态

- **第一块真实 Admin 页面迁移验证已打通**
- 当前页面骨架、只读接口和最小宿主联动已经成立
- `/Admin/UsageLogs` 对应的列表、汇总、链路详情三类只读 API 已在 `AITool.Admin` 中接入并通过独立宿主测试验证
- 页面脚本已经开始迁移，但样式与更复杂的前端交互仍属于“继续收口中”状态
- 本轮完成后，`AITool.Admin.IntegrationTests` 中关于 UsageLogs 的独立宿主验证已提升到 **4/4 通过**，能覆盖页面访问、汇总列表、请求详情与筛选条件生效
- 本轮完成后，独立 Admin 宿主中的 UsageLogs 页面对真实链路细节的展示完整度又向前推进了一步，但与 `AITool.Web` 中更成熟的页面体验仍未完全收口

---

### 已完成：第二块真实 Admin 页面/接口迁移——Conversations 对话记录

已经完成：

- `src/AITool.Admin/Pages/Admin/Conversations/Index.cshtml` — 完整迁入对话记录 Razor 视图（含内联 CSS、JavaScript），仅将 `@model` 命名空间从 `AITool.Web.Pages.Admin.Conversations.IndexModel` 改为 `AITool.Admin.Pages.Admin.Conversations.IndexModel`
- `src/AITool.Admin/Pages/Admin/Conversations/Index.cshtml.cs` — 完整迁入对话记录 PageModel，检查 `ConversationLogEnabled` 设置
- `src/AITool.Admin/Controllers/Admin/ConversationsApiController.cs` — 完整迁入对话记录查询接口（sessions/turns/title/delete），仅依赖只读查询链路（`IConversationLogStore`、`ConversationExtractionService`），不依赖写入侧的 `ConversationLogBatchWriter` 或 `IConversationLogService`
- `src/AITool.Admin/Controllers/Admin/RouteRulesApiController.cs` — 新增轻量路由入口查询端点，直接从 `AppDbContext` 查询 `ProxyRouteEntries` + `ProxyRouteRules`，替代原有对 `AdminQueryMetadataService` → `ProxyRequestMetadataCache` 运行时缓存的依赖
- `src/AITool.Admin/Pages/Shared/_Layout.cshtml` — 在监控运维分区新增"对话记录"链接，受 `ConversationLogEnabled` 条件控制
- `src/AITool.Admin/Pages/Shared/_LayoutMinimal.cshtml` — 修正 `@namespace` 从 `AITool.Web.Pages.Shared` 为 `AITool.Admin.Pages.Shared`
- `src/AITool.Admin/Program.cs` — 新增对话查询只读链路 DI 注册（`ConversationLogFileOptions`、`IConversationLogStore`、`ConversationExtractionService`）

#### 已从 AITool.Web 删除的文件

- `src/AITool.Web/Controllers/Admin/ConversationsApiController.cs`
- `src/AITool.Web/Pages/Admin/Conversations/Index.cshtml`
- `src/AITool.Web/Pages/Admin/Conversations/Index.cshtml.cs`
- `src/AITool.Web/Pages/Admin/Conversations/` 目录（空目录已移除）

#### 跨依赖解决说明

Conversations 页面 JavaScript 会调用 `/api/admin/route-rules/entries`，原 AITool.Web 中该端点通过 `AdminQueryMetadataService` → `ProxyRequestMetadataCache`（运行时缓存）获取数据。由于 AITool.Admin 无法访问运行时缓存，新建了 `RouteRulesApiController`，直接从数据库查询相同数据并复制合并逻辑（`ProxyRouteEntries` + `ProxyRouteRules` 去重合并），保证功能对等。

#### 写入侧服务保留说明

AITool.Admin 只注册了只读查询链路，写入侧服务（`ConversationLogBatchWriter`、`IConversationLogService`）仍保留在 AITool.Web 中，因为代理控制器（`OpenAiProxyController`、`AnthropicProxyController`）和对话测试（`ChatApiController`）需要继续写入对话记录。

#### 第二块页面迁移状态

- **Conversations 页面/接口完整迁入 AITool.Admin**
- **AITool.Web 中原文件已删除**
- **两个宿主编译均通过，0 error**

---

### 已完成：Chat 对话测试页面迁移到 AITool.Admin

已经完成：

- `src/AITool.Admin/Pages/Admin/Chat/Index.cshtml` — 完整迁入对话测试 Razor 视图（含内联 CSS、JavaScript），仅将 `@model` 命名空间从 `AITool.Web.Pages.Admin.Chat.IndexModel` 改为 `AITool.Admin.Pages.Admin.Chat.IndexModel`
- `src/AITool.Admin/Pages/Admin/Chat/Index.cshtml.cs` — 完整迁入对话测试 PageModel，仅依赖 `ISystemRuntimeSettingsService`，读取 `ConversationLogEnabled` 设置控制对话记录页签显示
- `tests/AITool.Admin.IntegrationTests/ChatPageTests.cs` — 新增 Chat 页面冒烟测试（3 个测试用例），验证页面 UI 元素渲染、对话记录页签根据 ConversationLogEnabled 开关正确显示/隐藏

#### JS API 端点保留说明

Chat 页面中的 JavaScript 调用 `/api/admin/chat/*`（targets、send、send-stream）端点仍由 Core 宿主上的 `ChatApiController` 提供。该控制器深度依赖代理运行时组件（`IProxyForwardService`、`RouteCircuitStateStore`、`ModelConcurrencyLimiter` 等），暂不迁移到 Admin。由于当前部署为单进程模式，JS 使用相对路径访问这些端点不受影响。

#### Chat 页面迁移状态

- **Chat/Index 页面完整迁入 AITool.Admin**
- **3 个 Chat 页面冒烟测试全部通过**
- **46 个 Admin 集成测试全部通过**（含修复后全部 ConversationPageTests）

---

### 已完成：第三批真实 Admin 页面/接口迁移——8 个控制器 + 8 组页面

本轮完成了第三批 Admin 页面和控制器从 AITool.Web 到 AITool.Admin 的迁移，覆盖了除 Chat、Developer/Invocations、System/Settings 以外的全部管理页面。

#### 已迁入 AITool.Admin 的控制器

- `src/AITool.Admin/Controllers/Admin/AccessKeysApiController.cs` — 访问密钥管理接口，使用 `AdminCacheInvalidationService`（Admin 版，async）
- `src/AITool.Admin/Controllers/Admin/AnalyticsApiController.cs` — 可视化分析接口，简化为直接 AppDbContext 查询（不依赖 `AnalyticsBackgroundQueryExecutor`）
- `src/AITool.Admin/Controllers/Admin/DetectionApiController.cs` — 模型检测接口，通过 `IServiceScopeFactory` 解析 `ModelHealthRequestService`
- `src/AITool.Admin/Controllers/Admin/ModelsApiController.cs` — 模型库管理接口，使用 `AdminCacheInvalidationService` + `AdminConcurrencyControlService`
- `src/AITool.Admin/Controllers/Admin/SiteCatalogApiController.cs` — 站点目录接口，使用 `ISiteCatalogClient`
- `src/AITool.Admin/Controllers/Admin/RouteRulesApiController.cs` — 路由规则接口（已在第二轮创建，本轮保留）

#### 已迁入 AITool.Admin 的页面（8 组，20 个文件）

- `Pages/Admin/AccessKeys/` — Index.cshtml + Index.cshtml.cs
- `Pages/Admin/Analytics/` — Index.cshtml + Index.cshtml.cs
- `Pages/Admin/Detection/` — Index.cshtml + Index.cshtml.cs
- `Pages/Admin/DetectionTasks/` — Index.cshtml + Index.cshtml.cs
- `Pages/Admin/ModelHealth/` — Index.cshtml + Index.cshtml.cs
- `Pages/Admin/Routes/` — Index.cshtml + Index.cshtml.cs
- `Pages/Admin/Sites/` — Index.cshtml + Index.cshtml.cs, Edit.cshtml + Edit.cshtml.cs, Export.cshtml + Export.cshtml.cs, Import.cshtml + Import.cshtml.cs（CreateModel 嵌入 Index.cshtml.cs）
- `Pages/Admin/Models/` — Index.cshtml + Index.cshtml.cs, Edit.cshtml + Edit.cshtml.cs（CreateModelModel 嵌入 Index.cshtml.cs）

#### 已新增的 Admin 侧服务

- `src/AITool.Admin/Services/AdminCacheInvalidationService.cs` — Admin 侧缓存失效门面，通过 `CoreAdminClient.FullSyncAsync()` 向 Core 下发全量配置快照；所有方法为 `async Task`（vs Web 版 `void`）
- `src/AITool.Admin/Services/AdminConcurrencyControlService.cs` — Admin 侧并发控制门面（占位实现），后续通过 CoreAdminClient 代理

#### 已从 AITool.Web 删除的文件

控制器（6 个）：
- `src/AITool.Web/Controllers/Admin/AccessKeysApiController.cs`
- `src/AITool.Web/Controllers/Admin/AnalyticsApiController.cs`
- `src/AITool.Web/Controllers/Admin/DetectionApiController.cs`
- `src/AITool.Web/Controllers/Admin/ModelsApiController.cs`
- `src/AITool.Web/Controllers/Admin/SiteCatalogApiController.cs`
- `src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs`

页面（8 组目录）：
- `src/AITool.Web/Pages/Admin/AccessKeys/` — 整个目录删除
- `src/AITool.Web/Pages/Admin/Analytics/` — 整个目录删除
- `src/AITool.Web/Pages/Admin/Detection/` — 整个目录删除
- `src/AITool.Web/Pages/Admin/DetectionTasks/` — 整个目录删除
- `src/AITool.Web/Pages/Admin/ModelHealth/` — 整个目录删除
- `src/AITool.Web/Pages/Admin/Routes/` — 整个目录删除
- `src/AITool.Web/Pages/Admin/Sites/` — 整个目录删除
- `src/AITool.Web/Pages/Admin/Models/` — 整个目录删除

#### AITool.Web 侧边栏与首页调整

- `src/AITool.Web/Pages/Shared/_Layout.cshtml` — 侧边栏已移除已迁移页面的导航链接（Analytics、Sites、Models、Routes、AccessKeys、Detection、DetectionTasks、ModelHealth），仅保留未迁移页面（Chat、Developer/Invocations、UsageLogs、System/Settings）
- `src/AITool.Web/Pages/Index.cshtml` — 简化为代理运行状态概览，不再展示管理仪表盘统计卡片与快捷操作
- `src/AITool.Web/Pages/Index.cshtml.cs` — 简化为从运行时缓存读取代理状态（后续接入 `ProxyRequestMetadataCache`），不再依赖 `AppDbContext` 做 Admin 统计查询

#### 跨依赖解决说明

所有迁移页面和控制器均采用以下统一适配模式：

- `AdminCacheInvalidationService`（Web 版）的同步 `void InvalidateXxx()` → `AdminCacheInvalidationService`（Admin 版）的异步 `async Task InvalidateXxxAsync()`
- `ModelConcurrencyLimiter` → `AdminConcurrencyControlService`（占位实现）
- `AnalyticsBackgroundQueryExecutor` → 直接 `AppDbContext` 同步查询
- `AdminQueryMetadataService` / `ProxyRequestMetadataCache` → 直接 `AppDbContext` 查询
- `IServiceScopeFactory` 用于 `DetectionApiController` 中的 `ModelHealthRequestService` 解析

#### 仍保留在 AITool.Web 的文件

控制器：
- `ChatApiController` — 深度依赖代理运行时（IProxyForwardService、RouteCircuitStateStore、ModelConcurrencyLimiter、IUsageLogService 等），**不可迁移**

页面：
- `Developer/Invocations/Index` — 已通过 CoreAdminClient 代理模式迁入 AITool.Admin（原判断"不可迁移"已被突破），AITool.Web 中原文件暂时保留
- `System/Settings` — 已迁入 AITool.Admin（通过 Core 全量同步自动处理熔断参数和缓存失效），AITool.Web 中原文件暂时保留
- `Chat/Index` — Admin 已有对应版本，Web 版保留是因为 JS API 端点仍由 Core 的 `ChatApiController` 提供

#### 第三批页面迁移状态

- **8 个控制器 + 8 组页面全部迁入 AITool.Admin**
- **AITool.Web 中原文件已删除**
- **AITool.Web 侧边栏和首页已调整**
- **两个宿主编译均通过，0 error**
- **全部 112 个测试通过（101 ApplicationTests + 6 IntegrationTests + 5 Admin.IntegrationTests）**

---

### 已完成：Developer/Invocations 开发者工具页面迁移到 AITool.Admin

本轮完成了开发者工具页面（调用追踪、客户端模拟器、并发检测三合一）从 AITool.Web 到 AITool.Admin 的迁移。该页面此前因深度依赖 Core 运行时内存单例（`DeveloperInvocationTraceStore`、`ModelConcurrencyLimiter`、`ProxyRequestMetadataCache`）被认为"不可迁移"，本轮通过 CoreAdminClient 代理模式彻底解决了这一依赖。

#### 已创建/修改文件

共享 DTO（AITool.Application，双宿主共用）：
- `src/AITool.Application/CoreRuntime/CoreDeveloperQueryModels.cs` — 新增 8 个开发者查询共享 DTO：
  - `CoreDeveloperInvocationListResponse` — 分页调用记录列表响应
  - `CoreDeveloperInvocationSummary` — 调用记录摘要
  - `CoreDeveloperInvocationDetail` — 调用记录详情
  - `CoreDeveloperInvocationAttempt` — 调用尝试记录
  - `CoreDeveloperConcurrencyResponse` — 并发查询响应
  - `CoreDeveloperConcurrencyItem` — 并发检测项
  - `CoreDeveloperMetadataResponse` — 客户端模拟器元数据响应
  - `CoreDeveloperModelItem` — 调试模型项

Core API 端点（AITool.Core）：
- `src/AITool.Core/Controllers/Core/CoreDeveloperQueryController.cs` — 新增 4 个开发者数据查询端点：
  - `GET /api/core/developer/invocations/list` — 分页调用追踪列表
  - `GET /api/core/developer/invocations/detail` — 单条调用追踪详情
  - `GET /api/core/developer/concurrency` — 当前模型并发状态快照
  - `GET /api/core/developer/metadata` — 客户端模拟器元数据（密钥、模型列表）

Admin 侧客户端方法：
- `src/AITool.Infrastructure/CoreRuntime/CoreAdminClient.cs` — 新增 4 个开发者查询代理方法 + `BaseAddress` 属性：
  - `GetDeveloperInvocationsAsync` — 分页列表查询
  - `GetDeveloperInvocationDetailAsync` — 详情查询
  - `GetDeveloperConcurrencyAsync` — 并发状态查询
  - `GetDeveloperMetadataAsync` — 模拟器元数据查询
  - `BaseAddress` 属性 — 从 HttpClient 基地址推导 Core 公开 URL

Admin 侧页面（AITool.Admin）：
- `src/AITool.Admin/Pages/Admin/Developer/Invocations/Index.cshtml.cs` — Admin 版 PageModel，通过 CoreAdminClient 代理所有运行时数据查询，不再直接访问 Core 运行时内存单例
- `src/AITool.Admin/Pages/Admin/Developer/Invocations/Index.cshtml` — Admin 版 Razor 视图（2288 行），含三页签 UI（调用追踪、客户端模拟器、并发检测），关键适配：
  - `@model` 命名空间改为 `AITool.Admin.Pages.Admin.Developer.Invocations.IndexModel`
  - 客户端模拟器的 `fetch('/v1/models')` 改为 `fetch(getBaseUrl() + '/v1/models')`，确保模型列表请求路由到 Core
  - 客户端模拟器的 `fetch(options.path)` 改为 `fetch(getBaseUrl() + options.path)`，确保所有模拟请求路由到 Core
  - 调用追踪/并发检测的 AJAX 请求继续走 Admin PageModel 的 `@Url.Page(...)` 端点，内部由 CoreAdminClient 代理到 Core

#### 架构适配说明

该页面迁移的关键突破在于解决了三重运行时依赖：

1. **调用追踪数据**：原 Web 版直接读取 `DeveloperInvocationTraceStore` 内存单例 → Admin 版通过 CoreAdminClient 调用 Core 的 `/api/core/developer/invocations/list` 和 `/detail` 端点
2. **并发状态数据**：原 Web 版直接读取 `ModelConcurrencyQueryService`（→ `ModelConcurrencyLimiter`）→ Admin 版通过 CoreAdminClient 调用 Core 的 `/api/core/developer/concurrency` 端点
3. **模拟器元数据**：原 Web 版直接读取 `AdminQueryMetadataService`（→ `ProxyRequestMetadataCache`）→ Admin 版通过 CoreAdminClient 调用 Core 的 `/api/core/developer/metadata` 端点

客户端模拟器的 API 请求（`/v1/chat/completions` 等）直接从浏览器发往 Core 宿主（通过 `getBaseUrl()` 获取 Core 地址），不经过 Admin 转发，保持低延迟。

#### Developer/Invocations 页面迁移状态

- **Developer/Invocations 页面完整迁入 AITool.Admin**
- **Core 和 Admin 两个宿主编译均通过，0 error，0 warning**
- **客户端模拟器请求正确路由到 Core，调用追踪/并发检测数据通过 CoreAdminClient 代理获取**
- **AITool.Web 中原 Developer/Invocations 文件保留（运行时依赖仍在 Web 侧生效）**

---

### 已完成：AITool.Core 物理独立宿主创建

本轮完成了 `AITool.Core` 物理独立宿主工程的创建，将代理运行时（Proxy Runtime）从 `AITool.Web` 完整复制出来，作为独立的纯 API 代理服务宿主。

#### 已创建文件

宿主骨架：
- `src/AITool.Core/AITool.Core.csproj` — Core 宿主工程文件，仅依赖 Application + Infrastructure + NLog，无 Hangfire/EF Core/Razor Pages
- `src/AITool.Core/Program.cs` — 纯代理运行时宿主入口，无 DB/无 Razor/无认证/无 Hangfire

代理控制器（从 Web 复制，命名空间改为 `AITool.Core.Controllers.Proxy`）：
- `Controllers/Proxy/AnthropicProxyController.cs`
- `Controllers/Proxy/OpenAiProxyController.cs`
- `Controllers/Proxy/OpenAiProxyController.Helpers.cs`
- `Controllers/Proxy/OpenAiProxyController.Responses.cs`
- `Controllers/Proxy/OpenAiProxyController.Streaming.cs`

Core 管理控制器（从 Web 复制，命名空间改为 `AITool.Core.Controllers.Core`）：
- `Controllers/Core/CoreConfigController.cs`
- `Controllers/Core/CoreConfigHandshakeController.cs`
- `Controllers/Core/CoreConfigSyncController.cs`
- `Controllers/Core/CoreEventAckController.cs`
- `Controllers/Core/CoreRuntimeStatusController.cs`

运行时服务（从 Web 复制，命名空间改为 `AITool.Core.Services`）：
- `Services/ProxyProtocol/Bridge.Core.cs`
- `Services/ProxyProtocol/Helpers.cs`
- `Services/ProxyProtocol/RequestConvert.cs`
- `Services/ProxyProtocol/ResponseConvert.cs`
- `Services/ProxyProtocol/Responses.cs`
- `Services/ProxyProtocol/StreamToAnthropic.cs`
- `Services/ProxyRequestMetadataCache.cs`
- `Services/ProxyRequestMetadataCache.AdminQueries.cs`
- `Services/ProxyRequestMetadataQueryModels.cs`
- `Services/ModelConcurrencyLimiter.cs`
- `Services/ModelConcurrencyQueryService.cs`
- `Services/DeveloperInvocationTraceStore.cs`
- `Services/DeveloperInvocationTraceQueryService.cs`
- `Services/ConsoleProxyLogFormatter.cs`
- `Services/AdminQueryMetadataService.cs`

解决方案文件：
- `AITool.slnx` — 已添加 `AITool.Core` 项目引用

Core 程序标记：
- `src/AITool.Core/CoreProgramMarker.cs` — 测试工程通过此类型定位 Core 程序集

Core 独立测试工程：
- `tests/AITool.Core.IntegrationTests/AITool.Core.IntegrationTests.csproj`
- `tests/AITool.Core.IntegrationTests/CoreHostSmokeTests.cs` — 3 个冒烟测试（宿主启动、/health 端点、/api/core/health 端点）

#### 已删除的冗余文件

以下三个文件是从 Web 复制后的桥接壳，但其真实实现已在 `AITool.Infrastructure.Hosting` 中：
- `Services/AppVersionInfo.cs` — 已删除（使用 `Infrastructure.Hosting.AppVersionInfo`）
- `Services/HttpExceptionLoggingFilter.cs` — 已删除（使用 `Infrastructure.Hosting.HttpExceptionLoggingFilter`）
- `Services/HttpLogFormatter.cs` — 已删除（使用 `Infrastructure.Hosting.HttpLogFormatter`）

#### Core 宿主设计原则

- **无数据库**：不依赖 EF Core / AppDbContext，运行时配置从 Admin 通过全量同步下发到本地文件
- **无 Razor Pages**：纯 API 控制器，不提供任何页面
- **无认证**：代理端点自行验证 AccessKey，不需要 ASP.NET Identity
- **无 Hangfire**：后台任务由 HostedService 承担
- **配置快照驱动**：使用 `CoreRuntimeConfigProvider` 从 `last-good-config.json` 恢复配置
- **事件驱动**：通过 `CoreAdminEventBus` + `CoreEventSpoolStore` 可靠推送事件到 Admin

#### Program.cs 关键差异（vs Web 的 Program.cs）

| 特性 | AITool.Web | AITool.Core |
|------|-----------|-------------|
| Razor Pages | ✅ 注册 | ❌ 不注册 |
| 数据库 / EF Core | ✅ 注册 | ❌ 不依赖 |
| Hangfire | ✅ 注册 | ❌ 不使用 |
| 认证 / 授权 | ✅ 注册 | ❌ 不使用 |
| 配置来源 | 数据库直接查询 | Admin 下发配置快照 |
| 默认端口 | 5030 | 5029（`CoreServer:Port`） |
| 版本号 | 1.0.1.4 | 1.0.1.4-core |

#### Core 宿主创建状态

- **AITool.Core 独立编译通过，0 error，0 warning**
- **AITool.Core 独立测试通过，3 个冒烟测试全部通过（宿主启动、/health、/api/core/health）**
- **整个解决方案（11 个项目，含 Core.IntegrationTests）编译通过**
- **277 个测试全部通过（101 + 127 + 46 + 3），0 失败**
- **AITool.Web 仍可正常编译，不受影响**

---

### 已完成：ProxyRequestMetadataCache 快照驱动适配

本轮完成了 `ProxyRequestMetadataCache` 在 Core 宿主中从配置快照读取代理运行时数据的全面适配。所有 6 个依赖数据库的运行时方法均已完成双路径改造：Core 宿主走快照读取，Web/Admin 宿主走原有数据库查询，互不影响。

#### 已修改文件

- `src/AITool.Core/Services/ProxyRequestMetadataCache.cs` — 6 个 DB 依赖方法全部重写为双路径：
  - `GetRuntimeSettingsAsync`：检查 `_configProvider`，从 `snapshot.RuntimeSettings` 映射到 `CachedProxyRuntimeSettings`（Core 宿主只填充代理运行所需字段子集）
  - `GetAccessKeysAsync`：检查 `_configProvider`，从 `snapshot.AccessKeys.Where(x => x.IsEnabled)` 映射到 `Dictionary<string, CachedProxyAccessKey>`
  - `GetRouteTargetsAsync`：检查 `_configProvider`，从 `snapshot.Sites` + `snapshot.RouteRules` 联查映射到 `List<CachedProxyRouteTarget>`
  - `GetEnabledModelsAsync`：检查 `_configProvider`，从 `snapshot.Models.Where(x => x.IsEnabled)` 映射到 `Dictionary<Guid, CachedEnabledModel>`
  - `GetFallbackMappingsAsync`：检查 `_configProvider`，三方联查 `snapshot.SiteModelMappings` + `snapshot.Sites` + `snapshot.Models`，按 ModelId 分组取首个作为回退目标
  - 每个方法在快照路径下使用 `_memoryCache.GetOrCreateAsync` + `Task.FromResult`（同步返回，无 DB 访问）

- `src/AITool.Core/Services/ProxyRequestMetadataCache.AdminQueries.cs` — `GetModelConcurrencyLimitsAsync` 同样完成双路径改造：从快照读取 `SiteModelMappings.Where(x => x.IsEnabled && x.MaxConcurrency > 0)`，构建 `Dictionary<string, int>`（key 为 `"{SiteId:N}:{RemoteModelName}"`）

- `src/AITool.Core/Program.cs` — DI 注册更新：
  - 添加 `using Microsoft.Extensions.Caching.Memory;`
  - `ProxyRequestMetadataCache` 注册改为工厂模式，显式传入 `ICoreRuntimeConfigProvider`

- `src/AITool.Core/Controllers/Core/CoreConfigSyncController.cs` — 缓存失效链路接入：
  - 添加 `using AITool.Core.Services;`
  - 构造函数新增 `ProxyRequestMetadataCache` 依赖
  - `SetCurrent(snapshot)` 后立即调用 `InvalidateAccessKeys()`、`InvalidateRuntimeSettings()`、`InvalidateRuntimeRouteTargets()` 强制缓存从新快照重建

#### 新增测试文件

- `tests/AITool.Core.IntegrationTests/CoreProxyEndpointTests.cs` — 5 个代理端点集成测试：
  - `Models_endpoint_without_config_returns_unauthorized` — 未同步配置时 /v1/models 返回 401
  - `Models_endpoint_after_sync_with_valid_key_returns_ok` — 同步后有效密钥返回 200 + 模型列表
  - `Models_endpoint_after_sync_with_wrong_key_returns_unauthorized` — 无效密钥返回 401
  - `Models_endpoint_after_sync_without_auth_returns_unauthorized` — 无认证头返回 401
  - `Models_endpoint_after_resync_with_new_key_returns_ok` — 重新同步后新密钥生效（验证缓存失效）

#### 双构造函数设计

```csharp
// 原有构造函数（Web/Admin 宿主，依赖 DB）
public ProxyRequestMetadataCache(IMemoryCache memoryCache, IServiceScopeFactory scopeFactory)

// 新增构造函数（Core 宿主，依赖配置快照）
public ProxyRequestMetadataCache(IMemoryCache memoryCache, IServiceScopeFactory scopeFactory, ICoreRuntimeConfigProvider configProvider)
```

原有的 Web/Admin 宿主仍使用原始构造函数，`_configProvider` 为 `null`，所有运行时方法继续走 DB 路径，零影响。

#### 快照数据覆盖完整性

| 运行时方法 | 快照数据来源 | 状态 |
|---|---|---|
| `GetRuntimeSettingsAsync` | `snapshot.RuntimeSettings` | ✅ 已适配 |
| `GetAccessKeysAsync` | `snapshot.AccessKeys` | ✅ 已适配 |
| `GetRouteTargetsAsync` | `snapshot.Sites` + `snapshot.RouteRules` | ✅ 已适配 |
| `GetEnabledModelsAsync` | `snapshot.Models` | ✅ 已适配 |
| `GetFallbackMappingsAsync` | `snapshot.SiteModelMappings` + `Sites` + `Models` | ✅ 已适配 |
| `GetModelConcurrencyLimitsAsync` | `snapshot.SiteModelMappings` | ✅ 已适配 |

#### 快照适配状态

- **全部 6 个 DB 依赖方法已完成快照适配**
- **双构造函数保证 Web/Admin 宿主零影响**
- **Core 宿主编译 0 error、0 warning**
- **5 个代理端点集成测试全部通过**
- **全部 155 个测试通过（28 Core + 127 Web/Admin），零回归**

---

### 已关闭：Core 真正物理独立宿主

这部分已经完成。Core 物理独立宿主已创建，代理运行时已完全迁入 `AITool.Core`，配置快照驱动已实现。

---

### 已完成：Core / Admin 联合部署基础配置

本轮完成了 Core 和 Admin 双宿主联合部署所需的基础配置文件和启动时自动同步机制。

#### 已创建/修改文件

配置文件：
- `src/AITool.Core/appsettings.json` — Core 宿主独立配置，包含 `CoreServer:Port`（5029）、`ProxyForwarding`、日志级别等
- `src/AITool.Admin/appsettings.json` — Admin 宿主独立配置，包含 `AdminServer:Port`（5030）、`CoreServer:BaseUrl`（`http://127.0.0.1:5029/`）、认证哈希、日志级别等

开发配置：
- `src/AITool.Core/Properties/launchSettings.json` — Core 开发环境配置，端口 5029
- `src/AITool.Admin/Properties/launchSettings.json` — 修正 Admin 开发环境端口从 48036/48037 统一到 5030

启动同步服务：
- `src/AITool.Admin/Services/CoreConfigSyncHostedService.cs` — Admin 启动后自动将数据库配置构建为快照并同步到 Core 宿主的后台服务
  - 启动后等待 2 秒（给 Core 启动时间）
  - 从数据库通过 `ISystemRuntimeSettingsService.BuildCoreRuntimeConfigSnapshotAsync` 构建完整快照
  - 通过 `CoreAdminClient.FullSyncAsync` 下发到 Core
  - 如果 Core 尚未就绪，按指数退避重试（最多 5 次，基础间隔 3 秒）
  - 在后台线程执行，不阻塞 Admin 宿主启动
- `src/AITool.Admin/Program.cs` — 注册 `CoreConfigSyncHostedService`

#### 联合部署端口分配

| 宿主 | 默认端口 | 配置键 | 说明 |
|------|---------|--------|------|
| AITool.Core | 5029 | `CoreServer:Port` | 代理主端口，面向 API 客户端 |
| AITool.Admin | 5030 | `AdminServer:Port` | 管理页面端口，面向管理员 |
| AITool.Web（单体） | 5029 | `Server:Port` | 兼容模式，同时跑代理 + 管理页面 |

#### Admin → Core 通信配置

- Admin 通过 `CoreServer:BaseUrl` 配置项指定 Core 的地址，默认 `http://127.0.0.1:5029/`
- Admin 通过 `CoreAdminClient`（HttpClient）与 Core 通信
- 通信能力：握手、全量同步、事件确认、事件回放

#### 联合部署配置状态

- **Core 和 Admin 均已有独立 appsettings.json**
- **Core 和 Admin 均已有 launchSettings.json 开发配置**
- **Admin 启动时自动同步配置到 Core 的机制已实现**
- **全部 302 个测试通过（101 + 127 + 46 + 28），零回归**

#### 核心宿主当前状态

- 协议与事件模型已经具备迁出条件
- 但物理 Core 宿主拆分还没开始真正实施

---

### 已完成：System/Settings 系统设置页面迁移到 AITool.Admin

本轮完成了系统设置页面从 AITool.Web 到 AITool.Admin 的迁移，并同步修复了 Core 全量同步链路中熔断参数未更新的架构缺口。

#### 已创建文件

Admin 侧页面：
- `src/AITool.Admin/Pages/Admin/System/Settings.cshtml` — 完整迁入系统设置 Razor 视图，仅将 `@model` 命名空间改为 `AITool.Admin.Pages.Admin.System.SettingsModel`
- `src/AITool.Admin/Pages/Admin/System/Settings.cshtml.cs` — Admin 版 PageModel，依赖 `ISystemRuntimeSettingsService`（直接 DB 读写）+ `AdminCacheInvalidationService`（触发 Core 全量同步），不再依赖 `RouteCircuitStateStore` 和 `AnalyticsBackgroundQueryExecutor`

测试：
- `tests/AITool.Admin.IntegrationTests/SettingsPageTests.cs` — 3 个集成测试：
  - `Get_settings_page_contains_runtime_setting_fields` — 验证页面展示关键字段
  - `Get_layout_hides_developer_invocation_navigation_when_feature_is_disabled` — 关闭开发者功能后不显示导航
  - `Get_layout_shows_developer_invocation_navigation_when_feature_is_enabled` — 启用开发者功能后显示导航

#### 已修复文件

Core 全量同步熔断参数缺口：
- `src/AITool.Core/Controllers/Core/CoreConfigSyncController.cs` — 新增 `RouteCircuitStateStore` 注入和 `ApplyCircuitBreakerSettings(snapshot)` 方法，确保 Core 收到 full-sync 后立即用新参数更新熔断状态
- `src/AITool.Core/Program.cs` — Core 启动恢复 `last-good-config` 后，从快照中提取熔断参数初始化 `RouteCircuitStateStore`

#### 架构适配说明

Settings 页面在 Web 版有 4 个依赖，迁移时分别处理：

| Web 版依赖 | Admin 版处理方式 |
|---|---|
| `ISystemRuntimeSettingsService` | 直接复用（Admin 已注册） |
| `AdminCacheInvalidationService`（Web 同步版） | 替换为 Admin 版（async，通过 CoreAdminClient 推送） |
| `RouteCircuitStateStore.UpdateOptions()` | 移到 Core 侧 full-sync 流程自动处理 |
| `AnalyticsBackgroundQueryExecutor.InvalidateAll()` | 由 Core full-sync 触发缓存重建自动覆盖 |

关键突破：原来 Settings 保存后需要同步调用 `_circuitStore.UpdateOptions()` 和 `_analyticsQueryExecutor.InvalidateAll()`，迁移后这两个操作被包含在 Core 的全量同步流程中——Admin 调用 `InvalidateRuntimeSettingsAsync()` 后，Core 收到快照会自动执行 `ApplyCircuitBreakerSettings()` 和缓存失效，不再需要 Admin 页面直接操作 Core 运行时对象。

#### System/Settings 页面迁移状态

- **Settings 页面完整迁入 AITool.Admin**
- **Core 全量同步熔断参数缺口已修复**
- **Core 启动熔断参数初始化已修复**
- **3 个 Settings 集成测试全部通过**
- **全部 299 个测试通过（101 ApplicationTests + 127 IntegrationTests + 36 Core.IntegrationTests + 35 Admin.IntegrationTests），零回归**

---

### 已完成：Core 代理转发端到端集成测试

本轮新增 Core 代理转发链路的端到端集成测试，通过 `FakeProxyForwardService` 替换真实转发实现，在不依赖外部上游站点的情况下验证完整的代理链路（鉴权 → 路由解析 → 并发控制 → 转发调用 → 响应回写）。

#### 新增测试文件

- `tests/AITool.Core.IntegrationTests/CoreProxyForwardingTests.cs` — 8 个代理转发集成测试：
  - `Chat_completions_non_streaming_returns_success_with_valid_key` — 非流式 Chat Completions 完整链路成功
  - `Chat_completions_non_streaming_passes_correct_upstream_parameters` — 非流式转发参数正确传递
  - `Chat_completions_streaming_returns_sse_events` — 流式 Chat Completions SSE 事件转发
  - `Chat_completions_with_wrong_key_returns_unauthorized` — 无效密钥返回 401
  - `Chat_completions_without_auth_returns_unauthorized` — 无认证头返回 401
  - `Chat_completions_with_unknown_model_returns_not_found` — 未知模型返回 404
  - `Chat_completions_fallback_to_second_route_when_first_fails` — 多路由回退机制验证
  - `Embeddings_non_streaming_returns_success` — Embeddings 非流式转发

#### 测试设计要点

- **FakeProxyForwardService**：实现 `IProxyForwardService`，记录所有转发调用参数，支持自定义返回结果
- **CoreProxyForwardingWebApplicationFactory**：扩展 `CoreHostWebApplicationFactory`，在 DI 中替换 `IProxyForwardService`
- **配置通过 full-sync API 下发**：每个测试先通过 `POST /api/core/config/full-sync` 推送快照，无需数据库
- **支持路由回退场景**：通过双路由配置快照 + 自定义 `ForwardResultFactory` 验证首条路由失败后自动回退

#### 测试状态

- **8 个新增测试全部通过**
- **全部 36 个 Core 集成测试通过（原 28 + 新 8），零回归**

---

### 已完成：Patch 增量同步协议

本轮实现了完整的 Patch 增量同步协议，使得配置同步从"每次全量"升级为"按类别增量推送"。

#### 新增文件

- `src/AITool.Application/CoreRuntime/ConfigPatchPayload.cs` — Patch 数据模型，包含 7 个可空实体集合 + Categories 标注 + PatchHash 去重
- `tests/AITool.Core.IntegrationTests/CorePatchSyncTests.cs` — 6 个增量同步集成测试

#### 新增功能

- **ConfigPatchPayload**：DTO 包含 7 个可空实体集合（Sites/Models/SiteModelMappings/RouteEntries/RouteRules/AccessKeys/RuntimeSettings），Categories 字段指定哪些变更，PatchHash 用于去重
- **Core patch-sync 端点**：`POST /api/core/config/patch-sync`，校验 Categories 非空、版本号递增、类别已知后，执行 MergePatch 替换指定集合并定向失效缓存
- **类别定向缓存失效**：`InvalidateCacheForCategories` 方法将实体类别映射到具体的缓存失效调用，避免全量刷新
- **Admin 侧 Patch 构建**：`AdminCacheInvalidationService.BuildPatchAsync` 只读取变更类别的数据库表
- **自动回退全量同步**：Core 返回 400（未初始化）时，Admin 自动回退到全量同步
- **SHA256 哈希去重**：Patch 端点比较 PatchHash 与当前快照中对应类别的哈希，相同则忽略

#### 测试覆盖

- 6 个 Patch 同步集成测试全部通过
- 全量同步测试不受影响
- 全部 302 个测试零回归



### 已完成：Admin 侧事件拉取闭环（Replay → Ingest → Ack）

本轮实现了 Admin 侧完整的事件拉取消费闭环，从 Core 拉取积压事件、消费入库、提交确认。

#### 新增文件

- `src/AITool.Admin/Services/CoreEventPullHostedService.cs` — BackgroundService 定时拉取服务
- `src/AITool.Admin/Services/CoreEventPullService.cs` — 核心拉取逻辑，从 HostedService 提取的可独立测试的服务
- `tests/AITool.ApplicationTests/CoreRuntime/CoreEventPullServiceTests.cs` — 4 个单元测试

#### 实现内容

- **CoreEventPullHostedService**：`BackgroundService`，每 10 秒创建新 DI scope 执行一轮拉取，启动后等待 5 秒确保 Core 可能就绪
- **CoreEventPullService**：核心拉取逻辑单元，单次 `PullAndProcessAsync` 执行完整的 Replay → Ingest → Ack 流程
  - 通过 `CoreAdminClient.ReplayAsync` 拉取 `_ackedSequenceId` 之后的所有积压事件
  - 通过 `AdminUsageLogEventIngestor.IngestUsageLogEventsAsync` 消费 UsageLog 类型事件写入 Admin 数据库
  - 通过 `CoreAdminClient.AckAsync` 提交确认，通知 Core 清理已处理的事件
  - 非UsageLog 类型事件也会被 ack 以避免 spool 膨胀
  - 跨轮次维护 `AckedSequenceId` 确保增量拉取
- **DI 注册**：`CoreEventPullService` 注册为 Scoped，`CoreEventPullHostedService` 注册为 HostedService

#### 测试覆盖

- 4 个单元测试全部通过：
  - 空积压事件返回 0，不执行 ack
  - UsageLog 事件完整拉取 → 入库 → ack 流程
  - 非 UsageLog 事件仍被 ack 防止 spool 膨胀
  - 跨轮次 ack 序号正确传递
- 使用 `StubHttpMessageHandler` 模拟 Core HTTP 接口
- 使用 `LoggerStub` 替代真实日志基础设施
- 全部 196 个测试零回归（ApplicationTests 105 + Admin IntegrationTests 49 + Core IntegrationTests 42）

### 已完成：DeveloperTraceEvent 第三条事件链路消费闭环

本轮完成了 DeveloperTraceEvent（开发者调用追踪）从 Core 发布到 Admin 消费的完整闭环，是继 UsageLog 和 ConversationTurn 之后的第三条事件链路。

#### 已创建/修改文件

**Core 侧发布器：**
-  — 新增，开发者调用追踪事件发布器，将  投影为  并发布到 Core 事件总线。请求体/响应体预览截断到 512 字符，避免大负载事件占用带宽

**Admin 侧消费器：**
-  — 新增，Admin 侧开发者追踪内存存储（Singleton），6 小时过期自动清理，最多 100 条
-  — 新增，Admin 侧 DeveloperTrace 事件消费器，从事件流筛选  类型，按 TraceId 去重后写入内存存储

**事件模型扩展：**
-  — 新增  事件负载模型（20+ 字段，覆盖 TraceId、模型、站点、Token 用量、耗时、预览等）
-  — 新增  工厂方法

**Admin 侧集成：**
-  — 改造为三 Ingestor 架构，新增  字段、构造函数参数和 ingest 调用
-  — 新增 （Singleton）和 （Scoped）DI 注册

**Core 侧集成：**
-  — 新增  DI 注册和  事件订阅
-  — 新增  事件，在追踪记录完成时触发

**测试适配：**
-  — 新增  字段和初始化，更新全部 7 处  构造函数调用

#### 架构依赖说明

 初始放在  中，但因  不引用 ，而该发布器需要使用 （位于 ），导致编译失败。最终将该发布器移到 ，因为它自然属于 Core 宿主的代理运行时层。

#### Triple Ingestor 架构

CoreEventPullService 现在同时消费三种事件类型：

| 事件类型 | Ingestor | 消费目标 | 持久化方式 |
|---|---|---|---|
|  | AdminUsageLogEventIngestor | Admin 数据库 | SQLite |
|  | AdminConversationTurnEventIngestor | Admin 本地 JSONL | 文件 |
|  | AdminDeveloperTraceEventIngestor | Admin 内存缓存 | 内存（6h 过期）|

#### DeveloperTraceEvent 闭环状态

- **Core 发布 → Admin 消费完整闭环已打通**
- **全解决方案编译 0 error，0 warning**
- **全部 6 个 CoreEventPullService 单元测试通过**
- **Triple Ingestor 架构已确立，后续新增事件类型只需增加对应 Ingestor**

---
### 已完成：事件流实时消费通道（SSE）

#### SSE 实时通知通道已实现能力

- Core 侧新增 SSE 端点 `GET /api/core/events/stream`（`CoreEventStreamController`），返回 `text/event-stream`，支持多客户端并发订阅
- `CoreAdminEventBus` 扩展为多订阅者 SSE 通知模式：`Subscribe()` 创建独立的 `SseSubscription`（有界通道，深度 64，DropOldest），`NotifyNewEvents()` 广播到所有活跃订阅者
- 已 Dispose 订阅者通过 `WeakReference` 自动 GC 清理，无内存泄漏
- `CoreEventSpoolBackgroundService` 在写入事件到 spool 后自动调用 `NotifyNewEvents`，触发 SSE 推送
- Admin 侧 `CoreEventPullHostedService` 改造为双通道架构：同时运行定时轮询（10 秒，回退）和 SSE 实时监听，收到通知后立即触发拉取，延迟从最大 10 秒降低到亚秒级
- SSE 断线自动重连（5 秒间隔），期间继续依赖定时轮询保证事件最终被拉取
- Admin `Program.cs` 新增 `"CoreSSE"` 命名 HttpClient 注册（无超时，支持无限 SSE 流）

#### 已创建/修改文件

**Core 侧 SSE 端点：**
- `src/AITool.Core/Controllers/Core/CoreEventStreamController.cs` — 新增 SSE 端点控制器，订阅 CoreAdminEventBus 的事件通知并转换为 SSE 格式输出

**Core 侧事件总线扩展：**
- `src/AITool.Infrastructure/CoreRuntime/CoreAdminEventBus.cs` — 新增 `Subscribe()` / `NotifyNewEvents()` 多订阅者 SSE 通知机制，SseSubscription 使用有界 Channel + WeakReference 自动清理
- `src/AITool.Infrastructure/CoreRuntime/CoreEventSpoolBackgroundService.cs` — 写入 spool 后调用 `NotifyNewEvents()` 触发 SSE 推送

**Admin 侧双通道拉取：**
- `src/AITool.Admin/Services/CoreEventPullHostedService.cs` — 重写为双通道架构（SSE 实时 + 定时轮询回退），使用 SemaphoreSlim 桥接通知到拉取循环
- `src/AITool.Admin/Program.cs` — 新增 "CoreSSE" 命名 HttpClient 注册

**Admin 侧客户端：**
- `src/AITool.Infrastructure/CoreRuntime/CoreAdminClient.cs` — 包含 `StreamEventNotificationsAsync` 方法（预留 SSE 客户端流式读取）

#### 测试覆盖（10 个新测试）

| 测试文件 | 测试数 | 验证内容 |
|---|---|---|
| `CoreAdminEventBusSubscriptionTests` | 7 | 独立订阅、广播、有序投递、死引用清理、有界通道 DropOldest、无订阅者安全、Dispose 行为 |
| `CoreEventStreamTests` | 3 | SSE 内容类型/响应头、事件通知推送、多并发客户端 |

#### SSE 实时通知通道状态

- **Core SSE 端点已实现并测试通过**
- **Admin 双通道拉取已实现（SSE 实时 + 定时轮询回退）**
- **10 个新测试全部通过**
- **全解决方案 368 个测试零回归（ApplicationTests 165 + IntegrationTests 108 + Core 47 + Admin 49）**
---

### 已完成：事件 sequence / ack 持久化元数据增强 + Spool 文件轮转/清理策略

当前已经有：

- `CoreEventSequenceProvider`
- `CoreEventSpoolStore`
- `Ack`
- `Replay`

#### 但还没做的更稳妥能力

#### sequence 持久化元数据

- 已实现 `CoreEventSequenceProvider` 的文件持久化：通过 `sequence.meta` 文件存储最新序号
- 原子写入机制：temp-file-then-rename，防止写入中断导致文件损坏
- 启动时恢复优先级：meta 文件 → spool 文件扫描 → 从 0 开始
- 对损坏/无效 meta 文件的容错处理（负数、非数字等）

#### ack 持久化元数据

- 已实现 `CoreEventAckStateStore` 的文件持久化：通过 `ack.meta` 文件存储已确认序号
- 同样采用 temp-file-then-rename 原子写入
- Admin 重启后能从 ack.meta 恢复上次确认位置，避免重复消费

#### Spool 文件轮转/清理策略

- 已实现两阶段清理机制，防止 Admin 长时间离线导致磁盘空间耗尽
- `CoreEventSpoolOptions` 新增 `MaxAgeDays`（默认 30 天）和 `MaxFileCount`（默认 60 个）两个安全阀参数
- `CoreEventSpoolStore.PruneExpiredFilesAsync` 实现两阶段清理：先按天数删除超龄文件，再按数量删除超数文件
- `CoreEventSpoolBackgroundService` 已集成定期清理触发：每 100 条事件或每 1 小时触发一次清理检查
- 清理失败不影响主链路事件写入
- 22 个单元测试覆盖：ExtractDateFromFileName 解析、年龄清理、数量清理、联合清理、空目录、边界值等场景

---

## 三、当前阶段的整体判断

当前已经完成：

- Core 协议闭环
- Core 最小可靠事件闭环
- Admin 最小客户端闭环
- AITool.Admin 独立宿主骨架编译通过
- AITool.Admin 独立测试宿主跑通最小启动验证

这说明我们已经从“只有设计文档”的阶段，进入了：

> **双宿主架构的真实实施阶段。**

当前真正的主任务不再是继续堆 Core 协议，而是：

- 让 Admin 真正消费并入库
- 迁移第一块真实页面或接口
- 继续清理宿主共享边界
- 再逐步把 Core 真正迁出到独立宿主

---

## 四、建议的下一步顺序

建议按这个顺序继续推进：

### 下一步

- 先让 Admin 完成一条真实事件链路的消费入库
- 优先建议选择 `UsageLog` 链路

### 再下一步

- 迁移一块低风险真实页面或接口
- 让独立 Admin 宿主开始承载真实功能

### 之后

- 再开始真正的 `AITool.Core` 宿主拆分
- 再接 patch
- 再接实时流
- 再增强 sequence/ack 持久化元数据


### 已完成：Web 侧已迁移页面/控制器/服务的最终清理

本轮完成了 AITool.Web 中所有已迁移到 AITool.Admin / AITool.Core 的冗余页面、控制器和服务文件的最终清理，以及对应的测试适配。

#### 已删除的页面/视图文件（5 个）

- `src/AITool.Web/Pages/Admin/Developer/Invocations/Index.cshtml` — 调用追踪 Razor 视图，已迁入 Core API + Admin 页面
- `src/AITool.Web/Pages/Admin/Developer/Invocations/_InvocationTraceList.cshtml` — 调用追踪子视图
- `src/AITool.Web/Pages/Admin/Developer/Invocations/Index.cshtml.cs` — 调用追踪 PageModel
- `src/AITool.Web/Pages/Admin/System/Settings.cshtml` — 系统设置 Razor 视图，已迁入 Admin
- `src/AITool.Web/Pages/Admin/System/Settings.cshtml.cs` — 系统设置 PageModel（4 参数构造函数）

整个 `Developer/` 和 `System/` 目录已删除。

#### 已删除的服务文件（5 个）

- `src/AITool.Web/Services/DeveloperInvocationTraceQueryService.cs` — 开发者调用追踪查询服务，已由 Core 的 `CoreDeveloperQueryController` API 替代
- `src/AITool.Web/Services/ModelConcurrencyQueryService.cs` — 并发查询服务，已由 Core API 替代
- `src/AITool.Web/Services/AdminConcurrencyControlService.cs` — Admin 并发控制服务，已迁入 Admin 宿主
- `src/AITool.Web/Services/ModelVendorCatalogService.cs` — 空桥接壳（真实实现在 Infrastructure.Hosting）
- `src/AITool.Web/Services/AnalyticsBackgroundQueryExecutor.cs` — 空桥接壳（真实实现在 Infrastructure.Hosting）

#### 已修改的文件

- `src/AITool.Web/Program.cs` — 移除 7 个已删除服务的 DI 注册：
  - `DeveloperInvocationTraceQueryService`（Singleton）
  - `ModelConcurrencyQueryService`（Singleton）
  - `AdminConcurrencyControlService`（Singleton）
  - `ModelVendorCatalogService`（Singleton）
  - `AnalyticsBackgroundQueryExecutor`（Singleton + HostedService）

- `src/AITool.Web/Pages/Shared/_Layout.cshtml` — 移除 `ISystemRuntimeSettingsService` 注入和"开发调试"侧边栏分区（Developer/Invocations、System/Settings 链接），仅保留"代理运行时"分区（代理状态、对话测试）

#### 已适配的测试文件

- `tests/AITool.IntegrationTests/System/SystemSettingsCacheTests.cs` — 重写：不再构造 Web 版 `SettingsModel`（4 参数构造函数已删除），改为直接调用 `settingsService.UpdateAsync()` + `cacheInvalidationService.InvalidateRuntimeSettings()` 验证缓存刷新链路

#### 已删除的测试文件（2 个）

- `tests/AITool.IntegrationTests/System/SystemSettingsPageTests.cs` — 测试已迁移到 Admin 的 Settings 页面，由 `AITool.Admin.IntegrationTests/SettingsPageTests.cs` 替代
- `tests/AITool.IntegrationTests/Developer/DeveloperInvocationsPageTests.cs` — 测试已迁移到 Core API + Admin 页面的 Invocations，不再需要 Web 侧页面测试

#### 清理后 Web/Services/ 保留文件（2 个）

- `AdminCacheInvalidationService.cs` — 同步版本，被 `ModelEditCacheTests` 引用
- `AdminQueryMetadataService.cs` — 被 `ChatApiController` 引用

#### Web 侧清理状态

- **10 个冗余文件已删除（5 页面/视图 + 5 服务）**
- **7 个 DI 注册已清理**
- **2 个过时测试已删除，1 个测试已重写**
- **49 个 IntegrationTests 全部通过，零回归**
- **Web/Services/ 仅剩 2 个仍有运行时消费者的文件**

---

## 六、最近一轮进度同步

### 本轮完成了什么

- 实现了 Admin 侧 ``conversation-turn`` 事件完整消费闭环
- 新增 ``AdminConversationTurnEventIngestor``，从 Core 事件流提取对话记录事件并写入 Admin 本地 JSONL 存储
- 改造 ``CoreEventPullService`` 为三 Ingestor 架构，同时消费 ``usage-log`` 和 ``conversation-turn`` 两种事件类型
- 在 Admin ``Program.cs`` 注册 ``AdminConversationTurnEventIngestor`` 为 Scoped 服务
- 编写 7 个单元测试验证对话记录消费器（过滤、反序列化、去重、批量写入、异常容忍）
- 更新 CoreEventPullService 测试适配双 Ingestor，新增混合事件类型端到端测试
- 全部 204 个测试零回归（ApplicationTests 113 + Admin 49 + Core 42）

### 本轮又完成了什么（route-fallback 事件完整闭环）

- 实现了 Core 侧 route-fallback 事件发布器：CoreRouteFallbackEventPublisher，在代理路由回退时发布事件到 CoreAdminEventBus
- 在 OpenAiProxyController 和 AnthropicProxyController 中集成回退事件追踪和发布（lastFailedRoute 模式 + SafePublishRouteFallbackAsync 容错包装）
- 实现了 Admin 侧路由回退事件内存存储 AdminRouteFallbackStore（200 条上限，6 小时过期自动清理）
- 实现了 Admin 侧路由回退事件消费器 AdminRouteFallbackEventIngestor，按 route-fallback 类型过滤并写入内存存储
- CoreEventPullService 扩展为四 Ingestor 架构：usage-log、conversation-turn、developer-trace、route-fallback
- 新增 CoreRouteFallbackEvent 事件模型（10 字段）、CoreAdminEventEnvelopeBuilder.CreateRouteFallbackEnvelope 信封构造
- Core/Admin 双侧 Program.cs 均注册了路由回退相关服务
- 编写 2 个 Publisher 集成测试 + 11 个 Store/Ingestor 单元测试
- 更新 CoreEventPullService 现有测试适配新参数
### 本轮又完成了什么（sequence/ack 持久化 + Spool 轮转）

- 实现了 Core 事件 sequence 持久化元数据：`CoreEventSequenceProvider` 通过 `sequence.meta` 文件持久化序号，支持重启恢复、损坏容错
- 实现了 Core 事件 ack 持久化元数据：`CoreEventAckStateStore` 通过 `ack.meta` 文件持久化确认序号，Admin 重启后从上次位置继续消费
- 实现了 Spool 文件轮转/清理策略：两阶段清理（超龄删除 + 超数删除），防止磁盘空间无限增长
- `CoreEventSpoolBackgroundService` 集成定期清理触发（每 100 条事件或每 1 小时）
- 编写 22 个 spool 轮转单元测试 + 5 个 ack 持久化测试 + 5 个 sequence 持久化测试
- 全部 347 个测试零回归（ApplicationTests 148 + Admin 49 + Core 42 + Integration 108）

### 本轮又完成了什么（RouteFallback 监控页面闭环）

- 创建了 Admin 侧 RouteFallback API 控制器：`RouteFallbackApiController`，提供 `/api/admin/route-fallback/list`（分页列表）和 `/api/admin/route-fallback/summary`（摘要统计）两个端点
- API 控制器直接读取 `AdminRouteFallbackStore` 内存存储，无需数据库查询或 CoreAdminClient 代理
- 创建了 Admin 侧 RouteFallback 监控 Razor 页面：`Pages/Admin/RouteFallback/Index.cshtml` + `Index.cshtml.cs`
- 页面包含 4 个摘要统计卡片（回退总数、涉及源站点数、涉及目标站点数、最近回退时间）+ 筛选条件（模型关键字、回退原因）+ 分页列表 + 5 秒自动刷新
- 在 `_Layout.cshtml` 监控运维导航区域添加了路由回退入口
- 编译零错误，12 个 RouteFallback 相关测试全部通过，Admin 集成测试 49/49 全部通过


### 本轮又完成了什么（SSE 实时推送 + 死代码清理 + 门面覆盖面确认）

- 实现了 Core→Admin SSE 实时事件通知通道：Core 端 `CoreAdminEventBus` 新增 `Subscribe()`/`NotifyNewEvents()` SSE 订阅机制，使用 WeakReference 自动清理死订阅者
- 实现了 Admin 端双通道拉取架构：`CoreEventPullHostedService` 同时运行轮询（10s 间隔，兜底）和 SSE 监听（实时），使用 SemaphoreSlim 桥接 SSE 通知到拉取循环
- SSE 连接支持自动重连（5s 间隔），最小拉取间隔 500ms 防止密集通知风暴
- 清理了 `CoreAdminClient` 中已废弃的 `StreamEventNotificationsAsync` 方法和 `SseNotification` 内部类（SSE 客户端功能已迁移到 `CoreEventPullHostedService` 直接使用 IHttpClientFactory）
- 确认 Admin 门面覆盖面已完整：ProxyRequestMetadataCache 和 DeveloperInvocationTraceStore 在 Admin 页面/控制器中已无直接引用，仅 ChatApiController 有意保留 ModelConcurrencyLimiter.AcquireAsync（运行时写路径）
- 新增 7 个 SSE 订阅单元测试（`CoreAdminEventBusSubscriptionTests`）+ 3 个 SSE 集成测试（`CoreEventStreamTests`），全部通过
- 编译零错误，全部测试零回归

### 本轮又完成了什么（ConfigApplied 配置变更事件闭环）

- 实现了 Core 侧 ConfigApplied 事件发布器：`CoreConfigAppliedEventPublisher`，在配置同步成功后发布 config-applied 事件到 CoreAdminEventBus（fire-and-forget 模式，不阻塞同步响应）
- Core 端 `CoreConfigSyncController`（Core 和 Web 两个版本）在 full-sync 和 patch-sync 成功后均触发事件发布，携带配置版本、哈希、同步模式、变更类别等审计信息
- 实现了 Admin 侧配置变更内存存储 `AdminConfigAppliedStore`（100 条上限，24 小时过期自动清理，线程安全）
- 实现了 Admin 侧 ConfigApplied 事件消费器 `AdminConfigAppliedEventIngestor`，按 config-applied 类型过滤并写入内存存储
- `CoreEventPullService` 扩展为五 Ingestor 架构：usage-log、conversation-turn、developer-trace、route-fallback、config-applied
- 新增 `CoreConfigAppliedEvent` 事件模型（7 字段：ConfigVersion、ConfigHash、SyncMode、ChangedCategories、PreviousConfigVersion、PreviousConfigHash、OccurredAt）
- `CoreAdminEventEnvelopeBuilder` 新增 `CreateConfigAppliedEnvelope` 信封构造方法
- Core/Admin/Web 三侧 Program.cs 均注册了 ConfigApplied 相关服务
- 编译零错误，全部测试零回归

### 本轮又完成了什么（CircuitBreaker 熔断状态变更事件闭环）

- 实现了 Core 侧 CircuitBreaker 事件发布器：`CoreCircuitBreakerEventPublisher`，通过订阅 `RouteCircuitStateStore.OnCircuitOpened` 事件，在路由因连续失败达到阈值被首次熔断时异步发布 circuit-breaker 事件到 CoreAdminEventBus
- `RouteCircuitStateStore` 新增 `OnCircuitOpened` 事件和 `CircuitOpenedEventArgs`，在 `Block()` 方法触发熔断时（count >= threshold）fire-and-forget 发布，不阻塞代理主流程
- 实现了 Admin 侧熔断状态变更内存存储 `AdminCircuitBreakerStore`（200 条上限，6 小时过期自动清理，线程安全）
- 实现了 Admin 侧 CircuitBreaker 事件消费器 `AdminCircuitBreakerEventIngestor`，按 circuit-breaker 类型过滤并写入内存存储
- `CoreEventPullService` 扩展为六 Ingestor 架构：usage-log、conversation-turn、developer-trace、route-fallback、config-applied、circuit-breaker
- 新增 `CoreCircuitBreakerEvent` 事件模型（6 字段：RouteId、FailureCount、FailThreshold、BlockDuration、RecoveryTime、OccurredAt）
- `CoreAdminEventEnvelopeBuilder` 新增 `CreateCircuitBreakerEnvelope` 信封构造方法
- Core/Admin 双侧 Program.cs 均注册了 CircuitBreaker 相关服务
- Core Program.cs 在 `RouteCircuitStateStore.OnCircuitOpened` 事件中通过 fire-and-forget 模式调用发布器，确保不阻塞代理主流程
- 测试适配：`CoreEventPullServiceTests` 新增 `_configAppliedIngestor` 和 `_circuitBreakerIngestor` 字段，更新全部构造函数调用
- 编译零错误，165 个 ApplicationTests 全部通过，零回归

### 本轮又完成了什么（ConfigApplied + CircuitBreaker Ingestor/Store 独立测试补齐）

- 为 `AdminConfigAppliedStore` 创建了 8 个独立单元测试：单条添加、倒序排列、容量裁剪（100 条上限）、过期清理（24 小时）、GetLatest 正确/空/过期、深拷贝验证
- 为 `AdminConfigAppliedEventIngestor` 创建了 4 个独立单元测试：事件类型过滤、空批次、无匹配事件、畸形负载跳过
- 为 `AdminCircuitBreakerStore` 创建了 8 个独立单元测试：单条添加、倒序排列、容量裁剪（200 条上限）、过期清理（6 小时）、GetLatest 正确/空/过期、深拷贝验证
- 为 `AdminCircuitBreakerEventIngestor` 创建了 4 个独立单元测试：事件类型过滤、空批次、无匹配事件、畸形负载跳过
- 新增测试文件：`tests/AITool.ApplicationTests/CoreRuntime/AdminConfigAppliedStoreAndIngestorTests.cs`（24 个测试）
- 新增测试文件：`tests/AITool.ApplicationTests/CoreRuntime/AdminCircuitBreakerStoreAndIngestorTests.cs`（24 个测试）
- ApplicationTests 全量通过 189 个测试，零回归
- 至此六大事件类型的 Ingestor/Store 全部拥有独立测试覆盖

### 本轮又完成了什么（ConfigApplied + CircuitBreaker + DeveloperTrace Publisher 独立测试）

- 为 CoreConfigAppliedEventPublisher 创建了 3 个独立单元测试：全量同步字段投影、增量同步 changedCategories 保留、连续序号递增
- 为 CoreCircuitBreakerEventPublisher 创建了 3 个独立单元测试：熔断参数投影、null 参数抛 ArgumentNullException、连续序号递增
- 为 CoreDeveloperTraceEventPublisher 创建了 7 个集成测试：完成追踪字段投影、pending 状态跳过、长体截断（512 字符）、空体处理、null 入参异常、空 Attempts 空 ForwardingMode、连续序号递增
- 新增测试文件：tests/AITool.ApplicationTests/CoreRuntime/CoreConfigAppliedEventPublisherTests.cs（3 个测试）
- 新增测试文件：tests/AITool.ApplicationTests/CoreRuntime/CoreCircuitBreakerEventPublisherTests.cs（3 个测试）
- 新增测试文件：tests/AITool.Core.IntegrationTests/CoreDeveloperTraceEventPublisherTests.cs（7 个测试）
- ApplicationTests 全量通过 195 个测试（+6），Core.IntegrationTests 全量通过 54 个测试（+7），零回归
- 至此六大事件类型的 Publisher 全部拥有独立测试覆盖
- 发现并记录了不同事件类型的 OccurredAt 策略差异：ConfigApplied 和 CircuitBreaker 使用 DateTimeOffset.UtcNow，DeveloperTrace 使用 payload.FinishedAt（来自 entry.UpdatedAt）
### 当前还剩什么

- AITool.Web 中仍有 1 个 Admin 页面：Chat/Index（Admin 已有完整版本，Web 保留用于 JS API 端点）
- AITool.Web 中仍有 1 个 Admin 控制器：ChatApiController（深度代理运行时依赖，不可迁移）
- AITool.Web/Services/ 中仅剩 2 个文件：`AdminCacheInvalidationService`（测试引用）+ `AdminQueryMetadataService`（ChatApiController 引用）
- Docker / 容器化部署配置尚未创建（用户明确暂不需要）
- ~~实时事件流推送（SSE）~~ 已完成（本轮实现，双通道 SSE+轮询）

### 当前阻塞点是什么

- 暂无阻塞点
- 后续重点工作转向实时推送通道和更多事件类型消费

### 下一步准备做什么

- ~~推进实时事件流推送通道（WebSocket/SSE）~~ 已完成（SSE 双通道实现 + 死代码清理）
- 探索更多事件类型消费（如 detection、性能指标等）
- 每完成一个小阶段后继续同步更新本文档


---

## 阶段记录

## 阶段记录 — 2026-06-12 统一 ModelConcurrencyLimiter 到 Infrastructure/Proxy 层（提交 70130d2）

### 变更背景

`ModelConcurrencyLimiter` 及其关联类型（`ConcurrencyAcquireMode`、`ConcurrencyAcquireResult`、`ActiveModelConcurrencyEntry`）原先在 `Core/Services` 和 `Web/Services` 各有一份完整副本，逻辑完全相同仅命名空间不同。随着 Core/Admin 双宿主架构推进，需要将这类共享代理基础设施统一到 `Infrastructure/Proxy` 层，消除双副本维护负担。

### 具体变更

- **新建** `src/AITool.Infrastructure/Proxy/ModelConcurrencyLimiter.cs`：包含 4 个公开类型（`ConcurrencyAcquireMode`、`ConcurrencyAcquireResult`、`ActiveModelConcurrencyEntry`、`ModelConcurrencyLimiter`），命名空间为 `AITool.Infrastructure.Proxy`
- **更新** `src/AITool.Core/GlobalUsings.cs`：添加 4 个 `global using` 别名，将短名映射到 `AITool.Infrastructure.Proxy` 下的类型
- **更新** `src/AITool.Web/GlobalUsings.cs`：同上，添加 4 个别名
- **更新** `tests/AITool.IntegrationTests/Proxy/ModelConcurrencyLimiterTests.cs`：`using AITool.Web.Services` → 删除，已有 `using AITool.Infrastructure.Proxy`
- **更新** `tests/AITool.IntegrationTests/Proxy/ProxyMetadataCacheTests.cs`：删除多余的 `using AITool.Web.Services`
- **更新** `tests/AITool.IntegrationTests/Chat/ChatApiTests.cs`：`using AITool.Web.Services` → `using AITool.Infrastructure.Proxy`
- **删除** `src/AITool.Core/Services/ModelConcurrencyLimiter.cs`
- **删除** `src/AITool.Web/Services/ModelConcurrencyLimiter.cs`（Git 识别为 rename）

### 编译与测试

- 编译：0 错误
- 代理相关测试：35 个全部通过
- Core 宿主集成测试：54 个全部通过

### 当前状态

- `Core/Services` 目录下剩余 5 个文件：`ModelConcurrencyQueryService`、`CoreDeveloperTraceEventPublisher`、`CoreRouteFallbackEventPublisher`、`DeveloperInvocationTraceStore`、`DeveloperInvocationTraceQueryService`
- `Web/Services` 目录下剩余 2 个文件：`DeveloperInvocationTraceStore`、`AdminAuthService`
- 下一步统一目标：`DeveloperInvocationTraceStore`（Core 和 Web 各有一份）

 — 2026-06-10/11 收口共享宿主层服务归属

### 本轮完成了什么

- **修复 AnalyticsApiController OOM Bug**：当 rangeType=all 且 bucketType=day 时，ResolveTimeRange 返回 DateTimeOffset.MinValue 作为起始时间，BuildBuckets 从公元元年起生成约 74 万个空桶导致内存溢出。修复方式：在 baseLogs 过滤之后，用实际数据边界替代 MinValue 起始时间，然后重新解析桶类型。修复后 14/14 AnalyticsPageTests 全部通过，49/49 Admin 集成测试全部通过。
- **迁移 ModelVendorCatalogService**：从 Infrastructure/Hosting 层迁入 Admin/Services，命名空间由 AITool.Infrastructure.Hosting 改为 AITool.Admin.Services。此服务仅被 Admin 宿主使用（模型管理页面的厂商分组展示），不含代码库内部依赖。同时更新了 Admin/Pages/Admin/Models/Index.cshtml.cs 的 using 引用和 ApplicationTests 的 using 引用。
- **删除死代码 AnalyticsBackgroundQueryExecutor**：经全面搜索确认零 DI 注册、零代码引用，属于纯死代码。Admin 的 AnalyticsApiController 已简化为直接 DB 查询，不再使用此 BackgroundService。

### 当前还剩什么

- AITool.Web 中仍有 1 个 Admin 页面：Chat/Index（Admin 已有完整版本，Web 保留用于 JS API 端点）
- AITool.Web 中仍有 1 个 Admin 控制器：ChatApiController（深度代理运行时依赖，不可迁移）
- AITool.Web/Services/ 中仅剩 2 个文件：AdminCacheInvalidationService + AdminQueryMetadataService
- Docker / 容器化部署配置尚未创建（用户明确暂不需要）

### 下一步准备做什么

- 评估并推进更多共享宿主层服务向 Admin 侧归集
- 探索更多事件类型消费（如 detection、性能指标等）
- 每完成一个小阶段后继续同步更新本文档

---

---

## 阶段记录 — 2026-06-12 DI 注册去重扩展方法提取

### 本轮完成了什么

- **提取共享 DI 扩展方法**：新增  目录，包含两个扩展方法类：
  -  — 三宿主共享注册：控制器（含 HttpExceptionLoggingFilter）、内存缓存、对话日志文件存储、对话提取服务
  -  — Web+Admin 共享注册：Razor Pages、Cookie 认证与授权、EF Core 数据库上下文、系统运行时设置服务、站点目录客户端、Hangfire 内存存储与调度器
- **Infrastructure.csproj 添加包引用**：，使扩展方法可以定义在 Infrastructure 层
- **Core/Admin/Web 三个 Program.cs 简化**：各宿主用两行扩展调用替代大量重复的内联 DI 注册，代理运行时独有服务保持内联
- **编译零错误，全部测试零回归**

### 文件变更清单

- 新增 
- 新增 
- 修改 （+1 包引用）
- 修改 （内联 DI → AddCommonInfrastructure 调用）
- 修改 （内联 DI → AddCommonInfrastructure + AddAdminInfrastructure 调用）
- 修改 （内联 DI → AddCommonInfrastructure + AddAdminInfrastructure 调用）

### 当前还剩什么

- AITool.Web 中仍有 1 个 Admin 页面：Chat/Index（Admin 已有完整版本，Web 保留用于 JS API 端点）
- AITool.Web 中仍有 1 个 Admin 控制器：ChatApiController（深度代理运行时依赖，不可迁移）
- AITool.Web/Services/ 中仅剩 2 个文件：AdminCacheInvalidationService + AdminQueryMetadataService
- Docker / 容器化部署配置尚未创建（用户明确暂不需要）

### 下一步准备做什么

- 评估 Web/Program.cs 中剩余的代理运行时 DI 注册是否可以进一步归纳
- 探索更多事件类型消费（如 detection、性能指标等）
- 每完成一个小阶段后继续同步更新本文档

## 七、结论

当前项目状态可以概括为：

> **Admin 侧已实现六事件类型消费闭环：``usage-log`` 事件写入 Admin 数据库，``conversation-turn`` 事件写入 Admin 本地 JSONL 存储，``developer-trace`` 和 ``route-fallback`` 事件写入 Admin 内存存储。Admin 宿主通过 ``CoreEventPullHostedService`` 定时从 Core 拉取事件、按类型分发消费、统一提交确认。Core ↔ Admin 的配置同步已支持全量 + 增量双模式；Admin 宿主已迁移 12 组页面 + RouteFallback 监控页面，覆盖全部管理页面与系统配置能力；AITool.Core 物理独立宿主已创建并编译通过（纯代理运行时，无 DB/无 Razor/无认证）；Web 侧清理已完成，仅剩 ChatApiController（不可迁移）和 Chat/Index 页面。CircuitBreaker 熔断事件（第六条事件链路）已完成闭环。**
---

## 阶段记录 — 2026-06-12 统一代理元数据缓存到 Infrastructure 层并修复 DI 自动注入 bug

### 本轮完成了什么

- **统一 ProxyRequestMetadataCache 到 Infrastructure 层**：将 Core/Services 和 Web/Services 中的 ProxyRequestMetadataCache.cs、ProxyRequestMetadataCache.AdminQueries.cs、AdminQueryMetadataService.cs 三个文件统一迁移到 Infrastructure/Proxy 目录。Core 和 Web 中的旧副本已删除，所有控制器和测试文件的 using 引用已更新。
- **修复 DI 自动注入 bug**：Web 宿主的 ProxyRequestMetadataCache 注册改为工厂委托模式（AddSingleton(sp => new T(...))），显式传入 configProvider: null，防止 DI 自动注入已注册的 CoreRuntimeConfigProvider 实例导致缓存走配置快照路径而绕过数据库查询。
- **清理残留本地文件**：删除了 14 个从未提交过的本地残留副本文件（ConsoleProxyLogFormatter、ProxyProtocolBridge.*、ProxyRequestMetadataQueryModels），这些文件的命名空间虽不同但逻辑与 Infrastructure 版本完全一致，且 GlobalUsings.cs 已映射类型别名。
- **修复 CoreAdminClientTests 断言**：测试中 replay 事件现在包含 config-applied + usage-log 两种类型（顺序），断言从 HaveCount(1) 更正为 HaveCount(2)，ack 引用从 replay[0] 更正为 replay[1]。

### 文件变更清单

- 重命名 Core/Services → Infrastructure/Proxy：AdminQueryMetadataService.cs、ProxyRequestMetadataCache.AdminQueries.cs、ProxyRequestMetadataCache.cs
- 删除 Web/Services 副本：AdminQueryMetadataService.cs、ProxyRequestMetadataCache.AdminQueries.cs、ProxyRequestMetadataCache.cs
- 修改 Web/Program.cs：ProxyRequestMetadataCache 注册改为工厂委托，显式传入 configProvider: null
- 修改 ~20 个控制器和测试文件的 using 引用
- 修改 CoreAdminClientTests.cs：修正 replay 事件计数和 ack 序号引用
- 删除 14 个本地残留副本文件（从未提交）

### 测试验证

- Web 集成测试：108/108 通过
- Core 集成测试（AITool.Core.IntegrationTests）：54/54 通过
- IntegrationTests.Core 命名空间测试：7/7 通过
- 编译零错误，全部测试零回归

### 当前还剩什么

- AITool.Web 中仍有 1 个 Admin 页面：Chat/Index（Admin 已有完整版本，Web 保留用于 JS API 端点）
- AITool.Web 中仍有 1 个 Admin 控制器：ChatApiController（深度代理运行时依赖，不可迁移）
- AITool.Web/Services/ 中仅剩 2 个文件：AdminCacheInvalidationService + AdminQueryMetadataService
- Core/Services 和 Web/Services 中仍有重复副本：ModelConcurrencyLimiter、DeveloperInvocationTraceStore
- Docker / 容器化部署配置尚未创建（用户明确暂不需要）

### 下一步准备做什么

- 统一 ModelConcurrencyLimiter 到 Infrastructure 层，消除 Core/Web 重复副本
- 统一 DeveloperInvocationTraceStore 到 Infrastructure 层，消除 Core/Web 重复副本
- 每完成一个小阶段后继续同步更新本文档

---

## 阶段记录 — 2026-06-12 继续 Program.cs 瘦身与共享工具类提取

### 本轮完成了什么

- **CoreRouteFallbackEventPublisher 验证**：确认该发布器已通过构造函数注入在 AnthropicProxyController 和 OpenAiProxyController 中使用，不需要像 DeveloperTrace/CircuitBreaker 那样在 Program.cs 中手动接线事件。DI 注册即可。
- **AdminAuthService 迁移到 Infrastructure/Hosting**：将 AdminAuthService.cs 从 AITool.Web/Services 迁移到 AITool.Infrastructure/Hosting，消除 Web/Services 命名空间。更新 Login.cshtml.cs 和 Program.cs 的 using 引用，清除 5 个控制器文件中残留的无用 using AITool.Web.Services。删除已清空的 Web/Services 目录。
- **IsAdmin* 请求匹配提取到 AdminRequestMatcher**：新增 AdminRequestMatcher 静态类到 Infrastructure/Hosting，提供 IsAdminRequest、IsAdminPageRequest、IsLoginPageRequest、IsAdminApiRequest、IsHangfireRequest、IsAdminAuthRequest 六个方法。Web/Program.cs 删除约 40 行内联 static 方法。AdminInfrastructureExtensions 用 IsAdminAuthRequest 替换私有 IsAdminRequest 方法。
- **数据库迁移辅助方法提取到 DatabaseSchemaMigrator**：新增 DatabaseSchemaMigrator 静态类到 Infrastructure/Persistence，包含 EnsureProxyUsageLogSchemaAsync、EnsureConversationLogSchemaAsync、ColumnExistsAsync。Web/Program.cs 删除约 170 行内联迁移方法，减少到约 180 行。

### 文件变更清单

- 新增 Infrastructure/Hosting/AdminAuthService.cs（从 Web/Services 迁移）
- 新增 Infrastructure/Hosting/AdminRequestMatcher.cs（从 Web/Program.cs 提取）
- 新增 Infrastructure/Persistence/DatabaseSchemaMigrator.cs（从 Web/Program.cs 提取）
- 删除 Web/Services/AdminAuthService.cs 和 Web/Services/ 目录
- 修改 Web/Program.cs：删除 IsAdmin* 方法、数据库迁移方法，using 精简（移除 System.Data.Common、AITool.Web.Services）
- 修改 Web/Pages/Login.cshtml.cs：using 从 AITool.Web.Services 改为 AITool.Infrastructure.Hosting
- 修改 5 个控制器文件：移除无用 using AITool.Web.Services
- 修改 AdminInfrastructureExtensions.cs：用 AdminRequestMatcher 替换私有 IsAdminRequest

### 测试验证

- 构建零错误
- 登录相关测试：3/3 通过
- 认证相关测试：6/6 通过
- 集成测试：54/54 通过（含 Core + Web）

### 当前 Web/Program.cs 状态

- 约 180 行（从原始 435 行减少到不到一半）
- 已提取：全局异常处理、本地 IP 查询、请求体读取、Admin 请求匹配、数据库迁移
- 仍保留：DI 注册、启动初始化逻辑、认证中间件、Hangfire 配置、Schema 迁移调用

### 下一步准备做什么

- 评估整体架构拆分完成度
- 检查是否有其他可提取的共享模式


---

## 阶段记录 — 2026-06-12 认证中间件提取与启动初始化去重

### 本轮完成了什么

- **AdminAuthenticationMiddleware 提取**：将 Web/Program.cs 中 40 行内联认证中间件提取为独立的 AdminAuthenticationMiddleware 类，放在 Infrastructure/Hosting 命名空间下。新增 UseAdminAuthentication() 扩展方法，Web/Program.cs 中用一行 `app.UseAdminAuthentication()` 替换原来的 `app.Use(async (context, next) => { ... })` 内联中间件块。Web/Program.cs 从 142 行缩减到 132 行。
- **AdminStartupInitializer 提取**：将 Web 和 Admin 两个 Program.cs 中重复的数据库初始化逻辑（EnsureCreated + Schema 迁移 + Hangfire 调度注册）提取为 AdminStartupInitializer 静态类，放在 Infrastructure/Persistence 命名空间下。Web/Program.cs 启动初始化 scope 简化为仅保留代理运行时独有的配置恢复和熔断参数初始化。Admin/Program.cs 的启动初始化 scope 完全消除。
- **Using 清理**：移除 Web/Program.cs 中不再直接使用的 `Microsoft.EntityFrameworkCore`，移除 Admin/Program.cs 中不再直接使用的 `AITool.Infrastructure.Scheduling` 和 `Hangfire`。
- **格式修正**：顺带修复 dotnet format 检测到的空白格式问题（Edit.cshtml.cs、ChatApiController.cs、测试文件）。

### 文件变更清单

- 新增 Infrastructure/Hosting/AdminAuthenticationMiddleware.cs（从 Web/Program.cs 内联中间件提取）
- 新增 Infrastructure/Hosting/AdminAuthenticationMiddlewareExtensions.cs（UseAdminAuthentication 扩展方法）
- 新增 Infrastructure/Persistence/AdminStartupInitializer.cs（Web/Admin 共享启动初始化）
- 修改 Web/Program.cs：内联认证中间件替换为 UseAdminAuthentication()，启动初始化拆分为 AdminStartupInitializer + 独有配置恢复
- 修改 Admin/Program.cs：启动初始化 scope 替换为 AdminStartupInitializer，移除 2 个不再使用的 using

### 测试验证

- 构建零错误
- ApplicationTests: 195 通过
- Admin.IntegrationTests: 49 通过
- IntegrationTests: 108 通过
- Core.IntegrationTests: 54 通过
- 全部 406 个测试零失败

### 当前 Web/Program.cs 状态

- 约 132 行（从原始 435 行缩减到 30%）
- 已提取：全局异常处理、本地 IP 查询、请求体读取、Admin 请求匹配、数据库迁移、认证中间件、启动初始化
- 仍保留：DI 注册（无可提取，所有服务都是 Web 独有的组合）、代理运行时配置恢复和熔断初始化、中间件管道配置、端点映射

### 当前 Admin/Program.cs 状态

- 约 135 行
- DI 注册占大部分（Admin 独有的事件消费器、存储、HostedService 等）
- 启动初始化已通过 AdminStartupInitializer 简化
- 管道配置已非常精简

### 下一步准备做什么

- Core/Program.cs 中的事件接线代码（DeveloperTrace、CircuitBreaker）是 Core 独有的启动逻辑，不需要跨宿主共享，保持在 Program.cs 中是合理的
- 继续检查是否有其他可提取的共享模式
- 评估整体架构拆分的完成度

---

## 阶段记录 — 2026-06-12 架构拆分完成度评估与最终清理

### 本轮完成了什么

- **Using 引用清理**：移除 Web/Program.cs 中未使用的 `using AITool.Infrastructure.Scheduling;`，移除 Admin/Program.cs 中未使用的 `using HttpLogFormatter` 别名。Admin/Program.cs 的 `using AITool.Infrastructure.Conversations;` 经验证仍被 `AdminConversationTurnEventIngestor` 使用，已恢复。
- **Controller 层重复评估**：对比 Web 和 Core 的 Controller 目录，发现 9/10 对 Controller 文件是完全相同的代码副本（仅 namespace 不同），其中 CoreConfigSyncController 有实质功能差异（Core 版新增 patch-sync、缓存失效、熔断参数同步）。Web 独有 ChatApiController（依赖 DbContext），Core 独有 CoreEventStreamController 和 CoreDeveloperQueryController。Controller 重复是设计预期——Web 作为过渡期单体仍需保留完整的代理和管理功能，未来 Web 下线后其 Controller 自然移除。
- **全面架构评估完成**：确认架构拆分已达到可交付状态。

### 架构拆分最终状态总结

#### 三宿主 Program.cs 行数

| 宿主 | 行数 | 说明 |
|------|------|------|
| Web | ~128 | 从原始 435 行缩减到约 30%，保留 DI 注册、代理运行时初始化、中间件管道 |
| Core | ~188 | 纯代理运行时，含事件接线、SSE、开发者查询等 Core 独有功能 |
| Admin | ~131 | 纯管理后台，含事件消费器、HostedService 等 Admin 独有功能 |

#### Infrastructure 层组织

| 目录 | 文件数 | 说明 |
|------|--------|------|
| DependencyInjection | 3 | AddCommonInfrastructure / AddAdminInfrastructure / AddProxyRuntimeInfrastructure 三组 DI 注册 |
| Hosting | 11 | 全局异常处理、认证中间件、Admin 请求匹配、日志格式化、请求体读取等 |
| Persistence | 3 | AppDbContext、DatabaseSchemaMigrator、AdminStartupInitializer |

#### 各宿主职责边界

- **Web**（端口 5029）：过渡期单体，同时承担代理转发 + 管理后台 + 数据库拥有者三重角色。DI 注册包含三组基础设施完整注册 + 代理运行时独有服务。
- **Core**（端口 5029）：纯代理运行时宿主，无 DB/无 Razor/无 Auth。DI 注册仅包含 CommonInfrastructure + ProxyRuntimeInfrastructure。4 个 Core 独有 Service 类（事件发布、查询）。
- **Admin**（端口 5030）：纯管理后台宿主，拥有 DB。DI 注册包含 CommonInfrastructure + AdminInfrastructure + 7 个 Admin 独有 Service 类（事件拉取/消费/缓存失效/并发控制）。

#### 已提取的共享模式

1. **DI 分组注册**：CommonInfrastructure（3 宿主共享）、AdminInfrastructure（Web+Admin 共享）、ProxyRuntimeInfrastructure（Web+Core 共享）
2. **认证中间件**：AdminAuthenticationMiddleware + UseAdminAuthentication() 扩展方法
3. **启动初始化**：AdminStartupInitializer 封装数据库创建 + Schema 迁移 + Hangfire 调度
4. **全局异常处理**：GlobalExceptionHandlerExtensions 封装异常捕获和统一 JSON 响应
5. **Admin 认证服务**：AdminAuthService + AdminRequestMatcher 封装认证逻辑和路径匹配
6. **数据库迁移**：DatabaseSchemaMigrator 封幂等 Schema 补齐
7. **版本信息**：AppVersionInfo 封装应用版本号

#### 已知的设计遗留项（非阻塞）

1. **Controller 重复**：Web 和 Core 的 Proxy Controller 完全重复（9/10 对），CoreConfigSyncController 有功能差异。这是过渡期的预期状态，Web 下线后自然消除。
2. **版本号分散**：三个宿主各自硬编码版本号（1.0.1.4 / 1.0.1.4-admin / 1.0.1.4-core），未来应集中到 Directory.Build.props。
3. **AdminConcurrencyControlService 占位**：Admin 侧并发控制服务目前是占位实现，后续需通过 CoreAdminClient 实现代理运行时并发限制变更。

### 文件变更清单

- 修改 Web/Program.cs：移除未使用的 using AITool.Infrastructure.Scheduling
- 修改 Admin/Program.cs：移除未使用的 using HttpLogFormatter 别名

### 测试验证

- 构建零错误零警告
- ApplicationTests: 195 通过
- Admin.IntegrationTests: 49 通过
- IntegrationTests: 108 通过
- Core.IntegrationTests: 54 通过
- 全部 406 个测试零失败

### 架构拆分完成度结论

**核心结论：架构拆分已基本完成。**

- 三个宿主的 Program.cs 均已精简到合理范围（128-188 行），启动逻辑已下沉到 Infrastructure 层。
- 共享基础设施（认证、异常处理、数据库迁移、DI 注册）已全部提取到可复用的 Infrastructure 类。
- Web 项目无 Services 残留，仅保留 Controllers + Pages。
- Core 和 Admin 各自拥有独立的 Services，职责边界清晰。
- 所有 406 个测试通过，Core/Proxy 主链路稳定。

后续工作（非当前拆分范围）：
- Web 下线时的 Controller 清理
- 版本号集中管理
- AdminConcurrencyControlService 完整实现

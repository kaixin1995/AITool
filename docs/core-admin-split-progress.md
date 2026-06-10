# Core / Admin 拆分当前进展说明

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
- IntegrationTests：`175/175` 通过
- AITool.Web：构建成功，`0 error`（34 warnings 均为既有 nullable 警告）
- AITool.Admin：单独构建成功
- AITool.Admin.IntegrationTests：`5/5` 通过

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

### 未完成：Core 真正物理独立宿主

当前虽然已经完成了大量 Core 协议与运行时模型工作，但它们仍然跑在当前 `AITool.Web` 宿主中。

#### 核心宿主拆分待办

- 单独创建真正的 `AITool.Core` 宿主工程
- 把 `/v1/*`、`/api/core/*` 真正迁到 `AITool.Core`
- 让 `AITool.Web` 逐步退出核心代理角色

#### 核心宿主当前状态

- 协议与事件模型已经具备迁出条件
- 但物理 Core 宿主拆分还没开始真正实施

---

### 未完成：patch 增量更新协议

当前配置同步只有：

- `full-sync`
- `handshake`
- `noop / full-sync-required / admin-version-behind`

#### 还没有做

- `patch` 协议模型
- `baseVersion` 校验后的增量更新应用
- 增量失败后自动回退到全量同步

这部分还在后续阶段。

---

### 未完成：事件流实时消费通道

当前事件链路仍然是：

- 发布 → 总线 → spool → replay/ack

#### 实时消费待办

- 真正的 Core → Admin 长连接实时推送
- Admin 长连接消费
- 实时流与 replay 的衔接

目前属于：

- **可靠协议最小闭环已完成**
- **实时推送通道尚未接入**

---

### 未完成：事件 sequence / ack 持久化元数据增强

当前已经有：

- `CoreEventSequenceProvider`
- `CoreEventSpoolStore`
- `Ack`
- `Replay`

#### 但还没做的更稳妥能力

- sequence 持久化元数据文件
- ack 持久化元数据文件
- Core 重启后恢复 sequence/ack 状态
- 更细粒度的 spool 文件轮转策略

目前最小版本已经能工作，但这部分仍然可以继续增强。

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

---

## 五、最近一轮进度同步

### 本轮完成了什么

- 扩展了 Admin 门面服务覆盖面，完成后台页面和控制器对运行时对象的直接依赖剥离
- 具体变更涉及 8 个源文件 + 1 个测试文件，替换模式统一一致
- `AdminCacheInvalidationService` 覆盖范围扩展：Models/Index、Models/Edit、Sites/Index、Sites/Edit、Sites/Import、RouteRulesApiController 这 6 处后台写入口全部从 `ProxyRequestMetadataCache` 切换到门面服务
- `AdminConcurrencyControlService` 覆盖范围扩展：RouteRulesApiController、ModelsApiController 这 2 处从 `ModelConcurrencyLimiter` 切换到门面服务
- `ModelConcurrencyQueryService.RecentRetention` 替换了 Developer Invocations 页面对 `ModelConcurrencyLimiter.RecentRetention` 的直接引用
- 修复了 `ModelEditCacheTests` 测试文件以适配构造函数签名变更
- 已重新执行完整构建验证和测试套件：
  - ApplicationTests：`101/101` 通过
  - IntegrationTests：`175/175` 通过
  - Admin.IntegrationTests：`5/5` 通过
  - AITool.Web 构建：成功，0 error
  - AITool.Admin 构建：成功，0 error

### 当前还剩什么

- `/Admin/UsageLogs` 页面本身仍需继续向 `AITool.Web` 现有页面行为靠齐，尤其是样式细节、更多展示字段、列表行信息密度与详情抽屉内容完整度
- 还没有开始第二批 `/Admin/*` 页面与接口迁移，当前主线仍需在 UsageLogs 进一步收口后再进入下一批页面迁移
- `AITool.Core` 物理独立宿主、patch 增量同步、实时事件流与 sequence/ack 持久化增强仍未进入本轮实施
- Admin 侧所有页面和控制器对 `ProxyRequestMetadataCache` 的直接依赖剥离已全部完成；后续仍需确认是否有非 Admin 文件（如代理运行时控制器）存在合理的直接引用，这些属于预期保留，不需要通过门面收口

### 当前阻塞点是什么

- 目前没有新的代码级阻塞
- 当前构建输出仍存在来自既有 `AITool.Infrastructure` 文件的 nullability warning，本轮未改动这些历史文件

### 下一步准备做什么

- 继续完善 `src/AITool.Admin/Pages/Admin/UsageLogs/Index.cshtml`，把更多 Web 侧已存在的展示细节、字段和交互逐步迁入独立 Admin 宿主
- 在 UsageLogs 页面继续稳定后，开始进入第二批 `/Admin/*` 页面与接口迁移
- 确认 `ProxyRequestMetadataCache` 的剩余直接引用均属于代理运行时控制器（预期保留），评估是否需要为这些场景也做门面收口
- 每完成一个小阶段后继续同步更新本文档，并补充对应测试验证结果

---

## 五、结论

当前项目状态可以概括为：

> **协议与运行时基础已经打好，双宿主也开始真正落地；现在独立 Admin 宿主已经能单独编译和测试启动，下一阶段重点应转向真实事件消费入库与第一块页面/接口迁移。**

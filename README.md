# AI Tool - 项目详细文档

## 项目简介

AI Tool 是一个 **AI API 网关 / 反向代理**，用于统一管理和转发多个 AI 服务站点的请求。它提供管理后台来管理站点、模型、路由规则、访问密钥，并通过 OpenAI/Anthropic 兼容协议对外提供代理服务，支持按优先级自动故障转移。

核心能力：
- 多站点管理（注册 OpenAI/Anthropic 兼容的 AI 服务站点）
- 统一模型库（将不同站点的同名模型归一化管理）
- 路由规则（为模型配置多站点优先级，失败自动重试下一个站点）
- 熔断保护（站点连续失败达到阈值后临时屏蔽）
- 实时流式透传（OpenAI 和 Anthropic SSE 原生流式转发）
- 跨协议桥接（OpenAI 客户端可透明使用 Anthropic 后端，反之亦然）
- 并发控制（按站点+模型维度限制最大并发，支持跳过或排队等待两种策略）
- 访问密钥（对外提供统一的 API Key 认证）
- 模型检测（定时/手动探测模型可用性）
- 健康监控（模型可用率时间线图表）
- 对话测试（内置 Chat 页面端到端测试代理链路）
- 使用日志（Token 级别的用量追踪，含重试次数、缓存命中、流式延迟等）
- 对话记录（结构化记录用户输入与 AI 输出，按会话分组浏览）
- 统计分析（用量趋势、模型分布、缓存命中率等可视化仪表盘）
- 开发者追踪（内存环形缓冲区，调试代理请求全链路）
- OpenAI Responses API 代理（支持 HTTP 和 WebSocket 两种模式）
- **Core-Admin 双宿主部署**（Admin 管理面 + Core 代理面独立进程，生产可分离部署）

---

## 技术栈

| 层级 | 技术 |
|------|------|
| 运行时 | .NET 8.0 (ASP.NET Core) |
| 数据库 | SQLite (EF Core, EnsureCreated 模式，无 Migration) |
| 前端 | Razor Pages + Bootstrap 5.3.3 + 原生 CSS |
| 交互方式 | 管理页面全部使用 AJAX（fetch API），无整页刷新 |
| 日志 | NLog |
| 测试 | xUnit + FluentAssertions + 隔离 SQLite 数据库 |

---

## 项目结构

```
AI-Tool/
├── src/
│   ├── AITool.Domain/            # 领域实体（纯 POCO，零依赖，sealed 类）
│   ├── AITool.Application/       # 应用层接口和 DTO（纯接口定义，不含实现）
│   ├── AITool.Infrastructure/    # 基础设施实现（EF Core、HttpClient、代理运行时）
│   ├── AITool.Admin/             # Admin 宿主（Razor Pages + 管理 API、DB 读写、配置下发）
│   └── AITool.Core/              # Core 宿主（代理端点 + 运行时查询 API、内存状态持有）
├── tests/
│   ├── AITool.ApplicationTests/         # 单元测试
│   ├── AITool.Admin.IntegrationTests/   # Admin 集成测试
│   └── AITool.Core.IntegrationTests/    # Core 集成测试
├── tools/
│   └── ProtocolSyncCheck/       # 协议同步校验工具
└── AITool.slnx                  # 解决方案文件
```

**依赖关系：** `Domain` ← `Application` ← `Infrastructure` ← `Admin` / `Core`

- `Domain`：纯 POCO 实体，零外部依赖，所有类为 `sealed`
- `Application`：仅引用 `Domain`，定义接口和 DTO，不含任何实现
- `Infrastructure`：引用 `Application` 和 `Domain`，实现所有接口，含 Core-Admin 事件总线
- `Admin`：引用所有项目，管理后台宿主（端口 5030），持有 SQLite 数据库
- `Core`：引用 Infrastructure，代理运行时宿主（端口 5029），不直接访问数据库

---

## Core-Admin 双宿主架构

### 架构概览

```
┌─ Admin 宿主机 (端口 5030) ───────────────────────────────────────────┐
│                                                                       │
│  Razor Pages + 管理 API 控制器                                        │
│  SQLite 数据库 (aitool.db) — 唯一写入口                               │
│  配置下发: CoreConfigSyncHostedService (全量) + AdminCacheInvalidationService (增量) │
│  事件拉取: CoreEventPullHostedService → CoreEventPullService         │
│                                                                       │
└───────────────────────────────────────────────────────────────────────┘
          │ 全量/增量同步 (HTTP POST)          │ 事件拉取 (HTTP GET replay)
          ▼                                    ▼
┌─ Core 宿主机 (端口 5029) ───────────────────────────────────────────┐
│                                                                       │
│  代理端点: /v1/chat/completions, /v1/messages, /v1/responses ...     │
│  运行时查询: /api/core/developer/*, /api/core/runtime/*              │
│  配置接收: /api/core/config/full-sync, /api/core/config/patch-sync   │
│  事件发布: CoreAdminEventBus → CoreEventSpoolStore (磁盘 JSONL)      │
│                                                                       │
│  内存状态持有:                                                        │
│    CoreRuntimeConfigProvider  (配置快照，完整路由/站点/密钥数据)      │
│    DeveloperInvocationTraceStore (调用追踪，100条/6小时)             │
│    ModelConcurrencyLimiter     (并发控制)                             │
│    RouteCircuitStateStore      (熔断状态)                             │
│                                                                       │
└───────────────────────────────────────────────────────────────────────┘
```

### 配置同步体系

Admin 是配置的**唯一写入源**（SQLite），Core 通过以下机制保持内存状态同步：

| 同步方式 | 触发时机 | 传输内容 | 说明 |
|----------|----------|----------|------|
| **全量同步** (full-sync) | Admin 启动时 | 完整 `CoreRuntimeConfigSnapshot` | Core 无配置时必要；启动后 2s 延迟 + 最多 5 次指数退避重试 |
| **增量同步** (patch-sync) | 每次管理操作后 | `ConfigPatchPayload`（只含变更类别） | 站点/路由/密钥/设置变更后立即推送 |

**版本号机制**：使用 Unix 时间戳毫秒作为配置版本号，保证跨 Admin 重启单调递增。Core 端仅接受 `patch.ConfigVersion > current.ConfigVersion` 的增量同步。

**兜底机制**：
- Core 首次启动时从本地 `last-good-config.json` 文件恢复配置（可脱离 Admin 独立运行）
- Core 拒绝增量同步（返回 400）时，Admin 自动回退到全量同步

### 事件同步体系

Core 端代理请求产生的日志数据通过 **唯一事件 + 磁盘 Spool + 断线重放** 机制同步到 Admin。

每次代理请求完成时（`OnTraceCompleted`），Core 发布**一份** `"proxy-request"` 统一事件，
包含该请求的完整画像：UsageLog 字段、调用追踪详情、完整 Request/Response Bodies、所有 Attempt 链。
Admin 收到后展开到三个落地点：

```
Core: DeveloperInvocationTraceStore (累积器) → OnTraceCompleted
        │
        ▼
      CoreUnifiedProxyEventPublisher → 一条 "proxy-request" 事件
        │
        ▼
      CoreAdminEventBus (内存 Channel) → CoreEventSpoolStore (磁盘 JSONL)
        │
        ▼
Admin: CoreEventPullService.PullAndProcessAsync() ← SSE + 10s 轮询
          │
          ├── AdminUnifiedProxyEventIngestor (单一 Ingestor 替代旧三份)
          │     ├── DB Sink: event.Attempts 展开为 N 条 ProxyUsageLog 行
          │     └── Memory Sink: event 存入 AdminDeveloperTraceStore (Invocations 数据源)
          │     注：对话记录由独立的 AdminConversationTurnEventIngestor 处理，不走 unified 事件
          │
          └── AckAsync(maxSeq) → Core 清理磁盘旧事件
```

**断线保护**：Admin 离线期间，Core 持续将事件写入磁盘 JSONL 文件（按日轮转，最多保留 30 天或 60 个文件）。Admin 恢复后，从 `ack.meta` 记录的序号开始重放，不会丢失数据。

---

## 分层架构

### AITool.Domain — 领域实体

纯 POCO 类，无外部依赖。所有实体使用 `Guid` 主键，均为 `sealed` 类，没有基类或共享接口。实体之间**没有 EF Core 导航属性**，关系通过 ID 手动关联。

**实体总览（12 个实体，5 个命名空间）：**

| 命名空间 | 实体 | 说明 |
|----------|------|------|
| `AITool.Domain.Sites` | `Site` | AI 服务站点 |
| `AITool.Domain.Models` | `ModelLibraryItem` | 统一模型库 |
| `AITool.Domain.Models` | `ModelHealthMonitor` | 模型健康监控配置 |
| `AITool.Domain.SiteCatalog` | `SiteModelMapping` | 站点-模型映射 |
| `AITool.Domain.Proxy` | `ProxyRouteEntry` | 代理路由入口（对外暴露的模型入口） |
| `AITool.Domain.Proxy` | `ProxyRouteRule` | 代理路由规则 |
| `AITool.Domain.Proxy` | `ProxyAccessKey` | 访问密钥 |
| `AITool.Domain.Proxy` | `ProxyUsageLog` | 使用日志 |
| `AITool.Domain.Proxy` | `ConversationTurnLog` | 对话记录（结构化存储用户输入与 AI 输出） |
| `AITool.Domain.Operations` | `SystemRuntimeSettings` | 系统运行时配置（单例 Id=1） |
| `AITool.Domain.Detection` | `DetectionTask` | 定时检测任务 |
| `AITool.Domain.Detection` | `DetectionTaskExecution` | 检测任务执行记录 |

#### Site — AI 服务站点

```csharp
sealed class Site
{
    Guid Id;                    // 主键
    string Name;                // 站点名称
    string BaseUrl;             // 站点根地址
    string EndpointPathMode;    // 接口路径模式: "standard-root" / "versioned-base"
    string ApiKey;              // 站点访问密钥
    string ProtocolType;        // 协议类型: "OpenAI" / "Anthropic"
    bool SupportsOpenAi;        // 是否支持 OpenAI 协议 (影响跨协议桥接)
    bool SupportsAnthropic;     // 是否支持 Anthropic 协议
    bool IsEnabled = true;      // 是否启用
    DateTimeOffset CreatedAt;   // 创建时间
}
```

#### ProxyUsageLog — 使用日志

```csharp
sealed class ProxyUsageLog
{
    Guid Id;                    // 主键
    Guid RequestId;             // 请求唯一标识（关联同次请求的多条日志）
    Guid AccessKeyId;           // 访问密钥ID
    string ProtocolType;        // 协议类型
    string RequestModel;        // 请求的模型名
    string AttemptedModel;      // 实际尝试的上游模型名
    Guid TargetSiteId;          // 命中的目标站点ID
    string Status;              // "success" / "fail"
    string Source = "proxy";    // "proxy" / "chat" / "detection-task"
    int RetryCount;             // 重试次数
    int AttemptIndex;           // 尝试序号
    bool IsFinalResult;         // 是否为最终结果
    bool FallbackTriggered;     // 是否触发故障转移
    string ErrorMessage;        // 错误信息
    int InputTokens;            // 输入 Token 数
    int CachedTokens;           // 缓存命中 Token 数
    int OutputTokens;           // 输出 Token 数
    bool IsStreaming;           // 是否为流式请求
    bool IsStreamInterrupted;   // 流式是否被中断
    int FirstTokenLatencyMs;    // 首 Token 延迟（毫秒）
    int StreamDurationMs;       // 流式传输总时长（毫秒）
    int TotalDurationMs;        // 请求总耗时（毫秒）
    string ReasoningEffort;     // 推理力度参数
    DateTimeOffset RequestedAt; // 请求时间
}
```

---

### AITool.Application — 应用层

定义接口和 DTO，不含实现。仅引用 `Domain` 项目。

#### 核心接口

| 接口 | 文件 | 说明 |
|------|------|------|
| `IProxyForwardService` | `Proxy/IProxyForwardService.cs` | 代理转发（含流式） |
| `IProxyCallRecorder` | `Proxy/IProxyCallRecorder.cs` | 统一记录服务（Trace + UsageLog + ConversationLog） |
| `IUsageLogService` | `UsageLogs/IUsageLogService.cs` | 使用日志写入 |
| `IConversationLogService` | `Conversations/IConversationLogService.cs` | 对话日志写入 |
| `ISystemRuntimeSettingsService` | `Operations/ISystemRuntimeSettingsService.cs` | 系统运行时配置 |
| `ICoreRuntimeConfigProvider` | `CoreRuntime/ICoreRuntimeConfigProvider.cs` | Core 运行时配置快照接口 |

#### IProxyCallRecorder — 统一记录服务

这是代理管道中**唯一的记录入口**，收拢了三种日志的写入：

```csharp
interface IProxyCallRecorder
{
    Guid? BeginTrace(ProxyCallContext context);                          // 创建开发者追踪
    Guid BeginTraceAttempt(Guid? traceId, ProxyCallContext context);     // 追加路由尝试
    void CompleteTraceAttempt(Guid? traceId, Guid attemptId, ProxyCallContext context); // 完成路由尝试
    void CancelTrace(Guid? traceId, string reason);                      // 客户端断连时取消追踪
    Task RecordUsageAsync(ProxyCallContext context, CancellationToken ct);       // 写入用量日志
    Task RecordConversationAsync(ProxyCallContext context, CancellationToken ct); // 写入对话日志
}
```

#### CoreRuntimeConfigSnapshot — Core 配置快照 DTO

```csharp
sealed class CoreRuntimeConfigSnapshot
{
    long ConfigVersion;                              // 单调递增版本号（Unix 时间戳毫秒）
    string ConfigHash;                               // SHA256 哈希，用于去重
    DateTimeOffset GeneratedAt;                      // 生成时间
    List<CoreRuntimeSite> Sites;                     // 站点列表（含 ApiKey、协议、启停状态）
    List<CoreRuntimeModel> Models;                   // 模型列表
    List<CoreRuntimeSiteModelMapping> SiteModelMappings;  // 站点模型映射
    List<CoreRuntimeRouteEntry> RouteEntries;         // 路由入口
    List<CoreRuntimeRouteRule> RouteRules;            // 路由规则（已按优先级排序）
    List<CoreRuntimeAccessKey> AccessKeys;            // 访问密钥（含明文密钥 + 哈希）
    CoreRuntimeSettings RuntimeSettings;              // 运行时设置（含 DeveloperFeaturesEnabled）
}
```

---

### AITool.Infrastructure — 基础设施层

所有接口的实现，包含数据库访问、HTTP 请求、代理运行时核心。

#### 数据库 — AppDbContext

```csharp
sealed class AppDbContext : DbContext
{
    DbSet<Site> Sites;
    DbSet<ModelLibraryItem> ModelLibraryItems;
    DbSet<SiteModelMapping> SiteModelMappings;
    DbSet<DetectionTask> DetectionTasks;
    DbSet<DetectionTaskExecution> DetectionTaskExecutions;
    DbSet<ProxyRouteEntry> ProxyRouteEntries;
    DbSet<ProxyRouteRule> ProxyRouteRules;
    DbSet<ProxyAccessKey> ProxyAccessKeys;
    DbSet<ProxyUsageLog> ProxyUsageLogs;
    DbSet<ConversationTurnLog> ConversationTurnLogs;
    DbSet<ModelHealthMonitor> ModelHealthMonitors;
    DbSet<SystemRuntimeSettings> SystemRuntimeSettings;
}
```

#### ProxyCallRecorder — 统一记录服务实现

```csharp
sealed class ProxyCallRecorder : IProxyCallRecorder
```

将 `ProxyCallContext` 派发到三个存储：
1. `DeveloperInvocationTraceStore`（开发者追踪，Core 内存）
2. `IUsageLogService`（使用日志）
3. `IConversationLogService`（对话日志）

所有方法内部均捕获异常，确保记录失败不影响代理主链路。

#### DeveloperInvocationTraceStore — 开发者调用追踪存储

```csharp
sealed class DeveloperInvocationTraceStore  // Singleton，Core 内存，LinkedList
```

- 最多保留 **100 条** 最近代理请求的详细记录
- **6 小时** 过期自动清理
- 生命周期：`BeginTrace` → `BeginTraceAttempt` → `CompleteTraceAttempt`
- 客户端断连时：`CancelPending(traceId, reason)` 强制标记为 error
- 追踪完成时触发 `OnTraceCompleted` → `CoreUnifiedProxyEventPublisher` → 统一 "proxy-request" 事件 → Admin 副本

#### ProxyRequestMetadataCache — 代理元数据缓存

```csharp
sealed class ProxyRequestMetadataCache  // Singleton，基于 IMemoryCache，NeverRemove 永不过期
```

缓存策略：所有配置数据（密钥、运行时设置、路由、模型等）永不过期，仅在 Admin 数据变更时通过 `Invalidate*` 方法清除对应 key，下次查询从 DB/快照重建。代理热路径零 DB 查询。

**双宿主自适应**：
- **Core 宿主**：从 `ICoreRuntimeConfigProvider` 的快照读取（内存，不查 DB）
- **Admin 宿主**：从 `AppDbContext` 查询数据库

核心方法：
- `GetRuntimeSettingsAsync()` — 获取运行时设置（含 `DeveloperFeaturesEnabled`）
- `GetRouteTargetsForModelAsync()` — 获取指定模型的候选路由列表（已排序，含站点合并信息）
- `ValidateAccessKeyAsync()` — 验证访问密钥
- `GetChatModelsAsync()` / `GetChatTargetsAsync()` — 对话测试页查询

#### CoreRuntimeConfigProvider — Core 配置快照持有器

```csharp
sealed class CoreRuntimeConfigProvider : ICoreRuntimeConfigProvider  // Singleton
```

- `GetCurrent()` → `Volatile.Read(ref _current)` 读取快照
- `SetCurrent(snapshot)` → `Interlocked.Exchange` 原子替换 + 异步持久化到 `last-good-config.json`
- `TryLoadFromFileAsync()` → Core 启动时恢复上次配置

#### CoreAdminEventBus — Core 事件总线

```csharp
sealed class CoreAdminEventBus  // Singleton，基于 Bounded Channel<CoreAdminEventEnvelope>
```

- 容量 **10000**，`DropOldest` 溢出策略
- 支持 SSE 订阅机制通知 Admin 新事件到达

#### CoreEventSpoolStore — 事件磁盘缓冲

```csharp
sealed class CoreEventSpoolStore  // Singleton
```

- JSONL 文件：`{RootPath}/events-{yyyyMMdd}.jsonl`，按日轮转
- `AppendAsync()` — 追加事件到磁盘
- `ListAfterAsync(afterSeq)` — 查询某序号后的所有事件（供 replay）
- `TrimAckedAsync(ackedId)` — 清理已被 Admin 确认的旧事件
- `PruneExpiredFilesAsync()` — 安全阀：超过 **30 天** 或 **60 个文件** 自动删除

#### CoreEventSpoolBackgroundService — 事件排空后台服务

```csharp
sealed class CoreEventSpoolBackgroundService : BackgroundService
```

持续从 `CoreAdminEventBus` 的 Channel 中读取事件，写入 `CoreEventSpoolStore` 的磁盘 JSONL 文件。

#### CoreEventSequenceProvider — 事件序号管理

```csharp
sealed class CoreEventSequenceProvider  // Singleton
```

- 维护 `sequence.meta` 文件，每次 `Next()` 调用递增并立即写盘（write-through）
- Core 重启后从 meta 文件或 JSONL 文件恢复序号

#### ProxyUsageLogBatchWriter — 使用日志批量写入器

```csharp
sealed class ProxyUsageLogBatchWriter : BackgroundService  // Singleton
```

- `Channel<UsageLogEntry>` 容量 4096，`DropWrite` 溢出
- 每 800ms 或累积 100 条批量写入 SQLite
- **Core 宿主**：自动检测 AppDbContext 未注册 → **跳过写 DB**（靠事件同步到 Admin）
- **Admin/统一宿主**：直接写 SQLite

#### RouteCircuitStateStore — 熔断状态存储

```csharp
sealed class RouteCircuitStateStore  // Singleton，纯内存状态
```

- `Block(routeId)` — 递增失败计数，达阈值触发熔断
- `Succeed(routeId)` — 成功时清除连续失败计数
- `IsBlocked(routeId)` — 检查是否在熔断窗口内
- `UpdateOptions(recoveryMinutes, threshold)` — 动态更新熔断参数

#### ModelConcurrencyLimiter — 并发控制器

```csharp
sealed class ModelConcurrencyLimiter  // Singleton
```

- 按站点+模型维度维护 `SemaphoreSlim`
- `SkipOnFull` 模式：并发已满 → 返回 false，调用方尝试下一路由
- `WaitForSlot` 模式：排队等待直到释放或超时
- `MaxConcurrency = 0` 时跳过并发控制

#### ProxyProtocolBridge — 跨协议桥接

```csharp
static class ProxyProtocolBridge  // 纯静态方法，无状态
```

OpenAI 和 Anthropic 协议之间的双向转换引擎，支持请求体、非流式响应、流式 SSE 事件的实时转换。

---

## 调用日志 (UsageLog) 数据流

### 写入路径

```
代理请求完成
  │
  ▼
ProxyCallRecorder.RecordUsageAsync(ProxyCallContext)
  │
  ▼
IUsageLogService.LogAsync(UsageLogEntry)
  │
  ▼
UsageLogService (单写)
  └──► ProxyUsageLogBatchWriter.EnqueueAsync()  [后台 Channel 批量写入]
         └─► Core 宿主: 跳过 (无 AppDbContext)，靠统一事件同步
         └─► Admin 宿主: 直接 INSERT ProxyUsageLogs 表

统一事件 (OnTraceCompleted 触发):
  CoreUnifiedProxyEventPublisher → CoreAdminEventBus (内存 Channel)
     └─► CoreEventSpoolStore (磁盘 JSONL)
          └─► Admin: CoreEventPullService 定时 Pull
               └─► AdminUnifiedProxyEventIngestor → 展开 attempts 写 DB + 写内存 Store
```

### 读取路径

```
浏览器 → AJAX → GET /api/admin/usage-logs/list?...
  │
  ▼
UsageLogsApiController (Admin)
  │
  ▼
AppDbContext.ProxyUsageLogs (直接查 SQLite)
  │
  ▼
返回 JSON → 前端渲染
```

**关键特性**：读路径不经过 Core，直接查 Admin 本地 DB。写路径通过事件总线保证 Core 端的数据最终到达 Admin 数据库。

---

## 开发者调试追踪 (Invocations) 数据流

### 写入路径

```
代理请求 (OpenAiProxyController / AnthropicProxyController)
  │
  ▼
ProxyCallRecorder.BeginTrace(callContext)
  │  → DeveloperInvocationTraceStore.AddRequest()
  │     [Core 进程内存，LinkedList，仅作请求生命周期累积器，不对外查询]
  │
  ▼
ProxyCallRecorder.BeginTraceAttempt(traceId, callContext)
  │  → DeveloperInvocationTraceStore.AddAttempt()
  │
  ▼
ProxyCallRecorder.CompleteTraceAttempt(traceId, attemptId, callContext)
  │  → DeveloperInvocationTraceStore.CompleteAttempt()
  │  → 触 OnTraceCompleted 事件
  │     └─► CoreUnifiedProxyEventPublisher → "proxy-request" 统一事件
  │          └─► CoreEventBus → Spool → Admin Pull
  │               └─► AdminUnifiedProxyEventIngestor
  │                    → AdminDeveloperTraceStore (存 CoreUnifiedProxyEvent)
  │
  ▼ [客户端断连]
ProxyCallRecorder.CancelTrace(traceId, "客户端已断开连接")
  │  → DeveloperInvocationTraceStore.CancelPending()
  │  → 触 OnTraceCompleted → 统一事件 Status="error"
```

### 读取路径

```
Admin Index.cshtml.cs OnGetAsync / OnGetList / OnGetDetail / OnGetConcurrency
  │
  ▼ (主路径)
CoreAdminClient.GetDeveloperInvocations*Async()  → Core API 实时查询
  │   GET /api/core/developer/invocations/list
  │   GET /api/core/developer/invocations/detail
  │   GET /api/core/developer/concurrency
  │
  ▼ (Core 不可用 / 异常时降级)
AdminDeveloperTraceStore.List() / Get()  [Admin 本地内存，100条/6小时]
  │   BuildLocalListResponse / BuildLocalDetailResponse / BuildLocalConcurrencyResponse
  │
  ▼
ToSummary → JSON 返回前端

前端 AJAX:
  ?handler=List&pageNumber=N      → 翻页
  ?handler=Detail&traceId=xxx     → 展开卡片详情（完整 headers、bodies、attempts）
```

**关键特性**：Invocations 数据**以 Core API 实时查询为主路径**（请求完成立即可见，无需等事件推送），Core 不可达时降级到 Admin 本地 `AdminDeveloperTraceStore`（由统一事件 `proxy-request` 推送累积，100条/6小时）。Core 的 `DeveloperInvocationTraceStore` 仅作为请求生命周期累积器，完成时通过统一事件推送到 Admin 作为降级数据源。

---

## API 端点汇总

### 代理端点（Core 宿主，端口 5029）

| 方法 | 路由 | 认证方式 | 说明 |
|------|------|----------|------|
| POST | `/v1/chat/completions` | `Authorization: Bearer {key}` | OpenAI Chat Completions 代理 |
| POST | `/v1/completions` | `Authorization: Bearer {key}` | OpenAI Legacy Completions 代理 |
| POST | `/v1/embeddings` | `Authorization: Bearer {key}` | OpenAI Embeddings 代理 |
| POST | `/v1/responses` | `Authorization: Bearer {key}` | Responses API 代理 (HTTP + WebSocket) |
| POST | `/v1/responses/compact` | `Authorization: Bearer {key}` | Responses Compact 代理 |
| GET | `/v1/models` | `Authorization: Bearer {key}` | 模型列表 |
| GET | `/v1/models/{modelId}` | `Authorization: Bearer {key}` | 模型详情 |
| POST | `/v1/messages` | `x-api-key: {key}` | Anthropic Messages 代理 |
| POST | `/v1/messages/count_tokens` | `x-api-key: {key}` | Anthropic Token 计数估算 |
| GET | `/health` | 无 | 健康检查 |

### Core 运行时查询端点（Core 宿主）

| 方法 | 路由 | 说明 |
|------|------|------|
| GET | `/api/core/health` | Core 健康检查 |
| GET | `/api/core/ready` | Core 就绪检查（配置快照已加载） |
| GET | `/api/core/runtime/status` | Core 运行时状态（版本、事件积压等） |
| GET | `/api/core/developer/invocations/list` | 分页查询调用追踪列表 |
| GET | `/api/core/developer/invocations/detail` | 单条追踪详情 |
| GET | `/api/core/developer/concurrency` | 模型并发状态快照 |
| GET | `/api/core/developer/metadata` | 客户端模拟器元数据 |
| GET | `/debug/runtime?key=xxx` | **Core 内存调试页**（受密钥保护的自包含 HTML） |

### Core 配置与事件端点（Core 宿主）

| 方法 | 路由 | 说明 |
|------|------|------|
| POST | `/api/core/config/full-sync` | 接收全量配置快照 |
| POST | `/api/core/config/patch-sync` | 接收增量配置补丁 |
| POST | `/api/core/config/handshake` | 配置握手 |
| GET | `/api/core/events/replay?afterSequenceId=N` | 事件重放 |
| GET | `/api/core/events/stream` | 事件 SSE 推送 |
| POST | `/api/core/events/ack` | 事件确认（清理磁盘） |

### 管理 API（Admin 宿主，端口 5030）

#### 访问密钥 `api/admin/access-keys`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/` | 获取密钥列表 |
| POST | `/create` | 创建密钥 |
| POST | `/toggle/{keyId}` | 切换启用/禁用 |
| POST | `/delete/{keyId}` | 删除密钥 |

#### 路由规则 `api/admin/route-rules`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/entries` | 获取路由入口列表 |
| POST | `/entries` | 创建路由入口 |
| POST | `/entries/delete` | 删除路由入口 |
| GET | `/list?modelName=xxx` | 获取模型的路由规则（按优先级排序） |
| POST | `/save` | 保存路由规则（删除旧的，按新顺序创建） |
| POST | `/toggle/{ruleId}` | 切换规则启用/禁用 |
| POST | `/delete/{ruleId}` | 删除规则 |

#### 对话测试 `api/admin/chat`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/models` | 获取可对话的模型列表 |
| GET | `/targets` | 获取站点模型目标列表 |
| GET | `/models/{modelId}/targets` | 获取指定模型的目标列表 |
| POST | `/send` | 发送对话消息（非流式） |
| POST | `/send-stream` | 发送对话消息（SSE 流式） |

#### 使用日志 `api/admin/usage-logs`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/list` | 分页查询使用日志 |
| GET | `/summary` | 使用日志统计摘要 |
| GET | `/request-detail/{requestId}` | 单次请求详细日志（含所有尝试） |

#### 其他管理端点

| 路径 | 说明 |
|------|------|
| `api/admin/site-catalog/*` | 站点模型目录拉取与导入 |
| `api/admin/detection/*` | 模型检测 |
| `api/admin/models/*` | 模型管理 |
| `api/admin/conversations/*` | 对话记录查询 |
| `api/admin/analytics/*` | 统计分析 |
| `api/admin/route-fallback/*` | 路由回退事件 |

---

## 管理后台页面

所有页面在 `src/AITool.Admin/Pages/Admin/` 下。

| 页面路径 | 功能 | 说明 |
|----------|------|------|
| `/Admin/Chat` | 对话测试 | 流式/非流式对话，支持路由选择 + 故障转移 |
| `/Admin/Sites` | 站点管理 | 创建/编辑/删除/导入/导出 |
| `/Admin/Models` | 模型库 | 模型列表 + 创建/编辑/删除，含映射状态 |
| `/Admin/Routes` | 路由规则管理 | 模型入口 → 候选实例队列 → 拖拽排序 → 保存 |
| `/Admin/AccessKeys` | 访问密钥管理 | 创建/切换/删除密钥 |
| `/Admin/Detection` | 模型检测 | 手动/定时检测，增量进度 |
| `/Admin/DetectionTasks` | 检测任务管理 | Cron 定时任务配置 |
| `/Admin/ModelHealth` | 模型健康监控 | 可用率时间线图表 |
| `/Admin/Conversations` | 对话记录 | 按会话浏览用户输入和 AI 输出 |
| `/Admin/UsageLogs` | 使用日志 | Token 级别用量追踪 |
| `/Admin/Analytics` | 统计分析 | 趋势、分布、缓存命中率等可视化 |
| `/Admin/System/Settings` | 系统设置 | 超时、重试、熔断、并发、日志保留、开发者功能 |
| `/Admin/Developer/Invocations` | 调试追踪 | 代理请求全链路详情（调用调试/客户端模拟/并发检测三栏） |

---

## 代理请求流程

```
客户端请求 → POST /v1/chat/completions (或 /v1/messages 等)
  ↓
读取请求体，解析 model 字段
  ↓
验证访问密钥 (ProxyRequestMetadataCache → SHA256 哈希比对)
  ↓
从缓存获取该模型的路由列表 (GetRouteTargetsForModelAsync)
  ↓
遍历路由 (按 ModelPriority → InstancePriority → Priority 排序):
  ├─ 跳过被熔断的路由 (RouteCircuitStateStore.IsBlocked)
  ├─ 跳过禁用的站点
  ├─ 并发控制 (ModelConcurrencyLimiter.AcquireAsync)
  │   ├─ SkipOnFull → 跳到下一路由
  │   └─ WaitForSlot → 排队等待
  ├─ 协议兼容性判断 → 必要时跨协议桥接 (ProxyProtocolBridge)
  ├─ 转发: ForwardAsync (非流式) 或 ForwardStreamingAsync (流式)
  ├─ success → RecordUsage → RecordConversation → CompleteTrace → 返回
  └─ fail → Block(route) → 尝试下一路由
  ↓
全部失败 → 返回 502 error
```

**Chat 对话测试与代理的关键区别**：
- Chat **不触发熔断**（不调用 `circuitStore.Block()`）
- Chat **不写入 Invocations 追踪**（与 master 行为一致）
- Chat 日志 `Source = "chat"`，代理日志 `Source = "proxy"`

---

## Core 运行时内存调试页

用于开发/测试时实时查看 Core 内存状态的受保护页面。

**访问方式**：`http://127.0.0.1:5029/debug/runtime?key=aitool-debug`

**安全保护**：密钥 SHA256 哈希校验（在 `appsettings.json` 的 `Debug:KeyHash` 配置）

**页面内容**：
- **路由规则 Tab** — 按代理选路优先级排序的完整路由表
- **站点 Tab** — 站点名称、协议、BaseUrl、ApiKey（脱敏 `sk-a***yz`）、启停状态
- **当前并发 Tab** — 模型、站点、活跃数（红色高亮）、上限、排队数

**数据来源**：`CoreRuntimeConfigProvider.GetCurrent()` + `ModelConcurrencyLimiter.ListRecent()` — 纯 Core 内存实时数据。

---

## 启动流程

### Admin (端口 5030)

```
1. 配置: AddCommonInfrastructure + AddAdminInfrastructure
2. DB 自动创建: EnsureCreated() + Schema 补丁
3. 启动 CoreConfigSyncHostedService: 延迟 2s → 构建全量快照 → 下发到 Core (最多 5 次重试)
4. 启动 CoreEventPullHostedService: 订阅 Core SSE 事件流
5. MapRazorPages() + MapControllers()
```

### Core (端口 5029)

```
1. 配置: AddCommonInfrastructure + AddProxyRuntimeInfrastructure
2. 尝试恢复本地配置: TryLoadFromFileAsync() → last-good-config.json
3. 订阅事件: DeveloperInvocationTraceStore.OnTraceCompleted → fire-and-forget 发布
4. 订阅事件: RouteCircuitStateStore.OnCircuitOpened → fire-and-forget 发布
5. MapControllers() → 代理端点 + 管理查询端点 + 配置接收端点
```

---

## 数据库

- **引擎：** SQLite
- **文件位置：** Admin 运行目录下的 `aitool.db`
- **初始化方式：** `EnsureCreated()`（不用 Migration，改实体后需删库重建或手动加列）
- **Core 不连接数据库：** Core 从 Admin 的配置快照读取，不直接访问 SQLite

---

## 测试

### 测试策略

- **单元测试**（`AITool.ApplicationTests`）：隔离的 SQLite 内存数据库
- **集成测试**（`AITool.Admin.IntegrationTests` + `AITool.Core.IntegrationTests`）：`WebApplicationFactory<Program>` 构建完整测试宿主，独立临时 SQLite

### Core 集成测试

| 文件 | 说明 |
|------|------|
| `Proxy/AnthropicProxyControllerTests.cs` | Anthropic 代理端到端测试 |
| `Proxy/OpenAiCrossProtocolProxyTests.cs` | OpenAI 入口跨协议桥接测试 |
| `Proxy/ProxyFallbackFlowTests.cs` | 代理故障转移流程测试 |
| `Proxy/ProxyResilienceTests.cs` | 代理韧性测试（超时、重试） |
| `Proxy/ResponsesProxyTests.cs` | Responses API 代理测试 |
| `Proxy/ModelConcurrencyLimiterTests.cs` | 并发控制器测试 |
| `Chat/ChatApiTests.cs` | 对话测试 API 测试 |

### Admin 集成测试

| 文件 | 说明 |
|------|------|
| `UsageLogs/UsageLogsApiTests.cs` | 使用日志 API 测试 |
| `Conversations/ConversationPageTests.cs` | 对话记录页面测试 |
| `Developer/DeveloperInvocationsPageTests.cs` | 开发者调试追踪页面测试 |
| `ClientSimulator/ClientSimulatorPageTests.cs` | 客户端模拟器页面测试 |
| `System/SystemSettingsPageTests.cs` | 系统设置页面测试 |

---

## 快速开始

```bash
# 还原依赖
dotnet restore

# 编译
dotnet build

# 启动 Admin (端口 5030) — 管理后台
cd src/AITool.Admin
dotnet run

# 启动 Core (端口 5029) — 代理端 + 运行时查询 API
cd src/AITool.Core
dotnet run
```

首次运行自动创建 SQLite 数据库。访问 `http://127.0.0.1:5030` 进入管理后台。

---

## 关键设计决策

1. **双宿主分离**：Admin 管理面 + Core 代理面独立进程。Admin 持有 DB 作为配置的唯一写入源，Core 通过配置快照全量/增量同步获取配置，不连接 DB。
2. **配置快照**：`CoreRuntimeConfigSnapshot` 是 Admin→Core 配置传递的唯一载体，包含路由规则（已按优先级预排序）、站点（含 ApiKey）、密钥、运行时设置。Core 从内存读取，不经 DB。
3. **事件总线 + 磁盘 Spool**：使用日志、对话记录、开发者追踪事件通过 Core 事件总线 → 磁盘 JSONL 文件 → Admin 定时拉取 + 幂等写入的模式，保证 Admin 离线时数据不丢失（最多 30 天磁盘缓冲）。
4. **不用 Migration**：使用 `EnsureCreated()` 自动建库 + Schema 补丁机制添加新列，适合快速迭代。
5. **无导航属性**：实体间通过 ID 关联，避免 EF Core 复杂查询翻译问题（SQLite 对 DateTimeOffset 等类型支持有限）。
6. **内存熔断**：`RouteCircuitStateStore` 是 Singleton，重启后状态丢失。渐进式熔断：连续失败达阈值才触发。
7. **路由规则删除重建**：保存路由规则时先删除旧的后按新顺序创建，保证优先级精确。
8. **每次尝试记录日志**：代理请求为每次路由尝试记录一条日志，最终结果标记 `IsFinalResult = true`。
9. **批量日志写入**：`ProxyUsageLogBatchWriter` 使用后台 Channel 批量写入 SQLite，避免代理热路径上的 I/O 竞争。
10. **元数据缓存永不过期**：`ProxyRequestMetadataCache` 的访问密钥、运行时设置、路由目标、模型列表等配置数据使用 `NeverRemove` 优先级，永不自动过期。仅在 Admin 修改数据后主动清除对应 key，下一次查询从 DB/快照重建并永久缓存。高频代理热路径永远不查 DB。平衡了极致性能和数据一致性。
11. **跨协议桥接纯静态无状态**：`ProxyProtocolBridge` 支持 OpenAI ↔ Anthropic 双向 SSE 流式转换。
12. **开发者追踪客户端断连保护**：`CancelTrace` 强制将 pending trace 标记为 error，避免 Invocations 页面出现永久等待的僵尸记录。
13. **调试页受密钥保护**：`/debug/runtime` 通过 SHA256 验证访问密钥，不依赖 Admin 认证体系，安全独立。
14. **DeveloperFeaturesEnabled 仅控制 UI 可见性**：代理控制器始终创建调用追踪（不再受此开关门控），确保 UsageLog 数据通过统一事件正常推送。DeveloperFeaturesEnabled 只控制 Invocations 页面 404 和侧边栏入口隐藏。
15. **ConversationLog 内存保护**：`FileConversationLogStore.QueryAsync` 最多返回 1000 条，从最新文件倒序流式过滤，避免全量加载 JSONL 文件导致内存线性增长。`ConversationLogService` 复用 `ProxyRequestMetadataCache` 读取开关，不再每次创建 DB scope。

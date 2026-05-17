# AI Tool - 项目详细文档

## 项目简介

AI Tool 是一个 **AI API 网关 / 反向代理**，用于统一管理和转发多个 AI 服务站点的请求。它提供一个管理后台来管理站点、模型、路由规则、访问密钥，并通过 OpenAI/Anthropic 兼容协议对外提供代理服务，支持按优先级自动故障转移。

核心能力：
- 多站点管理（注册 OpenAI/Anthropic 兼容的 AI 服务站点）
- 统一模型库（将不同站点的同名模型归一化管理）
- 路由规则（为模型配置多站点优先级，失败自动重试下一个站点）
- 熔断保护（站点连续失败达到阈值后临时屏蔽）
- 实时流式透传（OpenAI 和 Anthropic SSE 原生流式转发）
- 跨协议桥接（OpenAI 客户端可透明使用 Anthropic 后端，反之亦然）
- 访问密钥（对外提供统一的 API Key 认证）
- 模型检测（定时/手动探测模型可用性）
- 健康监控（模型可用率时间线图表）
- 对话测试（内置 Chat 页面端到端测试代理链路）
- 使用日志（Token 级别的用量追踪，含重试次数、缓存命中、流式延迟等）
- 统计分析（用量趋势、模型分布、缓存命中率等可视化仪表盘）
- 开发者追踪（内存储存环形缓冲区，调试代理请求全链路）

---

## 技术栈

| 层级 | 技术 |
|------|------|
| 运行时 | .NET 10 (ASP.NET Core) |
| 数据库 | SQLite (EF Core, EnsureCreated 模式，无 Migration) |
| 任务调度 | Hangfire (内存存储) |
| 前端 | Razor Pages + Bootstrap 5.3.3 + 原生 CSS |
| 交互方式 | 管理页面全部使用 AJAX（fetch API），无整页刷新 |
| 测试 | xUnit + FluentAssertions + 隔离 SQLite 数据库 |

---

## 项目结构

```
AI-Tool/
├── src/
│   ├── AITool.Domain/           # 领域实体（纯 POCO，零依赖，sealed 类）
│   ├── AITool.Application/      # 应用层接口和 DTO（纯接口定义，不含实现）
│   ├── AITool.Infrastructure/   # 基础设施实现（EF Core、HttpClient、Hangfire）
│   └── AITool.Web/              # Web 入口（控制器 + Razor Pages + Program.cs）
├── tests/
│   ├── AITool.ApplicationTests/ # 单元测试
│   └── AITool.IntegrationTests/ # 集成测试
└── AiTool.slnx                  # 解决方案文件
```

**依赖关系：** `Domain` ← `Application` ← `Infrastructure` ← `Web`

- `Domain`：纯 POCO 实体，零外部依赖，所有类为 `sealed`
- `Application`：仅引用 `Domain`，定义接口和 DTO，不含任何实现
- `Infrastructure`：引用 `Application` 和 `Domain`，实现所有接口
- `Web`：引用所有项目，作为宿主入口

---

## 分层架构

### AITool.Domain — 领域实体

纯 POCO 类，无外部依赖。所有实体使用 `Guid` 主键，均为 `sealed` 类，没有基类或共享接口。实体之间**没有 EF Core 导航属性**，关系通过 ID 手动关联。

**实体总览（12 个实体，6 个命名空间）：**

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
| `AITool.Domain.Operations` | `SystemRuntimeSettings` | 系统运行时配置（单例 Id=1） |
| `AITool.Domain.Detection` | `DetectionLog` | 模型检测日志（已废弃，后续将移除） |
| `AITool.Domain.Detection` | `DetectionTask` | 定时检测任务 |
| `AITool.Domain.Detection` | `DetectionTaskExecution` | 检测任务执行记录 |

#### Site — AI 服务站点

```csharp
// 命名空间：AITool.Domain.Sites
sealed class Site
{
    Guid Id;                    // 主键
    string Name;                // 站点名称（必填，最长200）
    string BaseUrl;             // 站点根地址（必填，最长500）
    string ApiKey;              // 站点访问密钥（必填，最长500）
    string ProtocolType;        // 协议类型，默认 "OpenAI"（必填，最长50）
    bool SupportsOpenAi;        // 是否支持 OpenAI 协议
    bool SupportsAnthropic;     // 是否支持 Anthropic 协议
    bool IsEnabled = true;      // 是否启用
    DateTimeOffset CreatedAt;   // 创建时间，默认 UtcNow
}
```

`ProtocolType` 区分 `"OpenAI"` 和 `"Anthropic"` 两种协议，影响请求转发时的目标路径和认证方式。`SupportsOpenAi` / `SupportsAnthropic` 标记该站点实际支持的协议类型，用于跨协议桥接时的兼容性判断。

#### ModelLibraryItem — 统一模型库

```csharp
// 命名空间：AITool.Domain.Models
sealed class ModelLibraryItem
{
    Guid Id;                        // 主键
    string ModelName;               // 统一模型名称（唯一索引，必填，最长200）
    string DisplayName;             // 页面显示名称（最长200）
    string ModelType;               // 模型类型，如 "chat"、"embedding"（必填，最长50）
    bool IsEnabled = true;          // 是否启用
    DateTimeOffset CreatedAt;       // 创建时间，默认 UtcNow
}
```

`ModelName` 有唯一索引，不同站点的同名模型归一到同一条 `ModelLibraryItem` 记录。导入模型时通过字典查找避免重复创建。

#### SiteModelMapping — 站点模型映射

```csharp
// 命名空间：AITool.Domain.SiteCatalog
sealed class SiteModelMapping
{
    Guid Id;                        // 主键
    Guid SiteId;                    // 站点ID（FK → Site）
    Guid ModelLibraryItemId;        // 模型库ID（FK → ModelLibraryItem）
    string RemoteModelName;         // 站点上的原始模型名（必填，最长200）
    string LastStatus = "unknown";  // 最后拉取/检测状态（必填，最长50）
    bool IsEnabled = true;          // 该站点上的模型是否启用
}
// 复合唯一索引：(SiteId, RemoteModelName)
```

`RemoteModelName` 是该模型在远程站点上的实际名称，可能与统一模型名不同。例如统一模型名 `gpt-4o`，某站点上可能叫 `gpt-4o-2024-08-06`。

#### ProxyRouteEntry — 代理路由入口

```csharp
// 命名空间：AITool.Domain.Proxy
sealed class ProxyRouteEntry
{
    Guid Id;                        // 主键
    string EntryName;               // 入口名称（对外暴露的模型名，唯一索引）
    DateTimeOffset CreatedAt;       // 创建时间，默认 UtcNow
}
```

`ProxyRouteEntry` 是路由规则的逻辑入口，一个入口下可挂载多条 `ProxyRouteRule`（不同站点、不同模型实例），通过拖拽排序管理优先级。

#### ProxyRouteRule — 代理路由规则

```csharp
// 命名空间：AITool.Domain.Proxy
sealed class ProxyRouteRule
{
    Guid Id;                        // 主键
    string ExternalModelName;       // 对外暴露的模型名（索引，必填，最长200）
    string UpstreamModelName;       // 上游模型名（用于日志标记）
    Guid SiteId;                    // 目标站点ID（FK → Site）
    string SiteModelName;           // 站点上的模型名（必填，最长200）
    int Priority;                   // 总优先级（数值越小优先级越高）
    int ModelPriority;              // 模型级优先级
    int InstancePriority;           // 实例级优先级
    bool IsEnabled = true;          // 是否启用
}
// 索引：ExternalModelName（用于快速查找该模型的所有路由）
```

`ExternalModelName` 对应 `ModelLibraryItem.ModelName`。路由按 `ModelPriority`、`InstancePriority`、`Priority` 三级排序，保存路由规则时按列表顺序设置优先级（0 最高）。

#### ProxyAccessKey — 访问密钥

```csharp
// 命名空间：AITool.Domain.Proxy
sealed class ProxyAccessKey
{
    Guid Id;                        // 主键
    string KeyName;                 // 密钥名称（必填，最长200）
    string PlainKey;                // 原始密钥（仅创建时存储，用于展示一次）
    string AccessKeyHash;           // SHA256 哈希值，只存哈希不存原文（必填，最长500）
    string MaskedValue;             // 脱敏显示值，如 "sk-***abc"（必填，最长100）
    bool IsEnabled = true;          // 是否启用
}
```

密钥验证流程：客户端传入原始密钥 → SHA256 哈希 → 与数据库中的 `AccessKeyHash` 比对。

#### ProxyUsageLog — 使用日志

```csharp
// 命名空间：AITool.Domain.Proxy
sealed class ProxyUsageLog
{
    Guid Id;                        // 主键
    string RequestId;               // 请求唯一标识（用于关联同一次请求的多条日志）
    Guid AccessKeyId;               // 访问密钥ID（FK → ProxyAccessKey）
    string ProtocolType;            // 协议类型 "OpenAI" 或 "Anthropic"（必填，最长50）
    string RequestModel;            // 请求的模型名（必填，最长200）
    string? AttemptedModel;         // 实际尝试的上游模型名
    Guid TargetSiteId;              // 命中的目标站点ID
    string Status;                  // 状态 "success" 或 "fail"（必填，最长50）
    string Source = "proxy";        // 来源 "proxy" 或 "chat"
    int RetryCount;                 // 尝试了几个路由（重试次数）
    int AttemptIndex;               // 当前尝试的序号（从 0 开始）
    bool IsFinalResult;             // 是否为最终结果（成功或最后一次失败）
    bool FallbackTriggered;         // 是否触发了故障转移
    string? ErrorMessage;           // 失败时的错误信息
    int InputTokens;                // 输入 Token 数
    int CachedTokens;               // 缓存命中的 Token 数（Prompt Cache）
    int OutputTokens;               // 输出 Token 数
    int TotalTokens;                // 总 Token 数（= InputTokens + OutputTokens）
    bool IsStreaming;               // 是否为流式请求
    bool IsStreamInterrupted;       // 流式请求是否被中断
    int? FirstTokenLatencyMs;       // 首 Token 延迟（毫秒）
    int? StreamDurationMs;          // 流式传输总时长（毫秒）
    int? TotalDurationMs;           // 请求总耗时（毫秒）
    string? ReasoningEffort;        // 推理力度参数（如 "low"、"high"）
    DateTimeOffset RequestedAt;     // 请求时间，默认 UtcNow
}
// 索引：RequestedAt（用于日志按时间查询和清理）
```

代理请求会为每次尝试记录一条日志（`AttemptIndex` 标识序号），最终成功或全部失败的那条标记 `IsFinalResult = true`。`FallbackTriggered` 标记是否因前次失败而触发了备用路由。

#### SystemRuntimeSettings — 系统运行时配置

```csharp
// 命名空间：AITool.Domain.Operations
sealed class SystemRuntimeSettings
{
    int Id = 1;                         // 固定为 1（单例）
    int ProxyRequestTimeoutSeconds;     // 代理请求超时（秒）
    int ProxyRetryCount;                // 代理请求重试次数
    int DetectionRequestTimeoutSeconds; // 检测请求超时（秒）
    int DetectionRetryCount;            // 检测请求重试次数
    int DetectionConcurrency;           // 检测并发数
    int CircuitBreakerFailureThreshold; // 熔断失败阈值
    int CircuitBreakerRecoveryMinutes;  // 熔断恢复时间（分钟）
    int UsageLogRetentionDays;          // 使用日志保留天数
    bool UsageLogAutoCleanupEnabled;    // 是否启用使用日志自动清理
    bool DeveloperFeaturesEnabled;      // 是否启用开发者调试功能
    DateTimeOffset? LastUsageLogPrunedAt;  // 上次清理时间
    int LastUsageLogPrunedCount;           // 上次清理数量
}
```

系统运行时配置通过管理后台 `/Admin/System/Settings` 页面维护，修改后即时生效（通过缓存失效机制）。

#### ModelHealthMonitor — 模型健康监控配置

```csharp
// 命名空间：AITool.Domain.Models
sealed class ModelHealthMonitor
{
    Guid Id;                            // 主键
    Guid ModelLibraryItemId;            // 模型库ID（唯一索引，FK → ModelLibraryItem）
    DateTimeOffset CreatedAt;           // 创建时间，默认 UtcNow
}
```

标记哪些模型需要在健康监控页面展示。

#### DetectionLog — 模型检测日志

```csharp
// 命名空间：AITool.Domain.Detection
sealed class DetectionLog
{
    Guid Id;                        // 主键
    Guid SiteId;                    // 被探测的站点ID
    Guid ModelLibraryItemId;        // 被探测的模型ID
    string Status;                  // "success" 或 "fail"（必填，最长50）
    int DurationMs;                 // 探测耗时（毫秒）
    string? ErrorMessage;           // 失败时的错误信息（最长2000）
    DateTimeOffset CheckedAt;       // 检测时间，默认 UtcNow
}
// 索引：CheckedAt（用于日志清理）
```

> **注意：** `DetectionLog` 已废弃，后续将移除。模型检测日志统一围绕 `ProxyUsageLog` 设计。

#### DetectionTask — 定时检测任务

```csharp
// 命名空间：AITool.Domain.Detection
sealed class DetectionTask
{
    Guid Id;                            // 主键
    string Name;                        // 任务名称（必填，最长200）
    string CronExpression;              // Cron 表达式（必填，最长100）
    bool IsEnabled = true;              // 是否启用
    Guid? ModelLibraryItemId;           // 指定模型ID，null 表示检测全部模型
}
```

`ModelLibraryItemId` 为 null 时，该任务会检测所有站点模型映射。

#### DetectionTaskExecution — 检测任务执行记录

```csharp
// 命名空间：AITool.Domain.Detection
sealed class DetectionTaskExecution
{
    Guid Id;                            // 主键
    Guid DetectionTaskId;               // 检测任务ID（FK → DetectionTask）
    string Status;                      // "running" / "completed" / "failed"（必填，最长50）
    DateTimeOffset StartedAt;           // 开始时间，默认 UtcNow
    DateTimeOffset? FinishedAt;         // 结束时间
    string? Summary;                    // 执行结果摘要（最长2000）
}
// 索引：StartedAt
```

### 实体关系图

```
Site ──1:N──> SiteModelMapping <──N:1── ModelLibraryItem
                  │
                  │ (检测日志记录)
                  ↓
              DetectionLog ──(SiteId, ModelLibraryItemId)

ModelLibraryItem ──1:1──> ModelHealthMonitor (唯一索引)

ProxyRouteEntry ──1:N──> ProxyRouteRule ──N:1──> Site (SiteId)
  (EntryName = ExternalModelName)

ProxyAccessKey ──1:N──> ProxyUsageLog <──N:1── Site
                            (AccessKeyId, TargetSiteId)

SystemRuntimeSettings (单例 Id=1)

DetectionTask ──1:N──> DetectionTaskExecution
     │
     └──0:1──> ModelLibraryItem (可选，null=全部模型)
```

---

### AITool.Application — 应用层

定义接口和 DTO，不含实现。仅引用 `Domain` 项目。

| 文件 | 说明 |
|------|------|
| **Proxy/** | |
| `IProxyForwardService.cs` | 代理转发接口（含流式）+ `ProxyForwardRequest`/`ProxyForwardResult` DTO |
| `ProxyForwardingOptions.cs` | 代理转发配置（超时、重试次数） |
| **Routing/** | |
| `IRouteSelectionService.cs` | 路由选择接口 + `RouteSelectionResult` DTO |
| **UsageLogs/** | |
| `IUsageLogService.cs` | 使用日志接口 + `UsageLogEntry` DTO |
| **SiteCatalog/** | |
| `ISiteCatalogClient.cs` | 站点模型目录拉取接口 |
| `PullSiteModelsCommand.cs` | 拉取模型命令 DTO + `PullSiteModelsResult` |
| **Detection/** | |
| `IModelProbeService.cs` | 模型探测接口 + `ModelProbeResult` DTO |
| `RunDetectionCommand.cs` | 执行检测命令 DTO + `RunDetectionResult` |
| **Dashboard/** | |
| `GetDashboardOverviewQuery.cs` | 仪表盘查询标记 + `DashboardOverviewResult` DTO |
| **Operations/** | |
| `ISystemRuntimeSettingsService.cs` | 系统运行时配置接口 |
| **Common/** | |
| `ILogRetentionService.cs` | 日志清理接口 |
| **Sites/** | |
| `CreateSiteCommand.cs` | 创建站点 DTO |
| **Models/** | |
| `CreateModelLibraryItemCommand.cs` | 创建模型 DTO |

#### 核心接口和 DTO 详细定义

**IProxyForwardService** — 代理转发

```csharp
interface IProxyForwardService
{
    Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken ct = default);
    Task<ProxyForwardResult> ForwardStreamingAsync(
        ProxyForwardRequest request,
        Func<string, CancellationToken, Task> onSseDataAsync,
        CancellationToken ct = default);
}

sealed class ProxyForwardRequest
{
    string TargetBaseUrl;         // 目标站点根地址
    string TargetApiKey;          // 目标站点 API 密钥
    string ProtocolType;          // "OpenAI" 或 "Anthropic"
    string TargetModelName;       // 目标站点上的模型名称
    string RequestBody;           // 原始请求体（JSON 字符串）
    string? PreparedRequestBody;  // 协议转换后的请求体（跨协议桥接时使用）
    bool EnableStreaming;         // 是否启用流式转发
    int RequestTimeoutSeconds;    // 请求超时时间（秒）
    int RetryCount;               // 重试次数
    string? TargetPath;           // 自定义目标路径（覆盖默认路径）
    Dictionary<string, string> ForwardHeaders;  // 需要转发的额外请求头
}

sealed class ProxyForwardResult
{
    bool Success;                 // 是否成功
    int StatusCode;               // HTTP 状态码
    string ResponseBody;          // 响应体内容
    int InputTokens;              // 输入 Token 数
    int CachedTokens;             // 缓存命中 Token 数
    int OutputTokens;             // 输出 Token 数
    bool IsStreaming;             // 是否为流式响应
    bool HasStartedStreaming;     // 是否已开始流式传输
    bool IsStreamInterrupted;     // 流式传输是否被中断
    int? FirstTokenLatencyMs;     // 首 Token 延迟（毫秒）
    int? StreamDurationMs;        // 流式传输总时长（毫秒）
    int? TotalDurationMs;         // 请求总耗时（毫秒）
    string? ErrorMessage;         // 错误信息
}
```

**IRouteSelectionService** — 路由选择

```csharp
interface IRouteSelectionService
{
    // 选择优先级最高的单条路由
    Task<RouteSelectionResult> SelectRouteAsync(string externalModelName, CancellationToken ct = default);
    // 选择路由，跳过指定站点（熔断场景）
    Task<RouteSelectionResult> SelectRouteAsync(string externalModelName, HashSet<Guid> excludedSiteIds, CancellationToken ct = default);
    // 获取所有启用的路由，按优先级升序（用于失败重试）
    Task<List<RouteSelectionResult>> SelectAllRoutesAsync(string externalModelName, CancellationToken ct = default);
}

sealed class RouteSelectionResult
{
    ProxyRouteRule? Route;    // 匹配到的路由规则
    bool Found => Route is not null;
}
```

**IUsageLogService** — 使用日志

```csharp
interface IUsageLogService
{
    Task LogAsync(UsageLogEntry entry, CancellationToken ct = default);
}

sealed class UsageLogEntry
{
    string RequestId;             // 请求唯一标识
    Guid AccessKeyId;             // 访问密钥ID
    string ProtocolType;          // "OpenAI" 或 "Anthropic"
    string RequestModel;          // 请求的模型名
    string? AttemptedModel;       // 实际尝试的上游模型名
    Guid TargetSiteId;            // 目标站点ID
    string Status;                // "success" 或 "fail"
    string Source = "proxy";      // "proxy" 或 "chat"
    int RetryCount;               // 尝试的路由数量
    int AttemptIndex;             // 当前尝试序号
    bool IsFinalResult;           // 是否为最终结果
    bool FallbackTriggered;       // 是否触发了故障转移
    string? ErrorMessage;         // 错误信息
    int InputTokens;              // 输入 Token 数
    int CachedTokens;             // 缓存命中 Token 数
    int OutputTokens;             // 输出 Token 数
    bool IsStreaming;             // 是否为流式请求
    bool IsStreamInterrupted;     // 流式是否被中断
    int? FirstTokenLatencyMs;     // 首 Token 延迟
    int? StreamDurationMs;        // 流式传输时长
    int? TotalDurationMs;         // 请求总耗时
    string? ReasoningEffort;      // 推理力度参数
}
```

**ISiteCatalogClient** — 站点模型目录拉取

```csharp
interface ISiteCatalogClient
{
    Task<IReadOnlyList<string>> GetModelsAsync(Site site, CancellationToken ct);
}
```

**IModelProbeService** — 模型探测

```csharp
interface IModelProbeService
{
    Task<ModelProbeResult> ProbeAsync(Site site, ModelLibraryItem model, CancellationToken ct);
}

sealed class ModelProbeResult
{
    bool Success;             // 探测是否成功
    int DurationMs;           // 探测耗时（毫秒）
    string? ErrorMessage;     // 失败时的错误信息
}
```

**ISystemRuntimeSettingsService** — 系统运行时配置

```csharp
interface ISystemRuntimeSettingsService
{
    Task<SystemRuntimeSettings> GetOrCreateAsync(CancellationToken ct = default);
    Task UpdateAsync(SystemRuntimeSettings settings, CancellationToken ct = default);
    Task<int> ClearUsageLogsAsync(CancellationToken ct = default);
}
```

**DashboardOverviewResult** — 仪表盘统计

```csharp
sealed class DashboardOverviewResult
{
    int EnabledSiteCount;         // 启用的站点数量
    int ModelCount;               // 模型总数
    int MappingCount;             // 站点模型映射总数
    int RecentDetectionCount;     // 最近24小时检测次数
    double RecentSuccessRate;     // 最近24小时检测成功率
    int EnabledTaskCount;         // 启用的定时检测任务数
}
```

---

### AITool.Infrastructure — 基础设施层

所有接口的实现，包含数据库访问、HTTP 请求、调度等。

#### 数据库 — AppDbContext

```csharp
sealed class AppDbContext : DbContext
{
    DbSet<Site> Sites;
    DbSet<ModelLibraryItem> ModelLibraryItems;
    DbSet<SiteModelMapping> SiteModelMappings;
    DbSet<DetectionLog> DetectionLogs;
    DbSet<DetectionTask> DetectionTasks;
    DbSet<DetectionTaskExecution> DetectionTaskExecutions;
    DbSet<ProxyRouteEntry> ProxyRouteEntries;
    DbSet<ProxyRouteRule> ProxyRouteRules;
    DbSet<ProxyAccessKey> ProxyAccessKeys;
    DbSet<ProxyUsageLog> ProxyUsageLogs;
    DbSet<ModelHealthMonitor> ModelHealthMonitors;
    DbSet<SystemRuntimeSettings> SystemRuntimeSettings;
}
```

EF Core 配置要点：
- 所有实体使用 `Guid` 主键
- `ModelLibraryItem.ModelName` — 唯一索引
- `SiteModelMapping` — `(SiteId, RemoteModelName)` 复合唯一索引
- `ModelHealthMonitor.ModelLibraryItemId` — 唯一索引
- `ProxyRouteEntry.EntryName` — 唯一索引
- `ProxyRouteRule.ExternalModelName` — 普通索引
- `DetectionLog.CheckedAt` — 索引
- `DetectionTaskExecution.StartedAt` — 索引
- `ProxyUsageLog.RequestedAt` — 索引
- 字符串字段均有 `HasMaxLength` 约束
- **无导航属性**，关系通过 ID 手动 Join 或字典查找

#### ProxyForwardService — 代理转发服务

```csharp
sealed class ProxyForwardService : IProxyForwardService  // HttpClient Typed 实例
```

核心流程：
1. 根据 `ProtocolType` 拼接目标 URL（支持 `TargetPath` 覆盖）：
   - OpenAI: `{BaseUrl}/v1/chat/completions`
   - Anthropic: `{BaseUrl}/v1/messages`
2. 使用 `PreparedRequestBody`（跨协议桥接时已转换）或替换请求体中的 `model` 字段
3. 设置认证头和额外转发头（`ForwardHeaders`）：
   - OpenAI: `Authorization: Bearer {ApiKey}`
   - Anthropic: `x-api-key: {ApiKey}` + `anthropic-version: 2023-06-01`
4. **非流式**：发送 HTTP POST 请求，读取响应，提取 Token 用量
5. **流式**（`ForwardStreamingAsync`）：逐行读取 SSE 事件流，通过回调 `onSseDataAsync` 实时传递给控制器，同时追踪首 Token 延迟和流式传输时长
6. 调用 `ExtractTokenUsage()` 从响应 JSON 提取 Token 用量：
   - OpenAI: `usage.prompt_tokens` + `usage.completion_tokens`
   - Anthropic: `usage.input_tokens` + `usage.output_tokens`（含 `cache_read_input_tokens`、`cache_creation_input_tokens`）
7. 返回 `ProxyForwardResult`（含状态码、响应体、Token 用量、流式指标）

#### RouteSelectionService — 路由选择服务

```csharp
sealed class RouteSelectionService : IRouteSelectionService  // Scoped
```

查询 `ProxyRouteRules` 表，按 `ModelPriority`、`InstancePriority`、`Priority` 升序排列。支持排除指定站点 ID 集合（熔断场景）。

#### RouteCircuitStateStore — 熔断状态存储

```csharp
sealed class RouteCircuitStateStore  // Singleton，纯内存状态
{
    void Block(Guid routeId);         // 记录一次失败，达阈值触发熔断
    void Succeed(Guid routeId);       // 成功时清除连续失败计数
    bool IsBlocked(Guid routeId);     // 判断是否在熔断窗口内
    void UpdateOptions(int failThreshold, int recoveryMinutes);  // 动态更新熔断参数
}
```

实现原理：
- `_failCounts: Dictionary<Guid, int>` — 每个路由的连续失败次数
- `_blockedSites: Dictionary<Guid, DateTimeOffset>` — 被熔断的路由及其解除时间
- `Block()`: 先检查是否已熔断（已熔断则跳过），递增失败计数，达阈值时记录解除时间
- `Succeed()`: 清除该路由的失败计数（不清除熔断状态，熔断只能等待超时解除）
- `IsBlocked()`: 检查是否在屏蔽窗口内，已超时则自动清除
- 熔断参数（阈值、恢复时间）可通过 `SystemRuntimeSettings` 动态配置

#### ProxyUsageLogBatchWriter — 使用日志批量写入器

```csharp
sealed class ProxyUsageLogBatchWriter : BackgroundService  // Singleton
```

- 使用 `Channel<UsageLogEntry>` 有界通道（容量 4096）接收日志
- 批量聚合：每 800ms 或累积 100 条触发一次批量写入
- 避免代理热路径上的 SQLite 写入竞争
- 测试模式下直接同步写入（绕过通道）

#### UsageLogService — 使用日志服务

```csharp
sealed class UsageLogService : IUsageLogService  // Scoped
```

将 `UsageLogEntry` 入队到 `ProxyUsageLogBatchWriter` 的通道中，由后台服务批量写入数据库。

#### SystemRuntimeSettingsService — 系统运行时配置服务

```csharp
sealed class SystemRuntimeSettingsService : ISystemRuntimeSettingsService  // Scoped
```

- `GetOrCreateAsync()`: 读取配置，不存在时自动创建默认值
- `UpdateAsync()`: 更新配置（含边界校验），更新后通知缓存失效
- `ClearUsageLogsAsync()`: 清空所有使用日志

#### OpenAiSiteCatalogClient — 站点模型目录拉取

```csharp
sealed class OpenAiSiteCatalogClient : ISiteCatalogClient  // HttpClient Typed 实例
```

实现：向站点发送 `GET {BaseUrl}/v1/models`，Header 带 `Authorization: Bearer {ApiKey}`，解析 OpenAI 格式的 `{ data: [{ id: "model-name" }] }` 响应，返回模型名称列表。

#### OpenAiModelProbeService — 模型探测服务

```csharp
sealed class OpenAiModelProbeService : IModelProbeService  // HttpClient Typed 实例
```

实现：向站点发送最小化请求 `POST {BaseUrl}/v1/chat/completions`，请求体 `{ model: "...", messages: [{role:"user",content:"hi"}], max_tokens: 1 }`，测量响应耗时。成功返回 `Success=true`，失败时解析错误响应体中的 `error.message` 字段。

#### ModelHealthRequestService — 模型健康请求服务

```csharp
sealed class ModelHealthRequestService  // HttpClient Typed 实例
```

发送真实对话请求（随机数学题目）到上游站点，验证模型可用性。结果记录到 `SiteModelMapping.LastStatus` 和 `ProxyUsageLog`。

#### LogRetentionService — 日志清理服务

```csharp
sealed class LogRetentionService : ILogRetentionService  // Scoped
```

删除超过配置保留天数的 `ProxyUsageLog` 记录。保留天数由 `SystemRuntimeSettings.UsageLogRetentionDays` 控制，注入 `Func<DateTimeOffset>` 便于测试。

#### HangfireDetectionScheduler — 定时检测调度器

```csharp
sealed class HangfireDetectionScheduler  // Singleton
```

- `ScheduleAllAsync()`: 启动时将所有启用的 `DetectionTask` 注册为 Hangfire RecurringJob，JobId 格式为 `detection-{task.Id}`
- `ExecuteDetectionTaskAsync()`: 执行单次检测任务
  1. 创建 `DetectionTaskExecution` 记录（状态 "running"）
  2. 查询所有站点模型映射（如果任务指定了模型则过滤）
  3. 按 `DetectionConcurrency` 并发调用 `IModelProbeService.ProbeAsync()` 探测
  4. 记录每条 `DetectionLog`，更新映射的 `LastStatus`
  5. 更新执行记录状态为 "completed"，记录摘要

---

## 核心业务流程

### 1. 代理请求流程（含故障转移和跨协议桥接）

```
客户端请求 → POST /v1/chat/completions (或 /v1/messages)
  ↓
读取请求体，解析 model 字段
  ↓
验证访问密钥（通过 ProxyRequestMetadataCache 缓存，5秒 TTL）
  ├─ OpenAI: 从 Authorization: Bearer {token} 提取
  ├─ Anthropic: 从 x-api-key Header 提取
  └─ SHA256 哈希 → 与 ProxyAccessKey.AccessKeyHash 比对
  ↓
从缓存获取该模型的所有候选路由（GetRouteTargetsForModelAsync）
  ↓
收集被熔断的路由 ID 集合（IsBlocked）
  ↓
遍历路由列表:
  ├─ 跳过被熔断的路由
  ├─ 查找目标站点 Site
  ├─ 跳过禁用的站点
  ├─ 判断协议兼容性（ResolveProtocolForClient）
  │   ├─ 站点协议与客户端协议一致 → 直接转发
  │   └─ 不一致 → 通过 ProxyProtocolBridge 进行跨协议转换
  │       ├─ OpenAI → Anthropic: 转换请求体格式、认证头
  │       └─ Anthropic → OpenAI: 转换请求体格式、认证头
  ├─ 判断是否流式请求：
  │   ├─ 非流式 → ForwardAsync()，响应体协议转换后返回
  │   └─ 流式 → ForwardStreamingAsync()，实时 SSE 事件协议转换
  │       ├─ OpenAI SSE → Anthropic SSE（或反向）
  │       └─ 追踪首 Token 延迟、流式传输时长
  ├─ 成功 → 记录日志(含 AttemptIndex、延迟指标) → circuitStore.Succeed() → 返回响应
  └─ 失败 → circuitStore.Block() → 加入熔断集合 → 尝试下一个路由
  ↓
全部失败 → 记录失败日志(Status="fail", IsFinalResult=true) → 返回 502 错误
```

**OpenAI 和 Anthropic 控制器的区别：**
- 认证方式不同（Bearer Token vs x-api-key）
- 转发时 `ProtocolType` 不同（影响目标 URL 和认证头设置）
- Anthropic 控制器转发 `anthropic-version`、`anthropic-beta` 等协议头
- 流式 SSE 格式不同（OpenAI `data: {...}` vs Anthropic `event: xxx\ndata: {...}`）
- 控制器内部逻辑结构一致，通过 `ProxyProtocolBridge` 统一处理协议差异

**跨协议桥接（ProxyProtocolBridge）：**
- `PrepareRequestBody()`: 根据客户端和目标协议转换请求体（模型名、消息格式、参数映射）
- `AdaptResponseBodyForClient()`: 将非流式响应从目标协议转换回客户端协议格式
- `ConvertOpenAiStreamChunkToAnthropic()` / `ConvertAnthropicStreamChunkToOpenAi()`: 实时流式 SSE 事件转换
- 站点需标记 `SupportsOpenAi` / `SupportsAnthropic` 来声明协议兼容性

**熔断机制：**
- 每次 `Block()` 调用递增连续失败计数
- 连续失败达阈值（默认 5 次，可通过系统设置配置）后触发熔断，屏蔽可配置时间（默认 2 分钟）
- 成功一次即清除连续失败计数（`Succeed()`）
- 熔断状态存储在内存中（Singleton），重启后丢失
- 被熔断的路由在代理请求循环中被跳过，不消耗请求

### 2. 路由规则管理流程

```
选择入口 → 调用 GET /api/admin/route-rules/entries 获取路由入口列表
  ↓
创建/管理入口 → POST /api/admin/route-rules/entries
  ↓
选择模型 → 调用 GET /api/admin/route-rules/discover-sites?modelName=xxx
  ↓
查找该模型关联的所有启用的 SiteModelMapping
  ↓
返回站点实例列表（SiteId、SiteName、RemoteModelName、SiteEnabled）
  ↓
前端展示站点列表，拖拽排序设置优先级
  ↓
保存（POST /api/admin/route-rules/save）：
  ├─ 删除该模型的所有旧规则
  └─ 按列表顺序创建新规则（Priority = 列表索引，0 最高）
```

`ExternalModelName` 对应 `ModelLibraryItem.ModelName`，客户端请求时使用这个名字。`SiteModelName` 通常取自 `SiteModelMapping.RemoteModelName`。

### 3. 模型检测流程

```
触发检测（手动 POST /api/admin/detection/probe/{mappingId} 或定时任务）
  ↓
查找该模型的所有启用的 SiteModelMapping
  ↓
按 DetectionConcurrency 并发探测每个站点上的模型
  ├─ 发送 { model, messages: [{role:"user",content:"hi"}], max_tokens: 1 }
  ├─ 测量响应耗时
  └─ 记录 DetectionLog（状态、耗时、错误信息）
  ↓
前端轮询增量进度（GET /api/admin/detection/progress/{taskId}）
  ├─ 只返回新结果（基于 LastReportedCount）
  └─ 避免重复刷新已完成的结果
```

### 4. 对话测试流程

```
选择模型 → 发送消息
  ↓
查找 ModelLibraryItem → 获取 ModelName
  ↓
从缓存获取路由列表
  ↓
有路由规则时：
  ├─ 加载所有启用的站点 ID 到内存
  ├─ 过滤出被熔断的站点（内存中调用 IsBlocked，避免 LINQ 翻译错误）
  ├─ 按优先级逐个尝试转发（同代理流程，支持流式和非流式）
  ├─ Chat 测试不触发熔断（不调用 circuitStore.Block()）
  └─ 全部失败返回错误
  ↓
无路由规则时 → 回退到 SiteModelMapping 直接查询（SendFallback）
  ↓
返回 AI 回复
  ├─ OpenAI: 解析 choices[0].message.content
  └─ Anthropic: 解析 content[0].text
```

**Chat 与代理的关键区别：**
- Chat **不触发熔断**（不调用 `circuitStore.Block()`），每次请求独立尝试所有站点
- Chat 日志 `Source = "chat"`，代理日志 `Source = "proxy"`
- Chat 不需要访问密钥认证（管理后台直接使用）
- Chat 没有路由规则时自动回退到 SiteModelMapping 查询
- Chat 支持流式调试（`POST /api/admin/chat/send-stream`）

### 5. 模型导入流程

```
选择站点 → GET /api/admin/site-catalog/fetch-models/{siteId}
  ↓
调用上游 GET /v1/models 获取远程模型列表
  ↓
对比已有 SiteModelMapping（按 RemoteModelName）
  ↓
前端展示模型列表，标记已导入/未导入
  ↓
用户勾选模型 → POST /api/admin/site-catalog/import-selected
  ↓
预加载已有模型库到字典（避免多站点同名模型的 UNIQUE 冲突）
  ↓
逐个处理：
  ├─ 选中且模型库不存在 → 创建 ModelLibraryItem + SiteModelMapping
  ├─ 选中且模型库已存在 → 复用 ModelLibraryItem，创建 SiteModelMapping
  ├─ 选中且映射已存在 → 确保启用
  └─ 未选中且映射已存在 → 禁用映射
```

### 6. 一键拉取全部模型流程

```
POST /api/admin/site-catalog/fetch-all-models
  ↓
创建 FetchAllProgress 对象（ConcurrentDictionary 存储）
  ↓
后台 Task.Run 并发拉取所有启用站点
  ↓
前端轮询 GET /api/admin/site-catalog/fetch-all-progress/{taskId}
  ↓
全部完成后前端展示合并结果
  ↓
用户勾选并导入（复用 import-selected 端点）
```

---

## API 端点汇总

### 代理端点（面向客户端）

| 方法 | 路由 | 认证方式 | 说明 |
|------|------|----------|------|
| POST | `/v1/chat/completions` | `Authorization: Bearer {key}` | OpenAI 兼容代理（支持流式和非流式） |
| GET | `/v1/models` | `Authorization: Bearer {key}` | OpenAI 兼容模型列表 |
| POST | `/v1/messages` | `x-api-key: {key}` | Anthropic 兼容代理（支持流式和非流式） |
| POST | `/v1/messages/count_tokens` | `x-api-key: {key}` | Anthropic Token 计数估算 |
| GET | `/health` | 无 | 健康检查 |

### 管理 API（面向管理后台）

#### 访问密钥 `api/admin/access-keys`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/` | 获取密钥列表 |
| POST | `/create` | 创建密钥（生成 `sk-` + 32位十六进制字符串，SHA256 哈希存储） |
| POST | `/toggle/{keyId}` | 切换启用/禁用 |
| POST | `/delete/{keyId}` | 删除密钥 |

#### 站点模型目录 `api/admin/site-catalog`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/fetch-models/{siteId}` | 拉取单个站点的模型列表 |
| POST | `/fetch-all-models` | 一键拉取全部站点模型（异步） |
| GET | `/fetch-all-progress/{taskId}` | 获取批量拉取进度 |
| POST | `/import-selected` | 导入用户勾选的模型 |

#### 模型检测 `api/admin/detection`

| 方法 | 端点 | 说明 |
|------|------|------|
| POST | `/probe/{mappingId}` | 探测单个站点模型映射 |
| POST | `/probe-model/{modelId}` | 探测模型的所有映射（异步） |
| POST | `/probe-all` | 探测全部映射（异步） |
| GET | `/progress/{taskId}` | 获取探测进度（增量） |

#### 模型管理 `api/admin/models`

| 方法 | 端点 | 说明 |
|------|------|------|
| POST | `/clear-all` | 清空所有模型及关联数据 |

#### 路由规则 `api/admin/route-rules`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/entries` | 获取路由入口列表 |
| POST | `/entries` | 创建路由入口 |
| POST | `/entries/delete` | 删除路由入口 |
| GET | `/site-instances` | 获取站点实例列表 |
| GET | `/models` | 获取有映射的模型列表（用于下拉选择） |
| GET | `/discover-sites?modelName=xxx` | 自动发现拥有该模型的站点 |
| GET | `/list?modelName=xxx` | 获取模型的路由规则（按优先级排序） |
| POST | `/save` | 保存路由规则（删除旧的，按新顺序创建） |
| POST | `/toggle/{ruleId}` | 切换规则启用/禁用 |
| POST | `/delete/{ruleId}` | 删除规则 |

#### 对话测试 `api/admin/chat`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/models` | 获取可对话的模型列表（含可用站点数） |
| POST | `/send` | 发送对话消息（非流式，支持路由规则 + 失败重试） |
| POST | `/send-stream` | 发送对话消息（SSE 流式，支持路由规则 + 失败重试） |

#### 使用日志 `api/admin/usage-logs`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/list` | 分页查询使用日志（支持时间范围、站点、来源筛选） |
| GET | `/request-detail/{requestId}` | 获取单次请求的详细日志（含所有尝试） |
| GET | `/summary` | 获取使用日志统计摘要 |

#### 统计分析 `api/admin/analytics`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/options` | 获取筛选器选项（站点列表、模型列表） |
| GET | `/dashboard` | 获取统计分析数据（趋势、分布、缓存命中率等，后台异步查询） |

---

## 管理后台页面

所有页面在 `src/AITool.Web/Pages/` 下，使用共享侧边栏布局（`Shared/_Layout.cshtml`）。

### 布局结构

- **侧边栏**：固定左侧 260px，移动端 `< 992px` 折叠为抽屉
- **顶部栏**：显示页面标题 + 版本号，含移动端菜单按钮
- **导航分区**：概览 / 资源管理 / 代理配置 / 监控运维 / 开发调试
- **当前页高亮**：JS 自动匹配路径并添加 `.active` 类

### 页面清单

| 页面路径 | 功能 | 交互方式 | 说明 |
|----------|------|----------|------|
| `/` (Index) | 仪表盘首页 | 服务端渲染 | 展示站点数、模型数、映射数、检测统计 |
| `/Login` | 管理员登录 | 表单 POST | MD5 密码验证（可选，未设置密码时跳过） |
| `/Admin/Dashboard` | 状态看板 | 服务端渲染 | 展示概览统计信息 |
| `/Admin/Chat` | 对话测试 | AJAX | 全屏页面（Layout=null），无侧边栏 |
| `/Admin/ClientSimulator` | 客户端模拟器 | AJAX | 模拟 OpenAI/Anthropic 客户端请求 |
| `/Admin/Sites` | 站点管理 | 服务端渲染 | 列表 + 创建/编辑/删除/导入 |
| `/Admin/Sites/Create` | 创建站点 | 表单 POST | |
| `/Admin/Sites/Edit` | 编辑站点 | 表单 POST | |
| `/Admin/Sites/Import` | 导入模型 | AJAX | 单站点拉取 + 勾选导入 |
| `/Admin/Sites/Export` | 导出数据 | 服务端渲染 | |
| `/Admin/Models` | 模型库 | AJAX | 模型列表 + 创建/编辑/删除 |
| `/Admin/Models/Create` | 创建模型 | 表单 POST | |
| `/Admin/Models/Edit` | 编辑模型 | 表单 POST | |
| `/Admin/Routes` | 路由规则管理 | AJAX | 模型选择→自动发现站点→拖拽排序→保存 |
| `/Admin/AccessKeys` | 访问密钥管理 | AJAX | 创建/切换/删除密钥 |
| `/Admin/Detection` | 模型检测 | AJAX + 轮询 | 手动触发检测 + 增量进度 |
| `/Admin/DetectionTasks` | 检测任务（定时） | 服务端渲染 | Cron 定时任务管理 |
| `/Admin/ModelHealth` | 模型健康监控 | 服务端渲染 | 可用率时间线图表 |
| `/Admin/UsageLogs` | 调用日志 | 服务端渲染 | 来源、目标站点、状态、重试次数、延迟指标列 |
| `/Admin/Analytics` | 统计分析 | AJAX | 用量趋势、模型分布、缓存命中率等可视化 |
| `/Admin/System/Settings` | 系统设置 | AJAX | 超时、重试、熔断、日志清理、开发者功能等配置 |
| `/Admin/Developer/Invocations` | 调试追踪 | AJAX | 查看近期代理请求的全链路详情 |

### 侧边栏导航分区

```
概览
  ├── 仪表盘 (/)
  ├── 状态看板 (/Admin/Dashboard)
  └── 对话测试 (/Admin/Chat)

资源管理
  ├── 站点管理 (/Admin/Sites)
  └── 模型库 (/Admin/Models)

代理配置
  ├── 路由规则 (/Admin/Routes)
  └── 访问密钥 (/Admin/AccessKeys)

监控运维
  ├── 模型检测 (/Admin/Detection)
  ├── 检测任务 (/Admin/DetectionTasks)
  ├── 模型健康 (/Admin/ModelHealth)
  ├── 使用日志 (/Admin/UsageLogs)
  └── 统计分析 (/Admin/Analytics)

开发调试
  ├── 系统设置 (/Admin/System/Settings)
  ├── 调试追踪 (/Admin/Developer/Invocations)
  └── 客户端模拟器 (/Admin/ClientSimulator)
```

---

## Web 层核心服务

### ProxyRequestMetadataCache — 代理元数据缓存

```csharp
sealed class ProxyRequestMetadataCache  // Singleton，基于 IMemoryCache
```

- 缓存访问密钥、运行时设置、路由目标、模型列表等，TTL 约 5 秒
- 核心方法：
  - `ValidateAccessKeyAsync()`: 验证访问密钥（缓存命中时无需查询数据库）
  - `GetRuntimeSettingsAsync()`: 获取系统运行时配置
  - `GetRouteTargetsForModelAsync()`: 获取指定模型的候选路由列表
  - `GetEnabledModelNamesAsync()`: 获取所有启用的模型名
  - `GetChatModelsAsync()`: 获取可用于对话测试的模型列表
  - `GetFallbackTargetAsync()`: 获取回退目标（无路由规则时使用）
- 失效方法：`InvalidateAccessKeys()`、`InvalidateRuntimeSettings()`、`InvalidateRouteTargets()` 等
- 管理后台修改配置后调用失效方法，确保 5 秒内代理路径读取到最新配置

### ProxyProtocolBridge — 跨协议桥接

```csharp
static class ProxyProtocolBridge  // 纯静态方法，无状态
```

OpenAI 和 Anthropic 协议之间的双向转换引擎，核心方法：
- `PrepareRequestBody()`: 转换请求体（消息格式、参数映射、模型名替换）
- `AdaptResponseBodyForClient()`: 转换非流式响应（Token 用量、内容格式）
- `BuildAnthropicStreamStart()` / `ConvertOpenAiStreamChunkToAnthropic()`: OpenAI → Anthropic 流式 SSE 转换
- `ConvertAnthropicStreamChunkToOpenAi()`: Anthropic → OpenAI 流式 SSE 转换
- `CompleteAnthropicStream()` / `EnsureAnthropicStreamClosed()`: 优雅关闭流式传输

### DeveloperInvocationTraceStore — 开发者调试追踪

```csharp
sealed class DeveloperInvocationTraceStore  // Singleton，内存环形缓冲区
```

- 最多保留 100 条最近代理请求的详细记录（6 小时过期自动清理）
- 每条记录包含：请求头、请求体、响应体、每次尝试的详情、耗时、协议信息
- 通过 `/Admin/Developer/Invocations` 页面查看
- 需在系统设置中启用 `DeveloperFeaturesEnabled`

### AnalyticsBackgroundQueryExecutor — 统计分析后台查询

```csharp
sealed class AnalyticsBackgroundQueryExecutor : BackgroundService  // Singleton
```

- 限制统计分析查询为单消费者队列（最多 4 个并发查询）
- 结果缓存 20 秒，避免重复查询
- 版本化缓存键，确保参数变化时刷新

### ModelVendorCatalogService — 厂商模型目录

```csharp
sealed class ModelVendorCatalogService  // Singleton
```

- 从 `model-vendor-catalog.json` 配置文件加载厂商和模型匹配规则
- 支持通配符、正则表达式、精确匹配三种模式
- 提供厂商名称、图标（SVG）、样式等元数据

### AdminAuthService — 管理员认证

```csharp
sealed class AdminAuthService  // Scoped
```

- MD5 密码管理，存储在 `appsettings.json`
- 方法：`HasPasswordConfigured()`、`VerifyPassword()`、`SetPasswordAsync()`

---

## 依赖注入配置

在 `Program.cs` 中注册：

| 注册 | 生命周期 | 说明 |
|------|----------|------|
| `AppDbContext` | Scoped | SQLite EF Core 上下文 |
| `ISiteCatalogClient` → `OpenAiSiteCatalogClient` | HttpClient Typed | 拉取上游站点模型列表 |
| `IModelProbeService` → `OpenAiModelProbeService` | HttpClient Typed | 模型可用性探测 |
| `ModelHealthRequestService` | HttpClient Typed | 模型健康检测 |
| `IRouteSelectionService` → `RouteSelectionService` | Scoped | 路由规则查询 |
| `IProxyForwardService` → `ProxyForwardService` | HttpClient Typed | 请求转发（含流式） |
| `IUsageLogService` → `UsageLogService` | Scoped | 使用日志入队 |
| `ISystemRuntimeSettingsService` → `SystemRuntimeSettingsService` | Scoped | 系统运行时配置 |
| `ProxyUsageLogBatchWriter` | Singleton (BackgroundService) | 使用日志批量写入 |
| `RouteCircuitStateStore` | Singleton | 熔断状态（内存） |
| `ProxyRequestMetadataCache` | Singleton | 代理元数据缓存 |
| `DeveloperInvocationTraceStore` | Singleton | 开发者调试追踪 |
| `AnalyticsBackgroundQueryExecutor` | Singleton (BackgroundService) | 统计分析后台查询 |
| `ModelVendorCatalogService` | Singleton | 厂商模型目录 |
| `AdminAuthService` | Scoped | 管理员认证 |
| `ILogRetentionService` → `LogRetentionService` | Scoped | 日志清理 |
| `HangfireDetectionScheduler` | Singleton | 定时任务调度 |

HttpClient Typed 实例由 `AddHttpClient<TInterface, TImpl>()` 注册，自动管理 `HttpClient` 生命周期。

---

## 启动流程（Program.cs）

```
1. 构建 WebApplication
   ├─ AddRazorPages()           — 注册 Razor Pages
   ├─ AddControllers()          — 注册 API 控制器
   ├─ AddDbContext<AppDbContext>() — SQLite 连接（数据库文件在运行目录下 aitool.db）
   ├─ AddHttpClient<T,T>()      — 注册 Typed HttpClient 服务
   ├─ AddScoped<T,T>()          — 注册 Scoped 服务
   ├─ AddSingleton<T>()         — 注册 Singleton 服务（含 BackgroundService）
   ├─ AddMemoryCache()          — 注册内存缓存
   └─ AddHangfire()             — 内存存储 + Dashboard + Server

2. 启动初始化（using scope）
   ├─ db.Database.EnsureCreated()  — 自动创建数据库
   ├─ 修补 IsEnabled 列            — 兼容旧数据库（ALTER TABLE 添加缺失列）
   └─ ScheduleAllAsync()           — 注册所有启用的检测任务到 Hangfire

3. 中间件配置
   ├─ UseStaticFiles()         — 启用静态文件服务
   ├─ 管理员认证中间件          — 检查登录状态（未设置密码时跳过）
   ├─ MapGet("/health")        — 健康检查端点
   ├─ UseHangfireDashboard()   — Hangfire 仪表盘（/hangfire）
   ├─ RecurringJob             — 日志清理（按 SystemRuntimeSettings 配置）
   ├─ MapRazorPages()          — 映射 Razor Pages 路由
   └─ MapControllers()         — 映射 API 控制器路由

4. app.Run()
```

数据库连接字符串优先从 `Configuration.GetConnectionString("DefaultConnection")` 读取，为空时默认 `Data Source={运行目录}/aitool.db`。

---

## 数据库

- **引擎：** SQLite
- **文件位置：** Web 应用运行目录下的 `aitool.db`
- **初始化方式：** `EnsureCreated()`（不用 Migration，改实体后需删库重建或手动加列）
- **日志保留：** 使用日志保留天数由 `SystemRuntimeSettings.UsageLogRetentionDays` 配置，自动清理由 `UsageLogAutoCleanupEnabled` 控制

**重要约束：**
- `ModelLibraryItem.ModelName` 有唯一索引，不同站点的同名模型归一到同一条记录
- `SiteModelMapping` 有 `(SiteId, RemoteModelName)` 复合唯一索引
- `ModelHealthMonitor.ModelLibraryItemId` 有唯一索引
- `ProxyRouteEntry.EntryName` 有唯一索引
- 实体之间没有 EF Core 导航属性，关系通过手动查询解析（ID 字典查找）
- LINQ 查询限制：不能在 `Where()` 中直接调用 C# 方法（如 `IsBlocked()`），需要先 `ToListAsync()` 加载到内存再过滤

**数据库补丁机制：**
启动时检查 `SiteModelMappings` 表是否存在 `IsEnabled` 列，不存在则 `ALTER TABLE` 添加。这是为了兼容旧数据库（该列是后来新增的）。

---

## 前端 UI 规范

- **CSS 框架：** Bootstrap 5.3.3
- **自定义主题：** `wwwroot/css/theme.css`，使用 CSS 变量
- **主色调：** `#6C9EFF`（柔和蓝）
- **字体：** 系统字体栈，支持中文（PingFang SC / Microsoft YaHei）
- **侧边栏：** 固定左侧 260px，移动端 `< 992px` 折叠为抽屉
- **交互模式：** 管理操作均使用 AJAX（fetch API），状态提示用 `alert` 组件
- **无外部 JS 库：** 拖拽排序使用 HTML5 原生 Drag and Drop API
- **CDN 依赖：** Bootstrap 5.3.3 CSS + JS Bundle（通过 jsdelivr CDN 加载）

---

## 定时任务

通过 Hangfire 管理（内存存储，重启后丢失）：

| 任务 ID | 执行时间 | 说明 |
|----------|----------|------|
| `log-retention-prune` | 每日 03:00 UTC | 清理超过保留天数的使用日志 |
| `detection-{taskId}` | 按各任务的 Cron 表达式 | 执行定时模型检测 |

Hangfire Dashboard：`/hangfire`（仅本地访问）

---

## 关键设计决策

1. **不用 Migration：** 使用 `EnsureCreated()` 自动建库，适合开发阶段快速迭代。改实体后需删库重建或手动加列（有补丁机制）。
2. **无导航属性：** 实体间通过 ID 关联，查询时手动 Join 或字典查找，避免 EF Core 复杂查询翻译问题（SQLite 对 DateTimeOffset 等类型支持有限）。
3. **内存熔断：** `RouteCircuitStateStore` 是 Singleton，重启后状态丢失。采用渐进式熔断：连续失败达阈值才触发，避免单次失败就屏蔽站点。阈值和恢复时间可通过系统设置动态配置。
4. **增量进度报告：** 检测进度使用 `LastReportedCount` 追踪，每次轮询只返回新增结果，避免重复刷新。
5. **模型名去重：** 导入模型时预加载字典，避免多站点同名模型的 UNIQUE 约束冲突。后续同名模型复用已有 `ModelLibraryItem`。
6. **Chat 不触发熔断：** 对话测试页每次请求都是独立的，不影响代理链路的熔断状态。
7. **每次尝试记录日志：** 代理请求为每次路由尝试记录一条日志（`AttemptIndex` 标识序号），最终结果标记 `IsFinalResult = true`，便于追踪完整的故障转移链路。
8. **路由规则删除重建：** 保存路由规则时先删除旧的再按新顺序创建，而不是更新，简化逻辑。
9. **批量日志写入：** `ProxyUsageLogBatchWriter` 使用后台通道批量写入，避免代理热路径上的 SQLite 写入竞争。
10. **元数据缓存：** `ProxyRequestMetadataCache` 提供 5 秒 TTL 的内存缓存层，保持代理路径高性能，同时允许管理后台修改即时传播。
11. **跨协议桥接：** `ProxyProtocolBridge` 纯静态无状态设计，支持 OpenAI ↔ Anthropic 双向转换（请求体、响应体、流式 SSE），使网关能透明连接不同协议的后端。
12. **后台统计查询：** `AnalyticsBackgroundQueryExecutor` 限制昂贵的聚合查询为单消费者队列，防止数据库过载。

---

## 常见 LINQ 陷阱

本项目使用 SQLite + EF Core，有以下已知限制：

1. **不能在 `Where()` 中调用 C# 方法：**
   ```csharp
   // 错误：SQLite 无法翻译 IsBlocked() 方法
   _dbContext.Sites.Where(s => _circuitStore.IsBlocked(s.Id))

   // 正确：先加载到内存，再过滤
   var siteIds = await _dbContext.Sites.Select(s => s.Id).ToListAsync();
   var blockedIds = new HashSet<Guid>(siteIds.Where(id => _circuitStore.IsBlocked(id)));
   ```

2. **`DateTimeOffset` 比较有限：** 部分 `DateTimeOffset` 运算无法翻译，需注意。

3. **`Contains` 与 `HashSet`：** 使用 `List.Contains()` 而非 `HashSet.Contains()`，前者能被 EF Core 翻译为 SQL `IN` 子句。

---

## 快速开始

```bash
# 还原依赖
dotnet restore

# 编译
dotnet build

# 运行（默认 http://localhost:5000）
cd src/AITool.Web
dotnet run
```

首次运行自动创建 SQLite 数据库。访问管理后台即可开始配置站点和模型。

---

## 测试

### 测试策略

- **单元测试**（`AITool.ApplicationTests`）：使用隔离的 SQLite 内存数据库测试业务逻辑
- **集成测试**（`AITool.IntegrationTests`）：使用 `WebApplicationFactory<Program>` 构建完整测试宿主，每个测试类使用独立的临时 SQLite 数据库文件

### 测试覆盖

**ApplicationTests（单元测试）：**

| 文件 | 说明 |
|------|------|
| `Operations/SystemRuntimeSettingsServiceTests.cs` | 系统配置服务测试 |
| `Operations/SystemRuntimeSettingsServiceSqliteTests.cs` | 系统配置 SQLite 持久化测试 |
| `Retention/LogRetentionServiceTests.cs` | 日志清理服务测试 |
| `Proxy/RouteCircuitStateStoreTests.cs` | 熔断状态存储测试 |
| `Proxy/UsageLogServiceTests.cs` | 使用日志服务测试 |
| `Routing/RouteSelectionServiceTests.cs` | 路由选择服务测试 |
| `Detection/DetectionTests.cs` | 模型检测逻辑测试 |

**IntegrationTests（集成测试）：**

| 文件 | 说明 |
|------|------|
| `Proxy/AnthropicProxyControllerTests.cs` | Anthropic 代理端到端测试 |
| `Proxy/OpenAiCrossProtocolProxyTests.cs` | OpenAI 入口跨协议桥接测试 |
| `Proxy/ProxyFallbackFlowTests.cs` | 代理故障转移流程测试 |
| `Proxy/ProxyResilienceTests.cs` | 代理韧性测试（超时、重试） |
| `Proxy/ProxyMetadataCacheTests.cs` | 代理元数据缓存测试 |
| `Auth/AdminAuthTests.cs` | 管理员认证测试 |
| `Analytics/AnalyticsPageTests.cs` | 统计分析页面测试 |
| `Chat/ChatApiTests.cs` | 对话测试 API 测试 |
| `Health/HealthEndpointTests.cs` | 健康检查端点测试 |
| `Models/ModelEditCacheTests.cs` | 模型编辑缓存测试 |
| `Sites/SiteBulkDeleteTests.cs` | 站点批量删除测试 |
| `Sites/SitePagesTests.cs` | 站点页面测试 |
| `System/SystemSettingsCacheTests.cs` | 系统设置缓存测试 |
| `System/SystemSettingsPageTests.cs` | 系统设置页面测试 |
| `UsageLogs/UsageLogsApiTests.cs` | 使用日志 API 测试 |
| `Developer/DeveloperInvocationsPageTests.cs` | 开发者调试追踪页面测试 |
| `ClientSimulator/ClientSimulatorPageTests.cs` | 客户端模拟器页面测试 |

---

## 典型使用场景

### 场景：配置一个 OpenAI 代理

1. **创建站点**：在站点管理页添加一个 OpenAI 兼容站点（填入名称、Base URL、API Key）
2. **导入模型**：点击导入，拉取该站点支持的模型列表，勾选需要的模型
3. **配置路由**：在路由规则页选择模型，自动发现拥有该模型的站点，拖拽设置优先级
4. **创建访问密钥**：在访问密钥页创建一个对外使用的 API Key
5. **对外代理**：客户端使用 `POST http://your-host/v1/chat/completions`，Header 带 `Authorization: Bearer {your-key}`，Body 中 `model` 填统一模型名

### 场景：多站点故障转移

1. 添加多个提供相同模型的站点（如 OpenAI、Azure OpenAI、本地 Ollama）
2. 在路由规则页为同一模型配置多个站点优先级
3. 代理请求时自动按优先级尝试，首个失败自动切换下一个
4. 连续失败达阈值的站点被临时屏蔽（熔断）
5. 在使用日志中查看每次请求的重试次数和故障转移详情

### 场景：跨协议混合使用

1. 注册一个 Anthropic 站点（如 Claude API）和一个 OpenAI 站点（如 GPT API）
2. 为同一个外部模型名配置两个站点的路由规则
3. OpenAI 客户端请求时，如果命中 Anthropic 站点，网关自动桥接协议转换
4. 流式请求也支持实时 SSE 事件格式转换（OpenAI SSE ↔ Anthropic SSE）

### 场景：定时检测模型可用性

1. 在检测任务页创建定时任务，设置 Cron 表达式
2. 系统按计划自动探测所有映射的可用性
3. 在模型健康页查看可用率趋势
4. 在检测日志页查看详细的探测记录

### 场景：用量分析和监控

1. 在使用日志页查看详细的调用记录（含延迟指标、缓存命中、流式状态）
2. 在统计分析页查看用量趋势、模型分布、缓存命中率
3. 在系统设置页调整超时、重试、熔断、日志保留等参数
4. 开启开发者调试功能，追踪代理请求的全链路详情

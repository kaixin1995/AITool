# AI Tool - 项目详细文档

## 项目简介

AI Tool 是一个 **AI API 网关 / 反向代理**，用于统一管理和转发多个 AI 服务站点的请求。它提供一个管理后台来管理站点、模型、路由规则、访问密钥，并通过 OpenAI/Anthropic 兼容协议对外提供代理服务，支持按优先级自动故障转移。

核心能力：
- 多站点管理（注册 OpenAI/Anthropic/Responses 兼容的 AI 服务站点，支持一个站点挂多把 Key 主备调度）
- 统一模型库（将不同站点的同名模型归一化管理，支持强制覆盖 reasoning_effort、绑定兼容规则集）
- 路由规则（为模型配置多站点优先级，失败自动重试下一个站点）
- 路由回退监控（从调用日志还原故障转移事件，展示哪些请求触发了路由跳转）
- 兼容规则集（按模型转发前的字段级变换：剔除 strip / 重命名 rename / 补默认值 default，区分透传与中转作用域）
- 熔断保护（站点连续失败达到阈值后临时屏蔽）
- 实时流式透传（OpenAI / Anthropic / Responses 三协议原生 SSE 流式转发）
- 跨协议桥接（OpenAI Chat Completions、Anthropic Messages、OpenAI Responses 三协议任意两两转换，流式与非流式都支持）
- 并发控制（按站点+模型维度限制最大并发，支持跳过或排队等待两种策略）
- 访问密钥（对外提供统一的 API Key 认证）
- 模型检测（定时/手动探测模型可用性）
- 健康监控（模型可用率时间线图表）
- 对话测试（内置 Chat 页面端到端测试代理链路）
- 使用日志（Token 级别的用量追踪，含重试次数、缓存命中、流式延迟等）
- 统计分析（用量趋势、模型分布、缓存命中率等可视化仪表盘）
- 开发者追踪（进程内环形缓冲区，调试代理请求全链路，含离线协议诊断）
- OpenAI Responses API 代理（支持 HTTP、WebSocket 和 Compact 三种模式）
- Codex 账号托管（OAuth/PKCE 登录、token 自动刷新、额度查询与缓存、冷却恢复、定期巡检、手动重置 credits）

---

## 技术栈

| 层级 | 技术 |
|------|------|
| 运行时 | .NET 8.0 (ASP.NET Core) |
| 数据库 | SQLite (SqlSugar，CodeFirst 建表 + 自动补列，WAL 模式) |
| ORM | SqlSugar（`SqlSugarScope` 单例 + `AppDbContext` Scoped 适配层，替代原 EF Core） |
| 任务调度 | Hangfire (内存存储) |
| 后端 API | REST API + JWT Bearer 认证（管理后台），AccessKey 认证（代理端点） |
| 前端 | Vue 3 + TypeScript + Vite + Naive UI + Tailwind CSS + Pinia + Vue Router + ECharts + Axios |
| 桌面壳 | `AITool.Desktop`（基于 Avalonia 的桌面封装，复用同一套后端 API） |
| 日志 | NLog（控制台 + Debug）+ 自定义 HTTP 异常日志过滤器 |
| API 文档 | Swagger / OpenAPI（可通过 `Swagger:Enabled` 关闭，Testing 环境自动关闭） |
| 部署 | 同进程托管（前端 build 产物输出到 wwwroot，由 ASP.NET Core 同时服务 API 与静态文件） |
| 测试 | xUnit + FluentAssertions + 隔离 SQLite 数据库 |

> **架构说明**：项目采用「Vue 3 SPA + REST API + JWT 认证」的前后端分离架构，ORM 使用 SqlSugar。管理后台由 `frontend/` 工程构建，构建产物输出到 `src/AITool.Web/wwwroot`，由 ASP.NET Core 同进程托管；非 `/api`、`/v1`、`/health`、`/hangfire` 的请求 fallback 到 `index.html`，交给 Vue Router（history 模式）处理。

---

## 项目结构

```
AI-Tool/
├── src/
│   ├── AITool.Domain/           # 领域实体（SqlSugar 特性标注的 POCO，sealed 类）
│   ├── AITool.Application/      # 应用层接口和 DTO（纯接口定义，不含实现）
│   ├── AITool.Infrastructure/   # 基础设施实现（SqlSugar、HttpClient、Hangfire、Codex）
│   ├── AITool.Web/              # Web 入口（Controllers + Program.cs + wwwroot 前端产物）
│   └── AITool.Desktop/          # Avalonia 桌面壳（复用同一套后端 API）
├── frontend/                    # Vue 3 前端工程（build 产物输出到 src/AITool.Web/wwwroot）
│   └── src/{api,views,layouts,router,stores,composables}
├── tests/
│   ├── AITool.ApplicationTests/ # 单元测试
│   └── AITool.IntegrationTests/ # 集成测试
├── tools/
│   └── ProtocolSyncCheck/       # 协议同步校验工具（离线比对协议转换一致性）
└── AITool.slnx                  # 解决方案文件
```

**依赖关系：** `Domain` ← `Application` ← `Infrastructure` ← `Web`，`frontend` 与 `AITool.Desktop` 各自独立构建

- `Domain`：领域实体，用 SqlSugar 特性（`SugarTable`/`SugarColumn`/`SugarIndex`）标注表与索引，所有类为 `sealed`
- `Application`：仅引用 `Domain`，定义接口和 DTO，不含任何实现
- `Infrastructure`：引用 `Application` 和 `Domain`，实现所有接口（含 Codex OAuth/额度/巡检）
- `Web`：引用所有项目，作为宿主入口（同时服务 API + 前端静态文件）
- `AITool.Desktop`：Avalonia 桌面客户端，通过 HTTP 调用同一套后端 API
- `frontend`：独立 npm 工程，`npm run build` 产物输出到 `Web/wwwroot`

---

## 分层架构

### AITool.Domain — 领域实体

使用 SqlSugar 特性标注的 POCO 类。所有实体使用 `Guid` 主键（`SystemRuntimeSettings` 除外，固定 `Id=1`），均为 `sealed` 类，没有基类或共享接口。实体之间**没有导航属性**，关系通过 ID 手动关联。

**实体总览（15 个表实体 + 1 个 JSON DTO，7 个命名空间）：**

| 命名空间 | 实体 | 说明 |
|----------|------|------|
| `AITool.Domain.Sites` | `Site` | AI 服务站点 |
| `AITool.Domain.Sites` | `SiteKey` | 站点访问密钥（一个站点多把 Key，主备调度） |
| `AITool.Domain.Models` | `ModelLibraryItem` | 统一模型库 |
| `AITool.Domain.Models` | `ModelHealthMonitor` | 模型健康监控配置 |
| `AITool.Domain.SiteCatalog` | `SiteModelMapping` | 站点-模型映射 |
| `AITool.Domain.Proxy` | `ProxyRouteEntry` | 代理路由入口（对外暴露的模型入口） |
| `AITool.Domain.Proxy` | `ProxyRouteRule` | 代理路由规则 |
| `AITool.Domain.Proxy` | `ProxyAccessKey` | 对外访问密钥 |
| `AITool.Domain.Proxy` | `ProxyUsageLog` | 使用日志 |
| `AITool.Domain.Proxy` | `CompatibilityProfile` | 兼容规则集（字段级变换规则，可被多个模型引用） |
| `AITool.Domain.Proxy` | `CompatibilityRule` | 兼容规则集里的一条规则（strip/rename/default） |
| `AITool.Domain.Operations` | `SystemRuntimeSettings` | 系统运行时配置（单例 Id=1） |
| `AITool.Domain.Codex` | `CodexAccount` | Codex OAuth 账号 |
| `AITool.Domain.Auth` | `RefreshTokenRecord` | JWT 刷新令牌记录 |
| `AITool.Domain.Detection` | `DetectionTask` | 定时检测任务 |
| `AITool.Domain.Detection` | `DetectionTaskExecution` | 检测任务执行记录 |

> 路由回退不是独立实体，`RouteFallbackApiController` 从 `ProxyUsageLog` 的多次尝试记录中还原回退事件，仅用于监控展示。

#### Site — AI 服务站点

```csharp
// 命名空间：AITool.Domain.Sites
sealed class Site
{
    Guid Id;                    // 主键
    string Name;                // 站点名称（必填，最长200）
    string BaseUrl;             // 站点根地址（必填，最长500）
    string EndpointPathMode;    // 接口路径模式，默认 "standard-root"（必填，最长50）
    string ApiKey;              // 站点访问密钥（保留兼容字段；自建站点实际密钥存放在 SiteKey 表）
    string ProtocolType;        // 协议类型，默认 "OpenAI"（必填，最长50）
    bool SupportsOpenAi;        // 是否支持 OpenAI Chat Completions 协议
    bool SupportsAnthropic;     // 是否支持 Anthropic Messages 协议
    bool SupportsResponses;     // 是否支持 OpenAI Responses 原生接口（支持则透传，否则转换）
    bool IsEnabled = true;      // 是否启用
    DateTimeOffset CreatedAt;   // 创建时间，默认 UtcNow
    string? ManagedSource;      // 托管来源；null=自建，"Codex"=Codex 账号自动创建的隐藏站点
    string? ExtraHeadersJson;   // 自定义转发头 JSON（Codex 隐藏站点存储 Originator 等特殊头）
}
```

`ProtocolType` 区分 `"OpenAI"`、`"Anthropic"`、`"Responses"` 协议，影响请求转发时的目标路径和认证方式。`SupportsOpenAi` / `SupportsAnthropic` / `SupportsResponses` 标记该站点实际支持的协议类型，用于跨协议桥接时的兼容性判断：客户端协议与站点协议一致时直接透传，不一致时通过 `ProxyProtocolBridge` 做兼容转换。

`EndpointPathMode` 控制接口路径拼接方式：
- `"standard-root"`：站点根地址不含版本路径，系统自动拼接 `/v1/` 前缀
- `"versioned-base"`：站点根地址已包含版本路径，系统直接追加接口路径

`ManagedSource = "Codex"` 的隐藏站点不出现在站点管理页面，由 `CodexAccount` 关联创建并以 Responses 协议接入转发链路。自建站点支持多把 Key：实际密钥存放在 `SiteKey` 表，缓存层把"路由 × 多个 Key"展开成多条候选路由，实现主备 Key 调度与各自独立的并发计数；老站点首次启动时会把 `Site.ApiKey` 迁移成一条默认 `SiteKey`。

#### ModelLibraryItem — 统一模型库

```csharp
// 命名空间：AITool.Domain.Models
sealed class ModelLibraryItem
{
    Guid Id;                        // 主键
    string ModelName;               // 统一模型名称（唯一索引，必填，最长200）
    string DisplayName;             // 页面显示名称（最长200）
    string ModelType = "chat";      // 模型类型（固定 chat，兼容旧字段）
    string OverrideReasoningEffort; // 强制覆盖的思考等级，留空=透传客户端值
    Guid? CompatibilityProfileId;   // 绑定的兼容规则集 Id，null=不应用规则
    bool IsEnabled = true;          // 是否启用
    DateTimeOffset CreatedAt;       // 创建时间，默认 UtcNow
}
```

`ModelName` 有唯一索引，不同站点的同名模型归一到同一条 `ModelLibraryItem` 记录。导入模型时通过字典查找避免重复创建。

`OverrideReasoningEffort` 非空时，无论客户端传什么思考等级，转发给上游时都强制覆盖成该值；留空则透传客户端原始值，支持 `low`/`medium`/`high`/`xhigh`/`max` 及自定义值。

`CompatibilityProfileId` 指向 `CompatibilityProfile`，转发上游前按该规则集对请求体做字段级变换。规则集独立维护，可被多个模型引用，避免在每台模型上重复配置相同规则。

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
    int MaxConcurrency;             // 最大并发数，0 表示不限制
}
// 复合唯一索引：(SiteId, RemoteModelName)
```

`RemoteModelName` 是该模型在远程站点上的实际名称，可能与统一模型名不同。例如统一模型名 `gpt-4o`，某站点上可能叫 `gpt-4o-2024-08-06`。

`MaxConcurrency` 控制该站点上此模型的最大并发请求数。当多个路由入口指向同一站点的同一模型时，并发总数不会超过此值。0 表示不限制。

#### SiteKey — 站点访问密钥

```csharp
// 命名空间：AITool.Domain.Sites
sealed class SiteKey
{
    Guid Id;                        // 主键
    Guid SiteId;                    // 所属站点ID（FK → Site，索引）
    string KeyValue;                // 实际密钥值（必填，最长500）
    string? Remark;                 // 备注，如"主号""备用号""测试号"
    int Priority;                  // 优先级，数字越小越优先被选中
    bool IsEnabled = true;          // 是否启用
    DateTimeOffset CreatedAt;       // 创建时间，默认 UtcNow
}
```

允许一个站点配置多把 Key，分别控制启用状态、优先级和备注。转发链路在缓存层把"路由 × 多个 Key"展开成多条候选路由，复用现有的优先级排序、故障熔断和并发占满跳下一个机制，实现主备 Key + 各自独立并发计数。老站点首次启动时会把 `Site.ApiKey` 迁移成一条 Priority=0 的默认 `SiteKey`；Codex 托管站点不迁移，仍直接使用 `Site.ApiKey`。

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

#### CompatibilityProfile — 兼容规则集

```csharp
// 命名空间：AITool.Domain.Proxy
sealed class CompatibilityProfile
{
    Guid Id;                        // 主键
    string Name;                    // 规则集名称，如「GPT-5 兼容」「z.ai 兼容」（最长100）
    string Description;             // 适用场景说明（最长500）
    string RulesJson = "[]";        // 规则数组 JSON（CompatibilityRule 列表）
    bool IsEnabled = true;          // 是否启用
    DateTimeOffset CreatedAt;       // 创建时间，默认 UtcNow
    DateTimeOffset UpdatedAt;       // 最后更新时间，默认 UtcNow
}
```

描述转发某上游前对请求体做的字段级变换，可被多个 `ModelLibraryItem` 通过 `CompatibilityProfileId` 引用，避免在每台模型上重复配相同的兼容规则。规则集合在启动期加载到内存缓存，转发时按当前路径（透传 / 兼容中转）筛选规则应用。任何写操作后都会失效 `ProxyRequestMetadataCache` 的兼容规则缓存。

#### CompatibilityRule — 兼容规则

```csharp
// 命名空间：AITool.Domain.Proxy
sealed class CompatibilityRule
{
    string Op = "strip";            // 操作类型：strip / rename / default
    string Target;                 // strip 的目标字段路径（裸字段名自动当作 messages[].字段名）
    string From;                   // rename 的原字段名（仅顶层）
    string To;                     // rename 的新字段名
    string Key;                    // default 的字段名（仅顶层）
    string Value;                  // default 的字段值（按 true/false/数字/字符串推断类型）
    string Scope = "all";          // 生效路径：passthrough（仅透传）/ bridge（仅中转）/ all（两者）
}
```

三种操作：
- `strip`：剔除目标字段。`target` 沿用路径语法：顶层字段直接写名字（如 `metadata`）；裸字段名自动当作 `messages[].字段名`（如 `reasoning_content`）；也可写精确路径 `a.b` 或 `a[].b`
- `rename`：重命名顶层字段（`from` → `to`）
- `default`：为缺失的顶层字段补默认值（`key` = `value`）

`scope` 控制规则在哪种转发路径下生效：`passthrough` 仅在协议透传时应用，`bridge` 仅在跨协议兼容中转时应用，`all`（默认）两者都应用。

#### ProxyUsageLog — 使用日志

```csharp
// 命名空间：AITool.Domain.Proxy
sealed class ProxyUsageLog
{
    Guid Id;                        // 主键
    Guid RequestId;                 // 请求唯一标识（用于关联同一次请求的多条日志）
    Guid AccessKeyId;               // 访问密钥ID（FK → ProxyAccessKey）
    string ProtocolType;            // 协议类型 "OpenAI" 或 "Anthropic"（必填，最长50）
    string? ForwardingMode;         // 调用模式（直接透传 / 兼容中转）
    string RequestModel;            // 请求的模型名（必填，最长200）
    string AttemptedModel;          // 实际尝试的上游模型名（必填，最长200）
    Guid TargetSiteId;              // 命中的目标站点ID
    string Status;                  // 状态 "success" 或 "fail"（必填，最长50）
    string Source = "proxy";        // 来源 "proxy"、"chat" 或 "detection-task"
    int RetryCount;                 // 尝试了几个路由（重试次数）
    int AttemptIndex;               // 当前尝试的序号（从 0 开始）
    bool IsFinalResult;             // 是否为最终结果（成功或最后一次失败）
    bool FallbackTriggered;         // 是否触发了故障转移
    string ErrorMessage;            // 失败时的错误信息（必填，默认空字符串）
    int InputTokens;                // 输入 Token 数
    int CachedTokens;               // 缓存命中的 Token 数（Prompt Cache）
    int OutputTokens;               // 输出 Token 数
    int TotalTokens;                // 总 Token 数（= InputTokens + CachedTokens + OutputTokens）
    bool IsStreaming;               // 是否为流式请求
    bool IsStreamInterrupted;       // 流式请求是否被中断
    int FirstTokenLatencyMs;        // 首 Token 延迟（毫秒）
    int StreamDurationMs;           // 流式传输总时长（毫秒）
    int TotalDurationMs;            // 请求总耗时（毫秒）
    string ReasoningEffort;         // 推理力度参数（如 "low"、"high"，必填，默认空字符串）
    DateTimeOffset RequestedAt;     // 请求时间，默认 UtcNow
}
// 索引：RequestedAt（用于日志按时间查询和清理）
```

代理请求会为每次尝试记录一条日志（`AttemptIndex` 标识序号），最终成功或全部失败的那条标记 `IsFinalResult = true`。`FallbackTriggered` 标记是否因前次失败而触发了备用路由。

`Source` 字段区分来源：`"proxy"` 代理请求、`"chat"` 对话测试、`"detection-task"` 模型检测任务。

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
    int ConcurrencyMode;                // 并发打满策略：0=跳到下一顺位，1=排队等待
    int ConcurrencyQueueTimeoutSeconds; // 排队等待超时（秒），默认 120
    DateTimeOffset? LastUsageLogPrunedAt;  // 上次清理时间
    int LastUsageLogPrunedCount;           // 上次清理数量
    bool CodexFeaturesEnabled;               // Codex 功能总开关（OAuth 账号/凭证/巡检）
    bool CodexInspectionEnabled;             // Codex 巡检自动执行开关
    int CodexInspectionIntervalSeconds;      // Codex 巡检周期（秒），下限 30
    int CodexQuotaMaxCacheHours;              // 额度缓存最大小时数（超时强制真实刷新）
    int CodexAutoDisableThresholdPercent;    // 自动禁用阈值（百分比，1-100）
    bool CodexInspectionCacheEnabled;        // 巡检缓存复用开关（减少上游请求）
}
```

系统运行时配置通过管理后台 `/system/settings` 页面维护，修改后即时生效（通过缓存失效机制）。

`ConcurrencyMode` 控制当站点+模型的并发打满时的行为：
- `0 (SkipOnFull)`：跳过当前站点，尝试下一个优先级的路由
- `1 (WaitForSlot)`：排队等待直到有位置释放或超时（超时后顺延到下一个路由）

`CodexFeaturesEnabled` 为总开关，关闭后隐藏 Codex 页面入口，并把所有 Codex 托管站点置为禁用（路由/模型/对话测试不再命中）；重新开启时仅恢复因总开关关闭而禁用的账号，不会误启用冷却中或手动禁用的账号。`CodexInspectionCacheEnabled` 开启后，未被使用且窗口未过期且未超过 `CodexQuotaMaxCacheHours` 的账号沿用上次额度快照，减少上游请求。

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

#### CodexAccount — Codex OAuth 账号

```csharp
// 命名空间：AITool.Domain.Codex
sealed class CodexAccount
{
    Guid Id;                        // 主键
    string DisplayName;             // 用户自定义名称（最长200）
    string? Email;                  // 从 id_token 解析的邮箱
    string? AccountId;              // chatgpt_account_id（去重首选依据）
    string? PlanType;               // 订阅计划：free / plus / team / pro
    string? AccessToken;            // 当前 access_token（同步写回隐藏 Site.ApiKey）
    string? RefreshToken;           // OAuth refresh_token
    string? IdToken;                // JWT id_token（含订阅窗口等）
    DateTimeOffset? TokenExpiresAt; // access_token 过期时间
    DateTimeOffset? LastRefreshAt;   // 最近一次成功刷新时间
    Guid LinkedSiteId;              // 关联的隐藏 Site 标识（逻辑外键）
    bool IsEnabled = true;          // 是否启用
    bool DisabledByFeatureToggle;    // 因总开关关闭被禁用（恢复时仅恢复此类账号）
    bool ManuallyDisabled;           // 用户手动禁用（巡检恢复时跳过）
    decimal? AutoDisableThreshold;   // 剩余额度自动禁用阈值
    bool IsQuotaCooling;             // 是否处于被动冷却
    DateTimeOffset? QuotaCoolingUntil; // 冷却恢复时间
    string? LastQuotaRawJson;        // 最近一次额度查询原始响应
    DateTimeOffset? LastQuotaCheckedAt; // 最近一次主动额度查询时间
    DateTimeOffset CreatedAt;        // 创建时间，默认 UtcNow
}
```

每个 Codex 账号在创建时自动关联一个隐藏的 `Site`（`LinkedSiteId`），该隐藏 Site 以 Responses 协议接入转发链路，复用现有的 Models / Routes / Chat 机制。OAuth token 会同步写回隐藏 Site 的 `ApiKey`，由 `CodexTokenRefreshService` 后台自动刷新。账号有多种禁用状态相互区分：总开关禁用、手动禁用、额度自动禁用、被动冷却，恢复逻辑各自独立，避免误启用。

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
  │              │
  │              │ (检测探针通过 ProxyUsageLog 记录)
  │              ↓
  │          ProxyUsageLog (Source="detection-task")
  │
  └──1:N──> SiteKey (多把 Key，主备调度)

ModelLibraryItem ──1:1──> ModelHealthMonitor (唯一索引)
ModelLibraryItem ──N:1──> CompatibilityProfile (CompatibilityProfileId，可空)

ProxyRouteEntry ──1:N──> ProxyRouteRule ──N:1──> Site (SiteId)
  (EntryName = ExternalModelName)

ProxyAccessKey ──1:N──> ProxyUsageLog <──N:1── Site
                            (AccessKeyId, TargetSiteId)

CodexAccount ──1:1──> Site (LinkedSiteId，隐藏站点，Responses 协议)

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
| `ProxyForwardConstants.cs` | 代理转发常量（协议名、默认路径等） |
| `ProxyProtocolResolver.cs` | 协议解析（透传/中转判定、目标协议选择） |
| **UsageLogs/** | |
| `IUsageLogService.cs` | 使用日志接口 + `UsageLogEntry` DTO |
| `UsageLogErrorClassifier.cs` | 使用日志错误分类器 |
| `PercentileCalculator.cs` | 用量百分位计算 |
| **SiteCatalog/** | |
| `ISiteCatalogClient.cs` | 站点模型目录拉取接口 |
| **Operations/** | |
| `ISystemRuntimeSettingsService.cs` | 系统运行时配置接口 + `UpdateSystemRuntimeSettingsRequest`/`ClearUsageLogsRequest` DTO |
| **Common/** | |
| `ILogRetentionService.cs` | 日志清理接口 + `LogPruneResult` DTO |
| `JsonSerializerPresets.cs` | JsonSerializer 预设 |
| **Sites/** | |
| `CreateSiteCommand.cs` | 创建站点 DTO |
| `SiteEndpointPathResolver.cs` | 站点接口路径解析工具（静态类） |
| **Models/** | |
| `CreateModelLibraryItemCommand.cs` | 创建模型 DTO |
| **Codex/** | |
| `ICodexOAuthClient.cs` | Codex OAuth/PKCE 授权、token 交换与刷新接口 |
| `ICodexModelCatalog.cs` | Codex 静态模型目录（进程内只读） |
| `ICodexModelFetcher.cs` | Codex 动态模型拉取接口（chatgpt.com/backend-api/codex/models） |
| `ICodexQuotaService.cs` | Codex 额度主动查询接口（结果缓存 + single-flight） |
| `ICodexQuotaCooldownService.cs` | Codex 额度被动冷却与重置接口 |
| `ICodexResetCreditsService.cs` | Codex 手动重置 credits 接口（查询剩余 + 消耗一张执行重置） |
| `CodexTokenSet.cs` / `CodexQuotaInfo.cs` / `CodexResetCreditsInfo.cs` 等 | Codex 相关 DTO |

> 注：路由选择不再由独立的 `IRouteSelectionService` 实现，而是合并到 `ProxyRequestMetadataCache.GetRouteTargetsForModelAsync()`，返回 `CachedProxyRouteTarget` 列表（含多 Key 展开）。模型探测由 `ModelHealthRequestService` 直接实现。检测任务调度由 `HangfireDetectionScheduler` 实现。

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
    string EndpointPathMode;      // 接口路径模式 ("standard-root" / "versioned-base")
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
    string ProtocolType;          // "OpenAI"、"Anthropic" 或 "Responses"
    string ForwardingMode;        // 调用模式（透传 / 兼容中转）
    string RequestModel;          // 请求的模型名
    string AttemptedModel;        // 实际尝试的上游模型名
    Guid TargetSiteId;            // 目标站点ID
    string Status;                // "success" 或 "fail"
    string Source = "proxy";      // "proxy"、"chat" 或 "detection-task"
    int RetryCount;               // 尝试的路由数量
    int AttemptIndex;             // 当前尝试序号
    bool IsFinalResult;           // 是否为最终结果
    bool FallbackTriggered;       // 是否触发了故障转移
    string ErrorMessage;          // 错误信息
    int InputTokens;              // 输入 Token 数
    int CachedTokens;             // 缓存命中 Token 数
    int OutputTokens;             // 输出 Token 数
    bool IsStreaming;              // 是否为流式请求
    bool IsStreamInterrupted;     // 流式是否被中断
    int FirstTokenLatencyMs;      // 首 Token 延迟
    int StreamDurationMs;         // 流式传输时长
    int TotalDurationMs;          // 请求总耗时
    string ReasoningEffort;       // 推理力度参数
}
```

**ISiteCatalogClient** — 站点模型目录拉取

```csharp
interface ISiteCatalogClient
{
    Task<IReadOnlyList<string>> GetModelsAsync(Site site, CancellationToken ct);
}
```

**ISystemRuntimeSettingsService** — 系统运行时配置

```csharp
interface ISystemRuntimeSettingsService
{
    Task<SystemRuntimeSettings> GetOrCreateAsync(CancellationToken ct = default);
    Task UpdateAsync(SystemRuntimeSettings settings, CancellationToken ct = default);
    Task<int> ClearUsageLogsAsync(ClearUsageLogsRequest request, CancellationToken ct = default);
}
```

**SiteEndpointPathResolver** — 站点接口路径解析

```csharp
static class SiteEndpointPathResolver
{
    // 规范化路径模式，无效值回退为 "standard-root"
    static string NormalizeMode(string mode);
    // 解析接口路径，根据 EndpointPathMode 决定是否添加 /v1/ 前缀
    static string ResolvePath(string baseUrl, string endpoint, string mode);
    // 构建完整 URL
    static string BuildUrl(string baseUrl, string endpoint, string mode);
}
```

---

### AITool.Infrastructure — 基础设施层

所有接口的实现，包含数据库访问、HTTP 请求、调度、Codex OAuth/额度/巡检等。

#### 数据库 — AppDbContext（SqlSugar 适配层）

```csharp
sealed class AppDbContext : IDisposable, IAsyncDisposable  // Scoped
{
    ISqlSugarClient Client { get; }              // 底层 SqlSugarScope 单例
    ISugarQueryable<Site> Sites { get; }
    ISugarQueryable<SiteKey> SiteKeys { get; }
    ISugarQueryable<CodexAccount> CodexAccounts { get; }
    ISugarQueryable<ModelLibraryItem> ModelLibraryItems { get; }
    ISugarQueryable<SiteModelMapping> SiteModelMappings { get; }
    ISugarQueryable<DetectionTask> DetectionTasks { get; }
    ISugarQueryable<DetectionTaskExecution> DetectionTaskExecutions { get; }
    ISugarQueryable<ProxyRouteEntry> ProxyRouteEntries { get; }
    ISugarQueryable<ProxyRouteRule> ProxyRouteRules { get; }
    ISugarQueryable<ProxyAccessKey> ProxyAccessKeys { get; }
    ISugarQueryable<ProxyUsageLog> ProxyUsageLogs { get; }
    ISugarQueryable<ModelHealthMonitor> ModelHealthMonitors { get; }
    ISugarQueryable<SystemRuntimeSettings> SystemRuntimeSettings { get; }
    ISugarQueryable<CompatibilityProfile> CompatibilityProfiles { get; }

    // 增删改便捷方法（替代 EF 的 Add/Remove + SaveChanges，SqlSugar 立即执行）
    Task<int> InsertAsync<T>(T entity, ...);
    Task<int> UpdateAsync<T>(T entity, ...);
    Task<int> DeleteAsync<T>(T entity, ...);
    // 后台服务串行化锁（仅供巡检/批量写/冷却恢复使用）
    Task<T> SerialExecuteAsync<T>(Func<Task<T>> action, ...);
}
```

`AppDbContext` 是基于 SqlSugar 的数据访问入口，内部持有线程安全的 `SqlSugarScope` 单例，对外暴露与原 `DbSet` 同名的 `ISugarQueryable<T>` 访问器，业务代码属性名保持不变，底层换成 SqlSugar 的查询/插入/删除能力。写操作立即执行，无需单独 `SaveChanges`。

`SerialExecuteAsync` 提供后台 DB 串行化锁：仅供后台服务（巡检/批量写/冷却恢复）使用，避免与代理热路径的批量写踩 `SqlSugarScope` 竞态；Web 请求路径不调用此锁，依赖 `SqlSugarScope` 自身线程安全 + SQLite WAL + `busy_timeout` 处理写冲突。

SqlSugar 配置要点：
- 所有实体使用 `Guid` 主键（`SystemRuntimeSettings` 固定 `Id=1`）
- `ModelLibraryItem.ModelName` — 唯一索引
- `SiteModelMapping` — `(SiteId, RemoteModelName)` 复合唯一索引
- `ModelHealthMonitor.ModelLibraryItemId` — 唯一索引
- `ProxyRouteEntry.EntryName` — 唯一索引
- `ProxyRouteRule.ExternalModelName` — 普通索引
- `DetectionTaskExecution.StartedAt` — 索引
- `ProxyUsageLog.RequestedAt` — 索引
- `ProxyAccessKey` — `(AccessKeyHash, IsEnabled)` 复合索引
- `SiteKey` — `SiteId` 索引
- `CodexAccount` — `LinkedSiteId`、`TokenExpiresAt` 索引
- 字符串字段均有 `Length` 约束（`SugarColumn` 特性）
- **无导航属性**，关系通过 ID 手动 Join 或字典查找
- DateTimeOffset 通过 SqlSugar AOP 在写入前转本地时区存储，保证往返瞬时正确

#### ProxyForwardService — 代理转发服务

```csharp
sealed class ProxyForwardService : IProxyForwardService  // HttpClient Typed 实例
```

核心流程：
1. 根据 `ProtocolType` 和 `EndpointPathMode` 通过 `SiteEndpointPathResolver` 拼接目标 URL（支持 `TargetPath` 覆盖）：
   - OpenAI Chat: `{BaseUrl}/v1/chat/completions`
   - Anthropic: `{BaseUrl}/v1/messages`
   - Responses: `{BaseUrl}/v1/responses`（Codex 等上游）
2. 使用 `PreparedRequestBody`（跨协议桥接时已转换）或替换请求体中的 `model` 字段
3. 设置认证头和额外转发头（`ForwardHeaders`）：
   - OpenAI: `Authorization: Bearer {ApiKey}`
   - Anthropic: `x-api-key: {ApiKey}` + `anthropic-version: 2023-06-01`
4. **非流式**：发送 HTTP POST 请求，读取响应，提取 Token 用量
5. **流式**（`ForwardStreamingAsync`）：逐行读取 SSE 事件流，通过回调 `onSseDataAsync` 实时传递给控制器，同时追踪首 Token 延迟和流式传输时长
6. 调用 `ExtractUsageFromElement()` 从响应 JSON 提取 Token 用量，支持三种格式：
   - OpenAI Chat Completions: `usage.prompt_tokens` + `usage.completion_tokens` + `prompt_tokens_details.cached_tokens`
   - OpenAI Responses API: `usage.input_tokens` + `usage.output_tokens` + `input_tokens_details.cached_tokens`
   - Anthropic: `usage.input_tokens` + `usage.output_tokens` + `cache_read_input_tokens` + `cache_creation_input_tokens`
7. 返回 `ProxyForwardResult`（含状态码、响应体、Token 用量、流式指标）
8. 流式中断检测：流式请求开始但未收到 `[DONE]`（OpenAI）或 `message_stop`（Anthropic）时标记为中断

#### 路由选择（合并到 ProxyRequestMetadataCache）

路由选择不再由独立服务实现，而是合并到 `ProxyRequestMetadataCache`：
- `GetRouteTargetsForModelAsync()`: 按模型名查询候选路由，把"路由 × 多个 Key"展开成 `CachedProxyRouteTarget` 列表，按 `ModelPriority`、`InstancePriority`、`Priority` 升序排列，支持排除被熔断的站点（熔断场景）。结果缓存约 5 秒。

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
- `_failCounts: ConcurrentDictionary<Guid, int>` — 每个路由的连续失败次数
- `_blockedSites: ConcurrentDictionary<Guid, DateTimeOffset>` — 被熔断的路由及其解除时间
- `Block()`: 递增失败计数，达阈值时记录解除时间
- `Succeed()`: 清除该路由的失败计数
- `IsBlocked()`: 检查是否在屏蔽窗口内，已超时则自动清除
- 熔断参数（阈值、恢复时间）可通过 `SystemRuntimeSettings` 动态配置，线程安全

#### ProxyUsageLogBatchWriter — 使用日志批量写入器

```csharp
sealed class ProxyUsageLogBatchWriter : BackgroundService  // Singleton
```

- 使用 `Channel<UsageLogEntry>` 有界通道（容量 4096，`DropWrite` 溢出策略）接收日志
- 批量聚合：每 800ms 或累积 100 条触发一次批量写入
- 写入时计算 `TotalTokens = InputTokens + CachedTokens + OutputTokens`
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
- `UpdateAsync()`: 更新配置（含边界校验，所有数值字段有下限保护）
- `ClearUsageLogsAsync()`: 按来源和时间范围清空使用日志

#### OpenAiSiteCatalogClient — 站点模型目录拉取

```csharp
sealed class OpenAiSiteCatalogClient : ISiteCatalogClient  // HttpClient Typed 实例
```

实现：向站点发送 `GET /v1/models`（根据 `EndpointPathMode` 构建完整 URL），Header 带 `Authorization: Bearer {ApiKey}`，解析 OpenAI 格式的 `{ data: [{ id: "model-name" }] }` 响应，返回模型名称列表。

#### ModelHealthRequestService — 模型健康请求服务

```csharp
sealed class ModelHealthRequestService  // Scoped
```

发送真实对话请求（随机数学题目）到上游站点，验证模型可用性。结果记录到 `SiteModelMapping.LastStatus` 和 `ProxyUsageLog`（Source="detection-task"）。同时承担模型探测职责（向站点发送最小化请求测量响应耗时）。

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
  3. 按 `DetectionConcurrency` 分批并发调用 `ModelHealthRequestService.ProbeMappingAsync()` 探测
  4. 更新映射的 `LastStatus`
  5. 更新执行记录状态为 "completed"，记录摘要

#### Codex 服务群 — OAuth 账号托管

Codex 能力由多个服务协作完成：

| 服务 | 生命周期 | 职责 |
|------|----------|------|
| `CodexOAuthClient` | HttpClient Typed | PKCE 授权、token 交换与刷新（实现 `ICodexOAuthClient`） |
| `CodexModelCatalog` | Singleton | 进程内只读的 Codex 静态模型目录（实现 `ICodexModelCatalog`） |
| `CodexModelFetcher` | HttpClient Typed | 动态拉取 `chatgpt.com/backend-api/codex/models`（实现 `ICodexModelFetcher`） |
| `CodexQuotaService` | HttpClient Typed | 额度主动查询，30s 结果缓存防抖 + single-flight（实现 `ICodexQuotaService`） |
| `CodexQuotaCooldownService` | Scoped | 命中上游 `usage_limit_reached` 时标记被动冷却，到期恢复（实现 `ICodexQuotaCooldownService`） |
| `CodexResetCreditsService` | HttpClient Typed | 查询剩余重置 credits + 消耗一张执行真实重置（实现 `ICodexResetCreditsService`） |
| `CodexAccountProvisioner` | Scoped | OAuth 登录/导入凭证后创建隐藏 `Site` + `CodexAccount` |
| `CodexCredentialRefreshService` | Scoped | 代理命中 Codex 上游 401 时即时刷新凭证并同步隐藏站点 |
| `CodexTokenRefreshService` | HostedService (Singleton) | 周期刷新 Codex 账号 OAuth token，写回隐藏 `Site.ApiKey` 并失效路由缓存 |
| `CodexCooldownRecoveryService` | HostedService (Singleton) | 周期恢复冷却到期的 Codex 账号（清除冷却，恢复 Site，若未被手动禁用） |
| `CodexInspectionService` | Singleton (HostedService) | 周期额度巡检 + 缓存策略 + 自动禁用，API 与后台共用状态 |
| `SiteUsageTracker` | Singleton | 站点使用时间内存映射，巡检判断账号是否被使用，避免回查 DB |

Codex 账号以 Responses 协议接入转发链路：每个 `CodexAccount` 关联一个隐藏 `Site`，复用现有的 Models / Routes / Chat / 故障转移 / 并发控制机制。功能总开关 `CodexFeaturesEnabled` 控制可见性与启用；巡检开关 `CodexInspectionEnabled` 控制定期额度巡检；`CodexFeatureToggleAttribute` / `CodexInspectionToggleAttribute` 在控制器层做 gating，关闭时返回 404。

---

## 核心业务流程

### 1. 代理请求流程（含故障转移、并发控制和跨协议桥接）

```
客户端请求 → POST /v1/chat/completions (或 /v1/messages、/v1/responses 等)
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
  ├─ 并发控制（ModelConcurrencyLimiter）
  │   ├─ SkipOnFull 模式：并发已满 → 跳到下一个路由
  │   └─ WaitForSlot 模式：排队等待直到释放或超时
  ├─ 判断协议兼容性（ResolveProtocolForClient）
  │   ├─ 站点支持客户端协议 → 直接透传
  │   └─ 不支持 → 通过 ProxyProtocolBridge 进行跨协议兼容转换
  │       ├─ OpenAI Chat ↔ Anthropic Messages
  │       ├─ OpenAI Chat ↔ Responses
  │       └─ Anthropic Messages ↔ Responses
  ├─ 应用兼容规则集（若模型绑定了 CompatibilityProfile）
  │   └─ 按当前路径（透传/中转）筛选 strip/rename/default 规则变换请求体
  ├─ 判断是否流式请求：
  │   ├─ 非流式 → ForwardAsync()，响应体协议转换后返回
  │   └─ 流式 → ForwardStreamingAsync()，实时 SSE 事件协议转换
  │       ├─ 三协议 SSE 任意两两转换
  │       └─ 追踪首 Token 延迟、流式传输时长
  ├─ 成功 → 记录日志(含 AttemptIndex、延迟指标) → circuitStore.Succeed() → 返回响应
  └─ 失败 → circuitStore.Block() → 释放并发槽位 → 尝试下一个路由
  ↓
全部失败 → 记录失败日志(Status="fail", IsFinalResult=true) → 返回 502 错误
```

> 路由回退监控（`/api/admin/route-fallback`）从同一次请求的多次尝试日志（`ProxyUsageLog`，按 `RequestId` 聚合）还原故障转移事件，用于展示哪些请求触发了路由跳转，而非独立的回退规则。

**OpenAI 和 Anthropic 控制器的区别：**
- 认证方式不同（Bearer Token vs x-api-key）
- 转发时 `ProtocolType` 不同（影响目标 URL 和认证头设置）
- Anthropic 控制器转发 `anthropic-version`、`anthropic-beta` 等协议头
- 流式 SSE 格式不同（OpenAI `data: {...}` vs Anthropic `event: xxx\ndata: {...}`）
- 控制器内部逻辑结构一致，通过 `ProxyProtocolBridge` 统一处理协议差异

**OpenAI 代理控制器支持的端点：**
- `/v1/chat/completions` — Chat Completions（主要代理端点）
- `/v1/completions` — Legacy Completions 代理
- `/v1/embeddings` — Embeddings 代理（仅 OpenAI 上游）
- `/v1/models` — 模型列表（自动检测 OpenAI/Anthropic 格式）
- `/v1/models/{modelId}` — 模型详情
- `/v1/responses` — Responses API（支持 HTTP 和 WebSocket 两种模式）
- `/v1/responses/compact` — Responses Compact 代理

**跨协议桥接（ProxyProtocolBridge）：**
- `PrepareRequestBody()`: 根据客户端和目标协议转换请求体（模型名、消息格式、参数映射）
- `AdaptResponseBodyForClient()`: 将非流式响应从目标协议转换回客户端协议格式
- 流式 SSE 转换：`ConvertOpenAiStreamChunkToAnthropic()` / `ConvertAnthropicStreamChunkToOpenAi()` / `ConvertChatStreamChunkToResponses()` / `ConvertAnthropicStreamChunkToResponses()` / `ConvertResponsesStreamingToChat()`，覆盖三协议任意两两转换
- 站点需标记 `SupportsOpenAi` / `SupportsAnthropic` / `SupportsResponses` 来声明协议兼容性
- 透传优先：站点支持客户端协议时直接透传，不支持时才做兼容转换（保留老逻辑）
- 目标为 Responses 时强制 `store=false`；Codex 目标会清理上游不支持的字段
- 防御性处理：`content:null`、`usage:null`、空 choices、空 output、role-only / usage-only 分片不会构造空响应；工具调用索引/ID/arguments 跨 SSE 分片稳定累积；完成事件去重；转换失败时不混入原协议 SSE

**熔断机制：**
- 每次 `Block()` 调用递增连续失败计数
- 连续失败达阈值（默认 5 次，可通过系统设置配置）后触发熔断，屏蔽可配置时间（默认 2 分钟）
- 成功一次即清除连续失败计数（`Succeed()`）
- 熔断状态存储在内存中（Singleton），重启后丢失
- 被熔断的路由在代理请求循环中被跳过，不消耗请求

**并发控制机制：**
- `ModelConcurrencyLimiter` 按站点+模型维度维护信号量
- `SiteModelMapping.MaxConcurrency` 控制最大并发数（0 = 不限制）
- 两种策略通过 `SystemRuntimeSettings.ConcurrencyMode` 配置：
  - `SkipOnFull (0)`：并发已满时跳过当前站点，尝试下一个优先级路由
  - `WaitForSlot (1)`：排队等待直到有槽位释放或超时（超时后顺延到下一个路由）

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
  ├─ 发送随机数学题（如 "42 + 17 = ?"）
  ├─ 测量响应耗时
  └─ 更新 SiteModelMapping.LastStatus，记录 ProxyUsageLog (Source="detection-task")
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
  ├─ 过滤出被熔断的站点（内存中调用 IsBlocked，避免 SqlSugar 翻译错误）
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
| POST | `/v1/chat/completions` | `Authorization: Bearer {key}` | OpenAI Chat Completions 代理（支持流式和非流式） |
| POST | `/v1/completions` | `Authorization: Bearer {key}` | OpenAI Legacy Completions 代理 |
| POST | `/v1/embeddings` | `Authorization: Bearer {key}` | OpenAI Embeddings 代理（仅 OpenAI 上游） |
| POST | `/v1/responses` | `Authorization: Bearer {key}` | OpenAI Responses API 代理（HTTP + WebSocket） |
| POST | `/v1/responses/compact` | `Authorization: Bearer {key}` | Responses Compact 代理 |
| GET | `/v1/responses` | `Authorization: Bearer {key}` | Responses WebSocket 代理 |
| GET | `/v1/models` | `Authorization: Bearer {key}` | OpenAI/Anthropic 兼容模型列表 |
| GET | `/v1/models/{modelId}` | `Authorization: Bearer {key}` | 模型详情 |
| POST | `/v1/messages` | `x-api-key: {key}` | Anthropic Messages 代理（支持流式和非流式） |
| POST | `/v1/messages/count_tokens` | `x-api-key: {key}` | Anthropic Token 计数估算 |
| GET | `/health` | 无 | 健康检查 |

### 管理 API（面向管理后台）

#### 访问密钥 `api/admin/access-keys`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/` | 获取密钥列表 |
| GET | `/{keyId}/plain` | 读取密钥明文（仅创建时存储，支持复制完整密钥） |
| POST | `/create` | 创建密钥（生成 `sk-` + 32位十六进制字符串，SHA256 哈希存储） |
| POST | `/toggle/{keyId}` | 切换启用/禁用 |
| POST | `/delete/{keyId}` | 删除密钥 |
| POST | `/update-routes/{keyId}` | 更新密钥绑定的路由入口集合 |

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
| PUT | `/mappings/{mappingId}/concurrency` | 更新站点模型映射的最大并发数 |

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
| GET | `/targets` | 获取所有站点模型目标 |
| GET | `/models/{modelId}/targets` | 获取指定模型的站点目标列表 |
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

#### 仪表盘 `api/admin/dashboard`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/stats` | 首页统计卡片（站点数、模型数、映射数、路由数、密钥数、检测任务数） |

#### 兼容规则集 `api/admin/compatibility-profiles`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/` | 列出所有规则集（按名称排序，含规则数摘要） |
| GET | `/{id}` | 取单条规则集详情（含 RulesJson 原文） |
| POST | `/` | 新建规则集 |
| PUT | `/{id}` | 更新规则集 |
| POST | `/{id}/toggle` | 切换启用/禁用 |
| DELETE | `/{id}` | 删除规则集（引用它的模型自动变为不应用规则） |

#### 路由回退 `api/admin/route-fallback`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/list` | 获取路由回退规则列表 |
| GET | `/summary` | 获取路由回退摘要 |

#### Codex 账号 `api/admin/codex`

| 方法 | 端点 | 说明 |
|------|------|------|
| POST | `/start-oauth` | 启动 OAuth/PKCE 授权流程 |
| POST | `/complete-oauth` | 完成 OAuth 授权，交换 token 并创建账号 |
| POST | `/import-credential` | 导入已有 OAuth 凭证 |
| GET | `/accounts` | 列出所有 Codex 账号 |
| POST | `/accounts/{id}/refresh-quota` | 主动刷新账号额度 |
| POST | `/accounts/{id}/reset-quota` | 重置账号额度快照 |
| POST | `/accounts/{id}/toggle` | 切换账号启用/禁用 |
| DELETE | `/accounts/{id}` | 删除账号（级联删除隐藏站点） |
| PUT | `/accounts/{id}` | 更新账号信息 |
| POST | `/accounts/{id}/refresh-token` | 手动刷新 OAuth token |
| GET | `/accounts/{id}/fetch-models` | 拉取账号可用模型 |
| POST | `/accounts/{id}/import-selected-models` | 导入勾选的 Codex 模型 |
| POST | `/inspection/run` | 手动触发一轮额度巡检 |
| GET | `/inspection/status` | 查询巡检运行状态 |
| GET | `/inspection/last-run` | 查询上一次巡检结果 |
| GET | `/inspection/logs` | 查询巡检日志 |
| GET | `/accounts/{id}/reset-credits` | 查询剩余重置 credits 与过期时间 |
| POST | `/accounts/{id}/consume-reset-credit` | 消耗一张 credit 执行真实重置 |
| POST | `/accounts/export-credentials` | 导出账号凭证 |

#### 开发者工具 `api/admin/developer`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/invocations` | 查询近期代理调用追踪记录（内存环形缓冲） |
| GET | `/invocations/{traceId}` | 获取单次调用的全链路详情 |
| POST | `/invocations/protocol-diagnostics` | 离线协议诊断：只调用内存桥接，不触发转发、不使用密钥、不写入调用记录 |

#### 系统设置 `api/admin/system`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/settings` | 获取系统运行时配置 |
| PUT | `/settings` | 更新系统运行时配置（含边界校验） |
| POST | `/usage-logs/clear` | 按来源和时间范围清空使用日志 |

#### 认证 `api/auth`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/status` | 查询登录状态与功能开关（登录前可调用） |
| POST | `/login` | 登录，校验密码后签发 access + refresh token |
| POST | `/refresh` | 用 refresh token 换取新的 access token |
| POST | `/setup-password` | 首次设置管理密码 |
| POST | `/logout` | 登出 |

---

## 管理后台页面

管理后台是 Vue 3 SPA，路由定义在 `frontend/src/router/index.ts`，页面组件在 `frontend/src/views/`，使用共享布局 `MainLayout.vue`。后端对非 `/api`、`/v1`、`/health`、`/hangfire` 的请求 fallback 到 `index.html`，交给 Vue Router（history 模式）处理。旧 Razor Pages 路径（如 `/Admin/ClientSimulator`、`/Admin/Developer/Invocations`）已配置 301 重定向到新 SPA 路由。

### 布局结构

- **侧边栏**：`MainLayout.vue` 固定左侧，移动端折叠为抽屉
- **顶部栏**：显示页面标题 + 版本号
- **导航分区**：概览 / 资源管理 / 代理配置 / 监控运维 / 开发调试
- **功能开关**：未开启对应功能的页面（Codex、开发者工具）由路由守卫重定向到仪表盘

### 页面清单

| 路由 | 视图组件 | 功能 |
|------|----------|------|
| `/` | `DashboardView` | 仪表盘首页，展示站点数、模型数、映射数、路由数、密钥数、检测任务数 |
| `/login` | `LoginView` | 登录 / 首次设置密码（JWT） |
| `/chat` | `ChatView` | 对话测试，支持流式和非流式 |
| `/sites` | `SitesView` | 站点管理，列表 + 创建/编辑/删除/导入 |
| `/codex` | `CodexView` | Codex OAuth 账号管理（需开启 Codex 功能） |
| `/models` | `ModelsView` | 模型库，含厂商图标、映射状态、兼容规则集绑定 |
| `/routes` | `RouteManagementView` | 路由规则管理 + 兼容规则集配置（多页签） |
| `/access-keys` | `AccessKeysView` | 访问密钥管理 |
| `/detection` | `DetectionView` | 模型检测，手动触发 + 增量进度 |
| `/detection-tasks` | `DetectionTasksView` | 检测任务（Cron 定时） |
| `/model-health` | `ModelHealthManagementView` | 模型健康监控，可用率时间线（含路由回退页签） |
| `/usage-logs` | `UsageLogsView` | 调用日志，含延迟指标、重试次数 |
| `/analytics` | `AnalyticsView` | 统计分析，用量趋势、模型分布、缓存命中率 |
| `/system/settings` | `SystemSettingsView` | 系统设置，含 Codex 巡检开关 |
| `/developer/invocations` | `DeveloperInvocationsView` | 调试工具（需开启开发者功能），多页签：调用追踪 / 客户端模拟器 / 并发熔断 / 协议诊断 |

### 调试工具页签

`/developer/invocations` 下含多个页签（通过 hash 深链接）：

- **调用追踪**（`#developerInvocationsPane`）：查看近期代理请求的全链路详情
- **客户端模拟器**（`#developerSimulatorPane`）：模拟 OpenAI/Anthropic 客户端请求
- **并发与熔断**（`#developerConcurrencyPane` / `#developerCircuitBreakerPane`）：查看并发占满和熔断状态
- **协议诊断**（`#developerProtocolDiagnosticsPane`）：离线协议转换诊断

### 离线协议诊断

协议诊断页签用于排查三协议相互转换问题，**只在本地执行已有协议桥接，不调用上游、不使用密钥、不写入调用记录**：

- 选择请求转换 / 响应转换方向
- 选择 OpenAI / Anthropic / Responses 源协议与目标协议
- 流式片段开关，Anthropic → Responses 流式需填 eventName
- 输入 payload（JSON 或 SSE 片段），执行离线转换
- 展示转换结果、事件数、完成状态、转换失败状态
- 模型名固定为 `deepseek-v4-flash`（仅诊断用，不真实调用）

### 侧边栏导航分区

```
概览
  ├── 仪表盘 (/)
  └── 对话测试 (/chat)

资源管理
  ├── 站点管理 (/sites)
  └── 模型库 (/models)

代理配置
  ├── 路由规则 (/routes)
  ├── 兼容规则集 (/routes?tab=compatibility)
  └── 访问密钥 (/access-keys)

监控运维
  ├── 模型检测 (/detection)
  ├── 检测任务 (/detection-tasks)
  ├── 模型健康 (/model-health)
  ├── 路由回退监控 (/model-health?tab=fallback)
  ├── 使用日志 (/usage-logs)
  └── 统计分析 (/analytics)

开发调试
  ├── 系统设置 (/system/settings)
  └── 调试工具 (/developer/invocations)
```

---

## Web 层核心服务

### ProxyRequestMetadataCache — 代理元数据缓存

```csharp
sealed class ProxyRequestMetadataCache  // Singleton，基于 IMemoryCache
```

- 缓存访问密钥、运行时设置、路由目标、兼容规则集、模型列表等，TTL 约 5 秒
- 核心方法：
  - `ValidateAccessKeyAsync()`: 验证访问密钥（缓存命中时无需查询数据库）
  - `GetRuntimeSettingsAsync()`: 获取系统运行时配置
  - `GetRouteTargetsForModelAsync()`: 获取指定模型的候选路由列表（含多 Key 展开）
  - `GetCompatibilityProfilesAsync()`: 获取启用的兼容规则集
  - `GetEnabledModelNamesAsync()`: 获取所有启用的模型名
  - `GetChatModelsAsync()`: 获取可用于对话测试的模型列表
  - `GetFallbackTargetAsync()`: 获取回退目标（无路由规则时使用）
- 失效方法：`InvalidateAccessKeys()`、`InvalidateRuntimeSettings()`、`InvalidateRouteTargets()`、`InvalidateCompatibilityProfiles()` 等
- 管理后台修改配置后调用失效方法，确保 5 秒内代理路径读取到最新配置

### ModelConcurrencyLimiter — 并发控制器

```csharp
sealed class ModelConcurrencyLimiter  // Singleton
```

- 按站点+模型维度维护 `SemaphoreSlim`，控制最大并发请求数
- 两种策略：
  - `SkipOnFull`：并发已满时立即返回 false，调用方尝试下一个路由
  - `WaitForSlot`：排队等待直到有槽位释放或超时
- 槽位释放通过 `Release()` 方法，确保并发计数准确
- `MaxConcurrency = 0` 时跳过并发控制（不限制）

### ProxyProtocolBridge — 跨协议桥接

```csharp
static class ProxyProtocolBridge  // 纯静态方法，无状态
```

OpenAI Chat Completions、Anthropic Messages、OpenAI Responses 三协议之间的双向转换引擎，覆盖请求体、非流式响应体、SSE 流式事件三种维度。透传优先：上游支持客户端协议时直接透传，不支持时才做兼容转换。

核心方法：
- `PrepareRequestBody()`: 根据客户端和目标协议转换请求体（消息格式、参数映射、模型名替换），目标为 Responses 时强制 `store=false`，Codex 目标清理上游不支持的字段
- `AdaptResponseBodyForClient()`: 将非流式响应从目标协议转换回客户端协议格式
- 流式 SSE 转换（带状态机，跨分片累积工具调用）：
  - `BuildAnthropicStreamStart()` / `ConvertOpenAiStreamChunkToAnthropic()` / `CompleteAnthropicStream()`: OpenAI → Anthropic
  - `ConvertAnthropicStreamChunkToOpenAi()`: Anthropic → OpenAI
  - `ConvertChatStreamChunkToResponses()` / `ConvertAnthropicStreamChunkToResponses()`: → Responses
  - `ConvertResponsesStreamingToChat()` / `ConvertResponsesSseToResponse()`: Responses → Chat / 非 SSE 聚合
- `IsCodexTarget()`: 判断目标 URL 是否为 Codex 上游，决定是否走 `CodexNonStreamingBridge`

防御性设计：
- `content:null`、`usage:null`、空 choices、空 output 不构造空响应
- role-only / usage-only 分片不启动空 Responses 生命周期
- 工具调用索引 / ID / arguments 跨 SSE 分片稳定累积
- 流式完成事件去重，转换失败时不混入原协议 SSE

### CodexNonStreamingBridge — Codex 非流式聚合桥接

```csharp
internal sealed class CodexNonStreamingBridge  // Codex 非流式请求专用
```

Codex 上游的非流式 Responses 请求实际通过内部 SSE 聚合实现：收集 Codex SSE → 提取 usage → 通过 `ConvertResponsesSseToResponse()` 重建普通 Responses JSON。若 SSE 中没有有效 `response.completed` 事件，结果标记失败并返回错误信息，避免把空响应包装成成功。

### DeveloperInvocationTraceStore — 开发者调试追踪

```csharp
sealed class DeveloperInvocationTraceStore  // Singleton，内存环形缓冲区
```

- 最多保留 40 条最近代理请求的详细记录（约 20 分钟过期自动清理）
- 每条记录包含：请求头、请求体、响应体、每次尝试的详情、耗时、协议信息
- 通过 `/developer/invocations` 调试工具的"调用追踪"页签查看
- 需在系统设置中启用 `DeveloperFeaturesEnabled`
- 进程内 Singleton，重启后丢失

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
sealed class AdminAuthService  // Singleton
```

- 密码哈希采用 PBKDF2 加盐格式，兼容旧的无盐 MD5（登录成功后透明升级为 PBKDF2）
- 密码哈希存储在 `appsettings.json` 的 `AdminAuth` 配置节
- 方法：`HasPasswordConfigured()`、`VerifyPassword()`、`SetPasswordAsync()`、`UpgradePasswordAsync()`

### JwtTokenService — JWT 令牌服务

```csharp
sealed class JwtTokenService  // Scoped
```

- 签发 access token + refresh token 对
- access token 用于 `/api/*` 接口的 `Authorization: Bearer {access}` 认证
- refresh token 存储在 `RefreshTokenRecord` 表，过期或登出后失效
- 配置项：`Jwt:Issuer`、`Jwt:Audience`、`Jwt:SigningKey`（`JwtOptions`）
- `LoginRateLimitService` 提供 IP 维度的登录暴力破解防护（失败计数 + 锁定）

---

## 依赖注入配置

在 `Program.cs` 中注册：

| 注册 | 生命周期 | 说明 |
|------|----------|------|
| `ISqlSugarClient` (`SqlSugarScope`) | Singleton | SqlSugar 线程安全单例客户端 |
| `AppDbContext` | Scoped | SqlSugar 适配层（保持原 EF 属性名） |
| `SemaphoreSlim`（DB 串行化锁） | Singleton | 后台 DB 操作串行化锁 |
| `ISiteCatalogClient` → `OpenAiSiteCatalogClient` | HttpClient Typed | 拉取上游站点模型列表 |
| `ModelHealthRequestService` | Scoped | 模型健康检测 + 模型探测 |
| `SiteKeySelector` | Scoped | 站点级操作取用活动密钥 |
| `IProxyForwardService` → `ProxyForwardService` | HttpClient Typed | 请求转发（含流式，SocketsHttpHandler 连接池） |
| `IUsageLogService` → `UsageLogService` | Scoped | 使用日志入队 |
| `ISystemRuntimeSettingsService` → `SystemRuntimeSettingsService` | Scoped | 系统运行时配置 |
| `ProxyUsageLogBatchWriter` | Singleton (HostedService) | 使用日志批量写入 |
| `RouteCircuitStateStore` | Singleton | 熔断状态（内存） |
| `ModelConcurrencyLimiter` | Singleton | 并发控制（按站点+模型信号量） |
| `ProxyRequestMetadataCache` | Singleton | 代理元数据缓存 |
| `DeveloperInvocationTraceStore` | Singleton | 开发者调试追踪 |
| `SiteUsageTracker` | Singleton | 站点使用时间内存映射（Codex 巡检用） |
| `AnalyticsBackgroundQueryExecutor` | Singleton (HostedService) | 统计分析后台查询 |
| `ModelVendorCatalogService` | Singleton | 厂商模型目录 |
| `AdminAuthService` | Singleton | 管理员密码认证 |
| `JwtTokenService` | Scoped | JWT 令牌签发与刷新 |
| `LoginRateLimitService` | Singleton | 登录暴力破解防护 |
| `ILogRetentionService` → `LogRetentionService` | Scoped | 日志清理 |
| `HangfireDetectionScheduler` | Singleton | 定时任务调度 |
| `ICodexOAuthClient` → `CodexOAuthClient` | HttpClient Typed | Codex OAuth/PKCE |
| `ICodexModelCatalog` → `CodexModelCatalog` | Singleton | Codex 静态模型目录 |
| `ICodexModelFetcher` → `CodexModelFetcher` | HttpClient Typed | Codex 动态模型拉取 |
| `ICodexQuotaService` → `CodexQuotaService` | HttpClient Typed | Codex 额度主动查询 |
| `ICodexQuotaCooldownService` → `CodexQuotaCooldownService` | Scoped | Codex 额度被动冷却与重置 |
| `ICodexResetCreditsService` → `CodexResetCreditsService` | HttpClient Typed | Codex 手动重置 credits |
| `CodexAccountProvisioner` | Scoped | Codex 账号创建（隐藏 Site） |
| `CodexCredentialRefreshService` | Scoped | Codex 401 即时刷新凭证 |
| `CodexTokenRefreshService` | HostedService (Singleton) | 周期刷新 Codex OAuth token |
| `CodexCooldownRecoveryService` | HostedService (Singleton) | 周期恢复冷却到期的 Codex 账号 |
| `CodexInspectionService` | Singleton (HostedService) | Codex 额度巡检 + 自动禁用 |
| `MemoryMaintenanceService` | HostedService | 定期压缩 LOH，回收大对象碎片 |
| `AppVersionInfo` | Singleton | 应用版本号（当前 1.0.1.7） |

HttpClient Typed 实例由 `AddHttpClient<TInterface, TImpl>()` 注册，自动管理 `HttpClient` 生命周期；`IProxyForwardService` 配置 `SocketsHttpHandler` 连接池（`MaxConnectionsPerServer=200`）提升并发能力。

---

## 启动流程（Program.cs）

```
1. 构建 WebApplication
   ├─ 配置 NLog 日志（控制台 + Debug）
   ├─ AppVersionInfo(1.0.1.7)        — 版本号单例
   ├─ Server:Port 配置，默认 15029    — http://0.0.0.0:{port}
   ├─ ConfigureKestrel()             — MaxConcurrentConnections=500, KeepAlive=130s, MaxRequestBodySize=100MB
   ├─ AddResponseCompression()       — 启用响应压缩（含 HTTPS）
   ├─ AddControllers() + HttpExceptionLoggingFilter — API 控制器 + 异常日志过滤器
   ├─ AddMemoryCache()               — 内存缓存
   ├─ Configure<JwtOptions>() + JwtTokenService + LoginRateLimitService — JWT 配置
   ├─ AddAuthentication(JwtBearer)    — JWT Bearer 认证（/api/* 未带 token 返回 401 JSON）
   ├─ AddAuthorization()              — 授权
   ├─ AdminAuthService                — 管理员认证（PBKDF2 密码）
   ├─ AddSwaggerGen()                 — Swagger（Swagger:Enabled 控制，Testing 环境关闭，集成 JWT Authorize）
   ├─ AddSqlSugar()                   — SqlSugarScope 单例 + AppDbContext Scoped 适配
   ├─ AddHttpClient<T,T>()            — Typed HttpClient（SiteCatalog/Probe/Forward/Codex 等）
   ├─ AddScoped<T,T>() / AddSingleton<T>() — 业务服务 + 后台服务
   ├─ AddHangfire()                   — 内存存储 + Server
   └─ AddHangfireDashboard()          — 仪表盘（/hangfire）

2. 启动初始化（using scope）
   ├─ SqlSugarSetup.InitializeDatabase() — CodeFirst 建表 + 补列 + PRAGMA(WAL/synchronous/cache_size/busy_timeout)
   │   └─ MigrateLegacySiteKeys()        — 老站点 Site.ApiKey 迁移为默认 SiteKey
   ├─ SiteUsageTracker.WarmupAsync()     — 从 DB 预热站点最近使用时间
   ├─ ScheduleAllAsync()                 — 注册所有启用的检测任务到 Hangfire
   ├─ RouteCircuitStateStore.UpdateOptions() — 用运行时设置初始化熔断参数
   └─ ProxyRequestMetadataCache.GetRuntimeSettingsAsync() — 预热代理热路径缓存

3. 中间件配置
   ├─ UseExceptionHandler()      — 自定义异常处理（JSON 错误响应 + 请求体日志，Testing 跳过）
   ├─ UseResponseCompression()   — 响应压缩
   ├─ UseStaticFiles()           — 静态文件（/assets/ 长缓存，index.html 不缓存）
   ├─ UseWebSockets()            — WebSocket（Responses API 使用）
   ├─ UseAuthentication()        — JWT 认证
   ├─ UseAuthorization()         — 授权
   ├─ 管理员认证中间件           — 仅拦截 /api/admin/* 和 /hangfire（SPA 路由交给前端）
   ├─ UseSwagger() / UseSwaggerUI() — Swagger UI（/swagger，位于 SPA fallback 之前）
   ├─ MapGet("/health")          — 健康检查端点
   ├─ UseHangfireDashboard()     — Hangfire 仪表盘（/hangfire，未登录重定向到登录页）
   ├─ RecurringJob               — 日志清理（每日 03:00 UTC）
   ├─ MapControllers()           — 映射 API 控制器路由
   └─ MapFallbackToFile("index.html") — SPA fallback（非 /api、/v1、/health、/hangfire 返回 index.html）

4. app.Run()
```

数据库连接字符串优先从 `Configuration.GetConnectionString("DefaultConnection")` 读取，为空时默认 `Data Source={运行目录}/aitool.db`。默认端口 `15029`，可通过 `appsettings.json` 的 `Server:Port` 或环境变量覆盖。

---

## 数据库

- **引擎：** SQLite（WAL 模式，`synchronous=NORMAL`，`busy_timeout=5000`）
- **文件位置：** Web 应用运行目录下的 `aitool.db`
- **初始化方式：** SqlSugar CodeFirst（`SqlSugarSetup.InitializeDatabase()`，`InitTables` 自动建表 + 差量补列，只增不删），不使用 Migration
- **日志保留：** 使用日志的保留天数由 `SystemRuntimeSettings.UsageLogRetentionDays` 配置，自动清理由 `UsageLogAutoCleanupEnabled` 控制

**重要约束：**
- `ModelLibraryItem.ModelName` 有唯一索引，不同站点的同名模型归一到同一条记录
- `SiteModelMapping` 有 `(SiteId, RemoteModelName)` 复合唯一索引
- `ModelHealthMonitor.ModelLibraryItemId` 有唯一索引
- `ProxyRouteEntry.EntryName` 有唯一索引
- `ProxyAccessKey` 有 `(AccessKeyHash, IsEnabled)` 复合索引
- `SiteKey` 有 `SiteId` 索引
- `CodexAccount` 有 `LinkedSiteId`、`TokenExpiresAt` 索引
- 实体之间没有导航属性，关系通过手动查询解析（ID 字典查找）
- DateTimeOffset 通过 SqlSugar AOP 在写入前转本地时区存储，保证往返瞬时正确
- SqlSugarScope 单例线程安全；后台批量写走 `AppDbContext.SerialExecuteAsync` 串行化，Web 请求路径不加锁

**数据库补丁机制：**
`SqlSugar.CodeFirst.InitTables` 在表已存在时自动补齐缺失列（只增不删），无需手写 `ALTER TABLE`。启动时还会执行一次性数据迁移 `MigrateLegacySiteKeys`，把老站点的 `Site.ApiKey` 复制成一条默认 `SiteKey`（幂等，可重复执行）。

---

## 前端 UI 规范

- **框架：** Vue 3 + TypeScript + Vite
- **UI 组件库：** Naive UI
- **样式：** Tailwind CSS + 组件库主题
- **状态管理：** Pinia
- **路由：** Vue Router（history 模式，`createWebHistory('./')`）
- **HTTP：** Axios，统一 API 层在 `frontend/src/api/`
- **图表：** ECharts
- **布局：** `MainLayout.vue` 共享侧边栏布局，移动端折叠为抽屉
- **功能开关：** 路由守卫根据 `auth.status.features` 控制页面可访问性（Codex、开发者工具）
- **深链接：** 调试工具等页面通过 hash 深链接定位页签（如 `#developerProtocolDiagnosticsPane`）
- **旧路径兼容：** 旧 Razor Pages 路径（`/Admin/...`）配置 301 重定向到新 SPA 路由
- **构建：** `vue-tsc --noEmit` 类型检查 + `vite build`，产物输出到 `src/AITool.Web/wwwroot`

---

## 定时任务

通过 Hangfire 管理（内存存储，重启后丢失）：

| 任务 ID | 执行时间 | 说明 |
|----------|----------|------|
| `log-retention-prune` | 每日 03:00 UTC | 清理超过保留天数的使用日志 |
| `detection-{taskId}` | 按各任务的 Cron 表达式 | 执行定时模型检测 |

此外有多个 `HostedService` 后台服务（非 Hangfire）：`ProxyUsageLogBatchWriter`、`AnalyticsBackgroundQueryExecutor`、`CodexTokenRefreshService`、`CodexCooldownRecoveryService`、`CodexInspectionService`、`MemoryMaintenanceService`。

Hangfire Dashboard：`/hangfire`（未登录重定向到前端登录页）

---

## 关键设计决策

1. **SqlSugar CodeFirst：** 用 `InitTables` 自动建表 + 差量补列（只增不删），无需 Migration，改实体后启动时自动补齐缺失列，保持与旧数据库的兼容性。
2. **无导航属性：** 实体间通过 ID 关联，查询时手动 Join 或字典查找，避免复杂查询翻译问题。
3. **内存熔断：** `RouteCircuitStateStore` 是 Singleton，重启后状态丢失。采用渐进式熔断：连续失败达阈值才触发，避免单次失败就屏蔽站点。阈值和恢复时间可通过系统设置动态配置。
4. **增量进度报告：** 检测进度使用 `LastReportedCount` 追踪，每次轮询只返回新增结果，避免重复刷新。
5. **模型名去重：** 导入模型时预加载字典，避免多站点同名模型的 UNIQUE 约束冲突。后续同名模型复用已有 `ModelLibraryItem`。
6. **Chat 不触发熔断：** 对话测试页每次请求都是独立的，不影响代理链路的熔断状态。
7. **每次尝试记录日志：** 代理请求为每次路由尝试记录一条日志（`AttemptIndex` 标识序号），最终结果标记 `IsFinalResult = true`，便于追踪完整的故障转移链路。
8. **路由规则删除重建：** 保存路由规则时先删除旧的再按新顺序创建，而不是更新，简化逻辑。
9. **批量日志写入：** `ProxyUsageLogBatchWriter` 使用后台通道批量写入，避免代理热路径上的 SQLite 写入竞争。
10. **元数据缓存：** `ProxyRequestMetadataCache` 提供 5 秒 TTL 的内存缓存层，保持代理路径高性能，同时允许管理后台修改即时传播。
11. **三协议桥接：** `ProxyProtocolBridge` 纯静态无状态设计，支持 OpenAI Chat / Anthropic Messages / OpenAI Responses 三协议任意两两转换（请求体、响应体、流式 SSE），透传优先、不兼容才转换，使网关能透明连接不同协议的后端。
12. **后台统计查询：** `AnalyticsBackgroundQueryExecutor` 限制昂贵的聚合查询为单消费者队列，防止数据库过载。
13. **并发控制：** `ModelConcurrencyLimiter` 按站点+模型维度限制并发，支持跳过和排队两种策略，配合路由优先级实现优雅降级。
14. **多 Key 主备调度：** 站点支持多把 `SiteKey`，缓存层把"路由 × 多个 Key"展开成多条候选路由，复用优先级排序、熔断和并发占满跳下一个机制，实现主备 Key 与各自独立并发计数。
15. **兼容规则集：** `CompatibilityProfile` 独立于模型维护，可被多个模型引用，转发前按透传/中转作用域应用 strip/rename/default 规则，避免在每台模型重复配置。
16. **Codex 账号托管：** 每个账号关联隐藏 Site 以 Responses 协议接入转发链路，复用路由/模型/对话测试；token 自动刷新、额度查询与缓存、被动冷却恢复、定期巡检、手动重置 credits 协作，多种禁用状态相互区分避免误启用。
17. **离线协议诊断：** 调试工具的协议诊断页签只调用内存桥接，不触发转发、不使用密钥、不写入调用记录，用于排查协议转换问题而不影响真实链路。

---

## 常见 SqlSugar / SQLite 陷阱

本项目使用 SQLite + SqlSugar，有以下已知限制：

1. **不能在 `Where()` 中调用 C# 方法：**
   ```csharp
   // 错误：SQLite 无法翻译 IsBlocked() 方法
   _dbContext.Sites.Where(s => _circuitStore.IsBlocked(s.Id))

   // 正确：先加载到内存，再过滤
   var siteIds = await _dbContext.Sites.Select(s => s.Id).ToListAsync();
   var blockedIds = new HashSet<Guid>(siteIds.Where(id => _circuitStore.IsBlocked(id)));
   ```

2. **`DateTimeOffset` 存储偏移：** SqlSugar 存储 `DateTimeOffset` 时只存时钟值（不带 offset），读取时配本地时区 offset。项目通过 AOP 在写入前把所有 `DateTimeOffset` 转为本地时区，使存储的时钟值与读回的 offset 一致，保证往返瞬时正确。

3. **后台写串行化：** 后台批量写（巡检/批量写/冷却恢复）必须走 `AppDbContext.SerialExecuteAsync` 串行化，避免与代理热路径批量写踩 `SqlSugarScope` 竞态；Web 请求路径不加锁，依赖 SqlSugarScope 线程安全 + WAL + busy_timeout。

---

## 快速开始

### 生产部署（同进程托管）

```bash
# 1. 构建前端（产物输出到 src/AITool.Web/wwwroot）
cd frontend
npm install
npm run build
cd ..

# 2. 编译后端
dotnet build

# 3. 运行（默认 http://0.0.0.0:15029）
cd src/AITool.Web
dotnet run
```

访问 `http://localhost:15029`，首次访问会引导设置管理密码（JWT 认证）。

### 开发模式（前后端分离调试）

```bash
# 终端 1：启动后端 API（15029 端口）
cd src/AITool.Web
dotnet run

# 终端 2：启动前端 dev server（5173 端口，proxy 转发 /api /v1 到后端）
cd frontend
npm run dev
```

访问 `http://localhost:5173` 开发调试（支持热更新）。后端 API 也可直接访问 `http://localhost:15029/api/*`。

> 若后端端口非 15029，用环境变量覆盖：`VITE_API_TARGET=http://127.0.0.1:<端口> npm run dev`
> 默认端口也可通过 `appsettings.json` 的 `Server:Port` 配置覆盖。

---

## 测试

### 测试策略

- **单元测试**（`AITool.ApplicationTests`）：使用隔离的 SQLite 数据库测试业务逻辑
- **集成测试**（`AITool.IntegrationTests`）：使用 `WebApplicationFactory<Program>` 构建完整测试宿主，每个测试类使用独立的临时 SQLite 数据库文件

后端测试命令（仓库根目录）：

```bash
dotnet test
```

前端测试命令（`frontend/` 目录）：

```bash
npm run test          # vitest run
npm run type-check    # vue-tsc --noEmit
npm run build         # 类型检查 + vite build
```

### 测试覆盖

**ApplicationTests（单元测试）：**

| 文件 | 说明 |
|------|------|
| `Operations/SystemRuntimeSettingsServiceTests.cs` | 系统配置服务测试 |
| `Operations/SystemRuntimeSettingsServiceSqliteTests.cs` | 系统配置 SQLite 持久化测试 |
| `Retention/LogRetentionServiceTests.cs` | 日志清理服务测试 |
| `Proxy/RouteCircuitStateStoreTests.cs` | 熔断状态存储测试 |
| `Proxy/UsageLogServiceTests.cs` | 使用日志服务测试 |
| `Proxy/ProxyForwardServiceResponseTests.cs` | 代理转发响应解析测试 |
| `Proxy/ProxyProtocolResolverTests.cs` | 代理协议解析（透传/中转判定）测试 |
| `Routing/RouteSelectionServiceTests.cs` | 路由选择服务测试 |
| `Sites/SiteEndpointPathResolverTests.cs` | 站点路径解析测试 |
| `Health/ModelHealthRequestServiceTests.cs` | 模型健康检测服务测试 |
| `UsageLogs/PercentileCalculatorTests.cs` | 用量百分位计算测试 |
| `UsageLogs/UsageLogErrorClassifierTests.cs` | 用量日志错误分类测试 |
| `Codex/CodexModelFetcherTests.cs` | Codex 模型拉取测试 |

**IntegrationTests（集成测试）：**

| 文件 | 说明 |
|------|------|
| `Proxy/AnthropicProxyControllerTests.cs` | Anthropic 代理端到端测试 |
| `Proxy/OpenAiCrossProtocolProxyTests.cs` | OpenAI 入口跨协议桥接测试 |
| `Proxy/ProxyFallbackFlowTests.cs` | 代理故障转移流程测试 |
| `Proxy/ProxyResilienceTests.cs` | 代理韧性测试（超时、重试） |
| `Proxy/ProxyMetadataCacheTests.cs` | 代理元数据缓存测试 |
| `Proxy/ResponsesProxyTests.cs` | Responses API 代理测试 |
| `Proxy/ModelConcurrencyLimiterTests.cs` | 并发控制器测试 |
| `Proxy/ProxyProtocolBridgeThinkingTests.cs` | 协议桥接 thinking/reasoning 转换测试 |
| `Proxy/ProxyProtocolBridgeResponseConversionTests.cs` | 协议桥接响应转换测试 |
| `Auth/AdminAuthTests.cs` | 管理员认证测试 |
| `Auth/PasswordHasherTests.cs` | 密码哈希（PBKDF2）测试 |
| `Analytics/AnalyticsApiTests.cs` | 统计分析 API 测试 |
| `Analytics/AnalyticsBackgroundQueryExecutorTests.cs` | 统计分析后台查询执行器测试 |
| `Chat/ChatApiTests.cs` | 对话测试 API 测试 |
| `Health/HealthEndpointTests.cs` | 健康检查端点测试 |
| `UsageLogs/UsageLogsApiTests.cs` | 使用日志 API 测试 |
| `Services/SiteCascadeDeleterTests.cs` | 站点级联删除测试 |
| `DeveloperInvocationTraceStoreTests.cs` | 开发者调用追踪存储测试 |
| `Developer/ProtocolDiagnosticsApiTests.cs` | 离线协议诊断 API 测试 |
| `Contracts/ApiResponseTests.cs` | 统一响应包装契约测试 |

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

### 场景：Codex 账号托管

1. 在系统设置开启 Codex 功能，进入 Codex 管理页
2. 通过 OAuth/PKCE 登录或导入凭证，系统自动创建隐藏站点（Responses 协议）
3. 拉取账号可用模型并导入，为 Codex 模型配置路由规则
4. 后台自动刷新 token、周期额度巡检、被动冷却恢复，额度耗尽自动禁用
5. 在使用日志中查看 Codex 上游调用记录，与普通站点调用一致

### 场景：兼容规则集配置

1. 在路由管理页的"兼容规则集"页签新建规则集，如「GPT-5 兼容」
2. 添加规则：`strip reasoning_content`、`rename reasoning_effort → effort`、`default store=false`
3. 在模型库编辑页为对应模型绑定该规则集
4. 转发上游前自动应用规则（按透传/中转作用域筛选），无需修改客户端代码

### 场景：协议转换问题排查

1. 开启开发者功能，进入调试工具的"协议诊断"页签
2. 选择方向（请求转换 / 响应转换）、源协议、目标协议
3. 粘贴 payload（JSON 或 SSE 片段），执行离线转换
4. 查看转换结果、事件数、完成状态，快速定位协议不同步问题
5. 该工具不调用上游、不使用密钥、不写入调用记录，可安全反复使用

### 场景：定时检测模型可用性

1. 在检测任务页创建定时任务，设置 Cron 表达式
2. 系统按计划自动探测所有映射的可用性
3. 在模型健康页查看可用率趋势
4. 在使用日志中查看检测任务的详细探测记录（Source="detection-task"）

### 场景：用量分析和监控

1. 在使用日志页查看详细的调用记录（含延迟指标、缓存命中、流式状态）
2. 在统计分析页查看用量趋势、模型分布、缓存命中率
3. 在系统设置页调整超时、重试、熔断、并发控制、日志保留等参数
4. 开启开发者调试功能，追踪代理请求的全链路详情

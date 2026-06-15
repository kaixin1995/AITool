# 性能优化机会清单（第二轮）

> 审查日期：2026-06-15
> 审查范围：在第一轮（`performance-optimization-opportunities.md`）已做的 12 项基础上，新发现的优化机会
> 审查方法：4 个并行子任务分别深入 SQLite 存储层、.NET 8 现代特性、启动/缓存/前端网络、架构级重构，关键发现均经亲自验证。
>
> **重要发现**：本轮发现项目**完全没配置** SQLite PRAGMA、ResponseCompression、Kestrel Limits、缓存预热——这四项零风险高收益，是最该优先做的。

---

## 第一轮已做的（不重复）

JsonSerializerOptions 单例化、流式 StringBuilder 容量上限、UsageLogs/Analytics 非时间筛选下推、Detection/ModelHealth AsNoTracking、索引补充、序号批量写盘、Spool 批量写、TraceStore 锁节流、Gzip GetBuffer。

---

## 🟢 零风险配置优化（最高优先级，立即做）

这四项是纯配置/少量代码，功能完全不变，收益立竿见影。

### R2-1. SQLite 启用 WAL 模式 + PRAGMA 调优

**当前状态**：项目用裸 `UseSqlite(connectionString)`，全代码库**零 PRAGMA 配置**（已 Grep 验证）。SQLite 默认 DELETE 日志模式，**写时阻塞读**。

**优化**：在 `AdminStartupInitializer.InitializeAsync` 的 `EnsureCreated()` 之后执行（持久属性，一次性）：
```sql
PRAGMA journal_mode=WAL;        -- 读写不再互斥，并发读写吞吐 3-10 倍
PRAGMA synchronous=NORMAL;      -- 写 fsync 频次下降 50%+（WAL 下安全）
```
连接级（每次连接打开或连接字符串）：`cache_size=-65536`（64MB）、`busy_timeout=5000`。

**收益**：**这是数量级提升**。代理热路径的 UsageLog 写入（BatchWriter）与 Admin 页面查询不再互斥，高并发下读写吞吐 3-10 倍。

**成本**：低。无需改实体/查询，仅加启动 SQL。
**位置**：`src/AITool.Infrastructure/Persistence/AdminStartupInitializer.cs:38` 之后

### R2-2. 启用 ResponseCompression 中间件

**当前状态**：全代码库**零 ResponseCompression 配置**（已验证）。API JSON 响应（Analytics/UsageLogs/Invocations 列表）和 Razor 页面**全部未压缩**传输。

**优化**：Admin 和 Core 的 Program.cs 加：
```csharp
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);
app.UseResponseCompression();  // 置于 UseStaticFiles 前
```

**收益**：JSON 列表响应体积降 60-80%，对网络受限的内网部署尤其明显。UsageLogs/Analytics 大列表响应从几百 KB 降到几十 KB。

**成本**：低。注意 SSE 流式响应需测试（可能需对 `text/event-stream` 跳过压缩）。

### R2-3. 静态文件 Cache-Control 头

**当前状态**：`app.UseStaticFiles()` 无 `OnPrepareResponse` 设置 Cache-Control（已验证）。`theme.css`（21KB）每次访问都可能条件请求。

**优化**：
```csharp
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=86400"
});
```

**收益**：重复访问静态资源零往返。

### R2-4. 代理热路径缓存预热

**当前状态**：`ProxyRequestMetadataCache` 全部懒加载，首次代理请求触发 5+ 次 DB 往返（AccessKeys/RuntimeSettings/RouteTargets/Models/FallbackMappings）。

**优化**：`AdminStartupInitializer.InitializeAsync` 末尾并行调用预热：
```csharp
await Task.WhenAll(
    cache.GetRuntimeSettingsAsync(cancellationToken),
    cache.ValidateAccessKeyAsync("warmup", cancellationToken), // 或直接填充
    cache.GetRouteTargetsAsync(cancellationToken)
);
```

**收益**：消除首个代理请求的 30-100ms P99 抖动。

**成本**：低。

### R2-5. Kestrel 连接/请求体限制配置

**当前状态**：无 `Kestrel:Limits` 配置（已验证），全用默认值。代理大请求体（长对话、base64 图片）行为不可预测。

**优化**：
```csharp
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxConcurrentConnections = 500;
    o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(130);
    o.Limits.MaxRequestBodySize = null; // 不限制（代理需支持大 body）
});
```

**收益**：可预测的并发行为 + 大请求支持。

---

## 🔵 HttpClient / 网络层（低难度高收益）

### R2-6. HttpClient SocketsHttpHandler 连接池配置

**当前状态**：`AddHttpClient<IProxyForwardService, ProxyForwardService>()` 只设 Timeout，未配置 `SocketsHttpHandler` 的连接池参数。

**优化**：
```csharp
.AddHttpClient<IProxyForwardService, ProxyForwardService>()
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    MaxConnectionsPerServer = 200,
    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30)
});
```

**收益**：高并发下避免连接耗尽/DNS 不刷新。
**位置**：`src/AITool.Infrastructure/DependencyInjection/ProxyRuntimeInfrastructureExtensions.cs:65`

---

## 🟡 SQLite 查询层（中难度，根治全表加载）

### R2-7. 时间字段 DateTimeOffset → DateTime(UTC) 或 long

**这是根本性优化，解锁多个其他优化。**

**当前问题**：SQLite EF Core **不支持 DateTimeOffset 的 WHERE/ORDER BY 翻译**，导致 UsageLogs/Analytics/Detection/ModelHealth 四处查询被迫 `ToListAsync` 全表加载后客户端过滤+排序+分页。

**方案评估**：
| 方案 | EF Core 翻译 | 排序/范围查询 | 迁移成本 | 推荐 |
|------|-------------|--------------|---------|------|
| DateTimeOffset（现状） | ❌ 不支持 | 客户端 | — | 否 |
| DateTime（UTC） | ✅ 完整支持 | DB 端 | 中（改 3 实体 + DTO 转换） | ⭐ 推荐 |
| long（Unix 毫秒） | ✅ 最佳 | DB 端索引最快 | 高（C# 手写转换） | 可选 |

**推荐 DateTime(UTC)**：本项目全用 `DateTimeOffset.UtcNow`（即 UTC），改成 `DateTime.UtcNow` 业务无损，改动最小。改完后：
- 时间范围、ORDER BY、Skip/Take 全部下推到 DB
- 配合现有 `IX_ProxyUsageLogs_RequestedAt_Status` 索引走索引扫描
- `ExecuteDeleteAsync` 归档可用（需 R2-9）
- 列表查询从 O(全表) 降到 O(页大小)，**1-2 个数量级提升**

**影响范围**：`ProxyUsageLog.RequestedAt`、`ConversationTurnLog.CreatedAt/UserCreatedAt`、`DetectionTaskExecution.StartedAt/FinishedAt` 等。DTO 层仍可用 `DateTimeOffset` 对外（API 层转换）。EnsureCreated 模式不能自动改列类型，需在 Migrator 加列 + 回填。

### R2-8. 列表查询投影去掉大文本字段

**当前问题**：`ProxyUsageLog.ErrorMessage`(2000 字符) 在列表/统计查询时被全字段加载，但 Analytics 完全不需要它。

**优化**：Analytics 的 `BuildDashboardResponseAsync` 改 `Select` 投影只取需要的列（Token/耗时/状态/站点/模型/时间），避免加载 ErrorMessage。

**收益**：错误多的场景行平均字节数下降 50-80%。

### R2-9. ExecuteDelete 归档（依赖 R2-7）

R2-7 落地后，`LogRetentionService` 可以用 `ExecuteDeleteAsync`（时间字段能翻译了），彻底替代当前的 RemoveRange 路径，删除性能提升数倍。冷热分离（历史归档到独立表）也变为可能。

---

## 🟠 代理热路径 .NET 优化（中难度）

### R2-10. ModifyRequestBody 用 JsonNode 替代 Dictionary<object> 反射

**当前**：`ProxyForwardService.cs:513-537` 用 `Dictionary<string,object>` + 反射式 `Deserialize<object>` 改 model 名（每请求调）。

**优化**：复用已有的 `ProxyProtocolBridge.ReplaceModelName`（Helpers.cs:829）的 `JsonNode.Parse` → 改 model → `ToJsonString()` 方案，消除反射。

### R2-11. TimeRangesJson 在缓存构建时预解析

**当前**：`IsAvailableAt`（ProxyRequestMetadataCache.cs:1201）每请求每路由都重新 `JsonSerializer.Deserialize` TimeRangesJson（即使已经用了单例 options，反序列化本身仍重复）。

**优化**：`CachedProxyRouteTarget` 在缓存构建时（NormalizeTimeRangesJson 调用点）一次性解析为 `IReadOnlyList<CachedRouteTimeRange>` 存进对象，`IsAvailableAt` 直接遍历结构体。

### R2-12. SSE 行解析用 ReadOnlySpan&lt;char&gt;

**当前**：`line.StartsWith("data: ")` + `line["data: ".Length..]` 每行切出新 string。

**优化**：`line.AsSpan()` + `span.StartsWith` + `span.Slice`，仅 parse JSON 时 ToString。

### R2-13. ValueTask 改造缓存命中路径

**当前**：`GetRuntimeSettingsAsync` 等 Core 路径缓存命中时用 `Task.FromResult`，仍分配 Task。

**优化**：返回类型改 `ValueTask<T>`，缓存命中直接返回（隐式 ValueTask）。

---

## 🔴 架构级（需适度改功能/重构，数量级提升）

### R2-14. 预聚合表（Analytics 物化视图）

**当前问题**：`AnalyticsApiController.GetDashboard` 每次全表扫描 + 内存 GroupBy/Sum/分桶。

**方案**：新增 `ProxyUsageLogHourly` 表（按小时+模型+站点+状态预聚合），`ProxyUsageLogBatchWriter` 写入时旁路 upsert 汇总行。Dashboard 直接读汇总表。

**收益**：Analytics 从全表 N 行降到 168 行/周，**2-3 个数量级**。

### R2-15. CoreEventSpool 去掉文件全量重写

**当前**：`TrimAckedAsync`/`ListAfterAsync` 每次全量反序列化 spool 文件 → 过滤 → 重写整文件。

**方案**：spool 文件只追加不重写，ack 进度持久化为游标（已有 `CoreEventAckStateStore`），读取从游标续读，仅 `PruneExpiredFilesAsync` 按天删整文件。

**收益**：高频 ack IO 从 O(N) 降到 O(1)。

### R2-16. 前端轮询改 SSE（复用已有基建）

**当前**：UsageLogs(5s)、RouteFallback(5s)、Invocations(5s)、Detection(1s) 固定轮询。

**方案**：后端已有 SSE 基建（`/api/core/events/stream`），把这些页面改为 SSE 推送或至少加 `visibilitychange` 守护（页面不可见时停止轮询）。Invocations 已有此守护，推广到其他页面。

**收益**：空闲页空请求降为 0，后端 DB 压力下降。

---

## 优先级落地建议

### 第一波：零风险配置（半天，立即做）
**R2-1（WAL）、R2-2（压缩）、R2-3（静态缓存）、R2-4（缓存预热）、R2-5（Kestrel）、R2-6（HttpClient）**——全是纯配置，功能不变，收益立竿见影。尤其 R2-1 WAL 是数量级提升。

### 第二波：SQLite 查询根治（1-2 天）
**R2-7（DateTimeOffset→DateTime）** 是关键，解锁 R2-8/R2-9。改完后列表/统计查询延迟下降 1-2 个数量级。

### 第三波：热路径 .NET 优化（1 天）
**R2-10、R2-11、R2-13**——改动小、消除反射/重复反序列化。

### 第四波：架构级（按需，较大投入）
**R2-14（预聚合）** 收益最大但需新表 + 双写一致性。R2-15/R2-16 视实际压力决定。

---

## 验证方法

- **SQLite PRAGMA**：`sqlite3 aitool.db "PRAGMA journal_mode;"` 应返回 `wal`。
- **ResponseCompression**：浏览器开发者工具看响应头有 `Content-Encoding: gzip/br`。
- **缓存预热**：启动后立即发代理请求，观察无首次 DB 查询日志。
- **查询下推**：UsageLogs 页面打开速度对比（R2-7 前后）。

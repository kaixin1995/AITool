# 性能优化机会清单

> 审查日期：2026-06-14
> 审查范围：当前 `split-core-admin-architecture` 分支全量代码
> 审查方法：4 个并行子任务分别深入代理热路径、数据库访问、缓存/事件总线、内存/启动四个子系统，所有问题均经过行号抽样验证。
>
> **核心原则**：所有优化建议均保证**功能完全不变**——语义、错误处理、日志、边界行为、事件可靠性都不允许改变。本文档只列举"真实可行"的优化点，不包含需要改架构或改功能的建议。

---

## 实施记录（2026-06-15 落地）

以下优化已全部实施并通过测试验证（ApplicationTests 184 通过 / Core 集成 143 通过 / Admin 集成 53 通过，仅剩既有的 ConversationIngestor 5 个失败 + Conversation 周查询 1 个失败，与本次优化无关）。

| 优化项 | 状态 | 说明 |
|--------|------|------|
| P0-1 JsonSerializerOptions 单例化 | ✅ 已实施 | 新增 `JsonSerializerPresets`（Application/Common），替换全部 11 处 `new` |
| P0-2 流式 StringBuilder 容量上限 | ✅ 已实施 | 新增 `ProxyForwardConstants.MaxStreamBodyCaptureChars = 64KB`，ProxyForwardService + 两个控制器 |
| P0-4 UsageLogs 下推 | ✅ 已实施（修正） | 非时间筛选下推 DB，时间过滤/排序/分页在客户端 |
| P0-5 Analytics 下推 | ✅ 已实施（修正） | 同上策略 |
| P1-3 Detection 全表加载 | ✅ 已实施 | 投影为轻量 `LatestFinalLog` + AsNoTracking |
| P1-4 ModelHealth 全表加载 | ✅ 已实施 | AsNoTracking 补全（时间过滤仍客户端） |
| P1-5 AsNoTracking 补全 | ✅ 已实施 | Analytics GetOptions + 各处只读查询 |
| P1-2 索引补充 | ✅ 已实施 | AppDbContext + DatabaseSchemaMigrator 双路径建索引 |
| P0-6 序号批量写盘 | ✅ 已实施 | Next() 纯内存，Timer 每秒落盘，构造函数 Math.Max(meta, spool) 恢复 |
| P1-1 Spool 批量写 | ✅ 已实施 | 新增 AppendBatchAsync，BackgroundService 攒批 64 条单次文件写 |
| P2-4 TraceStore 锁节流 | ✅ 已实施 | PurgeThrottleInterval=30s 节流 |
| P2-5 Gzip GetBuffer | ✅ 已实施 | 避免 ToArray 二次拷贝 |

### 重要修正：SQLite DateTimeOffset 翻译限制

实施 P0-4/P0-5/P1-4 时发现关键事实：**SQLite EF Core provider（8.0.16）不支持 `DateTimeOffset` 类型的 WHERE/ORDER BY 表达式翻译**。原代码注释"避免数据库端翻译失败"是**正确的**，审查时误判为"理由错误"。

因此采用**部分下推策略**：非时间字段（SiteId/AccessKeyId/Status/Source/Model 等）下推到 DB 收窄范围，时间过滤/排序/分页在客户端完成。这仍比原全表加载好（WHERE 已收窄），且功能完全正确。

P1-6（ExecuteDelete）也因此不可用——`ExecuteDeleteAsync` 的 `Where(l => l.RequestedAt < cutoff)` 同样无法翻译。回退到基于 Id 投影的 RemoveRange 路径（先 Select Id+时间字段，客户端过滤，再按 Id 加载删除）。

---

## 优先级总览

| 优先级 | 含义 | 数量 |
|--------|------|------|
| **P0 致命** | 每请求必经热路径上的分配/IO 反模式，电脑配置有限下最敏感 | 6 |
| **P1 高** | 高频路径或随数据量线性恶化的问题 | 7 |
| **P2 中** | 特定场景（失败重试、批量清理、大 body）才触发 | 5 |
| **P3 低** | 微优化，收益小但改动也小 | 3 |

**建议落地顺序**：先做 P0-1/P0-2/P0-3（JsonSerializerOptions 单例化，改动最小、收益最大）→ 再做 P0-4/P0-5（全表加载下推，数量级提升）→ 再做 P0-6 + P1-1（事件写盘批量化）→ 最后 P2/P3。

---

## P0 — 致命级（每请求热路径）

### P0-1. JsonSerializerOptions 在热路径每次 `new`（11 处，最普遍的反模式）

**问题**：`JsonSerializerOptions` 每个实例都会重建内部的 `JsonTypeInfo` 缓存（反射元数据），是 System.Text.Json 最经典的性能反模式。全项目共 11 处 `new JsonSerializerOptions`，其中**3 处在每请求热路径上**。

**最严重的热路径位置**：

| 文件:行号 | 调用链 | 频率 |
|-----------|--------|------|
| `Infrastructure/Proxy/ProxyRequestMetadataCache.cs:949,954` | `GetRouteTargetsForModelAsync`（每请求路由选择）→ 每个候选路由调 `IsAvailableAt`（:1200）→ `NormalizeTimeRangesJson`（:940）→ **每次 new 2 个 Options + 反序列化** | 每请求 × 候选路由数 × 2 |
| `Infrastructure/Proxy/DeveloperInvocationTraceStore.cs:390` | `ProxyCallRecorder.BeginTrace` + `CompleteTraceAttempt`（每请求 2 次）→ `FormatBody` | 每请求 2 次 |

**优化建议**：抽取为 `private static readonly JsonSerializerOptions` 单例字段，功能完全不变。

```csharp
// 改前（每次调用重建）
var ranges = JsonSerializer.Deserialize<List<CachedRouteTimeRange>>(json,
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

// 改后（单例复用）
private static readonly JsonSerializerOptions CaseInsensitiveOptions =
    new() { PropertyNameCaseInsensitive = true };
var ranges = JsonSerializer.Deserialize<List<CachedRouteTimeRange>>(json, CaseInsensitiveOptions);
```

**影响**：消除热路径上的反射元数据重建，CPU 与 GC 双重收益。改动量极小，零功能风险。

**进一步优化**（可选）：`CachedProxyRouteTarget` 在缓存构建时（`NormalizeTimeRangesJson` 的调用点 :641/:682）一次性把 `TimeRangesJson` 解析为 `IReadOnlyList<CachedRouteTimeRange>` 存进缓存对象，`IsAvailableAt` 直接遍历结构体，彻底消除运行时反序列化。

---

### P0-2. 流式响应整流 StringBuilder 累积（大输出 LOH 灾难）

**问题**：流式 SSE 转发的同时，把整流逐行追加进 `StringBuilder`，最后 `ToString()` 生成完整副本字符串。长输出（几万 token = 几 MB）会在堆上反复扩容，进入大对象堆（LOH）。

**位置**：
- `Infrastructure/Proxy/ProxyForwardService.cs:312-323` — `ProcessStreamingResponseAsync` 里 `sb.AppendLine(line)` 累积整个 SSE 流
- `Core/Controllers/Proxy/OpenAiProxyController.Streaming.cs:430,630,760` — `responseBuilder.Append(chunk)` 累积整流
- `Core/Controllers/Proxy/AnthropicProxyController.cs:440` — 同样模式

**为什么累积**：`ResponseBody`/`responseBuilder` 最终值仅用于两个用途：(1) 失败日志诊断 `SafeLogFailedProxyAttempt` → `HttpLogFormatter.FormatBody`（已有截断）；(2) ConversationLog 记录。

**优化建议**：在 `StringBuilder` 上设容量上限（如 64KB），达到上限后停止追加但继续正常转发。日志和对话记录本来就是截断的，语义不变——"截断的响应体"仍是"截断的响应体"。

```csharp
// 概念示例（具体实现需在各调用点）
private const int MaxResponseBodyCapture = 64 * 1024;
if (sb.Length < MaxResponseBodyCapture) sb.AppendLine(line);
// 转发逻辑完全不受影响，始终正常进行
```

**影响**：高并发流式请求时 LOH 堆积 + GC 暂停明显缓解。这是"电脑配置有限"约束下收益最大的优化之一。**注意**：需逐个调用点确认 `ResponseBody` 的下游消费方都能接受截断（已知 FormatBody 截断、ConversationLog 也应截断）。

---

### P0-3. SSE 流逐行 `ReadLineAsync` + 每行 flush

**问题**：用 `StreamReader.ReadLineAsync` 逐行读 SSE 流，每个 SSE 事件块触发一次异步状态机切换 + 一次 lambda 回调 + 一次 `WriteAsync`/`FlushAsync`。一秒上百 token 就是上百次上下文切换和 socket flush。

**位置**：`Infrastructure/Proxy/ProxyForwardService.cs:313-321`

**优化建议**：
- **最小改动**：在 controller 侧把多个 SSE 事件块累积后再 `WriteAsync` 一次（部分分支已这么做，但 `ForwardOpenAiStreamPassthroughAsync` 的 `WriteRawSseBlockAsync` 仍每块 flush，`OpenAiProxyController.Streaming.cs:460`）。可改为多块合并后再 flush，SSE 协议语义不变（客户端按 `\n\n` 分隔解析）。
- **进阶**：改用 `PipeReader`/`ReadOnlySpan<byte>` 按缓冲块扫描 `\n`，减少分配。改动较大。

**影响**：减少高频上下文切换和小包 syscall，高 QPS 流式场景收益明显。

---

### P0-4. UsageLogsApiController.GetList — 全表加载到内存再过滤

**问题**：`ToListAsync()` 把**整个 UsageLog 表**拉到内存，再 `.Where()` 内存过滤、`OrderByDescending` 内存排序、`Skip/Take` 内存分页。这是高频写表，数据量随天数线性增长。

**位置**：`Admin/Controllers/Admin/UsageLogsApiController.cs:447-457`

**现有注释的理由是错的**：注释写"先整体取回再做 DateTimeOffset 相关过滤，避免数据库端翻译失败"——但 **SQLite EF Core 完全能翻译 DateTimeOffset 比较**，这个理由不成立。

**优化建议**：所有过滤下推到 DB 层，分页用 `Queryable.Skip/Take`，count 用独立 `CountAsync`。

```csharp
// 改后（全部下推到 DB）
var query = _dbContext.ProxyUsageLogs
    .AsNoTracking()
    .Where(x => x.RequestedAt >= rangeStart && x.RequestedAt < rangeEnd);
if (query.SiteId.HasValue) query = query.Where(x => x.TargetSiteId == query.SiteId.Value);
// ... 其余筛选条件同理

var totalCount = await query.CountAsync(cancellationToken);
var items = await query
    .OrderByDescending(x => x.RequestedAt)
    .Skip((page - 1) * pageSize).Take(pageSize)
    .Select(x => new AdminUsageLogListItemDto { ... })
    .ToListAsync(cancellationToken);
```

**影响**：UsageLogs 页面打开速度从 O(全表) 降到 O(页大小)。配合 P1-2 的索引补充，效果最佳。**这是数量级提升**。

**注意**：`IsModelMatched`（:455）含 `Contains` 逻辑，确认其能否翻译成 SQL `LIKE`；若不能，该条件保留在内存层（仅对分页后的少量结果过滤）或改写为 `EF.Functions.Like`。

---

### P0-5. AnalyticsApiController.GetDashboard — 全表加载后多次内存聚合

**问题**：`Select(new ProxyUsageLog{...}).ToListAsync()` 把整张 UsageLog 表拉回内存（这个 Select 形同虚设，仍在客户端构造实体），随后 :546-573 全部用内存 LINQ 做 Where/GroupBy/排序/聚合。多个 `Build*Trend` 方法（:637-764）还对同一批 `finalLogs` 反复 `Where().ToList()`，是 N×M 的内存扫描。

**位置**：`Admin/Controllers/Admin/AnalyticsApiController.cs:507-536`（加载），`:546-573`（聚合），`:637-764`（趋势）

**优化建议**：
- 时间范围、ProtocolType、ModelName、AccessKeyId 筛选下推 DB
- 汇总指标（TotalRequests/SuccessRequests/Token 求和/平均耗时）用 `GroupBy().Select(g => new {...})` 让 SQL 聚合
- 趋势分桶可在 DB 用预先计算的桶 key 后 `GroupBy`

**影响**：统计接口是最重的查询，全量加载 + 多次全表扫描，OOM 风险高。电脑配置有限下 Analytics 页面会非常卡。

---

### P0-6. CoreEventSequenceProvider.Next() — 每事件一次磁盘写（写穿 + 临时文件重命名）

**问题**：`Next()` 由每条代理使用日志、对话轮次、熔断/回退事件触发（即每请求至少一次）。每次 `Interlocked.Increment` 后立即 `TryWriteToMetaFile`，做 `File.WriteAllText(tmp)` + `File.Move`（含目录元数据刷盘）。

**位置**：`Infrastructure/CoreRuntime/CoreEventSequenceProvider.cs:73-78`（Next），`:116-130`（写盘）

**优化建议**：保持序号单调语义不变，把"每次写盘"改成"内存 `Interlocked.Increment` + 后台按批/定时（如 1 秒或 N 条）异步落盘"。重启恢复时已有 spool 文件扫描兜底（构造函数 :55-63），可靠性不变——最坏情况仅丢失未落盘的序号偏移，下次启动从 spool 恢复，与现有注释（:13-16）的设计意图一致。

```csharp
// 概念示例
public long Next() => Interlocked.Increment(ref _current);  // 纯内存，零 IO

// 后台定时器（已在运行的进程内）
private async Task FlushLoopAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        TryWriteToMetaFile(Interlocked.Read(ref _current));
    }
}
```

**影响**：消除热路径同步磁盘 IO，高 QPS 下磁盘不再成为瓶颈。**可靠性不变**（spool 文件本身已持久化完整信封）。

---

## P1 — 高优先级

### P1-1. CoreEventSpoolStore.AppendAsync — 每条事件一次 FileStream 打开/关闭

**问题**：对每条事件新建 `FileStream`（Append 模式，不设 FileOptions，无缓冲复用）+ 新建 `StreamWriter` + 一次 `Serialize`。`CoreEventSpoolBackgroundService` 串行 `await` 逐条写入。

**位置**：`Infrastructure/CoreRuntime/CoreEventSpoolStore.cs:38-54`（写），`CoreEventSpoolBackgroundService.cs:62-67`（逐条 await）

**优化建议**：维护一个常驻 `StreamWriter`（按天文件），从通道读取后 `WriteLine` 到缓冲，每 N 条或 T 毫秒 `FlushAsync` 一次。序号顺序由 Channel 单读 + SequenceProvider 保证，ack 语义不变。

**影响**：事件密集时通道积压直至触发 DropOldest（丢事件）。批量化后吞吐提升一个数量级。

---

### P1-2. ProxyUsageLog 缺索引（配合 P0-4/P0-5 的下推才能生效）

**问题**：`AppDbContext.cs:173-186` 仅有 `RequestedAt`、`RequestId` 索引。但 UsageLogsApiController/AnalyticsApiController 按 `Status/Source/TargetSiteId/AccessKeyId/AttemptedModel` 过滤，均无索引。

**位置**：`Infrastructure/Persistence/AppDbContext.cs:173-186`

**优化建议**：补复合索引（EnsureCreated 模式下，新增索引需要重建数据库或配合 Schema 补丁机制）：
- `HasIndex(e => new { e.RequestedAt, e.Status })`
- `HasIndex(e => e.TargetSiteId)`
- `HasIndex(e => e.AccessKeyId)`
- `HasIndex(e => e.AttemptedModel)`

**影响**：在 P0-4/P0-5 下推后，这些索引才真正生效；当前内存过滤下加索引无意义。**必须先做下推再补索引**。

**注意**：EnsureCreated 模式下加索引的部署问题——需确认项目的 Schema 补丁机制（`AdminStartupInitializer`）能否平滑添加索引而不丢数据。

---

### P1-3. Detection/Index.cshtml.cs — 全表加载（仍在，未修复）

**问题**：`ProxyUsageLogs.Where(x => x.IsFinalResult).ToListAsync()` 把所有"最终结果"日志拉回内存，再 `GroupBy((TargetSiteId, RequestModel))` 取每组最新一条。

**位置**：`Admin/Pages/Admin/Detection/Index.cshtml.cs:110-117`

**优化建议**：每组最新一条可下推（SQLite 支持 `GroupBy(key).Select(g => g.OrderByDescending(x => x.RequestedAt).First())`），或按 `(TargetSiteId, RequestModel, RequestedAt)` 建复合索引 + 只取所需列。

**影响**：表越大，每次开检测页越卡。

---

### P1-4. ModelHealth/Index.cshtml.cs — 无条件全表加载

**问题**：`ProxyUsageLogs.ToListAsync()` **无条件全表加载**，再内存过滤。注释写"避免 SQLite 无法翻译 DateTimeOffset 比较"——这个理由是错的。

**位置**：`Admin/Pages/Admin/ModelHealth/Index.cshtml.cs:335-339`，`:367-380` 还对 `allLogs` 做多轮内存过滤

**优化建议**：`Where(x => x.RequestedAt >= recentCutoff)` 直接下推 DB（命中 `RequestedAt` 索引）。

**影响**：随日志增长内存与首屏时间线性恶化。

---

### P1-5. 只读查询普遍缺 AsNoTracking

**问题**：多个只读查询未加 `AsNoTracking`，每行实体进 ChangeTracker，建身份字典、跟踪快照。对全表加载场景放大数倍开销。

**位置**（部分）：
- `Admin/Pages/Admin/Detection/Index.cshtml.cs:110,119,121,124`
- `Admin/Pages/Admin/ModelHealth/Index.cshtml.cs:277,280,324,329,335,341`
- `Admin/Controllers/Admin/AnalyticsApiController.cs:451`
- `Admin/Controllers/Admin/UsageLogsApiController.cs:434-442,547-555`（Sites/RouteRules/AccessKeys 三表每次列表全量读）

**优化建议**：所有只读查询统一加 `AsNoTracking()`。**注意**：ModelHealth 的 `:289-290` 有 `RemoveRange + SaveChanges`，那段需保留 tracking。

**影响**：减少 ChangeTracker 开销，配合全表加载修复效果加倍。

---

### P1-6. LogRetentionService 二次查询浪费 + 未用 ExecuteDelete

**问题**：先 `Select(l => l.Id).ToListAsync()` 查 Id，再 `Where(Id 含于列表).ToListAsync()` 把**整行（含大文本）**加载回来只为 `RemoveRange`。两次往返 + 全行加载。

**位置**：`Infrastructure/Retention/LogRetentionService.cs:73-115`

**优化建议**：EF Core 7+ 可直接 `ExecuteDeleteAsync`，单条 `DELETE` 语句、零实体加载、零 ChangeTracker：

```csharp
await _dbContext.ProxyUsageLogs
    .Where(x => x.RequestedAt < usageCutoff)
    .ExecuteDeleteAsync(cancellationToken);
```

**影响**：清理任务（每天凌晨 3 点）内存峰值大幅降低、耗时缩短。

**注意**：项目注释提到"InMemory provider（测试用）不支持 ExecuteDeleteAsync"——需确认是否真有测试依赖 InMemory + 清理逻辑。若有，生产用 ExecuteDelete、测试保留旧路径（通过 `#if` 或抽象）。

---

### P1-7. FileConversationLogStore — 全局互斥锁串行化读写

**问题**：全局 `SemaphoreSlim _storageLock`（:20）使读写、读读都串行。高频代理流量产生的对话写入会和这些管理查询互斥。`DeleteSessionAsync`（:153-172）和 `UpdateSessionTitleAsync`（:199-223）遍历所有分片文件、每个文件全量读、改后整文件重写。

**位置**：`Infrastructure/Conversations/FileConversationLogStore.cs:20`（锁），`:67-134`（QueryAsync），`:153-172`（Delete），`:199-223`（UpdateTitle）

**优化建议**：锁改为 `ReaderWriterLockSlim`，让多个只读 QueryAsync 并发，仅写互斥。功能完全不变。

**影响**：管理页查会话列表不再阻塞代理对话写入。

---

## P2 — 中优先级

### P2-1. 协议桥接失败诊断路径对大 body 反复 Split + JsonDocument.Parse

**问题**：流式响应失败需日志诊断时，这些函数对 `responseBody`（可能几 MB）执行 `Split('\n')` 生成字符串数组，再对每个 data 行 `JsonDocument.Parse`。一个流被解析多次。

**位置**：
- `Infrastructure/ProxyProtocol/ProxyProtocolBridge.Helpers.cs:957`（`ExtractOpenAiStreamingText`）
- `:1013`（`ExtractAnthropicStreamingText`）
- `ResponseConvert.cs:843,927`（`ExtractOpenAiStreamingMetadata`/`ExtractAnthropicStreamingMetadata`）

**优化建议**：合并文本/元数据提取为单次遍历；`Split` 改为按行 span 扫描。

**影响**：失败重试场景（该网关常态）下大 body 被多次全量解析。成功路径不受影响。

---

### P2-2. Responses→OpenAI→Anthropic 桥接的双重序列化往返

**问题**：`ConvertResponsesStreamingToChat` 返回的 SSE 字符串又被 `new StringReader` + `ReadLine()` 拆回行，再重新解析——刚拼好的字符串立刻拆掉。

**位置**：`Core/Controllers/Proxy/AnthropicProxyController.cs:630-658`

**优化建议**：让 `ConvertResponsesStreamingToChat` 直接返回结构化 chunk 列表，跳过中间 SSE 文本往返。功能等价。

**影响**：仅 Responses 协议桥接路径触发。每事件块多次字符串分配。

---

### P2-3. CoreEventSpoolStore.TrimAckedAsync/ListAfterAsync — 全文件全行读取后过滤

**问题**：每次 ack 或 replay 都把所有 spool 文件**全部读入内存**（`ReadAllAsync` 返回完整 `List`），再 `Where(SequenceId > x)`。

**位置**：`Infrastructure/CoreRuntime/CoreEventSpoolStore.cs:88-128`（TrimAcked），`:134-153`（ListAfter）

**优化建议**：`ListAfterAsync` 改为逐行流式读 + 命中即收（序号单调递增，可提前 break 或跳过已知旧文件）。ack 重写仍需全量，但可在文件级缓存"已知最大序号"避免重复读已 fully-acked 的文件。语义不变。

**影响**：Admin 每次 pull 后 ack 都触发一次全量读。积压越多越慢。

---

### P2-4. DeveloperInvocationTraceStore 热路径锁内无条件清理

**问题**：`AddRequest`/`AddAttempt`/`CompleteAttempt`/`CancelPending`/`Get`/`List` 每次进入 `lock(_gate)` 都无条件调用 `PurgeExpiredUnsafe()`。

**位置**：`Infrastructure/Proxy/DeveloperInvocationTraceStore.cs:68-75,83-86,121-124,198-200,243-245`

**优化建议**：用时间戳节流，上次清理超过 30 秒才执行一次 `PurgeExpiredUnsafe()`。功能不变。

**影响**：并发代理请求时减少锁内冗余工作。

---

### P2-5. GzipTextCompression 用 MemoryStream.ToArray() 全量拷贝

**问题**：`new MemoryStream()` + `output.ToArray()` 会做一次完整缓冲区拷贝；大文本（>512B 阈值意味着常有大对话体）双倍内存峰值。

**位置**：`Infrastructure/Conversations/GzipTextCompression.cs:40-46`

**优化建议**：用 `RecyclableMemoryStream`，或最小改动返回 `output.GetBuffer().AsMemory(0,(int)output.Length)` 配合 `ToBase64String(ReadOnlySpan<byte>)`。功能不变。

**影响**：大对话落库时临时大对象分配翻倍，增加 GC 压力。

---

## P3 — 低优先级（微优化）

### P3-1. ModelConcurrencyLimiter.BuildKey 每次插值字符串

**位置**：`Infrastructure/Proxy/ModelConcurrencyLimiter.cs:529-532`

**问题**：每次 `AcquireAsync`（热路径）都 `$"{siteId:N}:{remoteModelName}"` 拼字符串作为 `ConcurrentDictionary` 键。

**建议**：可用 `(Guid, string)` 复合键 + 自定义 `IEqualityComparer`，消除插值分配。影响小。

---

### P3-2. ProxyRequestMetadataCache 缓存键每次 new 字符串

**位置**：`Infrastructure/Proxy/ProxyRequestMetadataCache.cs:606,652`

**问题**：`RouteTargetsCacheKeyPrefix + "all"` 每次产生新字符串实例。

**建议**：改为 `const` 全键或 `static readonly` 字段。影响极小（IMemoryCache 内部用 hash 比较）。

---

### P3-3. ModifyRequestBody 用 JsonSerializer 逐属性往返（死代码兜底）

**位置**：`Infrastructure/Proxy/ProxyForwardService.cs:508-532`

**问题**：`ModifyRequestBody` 用 `JsonDocument.Parse` 读出每个属性，对每个非 model 字段执行 `JsonSerializer.Deserialize<object>(prop.Value.GetRawText())` 再整体序列化。N 次反射序列化。**该路径只在 `PreparedRequestBody` 为空时触发，但控制器已总是预生成 `PreparedRequestBody`，所以实际是死代码兜底——仍是隐患。**

**建议**：与 `ProxyProtocolBridge.ReplaceModelName`（Helpers.cs:829-846）一样，直接用 `JsonNode.Parse` → 改 `model` → `ToJsonString()`，单次往返。功能等价。

---

## 已确认良好的部分（无需改动）

为避免误改，列出审查中确认设计正确的部分：

- **HttpClient 复用**：通过 DI `AddHttpClient<IProxyForwardService, ProxyForwardService>` 注入，单例复用正确，`Timeout = InfiniteTimeSpan` 让 CancellationToken 统一控制超时（`ProxyForwardService.cs:32`）。
- **HttpCompletionOption.ResponseHeadersRead**：流式与非流式都用，不缓冲整个响应（`ProxyForwardService.cs:60,207`）。
- **CancellationToken 链接**：`CreateLinkedTokenSource` 正确链接客户端取消与超时（`:51,198`）。
- **JsonDocument Dispose**：热路径上 `using var doc = JsonDocument.Parse(...)` 都正确释放。
- **Channel 背压**：`CoreAdminEventBus` 主通道有界 10000（DropOldest），SSE 子通道有界 64，`ProxyUsageLogBatchWriter`/`ConversationLogBatchWriter` 有界 4096（DropWrite）。无无限增长风险。
- **SSE 订阅无泄漏**：`CoreAdminEventBus` 用 `WeakReference<SseSubscription>` + 清理死引用。
- **批量写入器**：Channel + 后台聚合 + AddRange + 单次 SaveChanges，独立 scope 避免与请求 DbContext 共用连接。无 N+1、无逐条 SaveChanges。
- **ConversationLogService**：5 秒 TTL 缓存读设置，未每次建 DB scope。
- **ConversationTurnLog 索引**：CreatedAt、RequestId、ConversationGroupKey、(SourceTool,SessionId,CreatedAt) 均已建索引。
- **Regex 缓存**：`ConversationExtractionService` 已用 `static readonly Regex` 编译缓存。
- **无 GC.Collect / .Wait() / 滥用 .Result**：全项目无手动 GC，无同步阻塞异步（仅 1 处 `GetAwaiter().GetResult()` 在构造函数冷启动恢复路径，见 P2 注释）。
- **环形缓冲实现**：LinkedList + Dictionary 索引、事件在锁外触发、TrimUnsafe 正确。

---

## 落地策略建议

### 第一阶段：零风险快速收益（1-2 小时，改动极小）
1. **P0-1** JsonSerializerOptions 单例化（3 个热路径位置）
2. **P0-2** 流式 StringBuilder 设容量上限
3. **P1-5** 只读查询补 AsNoTracking

这三项改动量极小、零功能风险、立即生效。

### 第二阶段：数据库下推（半天，需测试验证）
4. **P0-4** UsageLogsApiController.GetList 下推
5. **P0-5** AnalyticsApiController.GetDashboard 下推
6. **P1-3/P1-4** Detection/ModelHealth 下推
7. **P1-2** 补索引（确认 Schema 补丁机制后）
8. **P1-6** LogRetentionService 改 ExecuteDelete

这是数量级提升，但需逐个验证查询翻译正确性（特别是 DateTimeOffset 比较和 Contains）。

### 第三阶段：事件链路批量化（半天）
9. **P0-6** 序号写盘改批量
10. **P1-1** Spool 单条写改批量

需仔细验证 ack 语义和重启恢复路径。

### 第四阶段：专项优化（按需）
11. P0-3 SSE 批量 flush
12. P1-7 读写锁
13. P2-* 各项

---

## 验证方法

每个优化落地后，建议用以下方式验证功能不变 + 性能提升：
- **功能不变**：跑全量测试（`ApplicationTests` + Admin/Core 集成测试），195+ 测试应全绿。
- **性能对比**：对 UsageLogs/Analytics 页面、代理流式请求，用相同数据集对比优化前后的响应时间和内存峰值。可用 `/debug/runtime`（Core）观察内存态。
- **可靠性**（事件链路）：模拟 Admin 离线 → Core 持续产生事件 → Admin 恢复，验证无丢事件。

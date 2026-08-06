# Analytics 统计维度扩展实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在保留现有 Analytics 核心看板和统计口径的基础上，一次性增加来源、Access Key、协议、失败原因、HTTP 状态码、回退链路、延迟分位数和联动筛选能力，不增加导出功能。

**Architecture:** 在使用日志写入链路补齐结构化 HTTP 状态码和错误分类，在 Analytics 后端以同一批按 RequestId 归并的请求结果构建所有新旧统计，在前端增加统一细分分析 Tab 和稳定 key 驱动的筛选联动。继续复用现有后台统计队列、缓存和页面布局，不新增独立导出链路，不恢复旧 Razor 页面。

**Tech Stack:** ASP.NET Core/.NET 8、SqlSugar、Vue 3 Composition API、TypeScript、Naive UI、ECharts、Vitest、xUnit/现有集成测试基础设施。

**设计依据:** `docs/superpowers/specs/2026-08-04-analytics-dimensions-design.md`

---

## 文件结构与职责

### 后端

- `src/AITool.Application/UsageLogs/IUsageLogService.cs`：扩展日志写入 DTO，增加可空 HTTP 状态码和错误分类。
- `src/AITool.Application/UsageLogs/UsageLogErrorClassifier.cs`：新增纯函数式错误分类器，统一新日志和历史日志的分类规则。
- `src/AITool.Domain/Proxy/ProxyUsageLog.cs`：增加持久化字段和来源索引。
- `src/AITool.Infrastructure/Proxy/ProxyUsageLogBatchWriter.cs`：将新增字段写入实体，并在字段缺失时补充分类。
- 代理、Chat、检测入口：把已有的上游状态码传入 `UsageLogEntry`。
- `src/AITool.Web/Controllers/Admin/AnalyticsApiController.cs`：扩展查询参数、响应 DTO、RequestId 归并、细分聚合、分位数和回退链路。
- `tests/AITool.ApplicationTests/UsageLogs/UsageLogErrorClassifierTests.cs`：错误分类和兼容输入测试。
- `tests/AITool.ApplicationTests/Proxy/UsageLogServiceTests.cs`：新增字段的 Entry 到实体映射测试。
- `tests/AITool.IntegrationTests/Analytics/AnalyticsApiTests.cs`：Dashboard、筛选、去重、分位数和回退链路集成测试。

### 前端

- `frontend/src/views/usageSource.ts`：新增共享来源枚举和中文映射，避免 Usage Logs 与 Analytics 分叉。
- `frontend/src/views/UsageLogsView.vue`：改用共享来源映射，不改变现有显示和筛选行为。
- `frontend/src/api/analytics.ts`：增加查询参数、细分 DTO、分位数 DTO、回退链路 DTO。
- `frontend/src/views/analyticsState.ts`：增加维度筛选切换、筛选标签、表格排序等纯逻辑。
- `frontend/src/views/analyticsState.test.ts`：覆盖上述纯逻辑。
- `frontend/src/views/AnalyticsView.vue`：增加来源筛选、细分分析 Tab、联动筛选和延迟/回退展示。
- `frontend/src/api/analytics.test.ts`：扩展新参数、响应和轮询/错误状态测试。

---

## Task 1: 建立结构化日志字段和错误分类器

**Files:**
- Create: `src/AITool.Application/UsageLogs/UsageLogErrorClassifier.cs`
- Modify: `src/AITool.Application/UsageLogs/IUsageLogService.cs:17-138`
- Modify: `src/AITool.Domain/Proxy/ProxyUsageLog.cs:8-154`
- Test: `tests/AITool.ApplicationTests/UsageLogs/UsageLogErrorClassifierTests.cs`
- Test: `tests/AITool.ApplicationTests/Proxy/UsageLogServiceTests.cs`

- [ ] **Step 1: Write failing classifier tests**

新增测试应覆盖以下稳定规则：

```csharp
[Theory]
[InlineData(0, true, "stream-interrupted")]
[InlineData(408, false, "timeout")]
[InlineData(401, false, "authentication")]
[InlineData(429, false, "rate-limit")]
[InlineData(404, false, "model-not-found")]
[InlineData(502, false, "upstream-error")]
public void Classify_UsesStructuredSignals(int statusCode, bool interrupted, string expected)
{
    var actual = UsageLogErrorClassifier.Classify(
        statusCode,
        interrupted,
        "上游请求失败",
        "fail");

    Assert.Equal(expected, actual);
}
```

另加：

- 状态码为 0 且错误文本包含 timeout 时返回 `timeout`。
- 文本包含认证关键字时返回 `authentication`。
- 无法识别时返回 `other`。
- 成功请求的 `ErrorCategory` 返回 `null`；失败请求无法识别时返回 `other`，并分别固定测试。
- 分类优先级中流式中断高于其他文本分类。

- [ ] **Step 2: 运行测试确认红灯**

Run:

```powershell
dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter FullyQualifiedName~UsageLogErrorClassifierTests
```

Expected: FAIL，因为 `UsageLogErrorClassifier` 和新增字段尚不存在。

- [ ] **Step 3: 增加日志字段和分类器最小实现**

在 `UsageLogEntry` 增加：

```csharp
public int? HttpStatusCode { get; set; }
public string? ErrorCategory { get; set; }
```

在 `ProxyUsageLog` 增加对应可空字段，避免旧 SQLite 数据库补非空列失败：

```csharp
public int? HttpStatusCode { get; set; }

[SugarColumn(Length = 50, IsNullable = true)]
public string? ErrorCategory { get; set; }
```

新增 `UsageLogErrorClassifier.Classify(int? statusCode, bool isStreamInterrupted, string? errorMessage, string? status)`，按设计中的固定优先级返回稳定代码。分类器只读取状态码、流中断标志、状态和错误文本，不记录或返回错误正文。

在 `ProxyUsageLog` 现有索引后增加 `Source` 索引；错误分类和状态码只在实际加入查询筛选后再增加索引，避免无必要的写入开销。

新增中文注释只说明本次新增字段和分类器的关键目的，不修改旧注释。

- [ ] **Step 4: 增加 Entry 到实体映射的失败测试**

在现有 `UsageLogServiceTests` 增加断言：入队的 Entry 包含 `HttpStatusCode = 429`、`ErrorCategory = "rate-limit"` 时，批量写入的 `ProxyUsageLog` 保留这两个值；Entry 未提供分类时，写入层可通过分类器补齐。

- [ ] **Step 5: 运行测试确认映射测试仍红灯**

Run:

```powershell
dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter FullyQualifiedName~UsageLogServiceTests
```

Expected: 新增映射断言先失败，直到批量写入器完成映射。

- [ ] **Step 6: 运行 Task 1 测试确认通过**

Run:

```powershell
dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter "FullyQualifiedName~UsageLogErrorClassifierTests|FullyQualifiedName~UsageLogServiceTests"
```

Expected: PASS。

---

## Task 2: 把已有上游 HTTP 状态码写入使用日志

**Files:**
- Modify: `src/AITool.Infrastructure/Proxy/ProxyUsageLogBatchWriter.cs:176-203`
- Modify: `src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs` 的 3 个日志构造块
- Modify: `src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs` 的 2 个日志构造块
- Modify: `src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs` 的 2 个日志构造块
- Modify: `src/AITool.Web/Controllers/Admin/ChatApiController.cs` 的 4 个日志构造块
- Modify: `src/AITool.Infrastructure/Health/ModelHealthRequestService.cs:123` 的日志构造块
- Test: `tests/AITool.IntegrationTests/Proxy/ResponsesProxyTests.cs`
- Test: `tests/AITool.IntegrationTests/Proxy/AnthropicProxyControllerTests.cs`
- Test: `tests/AITool.IntegrationTests/Proxy/ProxyFallbackFlowTests.cs`

- [ ] **Step 1: 为状态码落库写回归测试**

在现有代理测试使用可控的上游 429/502 响应，验证最终 UsageLog 的 `HttpStatusCode` 与上游结果一致；在超时或网络异常测试中验证状态码为空，而不是伪造 502。

测试必须只断言状态码、状态和错误分类，不输出真实密钥、Token 或请求正文。

- [ ] **Step 2: 运行测试确认红灯**

Run:

```powershell
dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter "FullyQualifiedName~ResponsesProxyTests|FullyQualifiedName~AnthropicProxyControllerTests|FullyQualifiedName~ProxyFallbackFlowTests"
```

Expected: 新增状态码断言失败，因为各日志构造入口尚未赋值且批量写入器尚未映射。

- [ ] **Step 3: 完成批量写入器映射**

在 `ProxyUsageLogBatchWriter` 的 Entry → Entity 映射中写入：

```csharp
HttpStatusCode = entry.HttpStatusCode,
ErrorCategory = string.IsNullOrWhiteSpace(entry.ErrorCategory)
    ? UsageLogErrorClassifier.Classify(entry.HttpStatusCode, entry.IsStreamInterrupted, entry.ErrorMessage, entry.Status)
    : entry.ErrorCategory,
```

保持现有 `TotalTokens` 计算和批量写入行为不变。

- [ ] **Step 4: 补齐代理和 Chat 状态码传递**

各入口从已有结果对象取状态码：

- `ProxyForwardResult.StatusCode` 传给 OpenAI、Responses 和 Anthropic 的日志 Entry。
- Chat 结果对象已有 `StatusCode`，传给 Chat 日志 Entry。
- 检测服务如果只有异常而没有 HTTP 响应，保持 `null`。
- 不把内部异常映射成虚假的 HTTP 状态码。

每个入口保留现有 Source、Status、流中断和回退逻辑，只增加状态码字段。

- [ ] **Step 5: 运行代理回归测试确认通过**

Run:

```powershell
dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter "FullyQualifiedName~ResponsesProxyTests|FullyQualifiedName~AnthropicProxyControllerTests|FullyQualifiedName~ProxyFallbackFlowTests"
```

Expected: PASS。

- [ ] **Step 6: 运行完整应用层和代理测试**

Run:

```powershell
dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj
dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter FullyQualifiedName~Proxy
```

Expected: PASS；只允许出现既有 XML 文档注释警告，不新增编译错误。

---

## Task 3: 抽取前端来源映射并增加 Analytics 查询来源

**Files:**
- Create: `frontend/src/views/usageSource.ts`
- Modify: `frontend/src/views/UsageLogsView.vue:60-70,149-168`
- Modify: `frontend/src/api/analytics.ts:64-92`
- Modify: `frontend/src/views/AnalyticsView.vue:21-130`
- Modify: `src/AITool.Web/Controllers/Admin/AnalyticsApiController.cs:10-44,156-190,1044-1054`
- Test: `frontend/src/views/analyticsState.test.ts`
- Test: `frontend/src/api/analytics.test.ts`

- [ ] **Step 1: 写来源映射和参数构造的失败测试**

测试应先声明期望行为：

```ts
it('来源筛选使用与 Usage Logs 相同的稳定值', () => {
  expect(getUsageSourceLabel('claude-code')).toBe('Claude Code')
  expect(getUsageSourceLabel('detection-task')).toBe('定时检测')
})

it('Analytics 请求参数包含来源筛选', () => {
  expect(buildAnalyticsQuery({ source: 'codex' })).toMatchObject({ source: 'codex' })
})
```

如果当前 `buildParams` 是 SFC 内部函数，则先把纯参数构造抽到 `analyticsState.ts`，让测试不依赖组件挂载。

- [ ] **Step 2: 运行前端测试确认红灯**

Run:

```powershell
cd frontend
npm run test -- src/views/analyticsState.test.ts src/api/analytics.test.ts
```

Expected: FAIL，因为共享来源模块、来源状态和 query 参数尚不存在。

- [ ] **Step 3: 抽取共享来源模块**

在 `usageSource.ts` 中定义现有 8 个来源的 options 和 `getUsageSourceLabel`，未知值保留原值或显示 `-`，不要把未知来源静默改成已知来源。

Usage Logs 改为导入该模块，保持现有标签、颜色和筛选值完全不变。

- [ ] **Step 4: 增加 Analytics source 查询字段**

后端 `AnalyticsQueryDto` 和 `AnalyticsAppliedFilterDto` 增加可选 `Source`。数据库查询在时间范围后增加来源过滤；缓存键增加来源值。

前端 Analytics 增加 `source` 状态，并在 query 构造和 applied filter 映射中保留该字段。

- [ ] **Step 5: 运行前端测试确认通过**

Run:

```powershell
cd frontend
npm run test -- src/views/analyticsState.test.ts src/api/analytics.test.ts
```

Expected: PASS。

---

## Task 4: 重构 Analytics 请求级归并并建立后端测试夹具

**Files:**
- Modify: `src/AITool.Web/Controllers/Admin/AnalyticsApiController.cs:546-638`
- Create: `tests/AITool.IntegrationTests/Analytics/AnalyticsApiTests.cs`
- Test: `tests/AITool.IntegrationTests/Auth/AdminAuthTests.cs`

- [ ] **Step 1: 写 RequestId 去重和 fallback 口径测试**

建立至少两条请求：

- 请求 A：第一次失败、第二次成功，两个不同站点，最终结果成功。
- 请求 B：只有一次失败，最终结果失败。

断言：

- 请求总数为 2，而不是 3。
- 成功数为 1，失败数为 1。
- 回退请求数为 1。
- 回退后成功数为 1。
- 失败统计只包含请求 B。
- 回退链路保留请求 A 的首次站点和最终站点。

同时增加筛选测试，明确请求级口径：先按最终结果匹配站点、模型、Access Key、协议和来源确定请求集合，再恢复这些请求的完整尝试链路供回退分析；这样不会因为过滤掉中间尝试而丢失回退信息。

- [ ] **Step 2: 运行测试确认红灯或暴露现有口径**

Run:

```powershell
dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter FullyQualifiedName~AnalyticsApiTests
```

Expected: 新测试先因测试类/夹具未实现而失败；如果已有行为与新口径不一致，记录具体失败断言后再修改实现。

- [ ] **Step 3: 抽取请求级归并辅助函数**

在 Analytics 控制器内部或相邻专用文件中增加可测试的归并逻辑：

- 从时间范围内日志确定每个 RequestId 的最终记录，优先 `IsFinalResult`，再按 `AttemptIndex` 和 `RequestedAt` 兜底。
- 用最终记录应用请求级筛选。
- 对匹配的 RequestId 从时间范围日志恢复完整链路。
- 由最终记录生成核心趋势和维度统计，由完整链路生成回退统计。

保留现有时间分桶、DateTimeOffset UTC 修正和后台队列行为。

- [ ] **Step 4: 修正缓存键和应用筛选快照**

缓存键加入 `Source`，并确保 AppliedFilter 返回规范化后的来源值。现有筛选字段的默认值和大小写行为保持兼容。

- [ ] **Step 5: 运行 Analytics 集成测试确认通过**

Run:

```powershell
dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter FullyQualifiedName~AnalyticsApiTests
```

Expected: PASS。

- [ ] **Step 6: 运行未认证回归测试**

Run:

```powershell
dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter FullyQualifiedName~AdminAuthTests
```

Expected: Analytics 未认证请求仍返回 401。

---

## Task 5: 增加通用细分聚合 DTO 和来源/Access Key/协议分析

**Files:**
- Modify: `src/AITool.Web/Controllers/Admin/AnalyticsApiController.cs:109-150,354-421,616-638`
- Test: `tests/AITool.IntegrationTests/Analytics/AnalyticsApiTests.cs`

- [ ] **Step 1: 写三类维度聚合失败断言**

在 Analytics 集成测试中加入：

- 来源 `codex` 只聚合对应请求。
- Access Key 按 ID 聚合，显示名称来自 Access Key 关联数据，不显示密钥正文。
- 协议按原始 ProtocolType 聚合。
- 每个维度项同时返回稳定 `key` 和展示 `label`。
- 请求数、成功数、失败数、成功率、Token、平均耗时和回退数使用统一最终请求口径。

- [ ] **Step 2: 运行测试确认红灯**

Run:

```powershell
dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter FullyQualifiedName~AnalyticsApiTests
```

Expected: FAIL，因为新增 breakdown DTO 和响应字段尚不存在。

- [ ] **Step 3: 增加 DTO 和统一聚合函数**

新增通用维度点 DTO，至少包含：

```csharp
public sealed class AnalyticsBreakdownPointDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int RequestCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public double SuccessRate { get; set; }
    public long TotalTokens { get; set; }
    public double AverageTotalDurationMs { get; set; }
    public int FallbackRequestCount { get; set; }
}
```

在 Dashboard 响应加入来源、Access Key 和协议列表。统一聚合函数接收最终日志、回退 RequestId 集合和 key/label 选择器，避免为每个维度复制统计逻辑。

Access Key 只查询名称/标签，严禁读取或返回原始密钥。

- [ ] **Step 4: 保留现有站点/模型 DTO 兼容性**

不要改变现有站点和模型图表字段的含义。为现有分布点增加稳定的 `Key` 字段：站点使用 SiteId，模型使用 AttemptedModel；保留 `Label`、请求数和现有指标，以便前端点击时不通过展示文字反查标识。

- [ ] **Step 5: 运行测试确认通过**

Run:

```powershell
dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter FullyQualifiedName~AnalyticsApiTests
```

Expected: PASS。

---

## Task 6: 增加错误分类、HTTP 状态码和回退链路分析

**Files:**
- Modify: `src/AITool.Web/Controllers/Admin/AnalyticsApiController.cs:109-150,616-638,760-860`
- Test: `tests/AITool.ApplicationTests/UsageLogs/UsageLogErrorClassifierTests.cs`
- Test: `tests/AITool.IntegrationTests/Analytics/AnalyticsApiTests.cs`

- [ ] **Step 1: 写失败分类和状态码分析测试**

测试数据至少包含：

- 401 认证失败。
- 429 限流。
- 502 上游错误。
- 408 或 timeout 文本超时。
- 流式中断。
- 无法识别的失败。

断言每个分类只出现一个稳定 key，状态码分布按最终失败请求统计，分类统计不返回 `ErrorMessage`。

- [ ] **Step 2: 运行测试确认红灯**

Run:

```powershell
dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter FullyQualifiedName~UsageLogErrorClassifierTests
dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter FullyQualifiedName~AnalyticsApiTests
```

Expected: 新增响应字段和聚合函数尚不存在，测试失败。

- [ ] **Step 3: 增加失败原因和状态码 DTO**

复用 `AnalyticsBreakdownPointDto` 的通用指标结构；失败原因的 `Key` 使用分类代码，状态码的 `Key` 使用字符串状态码，状态码为空的网络/超时异常使用稳定的 `no-response`。

历史日志在聚合时调用同一分类器，避免旧数据全部显示为空。

- [ ] **Step 4: 增加回退链路 DTO 和聚合**

新增回退链路 DTO，至少包含：

```csharp
public sealed class AnalyticsFallbackChainPointDto
{
    public string FirstSiteKey { get; set; } = string.Empty;
    public string FirstSiteLabel { get; set; } = string.Empty;
    public string FinalSiteKey { get; set; } = string.Empty;
    public string FinalSiteLabel { get; set; } = string.Empty;
    public int RequestCount { get; set; }
    public int SuccessCount { get; set; }
    public double SuccessRate { get; set; }
    public double AverageAttemptCount { get; set; }
}
```

仅返回发生过 fallback 的链路，按请求数降序，可设置明确的 Top 20 上限；在代码注释和接口说明中明确该上限，不能静默假装返回全量。

- [ ] **Step 5: 运行后端测试确认通过**

Run:

```powershell
dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter FullyQualifiedName~UsageLogErrorClassifierTests
dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter FullyQualifiedName~AnalyticsApiTests
```

Expected: PASS。

---

## Task 7: 增加延迟分位数计算

**Files:**
- Create: `src/AITool.Application/UsageLogs/PercentileCalculator.cs`
- Test: `tests/AITool.ApplicationTests/UsageLogs/PercentileCalculatorTests.cs`
- Modify: `src/AITool.Web/Controllers/Admin/AnalyticsApiController.cs:109-150,616-638`
- Test: `tests/AITool.IntegrationTests/Analytics/AnalyticsApiTests.cs`

- [ ] **Step 1: 写百分位计算失败测试**

```csharp
[Fact]
public void Calculate_UsesNearestRankForP50P95P99()
{
    var values = new[] { 100d, 200d, 300d, 400d, 500d };

    var actual = PercentileCalculator.Calculate(values);

    Assert.Equal(300d, actual.P50);
    Assert.Equal(500d, actual.P95);
    Assert.Equal(500d, actual.P99);
    Assert.Equal(5, actual.SampleCount);
}
```

另加空集合、单元素和无效/负数处理测试。实现必须固定一种算法，不能让不同图表各自计算。

- [ ] **Step 2: 运行测试确认红灯**

Run:

```powershell
dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter FullyQualifiedName~PercentileCalculatorTests
```

Expected: FAIL，因为计算器不存在。

- [ ] **Step 3: 实现最小百分位计算器**

新增纯函数计算器：

- 过滤非有限值和负数。
- 按升序排序。
- 使用 nearest-rank 规则计算 P50/P95/P99。
- 返回样本数。
- 空集合返回 0 和样本数 0。

- [ ] **Step 4: 加入 Dashboard 延迟统计**

新增总耗时和首字延迟的百分位 DTO，并在同一批最终日志上计算。保留现有平均耗时字段，不修改旧图表的字段名称。

- [ ] **Step 5: 运行测试确认通过**

Run:

```powershell
dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter FullyQualifiedName~PercentileCalculatorTests
dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter FullyQualifiedName~AnalyticsApiTests
```

Expected: PASS。

---

## Task 8: 增加前端类型、细分 Tab 和联动纯逻辑

**Files:**
- Modify: `frontend/src/api/analytics.ts:3-132`
- Modify: `frontend/src/views/analyticsState.ts:1-32`
- Modify: `frontend/src/views/analyticsState.test.ts`
- Modify: `frontend/src/api/analytics.test.ts`

- [ ] **Step 1: 写前端联动失败测试**

覆盖以下纯逻辑：

```ts
it('点击同一维度项目时切换筛选', () => {
  expect(toggleDimensionFilter({ source: 'codex' }, 'source', 'codex')).toEqual({})
  expect(toggleDimensionFilter({}, 'source', 'codex')).toEqual({ source: 'codex' })
})

it('点击同一维度其他项目时替换筛选', () => {
  expect(toggleDimensionFilter({ source: 'chat' }, 'source', 'codex')).toEqual({ source: 'codex' })
})

it('删除筛选标签不会影响其他维度', () => {
  expect(removeAnalyticsFilter({ source: 'codex', protocolType: 'openai' }, 'source'))
    .toEqual({ protocolType: 'openai' })
})
```

另加响应映射测试，确保新增 breakdown 字段缺失时按空数组兼容，不影响旧 API 响应。

- [ ] **Step 2: 运行测试确认红灯**

Run:

```powershell
cd frontend
npm run test -- src/views/analyticsState.test.ts src/api/analytics.test.ts
```

Expected: FAIL，因为新纯函数和 DTO 尚不存在。

- [ ] **Step 3: 增加前端 DTO 和纯逻辑**

在 `analytics.ts` 增加：

- `AnalyticsBreakdownPoint`
- `AnalyticsFallbackChainPoint`
- `AnalyticsLatencyPercentiles`
- `AnalyticsAnalysisDimension`
- Dashboard 新增 breakdown 字段
- AppliedFilter 新增 `source`

在 `analyticsState.ts` 增加：

- `toggleDimensionFilter`
- `removeAnalyticsFilter`
- `resetAnalyticsFilters`
- `sortAnalyticsBreakdown`

这些函数只处理数据，不依赖 Vue、ECharts 或 Naive UI。

- [ ] **Step 4: 运行测试确认通过**

Run:

```powershell
cd frontend
npm run test -- src/views/analyticsState.test.ts src/api/analytics.test.ts
```

Expected: PASS。

---

## Task 9: 在 Analytics 页面接入细分区域和联动

**Files:**
- Modify: `frontend/src/views/AnalyticsView.vue:21-130,245-435,441-638`
- Modify: `frontend/src/views/analyticsFormat.ts`（仅在新增分位数/表格格式确有需要时）
- Test: `frontend/src/views/analyticsState.test.ts`

- [ ] **Step 1: 写页面状态行为测试**

先扩展纯状态测试，验证：

- 默认维度为 `source`。
- Tab 切换不清除已有筛选。
- 点击来源、Access Key、协议、站点、模型调用统一切换函数。
- Dashboard 缺失新增字段时显示空表，不影响核心图表。

- [ ] **Step 2: 运行测试确认红灯**

Run:

```powershell
cd frontend
npm run test -- src/views/analyticsState.test.ts
```

Expected: 新增行为未实现时失败。

- [ ] **Step 3: 增加页面筛选和 Tab 状态**

在 Analytics 页面增加：

- `source` 筛选状态。
- `activeAnalysisDimension`，默认 `source`。
- 统一筛选标签列表。
- 新增 breakdown 表格数据计算。
- 表头排序状态。

筛选变化继续复用现有防抖/轮询和取消请求逻辑，不能造成同一次交互重复发起多个 Dashboard 请求。

- [ ] **Step 4: 渲染细分分析区域**

在现有核心图表之后增加细分区域：

- 来源
- Access Key
- 协议
- 失败原因
- HTTP 状态码
- 回退链路
- 延迟分位数

来源、Access Key、协议、失败原因和状态码使用统一 `NDataTable` 或现有项目表格样式。回退链路使用专用列，延迟分位数使用两组指标表格。

默认按请求数降序，空数据使用现有 Analytics 空状态文案风格。

- [ ] **Step 5: 接入点击联动**

为细分表格行、站点分布和模型分布增加 click handler：

- 使用稳定 `key`，不使用显示文字反查 ID。
- 同一 ECharts 实例只注册一次 handler。
- 组件卸载时移除 handler 或销毁实例。
- 失败原因、状态码和回退链路只支持查看，不触发全局筛选。

- [ ] **Step 6: 完成响应式和深色模式样式**

保持当前 Analytics 的卡片、间距和字体层级：

- 1440/1280 宽度使用双列或现有主列布局。
- 768 宽度让细分表格支持横向滚动，不压缩到不可读。
- 375 宽度筛选项和 Tab 可横向滚动，表格不撑破页面。
- 深色模式复用现有 CSS 变量，不新增固定白色背景。

- [ ] **Step 7: 运行前端测试、类型检查和构建**

Run:

```powershell
cd frontend
npm run test
npm run typecheck
npm run build
```

Expected: 测试和类型检查通过，构建只保留既有大 chunk 警告，不新增错误。

---

## Task 10: 完成后端全量验证和真实页面验证

**Files:**
- No new production files; inspect all changed files.
- Test: all relevant backend and frontend tests.

- [ ] **Step 1: 运行后端全量测试**

Run:

```powershell
dotnet test
```

Expected: PASS；如有既有 XML 文档警告，记录但不扩大到无关文件。

- [ ] **Step 2: 检查数据库兼容性**

使用测试数据库和现有旧数据库夹具验证：

- 缺少新列时能完成增量补列。
- 旧日志能正常返回 Analytics。
- 空状态和无 HTTP 响应状态码不崩溃。
- 新字段不会向列表或前端返回敏感正文。

- [ ] **Step 3: 真实运行页面并交互验证**

如果环境具备浏览器自动化能力，实际打开 Analytics 页面并验证：

- 默认看板加载。
- 来源 Tab 数据与 Usage Logs 来源分类一致。
- Access Key、协议、来源点击后筛选标签和核心图表同步更新。
- 回退链路和延迟分位数显示。
- 失败原因和状态码不泄露原始错误文本。
- 375、768、1280、1440 宽度。
- 深色模式。

如果环境没有浏览器自动化能力，必须明确报告只完成自动化测试和构建，未完成真实视觉交互验证。

- [ ] **Step 4: 最终差异和安全检查**

Run:

```powershell
git diff --check
git status --short
```

逐项确认：

- 没有导出接口或导出按钮。
- 没有修改旧 Razor 页面。
- 没有恢复 Cookie 认证。
- 没有把密钥、Token、请求体或响应体加入 Analytics DTO。
- 没有覆盖用户已有的无关未提交修改。
- 没有提交或推送，除非用户另行明确要求。

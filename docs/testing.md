# 测试体系

> 本文是 [README.md](../README.md) 的测试细节篇。当前 master 共 **2 个测试项目**：ApplicationTests 118 个执行用例 + IntegrationTests 309 个执行用例（Fact/Theory 展开 InlineData 后的实测数，`dotnet test` 于 2026-08-18 实跑确认；下文表格内的「用例」列为源码属性数，Theory 会展开为更多执行用例）；前端有 20 个 vitest 文件、98 个执行用例。
> `tests/AITool.Core.IntegrationTests/` 与 `tests/AITool.Admin.IntegrationTests/` 目录只剩 bin/obj 构建残留（split 分支遗留，无源码、不在解决方案），不是现存测试项目。

## 1. 测试策略

| 项目 | 定位 | 隔离手段 |
|------|------|----------|
| `tests/AITool.ApplicationTests`（xUnit + FluentAssertions） | 单元/服务测试（118 用例） | `TestDatabaseFactory.Create()`：每个测试 `%TEMP%/aitool-test-{GUID}.db` 临时 SQLite 文件，`SqlSugarSetup.InitializeDatabase` 建表，Dispose 删文件 |
| `tests/AITool.IntegrationTests`（xUnit + FluentAssertions + `WebApplicationFactory<Program>`） | 端到端集成（309 用例） | 每个测试工厂持有独立 `%TEMP%/aitool-<场景>-{GUID}.db`；`IntegrationTestDbHelper.ReplaceWithSqlSugar` 覆盖生产 SqlSugar 注册；`UseEnvironment("Testing")`；`IProxyForwardService` 换 Fake / Stub `HttpMessageHandler`，**不打真实外部 API** |

公共工具：
- `tests/AITool.ApplicationTests/TestDatabaseFactory.cs` — 临时库创建/销毁
- `tests/AITool.IntegrationTests/IntegrationTestDbHelper.cs` — `ReplaceWithSqlSugar(services, databasePath)` + `InitializeDatabaseAsync(services)`；各测试文件内嵌 `WebApplicationFactory<Program>` 子类（`AnalyticsWebApplicationFactory`、`ChatWebApplicationFactory`、`ResponsesWebApplicationFactory`、`AnthropicProxyWebApplicationFactory`、`ProxyFallbackWebApplicationFactory` 等，模式统一：替换转发服务 + 临时库 + ConfigureClient 时 seed 数据）

## 2. ApplicationTests 文件清单

| 文件 | 用例 | 覆盖点 |
|------|------|--------|
| `Codex/CodexModelFetcherTests.cs` | 1 | Codex 模型拉取兼容 id 型条目（StubHandler 假 HTTP） |
| `Health/ModelHealthRequestServiceTests.cs` | 1 | 探测把上游 HTTP 状态码写进 usage log |
| `Operations/SystemRuntimeSettingsServiceSqliteTests.cs` | 1 | 运行时设置 SQLite 真实持久化 |
| `Operations/SystemRuntimeSettingsServiceTests.cs` | 6 | 读取/更新/钳制非法值/按来源与时间清日志 |
| `Operations/KeyedAsyncLockTests.cs` | 1 | 同键异步锁串行化与释放后复用 |
| `Proxy/ProxyForwardServiceRealHttpTests.cs` | 2 | 真实转发：Responses JSON 直接成功；Codex SSE 空 output 从 delta 重建 |
| `Proxy/ProxyForwardServiceResponseTests.cs` | 30 | 反射测私有静态：usage 提取、`HasUsableResponse`、`BuildFailureMessage`、`TryExtractResponsesCompletion` 各种 SSE 形态 |
| `Proxy/ProxyProtocolResolverTests.cs` | 6 | 透传/桥接判定、legacy responses 值、显式能力保留 |
| `Proxy/RouteCircuitStateStoreTests.cs` | 7 | 熔断阈值触发/过期解除/失败连胜重计/ResetAll 元数据清理 |
| `Proxy/UsageLogServiceTests.cs` | 6 | 落库（总 token、fallback 元数据、失败分类、有界队列不丢条目、瞬时刷盘失败重试） |
| `Retention/LogRetentionServiceTests.cs` | 3 | 保留清理：设置驱动/边界/关闭跳过 |
| `Routing/RouteSelectionServiceTests.cs` | 4 | 路由重试语义：空响应体视为失败重试、message_stop 判完成、无 done 判中断 |
| `Sites/SiteEndpointPathResolverTests.cs` | 3 | 端点路径 v1 前缀补全 |
| `UsageLogs/PercentileCalculatorTests.cs` | 4 | nearest-rank 百分位、无效值过滤 |
| `UsageLogs/UsageLogErrorClassifierTests.cs` | 5 | 错误分类优先级（流中断最高、成功 null） |
| `Google/GoogleAccountBasicsTests.cs` | 16 | Google 账号字段、额度窗口解析与 OAuth URL |
| `Google/GeminiForwardPipelineTests.cs` | 3 | Gemini 请求封套、project 注入、usage 口径 |

## 3. IntegrationTests 文件清单

| 文件 | 用例 | 覆盖点 |
|------|------|--------|
| `Analytics/AnalyticsApiTests.cs` | 9 | 统计面板：链去重只计最终结果、不泄露 AccessKey、失败分类、回退链 Top20、延迟百分位、过滤作用于最终记录 |
| `Analytics/AnalyticsBackgroundQueryExecutorTests.cs` | 1 | 有界队列满返回 QueueFull |
| `Auth/AdminAuthTests.cs` | 2 | 管理端未登录 401；代理路由走 AccessKey 不受登录拦截 |
| `Auth/PasswordHasherTests.cs` | 11 | PBKDF2 哈希/校验、legacy MD5 升级、JWT 签发/刷新/轮换/吊销 |
| `Chat/ChatApiTests.cs` | 9 | 对话 API：按路由协议选目标、SSE 流式、null usage 忽略、并发限流即时生效 |
| `Chat/ChatRealForwardResponsesTests.cs` | 2 | 真实 ProxyForwardService：Responses JSON 取内容、Codex SSE 聚合 |
| `Services/CredentialRefreshTests.cs` | 1 | Codex 401 凭证刷新按隐藏站点 single-flight |
| `Services/AccountQuotaInspectionTests.cs` | 1 | 通用巡检综合多个额度窗口的最大已用比例 |
| `Contracts/ApiResponseTests.cs` | 6 | 统一包装契约 |
| `Developer/ProtocolDiagnosticsApiTests.cs` | 18 | 离线协议诊断：链路阶段、流式方向矩阵、试运行规则 scope、非法协议/流框架 400 |
| `Developer/SqlMigrationApiTests.cs` | 9 | SQL 迁移：事务提交/回滚、密码确认、重复执行、路径穿越拒绝、试运行 |
| `DeveloperInvocationTraceStoreTests.cs` | 2 | 追踪存储 body 摘要截断 |
| `Health/HealthEndpointTests.cs` | 1 | /health 200 |
| `Persistence/DateTimeOffsetQueryConsistencyTests.cs` | 1 | 生产 SqlSugar 配置下 DateTimeOffset 存查一致性（UTC 探针 vs 本地时钟存储，防时区系统性偏移） |
| `Proxy/AnthropicProxyControllerTests.cs` | 23 | /v1/messages 端到端：鉴权头、count_tokens、SSE 透传、OpenAI→Anthropic 桥接（tool_use 事件/多 choice/空白分片）、Responses→Anthropic 桥接、**usage 累计不重复计缓存** |
| `Proxy/ModelConcurrencyLimiterTests.cs` | 21 | SkipOnFull/WaitForSlot、站点/模型维度独立、动态调限、排队、DB 失败容错、空闲 state 清理竞态 |
| `Proxy/OpenAiCrossProtocolProxyTests.cs` | 7 | chat/completions → Anthropic/Responses 桥接、tool_calls 映射、三种流式 SSE（含 legacy completions） |
| `Proxy/ProxyFallbackFlowTests.cs` | 30 | 路由 CRUD + 故障转移全流程：优先级/时间段规则、多上游模型组、回退记日志、首块写出后不回退、deepseek-harness UA 识别 |
| `Proxy/ProxyMetadataCacheTests.cs` | 8 | 缓存失效与延迟刷新（AccessKey/设置/路由快照/协议优先） |
| `Proxy/ProxyProtocolBridgeResponseConversionTests.cs` | 13 | 响应转换：Chat↔Responses、流式 done 只发一次、**缺 usage 时还原含缓存 prompt_tokens**、工具索引连续 |
| `Proxy/ProxyProtocolBridgeDirectBridgeTests.cs` | 23 | 三协议直接透传桥接与协议方向判定 |
| `Proxy/ProxyProtocolBridgeGeminiTests.cs` | 25 | Gemini 请求/响应桥接、SSE 状态机、思考等级与 usage 口径 |
| `Proxy/ProxyProtocolBridgeStreamStateTests.cs` | 3 | 流式状态机：tool_calls 后重开 thinking/text 块（新 content index）、Anthropic→Responses 读取 thinking 字段 |
| `Proxy/ProxyProtocolBridgeThinkingTests.cs` | 18 | thinking/reasoning 双向：budget_tokens↔reasoning_effort、adaptive 默认 high、keep_reasoning 规则、metadata 不透传 |
| `Proxy/ProxyResilienceTests.cs` | 2 | usage log 写入抛异常时代理仍成功 |
| `Proxy/ResponsesProxyTests.cs` | 25 | /v1/responses：透传/桥接非流式与流式、WebSocket、usage 记录、effort 提取、403/401/400 |
| `Services/SiteCascadeDeleterTests.cs` | 6 | 级联清理映射与规则、清空孤儿 entry |
| `UsageLogs/UsageLogsApiTests.cs` | 9 | 列表过滤、请求详情按 attempt 分组、汇总 |

## 4. usage token 断言口径（重要，对应 2026-08 的两次语义修复）

1. **内部记账：`InputTokens` = 不含缓存的新输入**
   - OpenAI Chat：`prompt_tokens=120, cached=45` → 断言 `InputTokens=75, CachedTokens=45`
   - Responses：`input_tokens=240, cached=80` → `InputTokens=160`
   - Anthropic：`input_tokens=405415, cache_read=405248` → `InputTokens=167`；`cache_creation` 也要减（1000-600-100=300，Cached=700）；缓存大于输入下限 0
2. **对外还原：转回 OpenAI 协议时 `prompt_tokens` 必须含缓存**
   - `Responses_stream/non_streaming_without_usage_restores_cache_inclusive_prompt_tokens`：新输入 7 + 缓存 2 → 断言 `prompt_tokens=9`、`prompt_tokens_details.cached_tokens=2`、`total_tokens=12`
3. **流式累计覆盖语义**（防 newapi 类中间层重复累计）
   - `Post_messages_stream_delta_with_cumulative_usage_does_not_double_count_cached_tokens`：message_start 与 message_delta 都带 `input_tokens=100, cache_read=80` → 落库 `CachedTokens=80`（不是 160）、`InputTokens=20`
4. 其他：Chat 流式忽略 OpenAI chunk 的 `usage:null`；Responses 透传与桥接返回体必须含 usage 字段

历史数据修正 SQL：`docs/usage-token语义修复SQL.md`、`docs/EF迁移SqlSugar数据修复SQL.md`。

## 5. 运行命令

```bash
# 后端全部测试（仓库根目录）
dotnet test

# 单项目
dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj

# 前端（frontend/ 目录）
npm run test          # vitest run
npm run type-check    # vue-tsc --noEmit
npm run build         # 类型检查 + vite build
```

前端 vitest 覆盖：`api/http.test.ts`、`api/chat.test.ts`、`api/routes.test.ts`、`api/analytics.test.ts`、`api/oauth.test.ts` + 各视图 `*State.test.ts`（约 20 个文件）。

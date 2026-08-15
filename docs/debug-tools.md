# 调试工具（/developer/invocations 六页签）

> 本文是 [README.md](../README.md) 的调试工具细节篇。入口：管理后台「监控运维 → 调试工具」（`/developer/invocations`，需系统设置开启 `DeveloperFeaturesEnabled`，路由守卫与后端 API 双重 gating——后端 `DeveloperInvocationsApiController` / `SqlMigrationsApiController` 在开关关闭时返回 404）。
> 页签支持 hash 深链：`#developerInvocationsPane`、`#developerSimulatorPane`、`#developerConcurrencyPane`、`#developerCircuitBreakerPane`、`#developerProtocolDiagnosticsPane`、`#developerSqlMigrationsPane`。

---

## 1. 调用调试（invocations）

**后端**：`DeveloperInvocationTraceStore`（`src/AITool.Web/Services/DeveloperInvocationTraceStore.cs`，Singleton，内存环形缓冲）：
- `AddRequest(DeveloperInvocationTraceRequest)`（L37）→ traceId；`AddAttempt(traceId, attempt)`（L69）→ attemptId（记录 `PreparedRequestBody` 转换后请求体，排查上游 400 的关键）；`CompleteAttempt(traceId, attemptId, result)`（L106）回写状态/token/耗时
- 容量：最多 **40 条**（L16），保留 **20 分钟**（L20）自动清理；`List()`/`Get(traceId)`（深拷贝）；静态 `CaptureHeaders`/`FormatBody`（JSON 缩进）/`SummarizeBody`（长字符串值收缩为「前100…(省略N字符)后20」）

**前端**（`DeveloperInvocationsView.vue`）：概览卡（记录数/失败/等待返回）；自动刷新 5s 开关、精简显示开关（summarize 重新加载）、立即刷新；记录手风琴（状态 pill、协议、来源、请求模型→尝试模型、路径/站点/HTTP/耗时/尝试统计）；展开详情含**每段尝试卡**：转发方式（direct 直接透传 / bridge 兼容中转）、上游协议、Token 三项、转换后请求体与返回体（复制 + 「诊断」按钮）；整体请求/响应体带「诊断此请求/诊断此响应」→ 通过 `setProtocolDiagnosticsPrefill` + provide/inject 信号**一键跳转协议诊断台并预填自动执行**。

## 2. 客户端模拟（simulator，ClientSimulator.vue）

按真实代理 URL 模拟客户端（不走管理 API）。配置：代理根地址、访问密钥（自动带入 `getDeveloperInit` 的 defaultBaseUrl/defaultAccessKey）、模型 tag 可输入、测试消息。**8 个端点页签**：

`GET /v1/models` · OpenAI 聊天 `/v1/chat/completions` · Anthropic 聊天 `/v1/messages`（x-api-key + anthropic-version 头） · Responses `/v1/responses` · Completions `/v1/completions` · Embeddings · Count Tokens · Responses Compact

每页签展示请求示例 JSON（密钥脱敏）+ 响应结果（流式逐 chunk 追加、自动滚动）；AbortController 停止（`SimulatorRequestRegistry` 管理每页签请求）；协议不匹配时自动切换支持的模型；`buildModelSupportLabels` 标注 原生/兼容。

## 3. 当前模型并发数检测（concurrency）

`GET /api/admin/developer/invocations/concurrency` → `ModelConcurrencyLimiter.ListActive()/ListRecent(6h)`。进入页签自动 5s 轮询；表格列：模型名/站点/并发数徽标/最大并发（不限）/排队数；提示仅展示最近 6 小时出现过的站点模型。

## 4. 熔断监控（circuit-breaker，CircuitBreakerTab.vue）

`GET .../circuit-breaker` → `RouteCircuitStateStore.GetAllCircuitStates()`（5s 轮询、页面可见才刷）。每行：**路由入口 entryName + 上游模型 tag + 站点名**（熔断键为站点+站点Key+模型维度，合成 Guid 兜底反查归属）；状态 tag：已熔断（error，含剩余时间）/ 失败累计（warning）+ 失败次数。操作：单条解除（`POST /{circuitKey}/reset`）、全部解除（`/reset-all`，Popconfirm 确认）。

## 5. 协议诊断（protocol-diagnostics，ProtocolDiagnosticsTab.vue，614 行）

**定位**：任意协议组合离线转换测试——不调上游、不用密钥、不写调用记录（`POST /api/admin/developer/invocations/protocol-diagnostics`，`DeveloperInvocationsApiController.cs:76`）。

**表单**：方向（请求转换 客户端→上游 / 响应转换 上游→客户端）、源/目标协议（OpenAI Chat / Anthropic Messages / OpenAI Responses 任意组合）、流式片段 checkbox、模型名；Anthropic→Responses 请求方向显示 eventName 输入；响应方向三个 token 输入（input/cached/output，用于 usage 还原）；payload textarea（JSON 或 SSE 片段）。

**规则试运行**：请求方向可选一个已启用的兼容规则集（`listProfiles→getProfile→parseCompatibilityRules`），`Rules` 随请求发送，转换按真实链路语义执行（scope 按透传/兼容路径筛选）；结果区显示「已应用规则集（N 条规则）」。

**结果展示**：
- 元信息：转换成功/失败 tag、conversionPath、事件数、是否检测到完成、失败原因
- **转换链路可视化（chain）**：`chain.mode`（direct 透传 / bridge 兼容转换 tag）+ `stages` 节点流（label/协议/**函数名**/note，bridge 节点橙色高亮，`→` 连接）
- **流式方向矩阵（eventMappings）**：上游事件 → 客户端事件对应表（含说明列）
- 缺失字段提醒（missingFields，warning tag）、输入识别摘要（inputSummary 键值 chip）
- **字段级对比（fieldMappings）**：源字段 → 目标字段 → 说明表格
- **转换前后 JSON diff**：`JsonDiffView`（`utils/jsonDiff.ts` 递归 diff：+新增/-移除/~修改，默认只显示差异行，上限 800 行防卡死）
- 转换后内容：`JsonTreeView` 折叠树（单节点子项上限 100）或 pre
- **一键保存为兼容规则**：弹窗从 missingFields 正则提取字段名生成候选 `default` 规则，可新建规则集（默认名「转换修复 - X→Y」）或追加到已有规则集；规则行可编辑 op（default/strip/rename）、key/value/from/to、scope（bridge/all/passthrough），调 `createProfile`/`updateProfile` 保存

**联动**：`takeProtocolDiagnosticsPrefill`（模块级一次性取用）+ inject 信号，从「调用调试」页签预填并自动执行。

**测试**：`tests/AITool.IntegrationTests/Developer/ProtocolDiagnosticsApiTests.cs`（18 用例：链路阶段、流式矩阵、试运行 scope、非法协议/流框架 400）。

## 6. SQL 迁移（sql-migrations，SqlMigrationsTab.vue，236 行）

**用途**：执行部署机上 `sql-migrations/` 目录的手工 SQL 修复脚本（如 `docs/usage-token语义修复SQL.md` 这类历史数据修正），**不用于 Schema 变更**（建表/补列由 SqlSugar CodeFirst 自动完成）。

**后端 `SqlMigrationRunnerService`**（`src/AITool.Web/Services/SqlMigrationRunnerService.cs`，479 行，Scoped）：
- 目录：`SqlMigrations:Directory` 配置可覆盖（测试用），默认 `{ContentRootPath}/sql-migrations`（部署后由管理员手工放 .sql；目录不存在时列表返回 `directoryExists=false` 提示）
- 限制：单文件 ≤1MB（L53）、语句 ≤500 条（L58）、静态 `SemaphoreSlim(1,1)` 全局串行（L68）
- `ListScriptsAsync`（L101）：目录 .sql 清单 + `SqlMigrationExecution` 审计表汇总（预览 64KB）
- `ExecuteAsync(fileName, password, dryRun, operatorIp, ct)`（L165）：文件名防穿越校验（L173-179）→ `AdminAuthService.VerifyPassword`（失败抛异常且**不写审计**）→ `RunStatementsAsync`（L230）用 `CopyNew()` 独立连接 + **事务**逐条执行，**dryRun 完成后回滚** → `RecordAsync`（L294）写审计表（FileName/SHA256 FileHash/DryRun/Success/RowsAffected/StatementCount/DurationMs/ErrorMessage/OperatorIp）+ NLog Warning
- `SplitStatements(sql)`（L344，internal static 供测试）：分号拆分器，识别 `--`/`/* */` 注释、`''`/`""`/`[]` 转义，注释内分号不拆

**前端**：列表（状态 tag：未执行 / 最近试运行成功 / 已成功执行 N 次 / 最近执行失败；文件名/大小/hash 前 8 位/最近执行时间）；选中后内容预览（超 64KB 截断提示、超 1MB 无法预览执行）+「试运行」「执行」按钮；**执行确认弹窗**必须输入管理员密码，正式执行且已成功过 N 次时红色警告「脚本已成功执行过 N 次，请确认幂等或已备份」；结果区 tag（试运行成功（已回滚）/执行成功/执行失败（已回滚））+ 语句数/影响行数/耗时/错误信息，完成后刷新列表。

**API**：`GET /api/admin/sql-migrations`（列表）、`POST /{fileName}/execute`（请求体仅 `{Password, DryRun}`，不接收 SQL 文本）。开发者开关关闭时整体 404。

**测试**：`tests/AITool.IntegrationTests/Developer/SqlMigrationApiTests.cs`（9 用例：事务提交/回滚、密码确认、重复执行、路径穿越拒绝、试运行）。

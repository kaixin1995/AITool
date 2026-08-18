# API 端点全表

> 本文是 [README.md](../README.md) 的 API 速查篇。代理端点（`/v1/*`、`/health`）使用 AccessKey 自校验；管理端点（`/api/*`）使用 JWT Bearer（`/api/admin/*` 由 `Program.cs` 内联中间件强制认证，未登录 401 JSON；`/api/auth/*` 登录前可访问）。
> 统一响应包装：`{success, message, data, errorCode}`（`Web/Contracts/ApiResponse.cs`，`ApiResponse.Ok/Fail`）。

---

## 1. 代理端点（面向客户端）

| 方法 | 路由 | 认证 | 说明 |
|------|------|------|------|
| POST | `/v1/chat/completions` | `Authorization: Bearer {AccessKey}` | OpenAI Chat 主入口（流式/非流式、跨协议桥接、故障转移） |
| POST | `/v1/completions` | Bearer | Legacy Completions（内部转 Chat 复用主链路） |
| POST | `/v1/embeddings` | Bearer | Embeddings（禁流式，仅 OpenAI 协议路由） |
| POST | `/v1/responses` | Bearer | Responses API（HTTP 模式） |
| GET | `/v1/responses` | Bearer | Responses API（WebSocket 模式） |
| POST | `/v1/responses/compact` | Bearer | Codex 远程压缩（Responses 主链路 + 上游专用 responses/compact 端点；Codex 目标删除 stream 字段） |
| GET | `/v1/models` | Bearer | 模型列表（按 `x-api-key`/`anthropic-version` 头自动切 OpenAI/Anthropic 格式；按 AccessKey 路由限定过滤） |
| GET | `/v1/models/{modelId}` | Bearer | 模型详情（403 `model_not_found` / `route_forbidden`） |
| POST | `/v1/messages` | `x-api-key: {AccessKey}`（Bearer 回退） | Anthropic Messages 主入口 |
| POST | `/v1/messages/count_tokens` | x-api-key | 本地 token 估算（不调上游） |
| GET | `/health` | 无 | `{status:"ok"}` |

> 代理端点支持 `X-AITool-Source` 头显式指定来源标识（否则按 User-Agent 识别 claude-code/codex/open-code/zcode/deepseek-harness），写入使用日志 `Source` 字段。

---

## 2. 认证 `api/auth`（AuthApiController）

| 方法 | 端点 | Action | 说明 |
|------|------|--------|------|
| GET | `/status` | L55 | 登录状态 + 功能开关（codexEnabled/codexInspectionEnabled/developerEnabled）+ 版本号/编译时间 |
| POST | `/login` | L88 | 密码登录（`LoginRateLimitService` IP 失败计数锁定），签发 access + refresh token |
| POST | `/refresh` | L150 | refresh token 换新 access（轮换语义，旧 token 失效） |
| POST | `/logout` | L176 | 登出（吊销 refresh token） |
| POST | `/setup` | L189 | 首次设置管理密码（≥6 位） |

## 3. 仪表盘 `api/admin/dashboard`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/stats` | 首页统计卡（站点/模型/路由/密钥/检测任务数） |

## 4. 站点管理 `api/admin/sites`（SitesApiController）

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/` | 站点列表（Codex 隐藏站点过滤） |
| GET | `/{id}` | 站点详情 |
| POST | `/` | 创建站点 |
| PUT | `/{id}` | 更新站点 |
| POST | `/{id}/toggle` | 启停 |
| DELETE | `/{id}` | 删除（`SiteCascadeDeleter` 级联清理映射/规则） |
| POST | `/bulk-delete` | 批量删除 |
| GET | `/export` | 导出站点 JSON（含完整 keys） |
| POST | `/import` | 导入（JSON 数组或 TSV） |
| GET/POST/PUT/DELETE | `/{id}/keys...` | 站点多 Key CRUD + toggle（L236-358） |

## 5. 站点模型目录 `api/admin/site-catalog`（SiteCatalogApiController）

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/fetch-models/{siteId}` | 拉取单站模型列表 |
| POST | `/fetch-all-models` | 一键拉取全部站点（异步 taskId） |
| GET | `/fetch-all-progress/{taskId}` | 批量拉取进度 |
| POST | `/import-selected` | 勾选导入模型库 |

## 6. 模型库 `api/admin/models`（ModelsApiController）

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/` | 模型列表（含映射与状态） |
| POST | `/` | 创建模型 |
| GET | `/{id}` | 详情 |
| PUT | `/{id}` | 更新（含 OverrideReasoningEffort / CompatibilityProfileId） |
| POST | `/{id}/toggle` | 启停 |
| DELETE | `/{id}` | 删除 |
| POST | `/clear-all` | 清空全部模型及关联数据 |
| GET | `/vendor-catalog` | 厂商目录（图标/匹配规则） |
| PUT | `/vendor-catalog` | 保存厂商目录 |
| GET | `/pricing` | 模型价格表（本地 model-pricing.json；含 usdToCny 汇率与峰谷配置；首次访问从模板初始化） |
| PUT | `/pricing` | 保存价格表（校验+写文件+立即刷新计价缓存；保存后统计/日志金额实时更新） |
| POST | `/{id}/mappings` | 新增站点映射 |
| DELETE | `/{id}/mappings/{mappingId}` | 删除映射 |
| PUT | `/mappings/{mappingId}/concurrency` | 更新映射最大并发 |

## 7. 路由规则 `api/admin/route-rules`（RouteRulesApiController）

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/entries` | 路由入口列表 |
| POST | `/entries` | 创建入口 |
| POST | `/entries/delete` | 删除入口 |
| GET | `/site-instances` | 候选站点实例列表（搜索用） |
| GET | `/models` | 有映射的模型列表 |
| GET | `/discover-sites?modelName=` | 自动发现拥有该模型的站点 |
| GET | `/list?modelName=` | 入口下的规则列表（按优先级） |
| POST | `/save` | 批量保存 `{externalModelName, rules}`（删除重建，含时间规则） |
| POST | `/toggle/{ruleId}` | 启停规则 |
| POST | `/delete/{ruleId}` | 删除规则 |

## 8. 兼容规则集 `api/admin/compatibility-profiles`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/` | 规则集列表 |
| GET | `/{id}` | 详情（含 RulesJson 原文） |
| POST | `/` | 新建 |
| PUT | `/{id}` | 更新 |
| POST | `/{id}/toggle` | 启停 |
| DELETE | `/{id}` | 删除（引用模型自动解绑） |

## 9. 访问密钥 `api/admin/access-keys`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/` | 列表 |
| GET | `/{keyId}/plain` | 读取明文（复制完整密钥） |
| POST | `/create` | 创建（`sk-` + 32 位 hex，SHA256 存储） |
| POST | `/toggle/{keyId}` | 启停 |
| POST | `/delete/{keyId}` | 删除 |
| POST | `/update-routes/{keyId}` | 更新允许的路由入口集合（AllowedRouteNames） |

## 10. 模型检测 `api/admin/detection` 与 `api/admin/detection-tasks`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/detection/matrix` | 检测矩阵（模型 × 站点映射状态） |
| POST | `/detection/probe/{mappingId}` | 单点探测 |
| POST | `/detection/probe-model/{modelId}` | 按模型探测（异步 taskId） |
| POST | `/detection/probe-all` | 全量探测（异步） |
| GET | `/detection/progress/{taskId}` | 增量进度（LastReportedCount） |
| GET | `/detection-tasks` | 定时任务列表（含执行历史） |
| POST | `/detection-tasks` | 创建（名称 + Cron + 可选模型） |
| POST | `/detection-tasks/{id}/toggle` | 启停（重注册 Hangfire） |
| POST | `/detection-tasks/{id}/execute` | 立即执行 |
| DELETE | `/detection-tasks/{id}` | 删除 |

## 11. 模型健康 `api/admin/model-health` 与 路由回退 `api/admin/route-fallback`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/model-health?range=1d/7d/30d` | 健康面板（成功率/时间线） |
| POST | `/model-health/{modelId}/monitor` | 添加监控模型 |
| DELETE | `/model-health/{modelId}/monitor` | 移除监控 |
| GET | `/route-fallback/list` | 回退事件分页（从 UsageLog 按 RequestId 还原，样本近 5000 条） |
| GET | `/route-fallback/summary` | 回退汇总统计 |

## 12. 对话测试 `api/admin/chat`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/models` | 可对话模型列表（含可用站点数） |
| GET | `/targets` | 全部站点目标（同站点多 Key 去重） |
| GET | `/models/{modelId}/targets` | 指定模型目标 |
| POST | `/send` | 非流式发送（含故障转移，不触发熔断） |
| POST | `/send-stream` | SSE 流式（命名事件 token/reasoning/meta/done/error；meta 含 attempts 明细） |

## 13. 使用日志 `api/admin/usage-logs`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/filters` | 筛选下拉（站点/密钥/来源） |
| GET | `/list` | 分页查询（DB 层过滤，PageSize 1-100；支持时间/站点/密钥/来源/状态/模型模糊） |
| GET | `/request-detail/{requestId}` | 同一请求的全部尝试 |
| GET | `/summary` | 汇总统计 |

## 14. 统计分析 `api/admin/analytics`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/options` | 筛选项 |
| GET | `/dashboard` | 可视化数据（重查询走 `AnalyticsBackgroundQueryExecutor` 后台队列，Pending/QueueFull 时 202 语义 + retryAfterMs） |

## 15. 系统设置 `api/admin/system`

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/settings` | 读取运行时设置 |
| PUT | `/settings` | 更新（边界钳制 + 缓存失效 + 熔断参数更新 + Codex 总开关联动） |
| POST | `/clear-usage-logs?clearAll=` | 按来源/时间范围清空（或全部清空） |

## 16. 开发者工具 `api/admin/developer/invocations`（需 DeveloperFeaturesEnabled）

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/init` | 计数 + 默认参数（defaultBaseUrl/defaultAccessKey）+ 可调试模型 |
| GET | `/list?page=&pageSize=` | 追踪记录列表（内存环形 40 条） |
| GET | `/{traceId}?summarize=` | 单次调用全链路详情（summarize 精简长文本） |
| GET | `/concurrency` | 当前模型并发快照（近 6h） |
| GET | `/circuit-breaker` | 熔断状态全量（站点+模型维度） |
| POST | `/circuit-breaker/{circuitKey}/reset` | 解除单条熔断 |
| POST | `/circuit-breaker/reset-all` | 解除全部熔断 |
| POST | `/protocol-diagnostics` | **离线协议诊断**（不调上游/不用密钥/不写记录；请求体 `ProtocolDiagnosticsRequest`：Direction/SourceProtocol/TargetProtocol/Streaming/ModelName/Payload/EventName/OverrideReasoningEffort/三元 token/InputTokens/CachedTokens/OutputTokens/试运行 Rules；返回 conversionPath、chain(mode/stages/eventMappings)、fieldMappings、missingFields、inputSummary、RulesApplied） |

> 诊断功能细节见 [debug-tools.md](debug-tools.md)。

## 17. SQL 迁移 `api/admin/sql-migrations`（需 DeveloperFeaturesEnabled，关闭时整体 404）

| 方法 | 端点 | 说明 |
|------|------|------|
| GET | `/` | 列出 `sql-migrations/` 目录脚本 + 执行历史（含 64KB 预览） |
| POST | `/{fileName}/execute` | 执行或试运行。请求体仅 `{password, dryRun}`（**不接收 SQL 文本**）；密码确认 + 事务回滚 + 全量审计 |

> 详见 [debug-tools.md](debug-tools.md)。

## 18. Codex `api/admin/codex`（类级 `CodexFeatureToggleAttribute`，关闭时 404）

| 方法 | 端点 | 说明 |
|------|------|------|
| POST | `/start-oauth` | 生成 OAuth/PKCE 授权 URL |
| POST | `/complete-oauth` | 粘贴回调 URL 完成 token 交换并建账号 |
| POST | `/import-credential` | 导入 CPA 格式凭证 JSON |
| GET | `/accounts` | 账号列表（含额度窗口） |
| POST | `/accounts/{id}/refresh-quota` | 主动刷新额度 |
| POST | `/accounts/{id}/reset-quota` | 重置额度快照 |
| POST | `/accounts/{id}/toggle` | 启停 |
| PUT | `/accounts/{id}` | 编辑（改名/换 refresh_token） |
| DELETE | `/accounts/{id}` | 删除（级联清理隐藏站点） |
| POST | `/accounts/{id}/refresh-token` | 手动刷新 access_token |
| GET | `/accounts/{id}/fetch-models` | 拉取账号可用模型 |
| POST | `/accounts/{id}/import-selected-models` | 导入勾选模型 |
| POST | `/inspection/run?force=` | 手动巡检（巡检开关关闭时 404） |
| GET | `/inspection/status` / `/inspection/last-run` / `/inspection/logs` | 巡检状态/上次结果/日志 |
| GET | `/accounts/{id}/reset-credits` | 查询剩余重置 credits |
| POST | `/accounts/{id}/consume-reset-credit` | 消耗一张 credit 执行真实重置 |
| POST | `/accounts/export-credentials` | 导出账号凭证（codex_credential_*.json） |

> Codex 体系详见 [codex.md](codex.md)。

## 19. Hangfire 仪表盘

`/hangfire` — Hangfire Dashboard（InMemory 存储），未登录由中间件重定向 `/login?returnUrl=...`。

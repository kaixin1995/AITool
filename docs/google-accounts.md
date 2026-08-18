# Google 账号托管（GeminiCLI / Antigravity）

把 Google 账号的 Gemini 订阅（Gemini CLI / Antigravity 两种客户端身份）接入 AITool 代理链路。移植自 [gcli2api](https://github.com/su-kaka/gcli2api)（`reference-projects/gcli2api`），与 Codex 账号托管（见 [codex.md](codex.md)）共用「隐藏 Site 复用」方案。

## 总体架构

```
Google 账号登录 / 导入凭证
        │ GoogleAccountsApiController (api/admin/google-accounts)
        ▼
GoogleAccountProvisioner ──► GoogleAccount（GoogleAccounts 表）
        │                        │
        │                        └─ LinkedSiteId ──► 隐藏 Site（ProtocolType=Gemini, ManagedSource=Google）
        │                                            └─ SiteModelMapping ×N（模型映射）
        ▼
转发链路（OpenAI / Anthropic / Responses 客户端）
        │ ProxyProtocolResolver：Gemini 站点对三种客户端协议统一桥接到 "Gemini"
        ▼
ProxyProtocolBridge（Gemini 桥）──► cloudcode-pa / daily-cloudcode-pa v1internal 端点
```

## 两种接入方式（AccountKind）

| | GeminiCli | Antigravity |
|---|---|---|
| 上游端点 | `https://cloudcode-pa.googleapis.com` | `https://daily-cloudcode-pa.googleapis.com` |
| 客户端身份 | Gemini CLI（UA `GeminiCLI/0.35.2/{model} (win32; x64; cloud-shell)`） | Antigravity CLI（UA `antigravity/cli/1.1.12 windows/amd64`） |
| OAuth scope | cloud-platform + userinfo（3 个） | 额外 cclog + experimentsandconfigs（5 个） |
| project 来源 | cloudresourcemanager 项目列表（唯一自动选 / 多个取含 default 的或第一个，兜底共享项目） | loadCodeAssist → cloudaicompanionProject（含 onboardUser 轮询回退） |
| 模型清单 | 静态（`GoogleAccountKinds.GeminiCliModels`，对齐 gcli2api BASE_MODELS） | 动态 `v1internal:fetchAvailableModels`（含 claude-sonnet-4-6-thinking 补齐） |
| 额度查询 | 无上游接口（仅展示 tier，对齐 gcli2api） | fetchAvailableModels → 每模型 quotaInfo.remainingFraction 窗口 |
| 额外元信息 | — | 订阅 tier（free/pro/ultra）+ 积分（availableCredits） |

常量定义：`src/AITool.Application/Google/GoogleAccountKinds.cs`。

## 登录流程（粘贴回调 URL，同 Codex）

1. `POST start-oauth {kind}` → `GoogleOAuthClient.CreateSession()`（state，10 分钟有效）+ `BuildAuthorizeUrl`（`access_type=offline` + `prompt=consent` 保证签发 refresh_token；回跳地址固定 `http://localhost:17891`，Google 桌面客户端允许任意本地回环端口——浏览器显示"无法访问"属正常，地址栏携带 code/state）。
2. 用户在浏览器完成授权后复制地址栏 URL。
3. `POST complete-oauth {kind, callbackUrl}` → 校验 state → `ExchangeCodeAsync` → 探测元信息（邮箱 userinfo / 项目 / tier）→ `ProvisionFromTokensAsync` 建账号。

凭证导入：`POST import-credential?kind=...`（gcli2api 凭证 JSON，需含 `refresh_token`；导入即用 refresh_token 换新 access_token）。

## 协议桥（AITool.Protocol）

- **请求**：`ProxyProtocolBridge.Gemini.cs`。Anthropic / OpenAI 客户端直转 Gemini 内层 GenerateContent；Responses 客户端先经既有 Responses→Anthropic 直转桥再转 Gemini。产出 `{model, project, request}` 封套（`WrapGeminiUpstreamBody`），project 取自路由目标的 `GoogleProjectId`（由 `ProxyRequestMetadataCache` 从 GoogleAccounts 注入）。
- **规范化**（`NormalizeGeminiInner`，对齐 gcli2api gemini_fix/antigravity_fix）：safetySettings 全类 BLOCK_NONE（flash-lite 用 5 类精简表）、强制 topK=64 / maxOutputTokens=64000、thinkingConfig 归一（gemini-3 用 thinkingLevel，2.5 用 thinkingBudget）、part 清理（空 part 剔除 / text 非 string 转字符串 / 尾部空白）。
- **Antigravity CLI 封套**（`ApplyAntigravityCliWrap`）：注入 sessionId（首条用户文本 SHA256 前缀）与 labels、toolConfig 默认 VALIDATED、requestId（`agent/{uuid}/{ms}/{trajectory}/1`）、剥离 safetySettings/stopSequences/presencePenalty/frequencyPenalty（CLI 不发送）、opus/sonnet 系列剥离末尾 model 消息（不支持预填充）。
- **thoughtSignature**：请求侧 functionCall 部件带官方跳过校验占位符 `skip_thought_signature_validator`（中转/换号后真实签名不可信）；响应侧过滤该占位符产生的 `...` 文本部件。
- **响应**：`ProxyProtocolBridge.GeminiResponse.cs`。Gemini → Anthropic（非流式 + SSE 状态机，thinking/text/tool_use 块与签名切块）、Gemini → OpenAI（非流式 + 流式 chunk）、Responses 客户端经 Anthropic 桥链转。usage 口径：input = promptTokenCount − cachedContentTokenCount（新输入）、cached = cachedContentTokenCount、output = candidatesTokenCount + thoughtsTokenCount（思考 token 计费）。
- **思考等级强覆盖（受保护功能）**：`ApplyGeminiThinkingEffort` 在 Gemini 目标以 thinkingConfig 表达（low→1024 / medium→8192 / high→16000 / xhigh·max→24576·32768；gemini-3 转 thinkingLevel），覆盖客户端 thinking/reasoning_effort 原值。

## Web 层接入点

- **TargetPath**：Gemini 上游不走 `SiteEndpointPathResolver`，由控制器按流式选择 `/v1internal:streamGenerateContent?alt=sse` 或 `/v1internal:generateContent`（`ResolveGeminiTargetPath`）。鉴权为 Bearer（`ProxyForwardService.BuildRequestMessage` 的非 Anthropic 分支）。
- **请求头**：`OpenAiProxyController.ApplyGeminiForwardHeaders`（按封套 userAgent 字段区分两种上游：GeminiCLI UA 带模型名；Antigravity UA + requestId + requestType=agent|image_gen）。
- **流式桥**：`OpenAiProxyController.GeminiStreaming.cs`（Gemini→OpenAI、Gemini→Responses、Gemini→Responses-WebSocket）与 `AnthropicProxyController.ForwardGeminiStreamAsAnthropicAsync`。Gemini 流无 [DONE]/message_stop 标记：candidates[0].finishReason 出现即视为正常完成（`ProxyForwardService` 流式解析与各桥的状态机均按此判定）。
- **401 实时刷新**：`GoogleCredentialRefreshService`（同 Codex 模式，`CreateCredentialRefreshCallback` 按 ManagedSource 分派）。
- **后台刷新**：`GoogleTokenRefreshService`（access_token 约 1 小时有效：扫描 5 分钟 / 提前 10 分钟 / 同账号最小间隔 5 分钟 / invalid_grant 退避 30 分钟）。
- **额度**：`GoogleAccountQuotaService : IAccountQuotaProvider`（ProviderKey=`google`，自动纳入通用巡检、自动禁用阈值与 OAuth 总开关）。
- **调试聊天页**：`ChatApiController` 对 Gemini 目标走同一协议桥（非流式 + 流式），SSE 块先转 OpenAI chunk 再复用既有解析。

## 数据与缓存

- `GoogleAccounts` 表（`Domain/Google/GoogleAccount.cs`，CodeFirst 自动建表/补列）。账号去重键：(AccountKind, Email)。
- `ProxyRequestMetadataCache`：`GetGoogleAccountsAsync`（5s 缓存 + Clone）、`InvalidateGoogleAccounts`（同时失效路由缓存，因路由目标携带 GoogleProjectId）。
- 站点管理页对 `ManagedSource=Google` 的隐藏 Site 与 Codex 一致自动过滤（删除保护同样生效）。

## 前端

- `frontend/src/views/OAuthView.vue` 第三个 tab「Google 账号」→ `components/GoogleAccountsPanel.vue`：双入口登录（GeminiCLI / Antigravity 粘贴回调 URL）、gcli2api 凭证导入、账号卡片（kind/tier/积分/项目/Token 到期/每模型额度进度条）、启停/编辑（可替换 refresh_token）/删除/拉取模型（勾选导入，已导入打标）。
- API 模块：`frontend/src/api/oauth.ts`（`listGoogleAccounts` / `startGoogleOAuth` / `completeGoogleOAuth` / `importGoogleCredential` / `refreshGoogleQuota` / `toggleGoogleAccount` / `deleteGoogleAccount` / `updateGoogleAccount` / `fetchGoogleModels` / `importSelectedGoogleModels`）。

## 测试

- `tests/AITool.IntegrationTests/Proxy/ProxyProtocolBridgeGeminiTests.cs`（24 个）：请求三方向转换、封套/CLI 封套、思考覆盖（含 gemini-3 等级表达）、响应块/usage/stop_reason 映射、SSE 状态机（签名切块/跨块工具索引/收尾幂等）、空内容兜底、schema $ref/allOf 清理、usage 提取口径。
- `tests/AITool.ApplicationTests/Google/GoogleAccountBasicsTests.cs`（15 个）：kinds 常量、授权 URL 构造（offline/consent/state/scope）、额度解析（remainingFraction→窗口、无数据返回 null）、协议解析器 Gemini 分支与历史行为回归、静态模型清单。

## 与 gcli2api 的取舍

- 不移植：假流式/抗截断模型名前缀（`假流式/`、`流式抗截断/`）、多凭证轮换重试（AITool 用路由多站点 + 熔断 + fallback 表达）、`stream2nostream` 配置、`enabledCreditTypes`（默认关闭）、MongoDB/PostgreSQL 存储。
- 保留核心语义：CLI 封套字段、thoughtSignature 跳过校验占位符、安全设置全开、强制 maxOutputTokens/topK、usage 三段口径、每模型额度窗口。

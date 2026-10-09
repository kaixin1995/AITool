# xAI 账号托管（Grok / SuperGrok）

把 xAI 的 SuperGrok 订阅账号接入 AITool 代理链路。OAuth 流程与额度查询移植自 [cc-switch](https://github.com/farion1231/cc-switch)（`reference-projects/cc-switch`，其额度实现又源自 CodexBar），与 Codex / Google / Kimi 账号托管（见 [codex.md](codex.md)、[google-accounts.md](google-accounts.md)）共用「隐藏 Site 复用」方案。

## 总体架构

```
xAI 设备码登录 / 导入凭证（~/.grok/auth.json）
        │ XaiAccountsApiController (api/admin/xai-accounts)
        ▼
XaiAccountProvisioner ──► XaiAccount（XaiAccounts 表）
        │                        │
        │                        └─ LinkedSiteId ──► 隐藏 Site（BaseUrl=https://api.x.ai/v1,
        │                                            SupportsOpenAi+SupportsResponses, ManagedSource="xai_oauth"）
        │                                            └─ SiteModelMapping ×N（模型映射）
        ▼
转发链路（OpenAI / Anthropic / Responses 客户端）
        │ ProxyProtocolResolver：OpenAI Chat 直传 /responses 与 /chat/completions 均原生；
        │ Anthropic 客户端桥接为 Responses（见下）
        ▼
api.x.ai/v1（Bearer access_token）
```

## 接入方式

| | SuperGrok（OAuth 设备码） |
|---|---|
| OAuth Issuer | `https://auth.x.ai`（OIDC Discovery 动态解析端点，进程内缓存） |
| 客户端身份 | client_id 与 **Grok CLI 一致**（`b1a00492-...`），token 对 grok.com 账单端点等效 |
| 授权流程 | RFC 8628 设备码（与 Kimi 同构：start-device-flow → 浏览器确认 → poll-token 轮询） |
| scope | `openid profile email offline_access grok-cli:access api:access` |
| 账号身份 | id_token / access_token 的 JWT `sub`（稳定账号标识，重复登录按此匹配既有账号），email/preferred_username 作展示名 |
| 上游推理端点 | `https://api.x.ai/v1`（OpenAI Chat 与 Responses 均为一等端点，无原生 Anthropic） |
| 额度查询 | grok.com gRPC-web 账单端点（见下） |
| token 生命周期 | access_token 约 6 小时，后台 `XaiTokenRefreshService` 提前 30 分钟刷新 |

常量定义：`src/AITool.Application/Xai/XaiContracts.cs`（`XaiConstants`）。

## 登录流程（RFC 8628 设备码）

1. `POST start-device-flow` → OIDC Discovery（端点强制校验 `https://auth.x.ai:443`，防 discovery 响应把凭证引向第三方主机）→ 设备授权请求，返回 user_code / verification_uri_complete。
2. 用户在新标签页打开授权页确认（或输入验证码）。
3. 前端按 `interval` 轮询 `POST poll-token`：`authorization_pending` 继续、`slow_down` 降频、成功即换取 token 并经 `XaiAccountProvisioner` 建账号（含隐藏站点 + 模型实拉）。

凭证导入：`POST import-credential`（粘贴 JSON）。支持两种形态：
- **Grok CLI 的 `~/.grok/auth.json`**：`{ "https://auth.x.ai::<client-id>": { "key": "<access_token>", "refresh_token": "...", "expires_at": "..." } }`，优先 OIDC 条目（`https://auth.x.ai::` 前缀），回退 legacy `/sign-in` 条目；
- **平铺 JSON**：`access_token` / `refresh_token` 字段。

仅有 refresh_token 时导入即刷新一次换 access_token。

## token 刷新与重新登录

- 后台 `XaiTokenRefreshService`（5 分钟扫描，提前 30 分钟刷新）刷 token 并写回 `XaiAccount.AccessToken` + 隐藏 Site 的 `ApiKey`（转发链路自动用新 token）。
- 刷新被上游拒绝的判定对齐 cc-switch：HTTP 401/403、HTTP 400 且响应体非法、`error=invalid_grant/invalid_token` → 抛 `XaiRefreshTokenInvalidException`，账号标记 **`RequiresReauth=true`** 并 12 小时退避；重新设备码登录（或编辑账号换 refresh_token）后自动清除。
- `RequiresReauth` 期间：转发链路的旧 token 自然 401；额度查询直接返回确定性错误（“refresh_token 已失效，请重新登录”），不再打上游。

## 额度查询（gRPC-web 账单端点）

- 端点：`POST https://grok.com/grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig`（Bearer token + 空帧 `[0,0,0,0,0]`）。
- 该端点**无公开 .proto**：`GrokQuotaParser`（`src/AITool.Infrastructure/Xai/GrokQuotaParser.cs`）以通用 protobuf 扫描启发式提取——已用百分比取路径末段为 1、值域 [0,100] 的 fixed32 字段（最浅最早优先）；重置时间取值域 [1.7e9, 2.1e9] 且晚于当前的 varint，优先精确路径 `[1,5,1]`；proto3 零用量省略字段时按周期标记特判 0%。
- 产出**单窗口**（`grok_credits`），按重置距离命名：4-12 天 → “每周额度”、20-45 天 → “月度额度”、其余 → “Credits 额度”。
- 缓存：protobuf 原始字节以 Base64 存 `XaiAccount.LastQuotaRawJson`，重启后免查询回放窗口；30 秒结果缓存 + 按账号 single-flight；纳入通用巡检（`IAccountQuotaProvider`，ProviderKey=`xai`），达到全局阈值自动禁用账号与隐藏站点。
- gRPC 应用层错误：`internal(13)/unavailable(14)` 视为瞬时（上层重试并保留上次成功值），其余为确定性失败；HTTP 408 同样按瞬时处理。

## 协议路径（ProxyProtocolResolver / ProxyProtocolBridge）

隐藏站点声明 `SupportsOpenAi=true, SupportsResponses=true, SupportsAnthropic=false`：

| 客户端协议 | 上游路径 | 说明 |
|---|---|---|
| OpenAI Chat | 直传 `/v1/chat/completions` | 原生 |
| Responses（Codex CLI 等） | 直传 `/v1/responses` | 原生（xAI Responses 为一等端点，支持 store:false / encrypted_content / reasoning effort） |
| Anthropic（Claude Code 等） | **Anthropic→Responses 直转桥**后发 `/v1/responses` | 复用 Antigravity 在用的双向桥（`ProxyProtocolBridge.BridgeAnthropicResponses.cs`，不经 Chat 中转，保留 reasoning/function_call 语义；响应回流经 `BuildAnthropicStreamFromResponses`） |

## Web 层接入点

- 控制器：`XaiAccountsApiController`（`api/admin/xai-accounts`，类级 OAuth 功能开关，端点清单见 [admin-api.md](admin-api.md)）。
- 额度服务：`XaiQuotaService`（巡检栈第 4 个 `IAccountQuotaProvider`，与 Codex/Google/Kimi 同构）。
- 前端：OAuth 管理页「Grok」——登录下拉 / 厂商过滤 / 设备码弹窗（与 Kimi 共用模板区域）/ 账号卡（含 `requiresReauth` 状态徽章）/ 额度刷新 / 模型拉取导入。
- 模型清单：`XaiModelFetcher`（GET `/v1/models` 实拉 + `XaiConstants.DefaultModels` 兜底；xAI 无别名体系，公开名 == 上游 ID）。

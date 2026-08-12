# OpenAI / Anthropic 协议 URL 支持对比

本文档整理当前项目 AITool 与 `reference-projects/CLIProxyAPI` 中 OpenAI、Anthropic/Claude 协议相关的 URL 支持情况。

本版规则：

- 只以 AITool 与 CLIProxyAPI 作为协议同步对比双方。
- 将 OpenAI / Anthropic 主协议接口与项目扩展、其他协议路径分开列出。
- OpenAI legacy 接口保留并单独标记，避免与当前主流接口混淆。

> CLIProxyAPI 位于解决方案根目录下的 `reference-projects/CLIProxyAPI`，保留独立 Git 仓库用于持续拉取上游更新。
>
> 字段级对齐明细请以工具最新生成的 [protocol-sync-report.md](protocol-sync-report.md) 为准；本文主要维护长期协议和 URL 参考信息。

## 结论先行

### AITool 与 CLIProxyAPI 共有的主协议接口

| 协议 | Method | URL | 说明 |
| --- | --- | --- | --- |
| OpenAI | GET | `/v1/models` | OpenAI 模型列表。|
| OpenAI | POST | `/v1/chat/completions` | Chat Completions。|
| OpenAI | POST | `/v1/completions` | Legacy Completions。|
| OpenAI | POST | `/v1/responses` | Responses API。|
| Anthropic | POST | `/v1/messages` | Anthropic Messages。|
| Anthropic | POST | `/v1/messages/count_tokens` | Anthropic Count Tokens。|

### CLIProxyAPI 已支持但 AITool 当前未实现的主协议接口

| 协议 | 分类 | Method | URL | 说明 |
| --- | --- | --- | --- | --- |
| OpenAI | 主协议 | POST | `/v1/images/generations` | 图像生成。|
| OpenAI | 主协议 | POST | `/v1/images/edits` | 图像编辑。|
| OpenAI | 主协议 | POST | `/v1/videos` | 视频创建。|
| OpenAI | 主协议 | GET | `/v1/videos/:id` | 视频查询。|

## OpenAI 主协议与兼容接口

| Method | URL | CLIProxyAPI | 当前项目 AITool | 备注 |
| --- | --- | --- | --- | --- |
| GET | `/v1/models` | ✅ | ✅ | 普通 OpenAI models 响应。|
| GET | `/v1/models/:model` | — | ✅ | AITool 单模型查询。|
| POST | `/v1/chat/completions` | ✅ | ✅ | Chat Completions。|
| POST | `/v1/completions` | ✅ | ✅ | AITool 将请求转入公共 Chat 链路，并输出 legacy `text_completion` 格式。|
| POST | `/v1/responses` | ✅ | ✅ | Responses API。|
| POST | `/v1/responses/compact` | ✅ 扩展 | ✅ 扩展 | Responses 压缩扩展。|
| GET | `/v1/responses` | ✅ WebSocket | ✅ WebSocket | Responses WebSocket 扩展。|
| POST | `/v1/embeddings` | — | ✅ | AITool 已提供 Embeddings 入口。|
| POST | `/v1/images/generations` | ✅ | — | 图像生成。|
| POST | `/v1/images/edits` | ✅ | — | 图像编辑。|
| POST | `/v1/videos` | ✅ | — | 视频创建。|
| GET | `/v1/videos/:id` | ✅ | — | 视频查询。|
| POST | `/v1/videos/generations` | ✅ 扩展 | — | 视频生成扩展。|
| POST | `/v1/videos/edits` | ✅ 扩展 | — | 视频编辑扩展。|
| POST | `/v1/videos/extensions` | ✅ 扩展 | — | 视频扩展。|
| GET | `/v1/realtime` | ✅ WebSocket | — | Realtime 扩展。|

### OpenAI 相关代码位置

#### CLIProxyAPI OpenAI 入口

- 主协议路由：[server_routes.go](reference-projects/CLIProxyAPI/internal/api/server_routes.go)
- OpenAI handlers：[openai_handlers.go](reference-projects/CLIProxyAPI/sdk/api/handlers/openai/openai_handlers.go)
- Responses handlers：[openai_responses_handlers.go](reference-projects/CLIProxyAPI/sdk/api/handlers/openai/openai_responses_handlers.go)

#### 当前项目 AITool OpenAI 入口

- OpenAI 主入口：[OpenAiProxyController.cs](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs)
- Responses HTTP / WebSocket：[OpenAiProxyController.Responses.cs](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs)
- OpenAI / Anthropic 流式处理：[OpenAiProxyController.Streaming.cs](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Streaming.cs)
- WebSocket、SSE、usage 和日志辅助：[OpenAiProxyController.Helpers.cs](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Helpers.cs)
- Legacy Completions 转换：[ProxyProtocolBridge.ResponseConvert.cs](src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.ResponseConvert.cs)

## Anthropic / Claude 主协议接口

| Method | URL | CLIProxyAPI | 当前项目 AITool | 备注 |
| --- | --- | --- | --- | --- |
| GET | `/v1/models` | ✅ | ✅ | 根据请求头或客户端标识返回 Anthropic models 格式。|
| GET | `/v1/models/:model` | — | ✅ | AITool 单模型查询。|
| POST | `/v1/messages` | ✅ | ✅ | Anthropic Messages。|
| POST | `/v1/messages/count_tokens` | ✅ | ✅ | Anthropic Count Tokens。|

### Anthropic `/v1/models` 识别方式

| 项目 | 判断方式 |
| --- | --- |
| CLIProxyAPI | `Anthropic-Version` 请求头，或以 `claude-cli` 开头的 User-Agent。|
| 当前项目 AITool | 根据 Anthropic 请求头识别并返回 Anthropic models 格式。|

### Anthropic 相关代码位置

#### CLIProxyAPI Anthropic 入口

- Claude models / Messages 路由：[server_routes.go](reference-projects/CLIProxyAPI/internal/api/server_routes.go)
- Claude Messages handler：[code_handlers.go](reference-projects/CLIProxyAPI/sdk/api/handlers/claude/code_handlers.go)

#### 当前项目 AITool Anthropic 入口

- Anthropic models 兼容入口：[OpenAiProxyController.cs](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs)
- Anthropic Count Tokens 和 Messages：[AnthropicProxyController.cs](src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs)

## 项目扩展与其他协议路径

这些路径不是通用 OpenAI 主协议，或属于 CLIProxyAPI / AITool 的特定客户端适配，应单独跟踪，不应直接作为主协议缺口处理。

| 项目 | Method | URL | 分类 | 说明 |
| --- | --- | --- | --- | --- |
| CLIProxyAPI / AITool | POST | `/v1/responses/compact` | Responses 扩展 | 用于 Responses 压缩。|
| CLIProxyAPI / AITool | GET | `/v1/responses` | WebSocket 扩展 | Responses WebSocket。|
| CLIProxyAPI | POST | `/v1/videos/generations` | 视频扩展 | 视频生成兼容入口。|
| CLIProxyAPI | POST | `/v1/videos/edits` | 视频扩展 | 视频编辑兼容入口。|
| CLIProxyAPI | POST | `/v1/videos/extensions` | 视频扩展 | 视频扩展入口。|
| CLIProxyAPI | GET/POST | `/v1/realtime*` | Realtime 扩展 | Realtime 和通话控制相关接口。|
| CLIProxyAPI | 多个 | `/backend-api/codex/responses*` | Codex alias | Codex 客户端后端兼容别名。|
| CLIProxyAPI | 多个 | `/api/provider/:provider/...` | Provider alias | 多 provider 别名层。|

## 当前项目兼容实现要点

### Legacy Completions 与 SSE 转换

AITool 的 `POST /v1/completions` 复用现有 OpenAI 公共代理链路：

- 保留访问密钥校验、路由选择、熔断和 fallback；
- 复用 OpenAI / Anthropic 协议桥接；
- 非流式响应转换为 legacy `text_completion`；
- 流式响应逐块转换为 legacy `text_completion` SSE，并保留 `data: [DONE]`。

相关实现见 [ProxyProtocolBridge.ResponseConvert.cs](src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.ResponseConvert.cs)、[OpenAiProxyController.Streaming.cs](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Streaming.cs) 和 [OpenAiCrossProtocolProxyTests.cs](tests/AITool.IntegrationTests/Proxy/OpenAiCrossProtocolProxyTests.cs)。

### Responses 流式处理

Responses 事件转换需要重点核对：

- `type`、`delta`、`text`、`index` 和 `output_index`；
- `response.output_text.delta`、函数调用参数增量和终止事件；
- tool call ID、arguments、finish reason 和 usage；
- HTTP 与 WebSocket 两种入口的事件顺序是否一致。

相关实现见 [ProxyProtocolBridge.Responses.cs](src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.Responses.cs) 和 [ChatApiController.cs](src/AITool.Web/Controllers/Admin/ChatApiController.cs)。

## 对当前项目的同步建议

ProtocolSyncCheck 每次运行应只检查：

- AITool 与 CLIProxyAPI 的路由差异；
- CLIProxyAPI 请求/响应字段与 AITool 字段处理的差异；
- 流式和非流式事件字段；
- tool call、usage、finish reason、model 等关键语义字段。

当报告出现“动态处理，无法确认”时，应结合报告中的字段位置和实际转换代码判断，不能仅根据字段名相同认定协议完全兼容。

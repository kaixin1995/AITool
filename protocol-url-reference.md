# OpenAI / Anthropic 协议 URL 支持对比

本文档整理当前项目、`reference-projects/new-api` 与 `reference-projects/CLIProxyAPI` 中 OpenAI、Anthropic/Claude 协议相关的 URL 支持情况。

本版规则：

- 已去掉 new-api 中注册但返回 501 的接口。
- 将“真正的 OpenAI / Anthropic 主协议接口”和“只是长得像 OpenAI 的项目扩展/其他协议路径”分开列出。
- OpenAI 的 legacy 接口会保留，但标记为 legacy，避免和当前主流接口混淆。

> 参考项目位于当前解决方案根目录下的 `reference-projects/`，该目录已被 `.gitignore` 屏蔽，两个项目保留各自 Git 仓库用于持续拉取上游更新。
>
> 维护约定：后续如果分析 new-api、CPA 或当前项目时发现 OpenAI / Anthropic 协议、URL、模型元数据或兼容策略有变化，应同步更新本文档，避免协议知识只停留在对话中。

## 结论先行

### 三方共有的主协议接口

| 协议 | Method | URL | 说明 |
| --- | --- | --- | --- |
| OpenAI | GET | `/v1/models` | 模型列表。三方都有，但 Anthropic 客户端识别方式不同。 |
| OpenAI | POST | `/v1/chat/completions` | Chat Completions。 |
| OpenAI | POST | `/v1/responses` | Responses API。 |
| Anthropic | GET | `/v1/models` | Anthropic models 格式。三方都有，但识别方式不同。 |
| Anthropic | POST | `/v1/messages` | Anthropic Messages。 |

### 两个参考项目都有、当前项目缺失的主协议接口

| 协议 | Method | URL | 说明 |
| --- | --- | --- | --- |
| OpenAI | GET | `/v1/responses` | CPA 与当前项目都支持 Responses WebSocket。 |
| OpenAI | POST | `/v1/images/generations` | 图像生成。new-api 与 CPA 都有。 |
| OpenAI | POST | `/v1/images/edits` | 图像编辑。new-api 与 CPA 都有。 |
| OpenAI | POST | `/v1/videos` | 视频创建。new-api 与 CPA 都有。 |
| OpenAI | GET | `/v1/videos/:id` | 视频任务/对象查询。new-api 与 CPA 都有。 |

### CPA 与当前项目共有、new-api 缺失的 Anthropic 接口

| 协议 | Method | URL | 说明 |
| --- | --- | --- | --- |
| Anthropic | POST | `/v1/messages/count_tokens` | Count Tokens。CPA 与当前项目都有。 |

## OpenAI 主协议 / 主流兼容接口对比

| Method | URL | new-api | CPA / CLIProxyAPI | 当前项目 AITool | 备注 |
| --- | --- | --- | --- | --- | --- |
| GET | `/v1/models` | ✅ | ✅ | ✅ | 模型列表。 |
| GET | `/v1/models/:model` | ✅ | — | ✅ | 单模型查询。当前项目已新增。 |
| POST | `/v1/chat/completions` | ✅ | ✅ | ✅ | Chat Completions。 |
| POST | `/v1/completions` | ✅ | ✅ | ✅ | OpenAI legacy Completions。当前项目已新增，并复用原有路由选择、熔断、兼容桥接链路；非流式和流式 SSE 都会从 Chat Completions 格式转换为 legacy `text_completion` 格式。 |
| POST | `/v1/responses` | ✅ | ✅ | ✅ | Responses API。 |
| POST | `/v1/responses/compact` | ✅ 扩展 | ✅ 扩展 | ✅ 扩展 | Responses 压缩扩展。当前项目已新增，复用 Responses 处理链路。 |
| GET | `/v1/realtime` | ✅ | — | — | Realtime WebSocket。new-api 有。 |
| POST | `/v1/embeddings` | ✅ | — | ✅ | Embeddings。当前项目已新增，并复用原有路由选择、熔断与兼容桥接链路。 |
| GET | `/v1/responses` | — | ✅ WebSocket | ✅ WebSocket | 当前项目已实现 Responses WebSocket，会继续复用现有路由选择、熔断、日志与兼容桥接能力。 |
| POST | `/v1/images/generations` | ✅ | ✅ | — | Images Generations。 |
| POST | `/v1/images/edits` | ✅ | ✅ | — | Images Edits。 |
| POST | `/v1/audio/transcriptions` | ✅ | — | — | Audio Transcriptions。 |
| POST | `/v1/audio/translations` | ✅ | — | — | Audio Translations。 |
| POST | `/v1/audio/speech` | ✅ | — | — | TTS。 |
| POST | `/v1/moderations` | ✅ | — | — | Moderations。 |
| POST | `/v1/videos` | ✅ | ✅ | — | 视频创建。 |
| GET | `/v1/videos/:id` | ✅ | ✅ | — | 视频查询。 |

### OpenAI 相关代码位置

#### new-api OpenAI 入口

- 主协议入口：[reference-projects/new-api/router/relay-router.go](reference-projects/new-api/router/relay-router.go)
- 视频入口：[reference-projects/new-api/router/video-router.go](reference-projects/new-api/router/video-router.go)
- 模型列表返回：[reference-projects/new-api/controller/model.go:208-316](reference-projects/new-api/controller/model.go#L208-L316)

#### CPA OpenAI 入口

- 主协议入口：[reference-projects/CLIProxyAPI/internal/api/server.go:381-400](reference-projects/CLIProxyAPI/internal/api/server.go#L381-L400)
- OpenAI models：[reference-projects/CLIProxyAPI/sdk/api/handlers/openai/openai_handlers.go:61-93](reference-projects/CLIProxyAPI/sdk/api/handlers/openai/openai_handlers.go#L61-L93)
- Chat Completions：[reference-projects/CLIProxyAPI/sdk/api/handlers/openai/openai_handlers.go:97-134](reference-projects/CLIProxyAPI/sdk/api/handlers/openai/openai_handlers.go#L97-L134)
- Completions：[reference-projects/CLIProxyAPI/sdk/api/handlers/openai/openai_handlers.go:151-179](reference-projects/CLIProxyAPI/sdk/api/handlers/openai/openai_handlers.go#L151-L179)
- Responses：[reference-projects/CLIProxyAPI/sdk/api/handlers/openai/openai_responses_handlers.go:366-393](reference-projects/CLIProxyAPI/sdk/api/handlers/openai/openai_responses_handlers.go#L366-L393)

#### 当前项目 AITool OpenAI 入口

- OpenAI/Anthropic models 兼容入口：[src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs)
- Completions / Chat Completions / Embeddings / Responses Compact：[src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs)
- Responses HTTP / WebSocket 主处理：[src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs)
- OpenAI / Anthropic 流式转发与 SSE 桥接：[src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Streaming.cs](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Streaming.cs)
- WebSocket、SSE、日志与追踪辅助方法：[src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Helpers.cs](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Helpers.cs)
- legacy Completions 请求、响应与 SSE 格式转换：[src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.ResponseConvert.cs](src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.ResponseConvert.cs)

## Anthropic / Claude 主协议接口对比

| Method | URL | new-api | CPA / CLIProxyAPI | 当前项目 AITool | 备注 |
| --- | --- | --- | --- | --- | --- |
| GET | `/v1/models` | ✅ | ✅ | ✅ | 三方都有，但识别 Anthropic 客户端的方式不同。 |
| GET | `/v1/models/:model` | ✅ | — | ✅ | Anthropic 单模型查询。当前项目复用 `/v1/models/{modelId}` 返回 Anthropic 格式。 |
| POST | `/v1/messages` | ✅ | ✅ | ✅ | Anthropic Messages。 |
| POST | `/v1/messages/count_tokens` | — | ✅ | ✅ | Anthropic Count Tokens。 |

### Anthropic `/v1/models` 识别方式

| 项目 | 判断方式 |
| --- | --- |
| new-api | 请求头同时包含 `x-api-key` 和 `anthropic-version` 时返回 Anthropic models 格式。 |
| CPA / CLIProxyAPI | `User-Agent` 以 `claude-cli` 开头时返回 Claude models 格式；也可走 `/api/provider/anthropic/v1/models` alias。 |
| 当前项目 AITool | 请求头包含 `x-api-key` 或 `anthropic-version` 时返回 Anthropic models 格式。 |

### Anthropic 相关代码位置

#### new-api Anthropic 入口

- Anthropic models 分流：[reference-projects/new-api/router/relay-router.go:22-40](reference-projects/new-api/router/relay-router.go#L22-L40)
- Anthropic Messages：[reference-projects/new-api/router/relay-router.go:86-89](reference-projects/new-api/router/relay-router.go#L86-L89)

#### CPA Anthropic 入口

- Claude models 分流：[reference-projects/CLIProxyAPI/internal/api/server.go:839-868](reference-projects/CLIProxyAPI/internal/api/server.go#L839-L868)
- Claude Messages / Count Tokens 路由：[reference-projects/CLIProxyAPI/internal/api/server.go:395-396](reference-projects/CLIProxyAPI/internal/api/server.go#L395-L396)
- Claude Messages 处理函数：[reference-projects/CLIProxyAPI/sdk/api/handlers/claude/code_handlers.go:67-86](reference-projects/CLIProxyAPI/sdk/api/handlers/claude/code_handlers.go#L67-L86)
- Claude Count Tokens 处理函数：[reference-projects/CLIProxyAPI/sdk/api/handlers/claude/code_handlers.go:96-126](reference-projects/CLIProxyAPI/sdk/api/handlers/claude/code_handlers.go#L96-L126)
- Claude models 处理函数：[reference-projects/CLIProxyAPI/sdk/api/handlers/claude/code_handlers.go:133-151](reference-projects/CLIProxyAPI/sdk/api/handlers/claude/code_handlers.go#L133-L151)

#### 当前项目 AITool Anthropic 入口

- Anthropic models 兼容入口：[src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs)
- Anthropic Count Tokens：[src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs:98-127](src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs#L98-L127)
- Anthropic Messages：[src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs:129-159](src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs#L129-L159)

## 看起来像 OpenAI，但不应放入 OpenAI 主协议表的路径

这些路径不是 OpenAI 官方主协议，或者属于项目扩展、其他协议、特定客户端适配。它们可以作为“兼容扩展”跟踪，但不建议当作 OpenAI 主协议缺口处理。

| 项目 | Method | URL | 分类 | 说明 |
| --- | --- | --- | --- | --- |
| new-api / CPA / 当前项目 | POST | `/v1/responses/compact` | 项目/Codex 扩展 | 不是通用 OpenAI 官方 REST 接口。可作为 Codex/Responses 压缩扩展单独跟踪。 |
| CPA / 当前项目 | GET | `/v1/responses` | Codex/Responses WebSocket 扩展 | CPA 与当前项目都支持 Responses WebSocket；这不是普通 OpenAI REST GET 接口。 |
| new-api | POST | `/v1/rerank` | 其他协议 | 更接近 Jina/Cohere Rerank，不是 OpenAI 主协议。 |
| new-api | POST | `/v1/models/*path` | Gemini 兼容 | 路径长得像 `/v1/models`，但实际按 Gemini relay 处理。 |
| new-api | POST | `/v1/engines/:model/embeddings` | OpenAI legacy/旧式兼容 | `engines` 是老式 OpenAI 路径，不是当前主流 OpenAI embeddings 路径。当前主流是 `/v1/embeddings`。 |
| new-api | POST | `/v1/edits` | OpenAI legacy/非当前主流 | 旧式 edits 路径，不应和当前 `/v1/images/edits` 混为一类。 |
| new-api | POST | `/v1/video/generations` | 项目扩展 | 单数 `video` 路径，不是主流 OpenAI videos 路径。 |
| new-api | GET | `/v1/video/generations/:task_id` | 项目扩展 | 同上。 |
| new-api | POST | `/v1/videos/:video_id/remix` | 项目扩展 | 视频 remix 扩展，不建议算主协议。 |
| new-api | GET | `/v1/videos/:task_id/content` | 内容代理/扩展 | 可作为视频内容代理跟踪，不建议算文本/主协议缺口。 |
| CPA | POST | `/v1/videos/generations` | xAI/视频扩展 | CPA 处理函数名是 `XAIVideosGenerations`，更像 xAI video API 适配。 |
| CPA | POST | `/v1/videos/edits` | xAI/视频扩展 | 同上。 |
| CPA | POST | `/v1/videos/extensions` | xAI/视频扩展 | 同上。 |
| CPA | GET | `/v1/models?client_version=...` | Codex 客户端特殊格式 | 路径仍是 `/v1/models`，但响应格式是 Codex client models，不是普通 OpenAI models。 |
| CPA | GET | `/backend-api/codex/responses` | Codex alias | ChatGPT/Codex 后端兼容别名，不是 OpenAI 主协议。 |
| CPA | POST | `/backend-api/codex/responses` | Codex alias | 同上。 |
| CPA | POST | `/backend-api/codex/responses/compact` | Codex alias | 同上。 |
| CPA | 多个 | `/api/provider/:provider/...` | Amp/provider alias | CPA 给 Amp CLI 和多 provider 提供的别名层，不是 OpenAI 官方路径。 |
| CPA | 多个 | `/api/provider/anthropic/...` | Anthropic provider alias | Anthropic alias 层，不是 OpenAI 路径。 |

## 已剔除的 501 路由

new-api 里以下路由虽然被注册，但统一走 `RelayNotImplemented` 返回 501，因此本版不再放入支持清单，也不作为协议同步缺口：

- `POST /v1/images/variations`
- `GET /v1/files`
- `POST /v1/files`
- `DELETE /v1/files/:id`
- `GET /v1/files/:id`
- `GET /v1/files/:id/content`
- `POST /v1/fine-tunes`
- `GET /v1/fine-tunes`
- `GET /v1/fine-tunes/:id`
- `POST /v1/fine-tunes/:id/cancel`
- `GET /v1/fine-tunes/:id/events`
- `DELETE /v1/models/:model`

相关实现见 [reference-projects/new-api/controller/relay.go:446-456](reference-projects/new-api/controller/relay.go#L446-L456)。

## 模型元数据与上下文长度

### new-api 模型元数据

new-api 当前后端 `Pricing` 结构不返回 `context_length` 或 `max_output_tokens`，见 [reference-projects/new-api/model/pricing.go:17-38](reference-projects/new-api/model/pricing.go#L17-L38)。前端在类型中预留了这些字段，但注释说明后端尚未返回，见 [reference-projects/new-api/web/default/src/features/pricing/types.ts:57-64](reference-projects/new-api/web/default/src/features/pricing/types.ts#L57-L64)。

前端实际通过 `inferModelMetadata()` 根据模型名、端点类型等推断上下文长度，见 [reference-projects/new-api/web/default/src/features/pricing/lib/model-metadata.ts:251-292](reference-projects/new-api/web/default/src/features/pricing/lib/model-metadata.ts#L251-L292) 与 [model-metadata.ts:338-343](reference-projects/new-api/web/default/src/features/pricing/lib/model-metadata.ts#L338-L343)。

关键结论：new-api 的上下文长度目前主要是前端启发式推断，不是后端模型接口真实返回。

### CPA 模型元数据

CPA 使用集中式模型注册表 `ModelInfo`。其中：

- `ContextLength` 映射 JSON 字段 `context_length`
- `MaxCompletionTokens` 映射 JSON 字段 `max_completion_tokens`
- Gemini 使用 `InputTokenLimit` / `OutputTokenLimit`

结构定义见 [reference-projects/CLIProxyAPI/internal/registry/model_registry.go:20-49](reference-projects/CLIProxyAPI/internal/registry/model_registry.go#L20-L49)。

静态模型数据来自 [reference-projects/CLIProxyAPI/internal/registry/models/models.json](reference-projects/CLIProxyAPI/internal/registry/models/models.json)，并且启动后会从远程模型目录刷新，见 [reference-projects/CLIProxyAPI/internal/registry/model_updater.go:21-27](reference-projects/CLIProxyAPI/internal/registry/model_updater.go#L21-L27) 与 [model_updater.go:114-129](reference-projects/CLIProxyAPI/internal/registry/model_updater.go#L114-L129)。

不同协议返回差异：

| 协议/接口 | 是否返回上下文长度 | 字段 | 代码位置 | 说明 |
| --- | --- | --- | --- | --- |
| OpenAI 普通 `/v1/models` | 否 | 无 | [openai_handlers.go:66-87](reference-projects/CLIProxyAPI/sdk/api/handlers/openai/openai_handlers.go#L66-L87) | 注册表转换时有 `context_length`，但普通 OpenAI models 接口过滤后只保留 `id`、`object`、`created`、`owned_by`。 |
| Claude `/v1/models` | 否 | 无 | [model_registry.go:1149-1164](reference-projects/CLIProxyAPI/internal/registry/model_registry.go#L1149-L1164) | Claude 格式转换本身不带上下文长度。 |
| Gemini `/v1beta/models` | 是 | `inputTokenLimit` / `outputTokenLimit` | [model_registry.go:1166-1187](reference-projects/CLIProxyAPI/internal/registry/model_registry.go#L1166-L1187) | Gemini 原生语义下，`inputTokenLimit` 可视为输入上下文窗口。 |
| Codex `client_version` 模型接口 | 是 | `context_window` / `max_context_window` | [codex_client_models.go:99-141](reference-projects/CLIProxyAPI/sdk/api/handlers/openai/codex_client_models.go#L99-L141) | 会从 `context_length` 或模板映射为 Codex 客户端字段。 |

## 当前项目兼容实现要点

### legacy Completions 与 SSE 转换

当前项目的 `POST /v1/completions` 不单独复制一套路由逻辑，而是先将 legacy Completions 请求转为 Chat Completions 请求，再复用现有 OpenAI 公共代理链路，因此保留原有：

- 访问密钥校验
- 路由选择与路由顺序
- 熔断与 fallback
- OpenAI / Anthropic 协议自动桥接
- 流式与非流式处理分支
- 用量日志、开发者追踪和对话日志

返回格式上需要注意：

- 非流式响应会从 Chat Completions 响应转换为 legacy `text_completion` 响应。
- 流式响应会逐个 SSE 块从 `chat.completion.chunk` 转换为 legacy `text_completion` SSE 块，并保留 `data: [DONE]` 结束标记。
- Anthropic 上游流式响应会先桥接为 Chat Completions SSE，再通过同一个 SSE 块转换器降级为 legacy Completions SSE，避免客户端收到 `delta` / `chat.completion.chunk` 格式。

相关实现见 [ProxyProtocolBridge.ResponseConvert.cs](src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.ResponseConvert.cs)、[OpenAiProxyController.Streaming.cs](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Streaming.cs) 与测试 [OpenAiCrossProtocolProxyTests.cs](tests/AITool.IntegrationTests/Proxy/OpenAiCrossProtocolProxyTests.cs)。

### 控制器拆分原则

`OpenAiProxyController` 已拆为 partial 文件，降低单文件体积并保持原有功能链路不变：

- [OpenAiProxyController.cs](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs)：主入口、模型、Completions、Chat Completions、Embeddings、公共 OpenAI 风格处理链路。
- [OpenAiProxyController.Responses.cs](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs)：Responses HTTP 与 WebSocket 主流程。
- [OpenAiProxyController.Streaming.cs](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Streaming.cs)：OpenAI / Anthropic SSE 透传、桥接和 legacy Completions 流式转换。
- [OpenAiProxyController.Helpers.cs](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Helpers.cs)：WebSocket、SSE、usage、日志、追踪等辅助逻辑。

后续继续新增 OpenAI 兼容接口时，优先复用现有公共处理函数和流式桥接函数；确需新增大量逻辑时应继续按功能拆分 partial 文件，避免控制器主文件再次膨胀。

## 已观察到的兼容性现象

### CPA 旧版本在 Claude Code 链路上的 `System messages are not allowed`

已观察到一种现象：

- 当前项目接入 CPA 时，普通对话测试可能正常
- 但通过 Claude Code 调用时，链路详情里会出现：`{"detail":"System messages are not allowed"}`
- 升级 CPA 后，该问题可能恢复正常

这更像是 **CPA 或其下游特定上游（尤其 Codex / Responses 类链路）对 system / developer 角色处理的兼容问题**，而不太像当前项目自己的通用代理错误。判断依据：

- 当前项目自身错误通常是 `error.message` 风格，不是 `detail` 风格。
- CPA 源码里有针对这个报错的测试与修复痕迹，明确提到过这是一个真实存在的问题：
  - [reference-projects/CLIProxyAPI/internal/translator/codex/openai/responses/codex_openai-responses_request_test.go:168-220](reference-projects/CLIProxyAPI/internal/translator/codex/openai/responses/codex_openai-responses_request_test.go#L168-L220)
- Claude Code 通常会稳定携带 system 提示词，比普通手工对话更容易触发这类兼容问题。

建议：

- 优先升级 CPA。
- 如果当前项目接入的是 CPA，且 CPA 站点本身支持 Anthropic `/v1/messages`，优先在当前项目里把该站点标记为支持 Anthropic，尽量走 **Anthropic → Anthropic 直连**，减少 **Anthropic → OpenAI 桥接** 带来的 system/developer 角色兼容风险。
- 如果再次出现类似问题，优先查看链路详情里的：
  - 实际上游协议类型
  - 实际请求路径（`/v1/messages` 还是 `/v1/chat/completions` / `/v1/responses`）
  - 目标模型是否属于 Codex / GPT-5.x / Responses 类模型

## 对当前项目的同步建议

优先评估这些“参考项目都支持但当前项目缺失”的主协议接口：

- `POST /v1/images/generations`
- `POST /v1/images/edits`
- `POST /v1/videos`
- `GET /v1/videos/:id`

如果只聚焦 Claude Code / OpenAI Responses / 文本中转，当前已补齐：

- `GET /v1/responses` WebSocket
- `POST /v1/completions` legacy 非流式与流式 SSE 格式转换

继续补 `/v1/images/generations`、`/v1/images/edits`、`/v1/videos*` 时，尽量继续复用当前已经抽出的 OpenAI 公共处理链路，避免丢失现有路由选择、熔断、兼容桥接、用量日志、开发者追踪和对话日志能力。

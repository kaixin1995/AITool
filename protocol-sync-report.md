# 协议同步检查报告

> 本报告由 `tools/ProtocolSyncCheck` 自动生成，用于检查当前项目已有接口在参考项目（new-api、CPA）中是否仍然被支持。

## 扫描摘要

| 项目 | 已识别路由数 | 缺失扫描文件 |
| --- | ---: | --- |
| 当前项目 AITool | 10 | — |
| new-api | 35 | — |
| CPA / CLIProxyAPI | 15 | — |

## 当前项目已有路由同步状态

> 以下为当前项目已实现的路由在各参考项目中的支持情况。⚠️ 表示该参考项目中未检测到此路由，可能意味着协议已变更或移除。

| 协议 | 分类 | Method | URL | 说明 | 代码位置 | new-api | CPA / CLIProxyAPI |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Anthropic | 主协议 | POST | `/v1/messages` | Anthropic Messages | [src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs:132](src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs#L132) | ✅ | ✅ |
| Anthropic | 主协议 | POST | `/v1/messages/count_tokens` | Anthropic Count Tokens | [src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs:101](src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs#L101) | ⚠️ 未检测到 | ✅ |
| OpenAI | 主协议 | POST | `/v1/chat/completions` | Chat Completions | [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs:361](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs#L361) | ✅ | ✅ |
| OpenAI | legacy | POST | `/v1/completions` | legacy Completions | [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs:312](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs#L312) | ✅ | ✅ |
| OpenAI | 主协议 | POST | `/v1/embeddings` | Embeddings | [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs:423](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs#L423) | ✅ | ⚠️ 未检测到 |
| OpenAI | 主协议 | GET | `/v1/models` | 模型列表 | [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs:177](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs#L177) | ⚠️ 未检测到 | ✅ |
| OpenAI | 主协议 | GET | `/v1/models/:model` | 单模型查询 | [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs:248](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs#L248) | ✅ | ⚠️ 未检测到 |
| OpenAI | 扩展 | GET | `/v1/responses` | Responses WebSocket 扩展 | [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs:24](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs#L24) | ⚠️ 未检测到 | ✅ |
| OpenAI | 主协议 | POST | `/v1/responses` | Responses API | [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs:94](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs#L94) | ✅ | ✅ |
| OpenAI | 扩展 | POST | `/v1/responses/compact` | Responses compact 扩展 | [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs:467](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs#L467) | ✅ | ✅ |

> ⚠️ 存在参考项目未检测到的路由，建议核实参考项目是否移除或变更了该接口。

## 参考信息：参考项目已支持但当前项目暂未实现的路由

> 以下路由仅作参考，不视为同步缺口。后续如需新增接口可优先考虑这些路由。

| 协议 | 分类 | Method | URL | 参考项目 | 说明 |
| --- | --- | --- | --- | --- | --- |
| OpenAI | 主协议 | POST | `/v1/audio/speech` | new-api | 语音合成 |
| OpenAI | 主协议 | POST | `/v1/audio/transcriptions` | new-api | 音频转录 |
| OpenAI | 主协议 | POST | `/v1/audio/translations` | new-api | 音频翻译 |
| OpenAI | legacy | POST | `/v1/edits` | new-api | 旧式 edits |
| OpenAI | 主协议 | POST | `/v1/images/edits` | new-api、CPA / CLIProxyAPI | 图像编辑 |
| OpenAI | 主协议 | POST | `/v1/images/generations` | new-api、CPA / CLIProxyAPI | 图像生成 |
| OpenAI | 主协议 | POST | `/v1/moderations` | new-api | Moderations |
| OpenAI | 主协议 | POST | `/v1/videos` | new-api、CPA / CLIProxyAPI | 视频创建 |
| OpenAI | 主协议 | GET | `/v1/videos/:id` | new-api、CPA / CLIProxyAPI | 视频查询 |

## 已识别路由支持矩阵

| 协议 | 分类 | Method | URL | 说明 | 当前项目 AITool | new-api | CPA / CLIProxyAPI |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Anthropic | 主协议 | POST | `/v1/messages` | Anthropic Messages | ✅ [src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs:132](src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs#L132) | ✅ [reference-projects/new-api/router/relay-router.go:88](reference-projects/new-api/router/relay-router.go#L88) | ✅ [reference-projects/CLIProxyAPI/internal/api/server.go:395](reference-projects/CLIProxyAPI/internal/api/server.go#L395) |
| Anthropic | 主协议 | POST | `/v1/messages/count_tokens` | Anthropic Count Tokens | ✅ [src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs:101](src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs#L101) | — | ✅ [reference-projects/CLIProxyAPI/internal/api/server.go:396](reference-projects/CLIProxyAPI/internal/api/server.go#L396) |
| OpenAI | legacy | POST | `/v1/completions` | legacy Completions | ✅ [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs:312](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs#L312) | ✅ [reference-projects/new-api/router/relay-router.go:93](reference-projects/new-api/router/relay-router.go#L93) | ✅ [reference-projects/CLIProxyAPI/internal/api/server.go:387](reference-projects/CLIProxyAPI/internal/api/server.go#L387) |
| OpenAI | legacy | POST | `/v1/edits` | 旧式 edits | — | ✅ [reference-projects/new-api/router/relay-router.go:109](reference-projects/new-api/router/relay-router.go#L109) | — |
| OpenAI | 主协议 | POST | `/v1/audio/speech` | 语音合成 | — | ✅ [reference-projects/new-api/router/relay-router.go:131](reference-projects/new-api/router/relay-router.go#L131) | — |
| OpenAI | 主协议 | POST | `/v1/audio/transcriptions` | 音频转录 | — | ✅ [reference-projects/new-api/router/relay-router.go:125](reference-projects/new-api/router/relay-router.go#L125) | — |
| OpenAI | 主协议 | POST | `/v1/audio/translations` | 音频翻译 | — | ✅ [reference-projects/new-api/router/relay-router.go:128](reference-projects/new-api/router/relay-router.go#L128) | — |
| OpenAI | 主协议 | POST | `/v1/chat/completions` | Chat Completions | ✅ [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs:361](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs#L361) | ✅ [reference-projects/new-api/router/relay-router.go:96](reference-projects/new-api/router/relay-router.go#L96) | ✅ [reference-projects/CLIProxyAPI/internal/api/server.go:386](reference-projects/CLIProxyAPI/internal/api/server.go#L386) |
| OpenAI | 主协议 | POST | `/v1/embeddings` | Embeddings | ✅ [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs:423](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs#L423) | ✅ [reference-projects/new-api/router/relay-router.go:120](reference-projects/new-api/router/relay-router.go#L120) | — |
| OpenAI | 主协议 | POST | `/v1/images/edits` | 图像编辑 | — | ✅ [reference-projects/new-api/router/relay-router.go:115](reference-projects/new-api/router/relay-router.go#L115) | ✅ [reference-projects/CLIProxyAPI/internal/api/server.go:389](reference-projects/CLIProxyAPI/internal/api/server.go#L389) |
| OpenAI | 主协议 | POST | `/v1/images/generations` | 图像生成 | — | ✅ [reference-projects/new-api/router/relay-router.go:112](reference-projects/new-api/router/relay-router.go#L112) | ✅ [reference-projects/CLIProxyAPI/internal/api/server.go:388](reference-projects/CLIProxyAPI/internal/api/server.go#L388) |
| OpenAI | 主协议 | GET | `/v1/models` | 模型列表 | ✅ [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs:177](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs#L177) | — | ✅ [reference-projects/CLIProxyAPI/internal/api/server.go:385](reference-projects/CLIProxyAPI/internal/api/server.go#L385) |
| OpenAI | 主协议 | GET | `/v1/models/:model` | 单模型查询 | ✅ [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs:248](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs#L248) | ✅ [reference-projects/new-api/router/relay-router.go:34](reference-projects/new-api/router/relay-router.go#L34) | — |
| OpenAI | 主协议 | POST | `/v1/moderations` | Moderations | — | ✅ [reference-projects/new-api/router/relay-router.go:149](reference-projects/new-api/router/relay-router.go#L149) | — |
| OpenAI | 主协议 | POST | `/v1/responses` | Responses API | ✅ [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs:94](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs#L94) | ✅ [reference-projects/new-api/router/relay-router.go:101](reference-projects/new-api/router/relay-router.go#L101) | ✅ [reference-projects/CLIProxyAPI/internal/api/server.go:398](reference-projects/CLIProxyAPI/internal/api/server.go#L398) |
| OpenAI | 主协议 | POST | `/v1/videos` | 视频创建 | — | ✅ [reference-projects/new-api/router/video-router.go:30](reference-projects/new-api/router/video-router.go#L30) | ✅ [reference-projects/CLIProxyAPI/internal/api/server.go:390](reference-projects/CLIProxyAPI/internal/api/server.go#L390) |
| OpenAI | 主协议 | GET | `/v1/videos/:id` | 视频查询 | — | ✅ [reference-projects/new-api/router/video-router.go:31](reference-projects/new-api/router/video-router.go#L31) | ✅ [reference-projects/CLIProxyAPI/internal/api/server.go:394](reference-projects/CLIProxyAPI/internal/api/server.go#L394) |
| OpenAI | 扩展 | POST | `/v1/engines/:model/embeddings` | 旧式 embeddings 兼容路径 | — | ✅ [reference-projects/new-api/router/relay-router.go:141](reference-projects/new-api/router/relay-router.go#L141) | — |
| OpenAI | 扩展 | POST | `/v1/models/*path` | Gemini 兼容路径 | — | ✅ [reference-projects/new-api/router/relay-router.go:144](reference-projects/new-api/router/relay-router.go#L144) | — |
| OpenAI | 扩展 | GET | `/v1/realtime` | Realtime WebSocket | — | ✅ [reference-projects/new-api/router/relay-router.go:78](reference-projects/new-api/router/relay-router.go#L78) | — |
| OpenAI | 扩展 | POST | `/v1/rerank` | Rerank 扩展 | — | ✅ [reference-projects/new-api/router/relay-router.go:136](reference-projects/new-api/router/relay-router.go#L136) | — |
| OpenAI | 扩展 | GET | `/v1/responses` | Responses WebSocket 扩展 | ✅ [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs:24](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs#L24) | — | ✅ [reference-projects/CLIProxyAPI/internal/api/server.go:397](reference-projects/CLIProxyAPI/internal/api/server.go#L397) |
| OpenAI | 扩展 | POST | `/v1/responses/compact` | Responses compact 扩展 | ✅ [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs:467](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs#L467) | ✅ [reference-projects/new-api/router/relay-router.go:104](reference-projects/new-api/router/relay-router.go#L104) | ✅ [reference-projects/CLIProxyAPI/internal/api/server.go:399](reference-projects/CLIProxyAPI/internal/api/server.go#L399) |
| OpenAI | 扩展 | POST | `/v1/video/generations` | 视频生成扩展 | — | ✅ [reference-projects/new-api/router/video-router.go:23](reference-projects/new-api/router/video-router.go#L23) | — |
| OpenAI | 扩展 | GET | `/v1/video/generations/:id` | 视频任务查询扩展 | — | ✅ [reference-projects/new-api/router/video-router.go:24](reference-projects/new-api/router/video-router.go#L24) | — |
| OpenAI | 扩展 | POST | `/v1/videos/:video_id/remix` | 视频 remix 扩展 | — | ✅ [reference-projects/new-api/router/video-router.go:25](reference-projects/new-api/router/video-router.go#L25) | — |
| OpenAI | 扩展 | POST | `/v1/videos/edits` | xAI 视频编辑扩展 | — | — | ✅ [reference-projects/CLIProxyAPI/internal/api/server.go:392](reference-projects/CLIProxyAPI/internal/api/server.go#L392) |
| OpenAI | 扩展 | POST | `/v1/videos/extensions` | xAI 视频扩展 | — | — | ✅ [reference-projects/CLIProxyAPI/internal/api/server.go:393](reference-projects/CLIProxyAPI/internal/api/server.go#L393) |
| OpenAI | 扩展 | POST | `/v1/videos/generations` | xAI 视频生成扩展 | — | — | ✅ [reference-projects/CLIProxyAPI/internal/api/server.go:391](reference-projects/CLIProxyAPI/internal/api/server.go#L391) |

## 已识别但不作为同步缺口的 501 / stub 路由

| 项目 | Method | URL | 说明 | 代码位置 |
| --- | --- | --- | --- | --- |
| new-api | GET | `/v1/files` | new-api 501 files | [reference-projects/new-api/router/relay-router.go:155](reference-projects/new-api/router/relay-router.go#L155) |
| new-api | POST | `/v1/files` | new-api 501 files | [reference-projects/new-api/router/relay-router.go:156](reference-projects/new-api/router/relay-router.go#L156) |
| new-api | DELETE | `/v1/files/:id` | new-api 501 files | [reference-projects/new-api/router/relay-router.go:157](reference-projects/new-api/router/relay-router.go#L157) |
| new-api | GET | `/v1/files/:id` | new-api 501 files | [reference-projects/new-api/router/relay-router.go:158](reference-projects/new-api/router/relay-router.go#L158) |
| new-api | GET | `/v1/files/:id/content` | new-api 501 files | [reference-projects/new-api/router/relay-router.go:159](reference-projects/new-api/router/relay-router.go#L159) |
| new-api | GET | `/v1/fine-tunes` | new-api 501 fine-tunes | [reference-projects/new-api/router/relay-router.go:161](reference-projects/new-api/router/relay-router.go#L161) |
| new-api | POST | `/v1/fine-tunes` | new-api 501 fine-tunes | [reference-projects/new-api/router/relay-router.go:160](reference-projects/new-api/router/relay-router.go#L160) |
| new-api | GET | `/v1/fine-tunes/:id` | new-api 501 fine-tunes | [reference-projects/new-api/router/relay-router.go:162](reference-projects/new-api/router/relay-router.go#L162) |
| new-api | POST | `/v1/fine-tunes/:id/cancel` | new-api 501 fine-tunes | [reference-projects/new-api/router/relay-router.go:163](reference-projects/new-api/router/relay-router.go#L163) |
| new-api | GET | `/v1/fine-tunes/:id/events` | new-api 501 fine-tunes | [reference-projects/new-api/router/relay-router.go:164](reference-projects/new-api/router/relay-router.go#L164) |
| new-api | POST | `/v1/images/variations` | new-api 501 图像 variations | [reference-projects/new-api/router/relay-router.go:154](reference-projects/new-api/router/relay-router.go#L154) |
| new-api | DELETE | `/v1/models/:model` | new-api 501 model delete | [reference-projects/new-api/router/relay-router.go:165](reference-projects/new-api/router/relay-router.go#L165) |

## 覆盖范围说明

本工具第一版只扫描当前项目关心的 OpenAI / Anthropic 主协议、legacy 接口和已知扩展路径。Gemini、MJ、Suno、Jimeng、管理后台、OAuth、健康检查和 provider alias 暂不作为主协议同步缺口。后续如果需要跟踪新的协议族，应先把路径加入工具中的 `ProtocolCatalog`。 

import { httpGet } from './http'
import { getAccessToken } from './http'

// 与后端 ChatApiController 的 ChatModelItem / ChatModelTargetItem 对齐。
export interface ChatModel {
  modelId: string
  displayName: string
  availableSiteCount: number
}
export interface ChatModelTarget {
  mappingId: string
  modelId: string
  modelDisplayName: string
  siteName: string
  siteModelName: string
}

// 与后端 ChatSendRequest 对齐（注意是 modelId/message 单条，不是 OpenAI 的 model/messages 数组）。
export interface ChatSendOptions {
  modelId: string
  message: string
  mappingId?: string
  enableReasoning?: boolean
  enableStreaming?: boolean
  reasoningEffort?: string
  signal?: AbortSignal
}

// 非流式响应（与后端 ChatSendResult 对齐）。
export interface ChatAttemptResult {
  attemptIndex?: number
  siteName?: string
  attemptedModel?: string
  siteModelName?: string
  status?: string
  errorMessage?: string
  isFinalResult?: boolean
  isStreaming?: boolean
  inputTokens?: number
  cachedTokens?: number
  outputTokens?: number
  totalTokens?: number
  firstTokenLatencyMs?: number
  totalDurationMs?: number
  requestBody?: string
  responseBody?: string
  forwardingMode?: string
  upstreamProtocolType?: string
}

export interface ChatSendResult {
  success: boolean
  content: string
  reasoningContent?: string
  error?: string | null
  durationMs?: number
  requestId?: string | null
  reasoningEnabled?: boolean
  isStreaming?: boolean
  inputTokens?: number
  cachedTokens?: number
  outputTokens?: number
  totalTokens?: number
  firstTokenLatencyMs?: number
  totalDurationMs?: number
  attempts?: ChatAttemptResult[]
}

export async function getChatModels(): Promise<ChatModel[]> {
  return httpGet<ChatModel[]>('/api/admin/chat/models')
}

export async function getChatTargets(modelId?: string): Promise<ChatModelTarget[]> {
  if (!modelId) return httpGet<ChatModelTarget[]>('/api/admin/chat/targets')
  return httpGet<ChatModelTarget[]>(`/api/admin/chat/models/${modelId}/targets`)
}

// 非流式发送。
export async function sendChat(opts: ChatSendOptions): Promise<ChatSendResult> {
  const body = {
    modelId: opts.modelId,
    mappingId: opts.mappingId ?? '00000000-0000-0000-0000-000000000000',
    message: opts.message,
    enableReasoning: opts.enableReasoning ?? false,
    enableStreaming: false,
    reasoningEffort: opts.reasoningEffort ?? 'high'
  }
  const resp = await fetch('/api/admin/chat/send', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${getAccessToken()}` },
    body: JSON.stringify(body),
    signal: opts.signal
  })
  if (!resp.ok) {
    const err = await resp.json().catch(() => ({}))
    throw new Error(err.message || `请求失败 (${resp.status})`)
  }
  return (await resp.json()) as ChatSendResult
}

// SSE 事件回调。
export interface ChatStreamCallbacks {
  onToken: (text: string) => void
  onReasoning?: (text: string) => void
  onMeta?: (meta: unknown) => void
  onDone?: () => void
  onError: (err: Error) => void
}

export function parseSseBlock(block: string): { event: string; data: string } | null {
  let event = 'message'
  const dataLines: string[] = []
  for (const line of block.split(/\r?\n/)) {
    if (line.startsWith('event:')) {
      event = line.slice(6).trim()
    } else if (line.startsWith('data:')) {
      dataLines.push(line.slice(5).trimStart())
    }
  }
  return dataLines.length > 0 ? { event, data: dataLines.join('\n') } : null
}

// 流式发送：后端用命名事件（event: token/reasoning/meta/done/error），不是 OpenAI 的 [DONE]。
// 必须解析 event: 行确定事件类型，再从 data: 行取 payload。
export async function sendChatStream(opts: ChatSendOptions, cb: ChatStreamCallbacks): Promise<void> {
  const body = {
    modelId: opts.modelId,
    mappingId: opts.mappingId ?? '00000000-0000-0000-0000-000000000000',
    message: opts.message,
    enableReasoning: opts.enableReasoning ?? false,
    enableStreaming: true,
    reasoningEffort: opts.reasoningEffort ?? 'high'
  }
  let reader: ReadableStreamDefaultReader<Uint8Array> | null = null
  try {
    const resp = await fetch('/api/admin/chat/send-stream', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'text/event-stream',
        Authorization: `Bearer ${getAccessToken()}`
      },
      body: JSON.stringify(body),
      signal: opts.signal
    })
    if (!resp.ok || !resp.body) {
      const err = await resp.json().catch(() => ({}))
      throw new Error(err.message || `请求失败 (${resp.status})`)
    }
    const contentType = resp.headers.get('content-type') ?? ''
    if (!contentType.toLowerCase().includes('text/event-stream')) {
      throw new Error(`预期 SSE 响应，实际为 ${contentType || '未知类型'}`)
    }

    const handleBlock = (block: string): boolean => {
      const event = parseSseBlock(block)
      if (!event) return false
      let payload: { content?: string; message?: string; attempts?: ChatAttemptResult[] } = {}
      try { payload = JSON.parse(event.data) } catch { /* 非 JSON 忽略 */ }
      if (event.event === 'token' && payload.content) {
        cb.onToken(payload.content)
      } else if (event.event === 'reasoning' && payload.content && cb.onReasoning) {
        cb.onReasoning(payload.content)
      } else if (event.event === 'meta') {
        cb.onMeta?.(payload)
      } else if (event.event === 'done') {
        cb.onDone?.()
        return true
      } else if (event.event === 'error') {
        const error = new Error(payload.message || '上游返回错误') as Error & {
          attempts?: ChatAttemptResult[]
        }
        error.attempts = payload.attempts
        throw error
      }
      return false
    }

    reader = resp.body.getReader()
    const decoder = new TextDecoder()
    let buffer = ''
    while (true) {
      const { done, value } = await reader.read()
      if (done) break
      buffer += decoder.decode(value, { stream: true })
      let separator = buffer.search(/\r?\n\r?\n/)
      while (separator >= 0) {
        const block = buffer.slice(0, separator)
        const separatorLength = buffer.startsWith('\r\n\r\n', separator) ? 4 : 2
        buffer = buffer.slice(separator + separatorLength)
        if (handleBlock(block)) return
        separator = buffer.search(/\r?\n\r?\n/)
      }
    }
    buffer += decoder.decode()
    if (buffer.trim() && handleBlock(buffer)) return
    // 未收到 done 标记时提示异常结束，避免把中断流误判为成功。
    throw new Error('流式连接已结束，但未收到完成标记')
  } catch (e) {
    if ((e as Error).name === 'AbortError') return
    cb.onError(e as Error)
  } finally {
    if (reader) {
      try { await reader.cancel() } catch { }
      try { reader.releaseLock() } catch { }
    }
  }
}

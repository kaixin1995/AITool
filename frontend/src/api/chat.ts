import { httpGet } from './http'
import { getAccessToken } from './http'

export interface ChatModel {
  modelId: string
  displayName: string
  availableSiteCount: number
}

export async function getChatModels(): Promise<ChatModel[]> {
  return httpGet<ChatModel[]>('/api/admin/chat/models')
}

export interface ChatSendOptions {
  model: string
  messages: Array<{ role: string; content: string }>
  stream?: boolean
  signal?: AbortSignal
}

// 非流式发送
export async function sendChat(opts: ChatSendOptions): Promise<{ content: string }> {
  const body = { model: opts.model, messages: opts.messages, stream: false }
  const resp = await fetch('/api/admin/chat/send', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${getAccessToken()}` },
    body: JSON.stringify(body)
  })
  if (!resp.ok) {
    const err = await resp.json().catch(() => ({}))
    throw new Error(err.message || `请求失败 (${resp.status})`)
  }
  const data = await resp.json()
  // 兼容 OpenAI chat 格式
  const content = data?.choices?.[0]?.message?.content ?? data?.content ?? ''
  return { content }
}

// 流式发送（SSE）：逐 chunk 回调。用 fetch + ReadableStream（因需带 Bearer header，不能用 EventSource）。
export async function sendChatStream(
  opts: ChatSendOptions,
  onDelta: (text: string) => void,
  onDone: () => void,
  onError: (err: Error) => void
): Promise<void> {
  const body = { model: opts.model, messages: opts.messages, stream: true }
  try {
    const resp = await fetch('/api/admin/chat/send-stream', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${getAccessToken()}` },
      body: JSON.stringify(body),
      signal: opts.signal
    })
    if (!resp.ok || !resp.body) {
      const err = await resp.json().catch(() => ({}))
      throw new Error(err.message || `请求失败 (${resp.status})`)
    }
    const reader = resp.body.getReader()
    const decoder = new TextDecoder()
    let buffer = ''
    while (true) {
      const { done, value } = await reader.read()
      if (done) break
      buffer += decoder.decode(value, { stream: true })
      const lines = buffer.split('\n')
      buffer = lines.pop() ?? ''
      for (const line of lines) {
        const trimmed = line.trim()
        if (!trimmed.startsWith('data:')) continue
        const jsonText = trimmed.slice(5).trim()
        if (jsonText === '[DONE]') { onDone(); return }
        try {
          const obj = JSON.parse(jsonText)
          // 兼容 OpenAI chat 流式：choices[0].delta.content
          const delta = obj?.choices?.[0]?.delta?.content ?? obj?.delta ?? ''
          if (delta) onDelta(delta)
        } catch {
          // 非 JSON 的 data 行忽略
        }
      }
    }
    onDone()
  } catch (e) {
    if ((e as Error).name === 'AbortError') return
    onError(e as Error)
  }
}

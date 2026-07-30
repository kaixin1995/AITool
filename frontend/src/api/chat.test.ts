import { afterEach, describe, expect, it, vi } from 'vitest'
import { parseSseBlock, sendChat, sendChatStream } from './chat'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('chat stream', () => {
  it('解析 CRLF 和多行 data 的 SSE 事件', () => {
    expect(parseSseBlock('event: token\r\ndata: {"content":"a"}\r\ndata: second'))
      .toEqual({ event: 'token', data: '{"content":"a"}\nsecond' })
  })

  it('非流式请求传递取消信号', async () => {
    vi.stubGlobal('localStorage', { getItem: () => 'test-token' })
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ success: true }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    }))
    vi.stubGlobal('fetch', fetchMock)
    const controller = new AbortController()

    await sendChat({ modelId: 'model', message: 'hello', signal: controller.signal })

    expect(fetchMock.mock.calls[0][1].signal).toBe(controller.signal)
  })

  it('请求显式声明 SSE 并在 done 前返回 token', async () => {
    vi.stubGlobal('localStorage', { getItem: () => 'test-token' })
    const encoder = new TextEncoder()
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(encoder.encode('event: token\r\ndata: {"content":"实时"}\r\n\r\n'))
        controller.enqueue(encoder.encode('event: done\ndata: {}\n\n'))
        controller.close()
      },
    })
    const fetchMock = vi.fn().mockResolvedValue(new Response(stream, {
      status: 200,
      headers: { 'Content-Type': 'text/event-stream; charset=utf-8' },
    }))
    vi.stubGlobal('fetch', fetchMock)
    const events: string[] = []

    await sendChatStream({ modelId: 'model', message: 'hello' }, {
      onToken: text => events.push(`token:${text}`),
      onDone: () => events.push('done'),
      onError: error => { throw error },
    })

    expect(fetchMock.mock.calls[0][1].headers.Accept).toBe('text/event-stream')
    expect(events).toEqual(['token:实时', 'done'])
  })

  it('流式错误保留后端返回的调用尝试明细', async () => {
    vi.stubGlobal('localStorage', { getItem: () => 'test-token' })
    const encoder = new TextEncoder()
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(encoder.encode('event: error\ndata: {"message":"全部失败","attempts":[{"siteName":"站点一","status":"fail"}]}\n\n'))
        controller.close()
      },
    })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(stream, {
      status: 200,
      headers: { 'Content-Type': 'text/event-stream' },
    })))
    const receivedError: { message?: string; attempts?: unknown[] } = {}

    await sendChatStream({ modelId: 'model', message: 'hello' }, {
      onToken: () => {},
      onError: error => {
        receivedError.message = error.message
        receivedError.attempts = (error as Error & { attempts?: unknown[] }).attempts
      },
    })

    expect(receivedError.message).toBe('全部失败')
    expect(receivedError.attempts).toEqual([
      expect.objectContaining({ siteName: '站点一', status: 'fail' }),
    ])
  })
})

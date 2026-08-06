import { describe, expect, it } from 'vitest'
import {
  buildModelSupportLabels,
  formatSimulatorResponse,
  SimulatorRequestRegistry,
  shouldReadStreamingResponse
} from './clientSimulatorState'

describe('客户端模拟器响应状态', () => {
  it('只格式化一次 HTTP 状态并保留响应体', () => {
    expect(formatSimulatorResponse(204, true, '')).toBe('HTTP 204')
    expect(formatSimulatorResponse(400, false, '{\n  "error": "bad"\n}'))
      .toBe('HTTP 400\n{\n  "error": "bad"\n}')
    expect(formatSimulatorResponse(500, false, '')).toBe('HTTP 500')
  })

  it('仅对成功且存在响应体的流式请求逐块读取', () => {
    expect(shouldReadStreamingResponse(true, true, true)).toBe(true)
    expect(shouldReadStreamingResponse(true, false, true)).toBe(false)
    expect(shouldReadStreamingResponse(true, true, false)).toBe(false)
    expect(shouldReadStreamingResponse(false, true, true)).toBe(false)
  })
})

describe('客户端模拟器独立请求状态', () => {
  it('按端点登记和停止请求，旧请求不能清除同端点的新请求', () => {
    const registry = new SimulatorRequestRegistry()
    const openAiFirst = new AbortController()
    const anthropic = new AbortController()
    registry.start('openai', openAiFirst)
    registry.start('anthropic', anthropic)

    expect(registry.isRunning('openai')).toBe(true)
    expect(registry.isRunning('anthropic')).toBe(true)
    expect(registry.stop('openai')).toBe(true)
    expect(openAiFirst.signal.aborted).toBe(true)
    expect(anthropic.signal.aborted).toBe(false)

    const openAiSecond = new AbortController()
    registry.start('openai', openAiSecond)
    expect(registry.finish('openai', openAiFirst)).toBe(false)
    expect(registry.isRunning('openai')).toBe(true)
    expect(registry.finish('openai', openAiSecond)).toBe(true)
    expect(registry.isRunning('openai')).toBe(false)
  })
})

describe('客户端模拟器模型能力提示', () => {
  it('原生协议不重复显示兼容标签', () => {
    expect(buildModelSupportLabels({
      supportsOpenAi: true,
      supportsAnthropic: true,
      supportsResponses: true,
      canUseOpenAi: true,
      canUseAnthropic: true,
      routeCount: 2
    })).toEqual([
      'OpenAI 原生',
      'Anthropic 原生',
      'Responses 原生',
      '路由 2'
    ])
  })
})

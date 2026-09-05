import { describe, expect, it } from 'vitest'
import {
  developerHashForTab,
  developerTabFromHash,
  getCurrentDisplayHeaders,
  getRewrittenHeaders,
  hasRewrittenHeaders,
  hasConvertedRequestBody,
  getCurrentDisplayRequestBody,
  hasConvertedResponseBody,
  getCurrentDisplayResponseBody,
  setProtocolDiagnosticsPrefill,
  supportsResponsesNatively,
  takeProtocolDiagnosticsPrefill
} from './developerInvocationsState'

describe('Developer Invocations 页签深链接', () => {
  it('兼容旧页面 hash', () => {
    expect(developerTabFromHash('#developerInvocationsPane')).toBe('invocations')
    expect(developerTabFromHash('#developerDiagnosticDumpsPane')).toBe('diagnostic-dumps')
    expect(developerTabFromHash('#developerSimulatorPane')).toBe('simulator')
    expect(developerTabFromHash('#developerProtocolDiagnosticsPane')).toBe('protocol-diagnostics')
    expect(developerTabFromHash('#developerSqlMigrationsPane')).toBe('sql-migrations')
    expect(developerTabFromHash('#protocol-diagnostics')).toBe('protocol-diagnostics')
    expect(developerTabFromHash('#sql-migrations')).toBe('sql-migrations')
  })

  it('页签切换时生成旧入口兼容 hash', () => {
    expect(developerHashForTab('invocations')).toBe('#developerInvocationsPane')
    expect(developerHashForTab('diagnostic-dumps')).toBe('#developerDiagnosticDumpsPane')
    expect(developerHashForTab('simulator')).toBe('#developerSimulatorPane')
    expect(developerHashForTab('protocol-diagnostics')).toBe('#developerProtocolDiagnosticsPane')
    expect(developerHashForTab('sql-migrations')).toBe('#developerSqlMigrationsPane')
  })

  it('兼容旧页面的 Responses 原生能力推断', () => {
    expect(supportsResponsesNatively({ supportsResponses: true })).toBe(true)
    expect(supportsResponsesNatively({
      supportsOpenAi: false,
      supportsAnthropic: false
    })).toBe(true)
    expect(supportsResponsesNatively({ supportsOpenAi: true })).toBe(false)
  })
})

describe('Developer Invocations → 协议诊断台 联动', () => {
  it('预填数据取走即清空（避免重复执行）', () => {
    expect(takeProtocolDiagnosticsPrefill()).toBeNull()
    setProtocolDiagnosticsPrefill({
      direction: 'request',
      sourceProtocol: 'OpenAI',
      targetProtocol: 'Anthropic',
      streaming: true,
      modelName: 'gpt-4.1',
      payload: '{"model":"gpt-4.1"}',
      eventName: 'content_block_delta'
    })
    const prefill = takeProtocolDiagnosticsPrefill()
    expect(prefill).not.toBeNull()
    expect(prefill?.direction).toBe('request')
    expect(prefill?.targetProtocol).toBe('Anthropic')
    expect(prefill?.eventName).toBe('content_block_delta')
    expect(takeProtocolDiagnosticsPrefill()).toBeNull()
  })

  it('故障现场扩展字段（preparedPayload/错误正文/站点）完整往返', () => {
    setProtocolDiagnosticsPrefill({
      direction: 'request',
      sourceProtocol: 'OpenAI',
      targetProtocol: 'Gemini',
      streaming: false,
      modelName: '1M',
      payload: '{"model":"1M","messages":[]}',
      preparedPayload: '{"contents":[]}',
      targetSiteName: 'GeminiPro1',
      attemptedModel: 'gemini-3.7-flash',
      statusCode: 400,
      errorMessage: 'Invalid argument'
    })
    const prefill = takeProtocolDiagnosticsPrefill()
    expect(prefill?.preparedPayload).toBe('{"contents":[]}')
    expect(prefill?.targetSiteName).toBe('GeminiPro1')
    expect(prefill?.statusCode).toBe(400)
    expect(prefill?.errorMessage).toBe('Invalid argument')
  })
})

describe('Developer Invocations 请求头重写切换与提取', () => {
  it('当存在重写请求头时正确识别与提取', () => {
    const detail = {
      requestHeaders: { 'User-Agent': 'curl/7.68.0', 'Authorization': 'Bearer sk-raw' },
      preparedRequestHeaders: { 'User-Agent': 'claude-code/0.2.29', 'anthropic-version': '2023-06-01' },
      attempts: []
    }

    expect(hasRewrittenHeaders(detail)).toBe(true)
    expect(getRewrittenHeaders(detail)).toEqual({ 'User-Agent': 'claude-code/0.2.29', 'anthropic-version': '2023-06-01' })
    expect(getCurrentDisplayHeaders(detail, 'original')).toEqual({ 'User-Agent': 'curl/7.68.0', 'Authorization': 'Bearer sk-raw' })
    expect(getCurrentDisplayHeaders(detail, 'rewritten')).toEqual({ 'User-Agent': 'claude-code/0.2.29', 'anthropic-version': '2023-06-01' })
    expect(getCurrentDisplayHeaders(detail)).toEqual({ 'User-Agent': 'claude-code/0.2.29', 'anthropic-version': '2023-06-01' })
  })

  it('当仅尝试列表内存在重写头时能够提取', () => {
    const detail = {
      requestHeaders: { 'User-Agent': 'python-requests/2.28.1' },
      attempts: [
        {
          preparedRequestHeaders: { 'User-Agent': 'opencode/1.15.0', 'x-api-key': 'sk-ant-test' }
        }
      ]
    }

    expect(hasRewrittenHeaders(detail)).toBe(true)
    expect(getRewrittenHeaders(detail)).toEqual({ 'User-Agent': 'opencode/1.15.0', 'x-api-key': 'sk-ant-test' })
  })

  it('当无重写头时返回原始请求头或友好提示', () => {
    const detail = {
      requestHeaders: { 'User-Agent': 'my-client/1.0' },
      attempts: []
    }

    expect(hasRewrittenHeaders(detail)).toBe(false)
    expect(getCurrentDisplayHeaders(detail, 'original')).toEqual({ 'User-Agent': 'my-client/1.0' })
    expect(getCurrentDisplayHeaders(detail, 'rewritten')).toHaveProperty('提示')
  })
})

describe('Developer Invocations 请求体与响应体转换切换与提取', () => {
  it('当请求体发生协议转换时识别并正确切换', () => {
    const detail = { requestBody: '{"model":"gpt-4","messages":[{"role":"user","content":"hi"}]}' }
    const attempt = { preparedRequestBody: '{"contents":[{"role":"user","parts":[{"text":"hi"}]}]}' }

    expect(hasConvertedRequestBody(detail, attempt)).toBe(true)
    expect(getCurrentDisplayRequestBody(detail, attempt, 'original')).toBe(detail.requestBody)
    expect(getCurrentDisplayRequestBody(detail, attempt, 'prepared')).toBe(attempt.preparedRequestBody)
    expect(getCurrentDisplayRequestBody(detail, attempt)).toBe(detail.requestBody) // 默认原始
  })

  it('当请求体为透传时判定为无转换', () => {
    const detail = { requestBody: '{"model":"gpt-4"}' }
    const attempt = { preparedRequestBody: '{"model":"gpt-4"}' }

    expect(hasConvertedRequestBody(detail, attempt)).toBe(false)
    expect(getCurrentDisplayRequestBody(detail, attempt, 'prepared')).toBe(detail.requestBody)
  })

  it('透传时仅缩进/空白不同（客户端带格式 vs 网关压缩重序列化）判定为无转换', () => {
    const detail = { requestBody: '{\n  "model": "gpt-4",\n  "stream": false\n}' }
    const attempt = { preparedRequestBody: '{"model":"gpt-4","stream":false}' }

    expect(hasConvertedRequestBody(detail, attempt)).toBe(false)
  })

  it('透传时空白不同且模型名被改写则判定为已转换', () => {
    const detail = { requestBody: '{\n  "model": "gpt-4"\n}' }
    const attempt = { preparedRequestBody: '{"model":"site-model-x"}' }

    expect(hasConvertedRequestBody(detail, attempt)).toBe(true)
  })

  it('当响应体发生协议转换时识别并正确切换', () => {
    const detail = { responseBody: '{"id":"chatcmpl-1","choices":[{"message":{"content":"hello"}}]}' }
    const attempt = { responseBody: '{"response":{"candidates":[{"content":{"parts":[{"text":"hello"}]}}]}}' }

    expect(hasConvertedResponseBody(detail, attempt)).toBe(true)
    expect(getCurrentDisplayResponseBody(detail, attempt, 'final')).toBe(detail.responseBody)
    expect(getCurrentDisplayResponseBody(detail, attempt, 'upstream')).toBe(attempt.responseBody)
    expect(getCurrentDisplayResponseBody(detail, attempt)).toBe(detail.responseBody) // 默认客户端响应
  })

  it('当响应体一致或透传时判定为无转换', () => {
    const detail = { responseBody: '{"text":"ok"}' }
    const attempt = { responseBody: '{"text":"ok"}' }

    expect(hasConvertedResponseBody(detail, attempt)).toBe(false)
    expect(getCurrentDisplayResponseBody(detail, attempt, 'upstream')).toBe(detail.responseBody)
  })
})


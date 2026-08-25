import { describe, expect, it } from 'vitest'
import {
  developerHashForTab,
  developerTabFromHash,
  getCurrentDisplayHeaders,
  getRewrittenHeaders,
  hasRewrittenHeaders,
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

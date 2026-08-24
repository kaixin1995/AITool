import { describe, expect, it } from 'vitest'
import {
  developerHashForTab,
  developerTabFromHash,
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
})

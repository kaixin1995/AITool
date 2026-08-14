export type DeveloperToolTab = 'invocations' | 'simulator' | 'concurrency' | 'circuit-breaker' | 'protocol-diagnostics'

// ────────────────────────────────────────────────
// 调用记录 → 协议诊断台 联动：详情面板把某条请求/响应体一键载入诊断表单。
// 纯函数式存储（模块级变量 + 取走即清空），不依赖 Vue 响应式，便于测试。
// ────────────────────────────────────────────────
export interface ProtocolDiagnosticsPrefill {
  direction: 'request' | 'response'
  sourceProtocol: string
  targetProtocol: string
  streaming: boolean
  modelName: string
  payload: string
  eventName?: string
  inputTokens?: number
  cachedTokens?: number
  outputTokens?: number
}

let pendingProtocolDiagnosticsPrefill: ProtocolDiagnosticsPrefill | null = null

export function setProtocolDiagnosticsPrefill(prefill: ProtocolDiagnosticsPrefill): void {
  pendingProtocolDiagnosticsPrefill = prefill
}

export function takeProtocolDiagnosticsPrefill(): ProtocolDiagnosticsPrefill | null {
  const prefill = pendingProtocolDiagnosticsPrefill
  pendingProtocolDiagnosticsPrefill = null
  return prefill
}

const hashToTab: Record<string, DeveloperToolTab> = {
  '#developerInvocationsPane': 'invocations',
  '#developerSimulatorPane': 'simulator',
  '#developerConcurrencyPane': 'concurrency',
  '#developerCircuitBreakerPane': 'circuit-breaker',
  '#developerProtocolDiagnosticsPane': 'protocol-diagnostics',
  '#protocol-diagnostics': 'protocol-diagnostics',
  '#invocations': 'invocations',
  '#simulator': 'simulator',
  '#concurrency': 'concurrency',
  '#circuit-breaker': 'circuit-breaker'
}

const tabToHash: Record<DeveloperToolTab, string> = {
  invocations: '#developerInvocationsPane',
  simulator: '#developerSimulatorPane',
  concurrency: '#developerConcurrencyPane',
  'circuit-breaker': '#developerCircuitBreakerPane',
  'protocol-diagnostics': '#developerProtocolDiagnosticsPane'
}

export function developerTabFromHash(hash: string): DeveloperToolTab {
  return hashToTab[hash] ?? 'invocations'
}

export function developerHashForTab(tab: DeveloperToolTab): string {
  return tabToHash[tab]
}

export function supportsResponsesNatively(model: {
  supportsResponses?: boolean
  supportsOpenAi?: boolean
  supportsAnthropic?: boolean
}): boolean {
  return model.supportsResponses === true
    || (
      model.supportsOpenAi === false
      && model.supportsAnthropic === false
    )
}

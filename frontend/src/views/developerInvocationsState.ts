import type { CompatibilityRuleForm } from './compatibilityState'

export type DeveloperToolTab = 'invocations' | 'diagnostic-dumps' | 'simulator' | 'protocol-diagnostics' | 'header-presets' | 'proxy-profiles' | 'sql-migrations'

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
  // 故障现场扩展信息与推荐规则
  targetSiteName?: string
  attemptedModel?: string
  statusCode?: number
  errorMessage?: string
  // 转换后（发往上游）的请求体：AI 自愈试探以它为起点，跨页签载入时避免丢失
  preparedPayload?: string
  trialRules?: CompatibilityRuleForm[]
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
  '#developerDiagnosticDumpsPane': 'diagnostic-dumps',
  '#developerSimulatorPane': 'simulator',
  '#developerProtocolDiagnosticsPane': 'protocol-diagnostics',
  '#developerHeaderPresetsPane': 'header-presets',
  '#developerProxyProfilesPane': 'proxy-profiles',
  '#developerSqlMigrationsPane': 'sql-migrations',
  '#diagnostic-dumps': 'diagnostic-dumps',
  '#protocol-diagnostics': 'protocol-diagnostics',
  '#header-presets': 'header-presets',
  '#proxy-profiles': 'proxy-profiles',
  '#sql-migrations': 'sql-migrations',
  '#invocations': 'invocations',
  '#simulator': 'simulator'
}

const tabToHash: Record<DeveloperToolTab, string> = {
  invocations: '#developerInvocationsPane',
  'diagnostic-dumps': '#developerDiagnosticDumpsPane',
  simulator: '#developerSimulatorPane',
  'protocol-diagnostics': '#developerProtocolDiagnosticsPane',
  'header-presets': '#developerHeaderPresetsPane',
  'proxy-profiles': '#developerProxyProfilesPane',
  'sql-migrations': '#developerSqlMigrationsPane'
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

export function hasRewrittenHeaders(detail?: {
  preparedRequestHeaders?: Record<string, string>
  attempts?: Array<{ preparedRequestHeaders?: Record<string, string> }>
}): boolean {
  if (!detail) return false
  if (detail.preparedRequestHeaders && Object.keys(detail.preparedRequestHeaders).length > 0) return true
  return detail.attempts?.some(a => a.preparedRequestHeaders && Object.keys(a.preparedRequestHeaders).length > 0) ?? false
}

export function getRewrittenHeaders(detail?: {
  preparedRequestHeaders?: Record<string, string>
  attempts?: Array<{ preparedRequestHeaders?: Record<string, string> }>
}): Record<string, string> {
  if (!detail) return {}
  if (detail.preparedRequestHeaders && Object.keys(detail.preparedRequestHeaders).length > 0) {
    return detail.preparedRequestHeaders
  }
  for (const a of detail.attempts || []) {
    if (a.preparedRequestHeaders && Object.keys(a.preparedRequestHeaders).length > 0) {
      return a.preparedRequestHeaders
    }
  }
  return {}
}

export function getCurrentDisplayHeaders(
  detail?: {
    requestHeaders?: Record<string, string>
    preparedRequestHeaders?: Record<string, string>
    attempts?: Array<{ preparedRequestHeaders?: Record<string, string> }>
  },
  mode?: 'original' | 'rewritten'
): Record<string, string> {
  if (!detail) return {}
  const effectiveMode = mode || (hasRewrittenHeaders(detail) ? 'rewritten' : 'original')
  if (effectiveMode === 'rewritten') {
    if (hasRewrittenHeaders(detail)) {
      return getRewrittenHeaders(detail)
    }
    return { '提示': '未配置客户端特征模拟或请求头方案（直接使用上游默认认证头与透传头，无改写）' }
  }
  return detail.requestHeaders || {}
}

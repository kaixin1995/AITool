import { httpGet, httpPost, httpDelete } from './http'
import type { CompatibilityRuleForm } from '@/views/compatibilityState'

export interface DeveloperInit {
  totalCount: number
  failedCount: number
  pendingCount: number
  defaultBaseUrl: string
  defaultAccessKey: string
  models: Array<{ modelName: string; routeCount: number; canUseOpenAi: boolean; canUseAnthropic: boolean; supportsOpenAi?: boolean; supportsAnthropic?: boolean; supportsResponses?: boolean }>
  defaultOpenAiModel: string
  defaultAnthropicModel: string
  tabs?: {
    invocations: boolean
    diagnosticDumps: boolean
    simulator: boolean
    protocolDiagnostics: boolean
    sqlMigrations: boolean
  }
}
export interface DeveloperInvocationSummary {
  traceId: string
  createdAt: string
  source: string
  protocolType: string
  requestPath: string
  requestModel: string
  targetSiteName: string
  attemptedModel: string
  status: string
  statusCode: number
  totalDurationMs: number
  attemptCount: number
  successAttemptCount?: number
  failedAttemptCount?: number
  pendingAttemptCount?: number
}
export interface DeveloperConcurrencyItem {
  siteId: string
  concurrencyKey: string
  modelName: string
  siteName: string
  activeCount: number
  maxConcurrency: number | null
  queueCount: number
  lastSeenAt: string
}

export interface DeveloperAiDiagnosePayload {
  modelId: string
  mappingId?: string
  enableReasoning?: boolean
  reasoningEffort?: string

  clientProtocol: string
  requestPath: string
  requestModel: string
  attemptedModel: string
  targetSiteName: string
  upstreamProtocolType: string
  forwardingMode: string
  statusCode: number
  errorMessage: string
  originalRequestBody: string
  preparedRequestBody: string
}

export interface DeveloperAiDiagnoseResult {
  success: boolean
  error?: string | null
  content?: string
  reasoning?: string | null
  summary?: string
  rootCause?: string
  suggestedAction?: string
  rules?: CompatibilityRuleForm[]
}

export interface AutoDiagnoseRoundItem {
  roundNumber: number
  hypothesis: string
  adjustedRequestBody: string
  explanation: string
  statusCode: number
  success: boolean
  responseBody: string
  durationMs: number
  errorMessage: string
}

export interface AutoDiagnoseLoopPayload {
  diagnosticModelId: string
  diagnosticMappingId?: string
  enableReasoning?: boolean
  reasoningEffort?: string

  targetSiteId?: string
  targetSiteName?: string
  targetBaseUrl?: string
  targetApiKey?: string
  targetEndpointPathMode?: string
  targetModelName: string
  sourceProtocol: string
  targetProtocol: string

  originalRequestBody: string
  initialPreparedRequestBody?: string
  initialErrorResponse?: string
  initialStatusCode?: number
  maxRounds?: number
}

export interface AutoDiagnoseLoopResult {
  success: boolean
  totalRounds: number
  rounds: AutoDiagnoseRoundItem[]
  rootCause: string
  summary: string
  suggestedAction: string
  workingPayload: string
  rules: CompatibilityRuleForm[]
  error?: string | null
}

export async function runAutoDiagnoseLoop(payload: AutoDiagnoseLoopPayload): Promise<AutoDiagnoseLoopResult> {
  return httpPost<AutoDiagnoseLoopResult>('/api/admin/developer/invocations/auto-diagnose-loop', payload)
}

export interface DiagnosticSamplingStatus {
  enabled: boolean
  remainingSeconds: number
  expiresAtUtc: string | null
  maxDurationMinutes: number
}

export interface DiagnosticDumpItem {
  fileName: string
  filePath: string
  category: 'failure' | 'sample'
  timestamp: string
  routeName: string
  siteName: string
  requestModel: string
  attemptedModel: string
  clientProtocol: string
  upstreamProtocol: string
  forwardingMode: string
  statusCode: number | null
  success: boolean
  totalDurationMs: number
  errorSummary: string
  fileSizeBytes: number
}

export interface DiagnosticConfig {
  maxBodyLengthMb: number
  maxRoundResponseMb: number
  retentionDays: number
  maxFailuresPerDay: number
}

export async function getDiagnosticConfig(): Promise<DiagnosticConfig> {
  return httpGet('/api/admin/developer/invocations/diagnostic-config')
}

export async function updateDiagnosticConfig(config: DiagnosticConfig): Promise<DiagnosticConfig> {
  return httpPost('/api/admin/developer/invocations/diagnostic-config', config)
}

export async function getDiagnosticSamplingStatus(): Promise<DiagnosticSamplingStatus> {
  return httpGet('/api/admin/developer/invocations/diagnostic-sampling')
}

export async function enableDiagnosticSampling(durationMinutes = 10): Promise<DiagnosticSamplingStatus> {
  return httpPost(`/api/admin/developer/invocations/diagnostic-sampling/enable?durationMinutes=${durationMinutes}`, {})
}

export async function disableDiagnosticSampling(): Promise<DiagnosticSamplingStatus> {
  return httpPost('/api/admin/developer/invocations/diagnostic-sampling/disable', {})
}

export async function getDiagnosticDumps(limit = 50): Promise<DiagnosticDumpItem[]> {
  return httpGet(`/api/admin/developer/invocations/diagnostic-dumps?limit=${limit}`)
}

export async function getDiagnosticDumpContent(fileName: string): Promise<any> {
  return httpGet(`/api/admin/developer/invocations/diagnostic-dumps/${fileName}`)
}

export async function clearDiagnosticDumps(retentionDays?: number): Promise<{ deletedCount: number }> {
  const url = typeof retentionDays === 'number'
    ? `/api/admin/developer/invocations/diagnostic-dumps?retentionDays=${retentionDays}`
    : '/api/admin/developer/invocations/diagnostic-dumps'
  return httpDelete(url)
}

export async function getDeveloperInit(): Promise<DeveloperInit> {
  return httpGet<DeveloperInit>('/api/admin/developer/invocations/init')
}
export async function getDeveloperList(page = 1, pageSize = 20): Promise<{ page: number; pageSize: number; totalPages: number; totalCount: number; failedCount: number; pendingCount: number; entries: DeveloperInvocationSummary[] }> {
  return httpGet(`/api/admin/developer/invocations/list?page=${page}&pageSize=${pageSize}`)
}
export async function getDeveloperDetail(traceId: string, summarize = false, signal?: AbortSignal): Promise<unknown> {
  return httpGet(`/api/admin/developer/invocations/${traceId}?summarize=${summarize}`, { signal })
}
export async function getDeveloperConcurrency(): Promise<{ refreshedAt: string; items: DeveloperConcurrencyItem[] }> {
  return httpGet('/api/admin/developer/invocations/concurrency')
}
export async function runAiDiagnose(payload: DeveloperAiDiagnosePayload): Promise<DeveloperAiDiagnoseResult> {
  return httpPost<DeveloperAiDiagnoseResult>('/api/admin/developer/invocations/ai-diagnose', payload)
}

import { httpGet, httpPost } from './http'
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

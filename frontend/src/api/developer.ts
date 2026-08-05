import { httpGet } from './http'

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
  modelName: string
  siteName: string
  activeCount: number
  maxConcurrency: number | null
  queueCount: number
  lastSeenAt: string
}

export async function getDeveloperInit(): Promise<DeveloperInit> {
  return httpGet<DeveloperInit>('/api/admin/developer/invocations/init')
}
export async function getDeveloperList(page = 1, pageSize = 20): Promise<{ page: number; pageSize: number; totalPages: number; totalCount: number; failedCount: number; pendingCount: number; entries: DeveloperInvocationSummary[] }> {
  return httpGet(`/api/admin/developer/invocations/list?page=${page}&pageSize=${pageSize}`)
}
export async function getDeveloperDetail(traceId: string, summarize = false): Promise<unknown> {
  return httpGet(`/api/admin/developer/invocations/${traceId}?summarize=${summarize}`)
}
export async function getDeveloperConcurrency(): Promise<{ refreshedAt: string; items: DeveloperConcurrencyItem[] }> {
  return httpGet('/api/admin/developer/invocations/concurrency')
}

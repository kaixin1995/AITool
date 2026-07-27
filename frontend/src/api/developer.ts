import { httpGet } from './http'

export interface DeveloperInit {
  totalCount: number
  failedCount: number
  pendingCount: number
  defaultBaseUrl: string
  defaultAccessKey: string
  models: Array<{ modelName: string; routeCount: number; canUseOpenAi: boolean; canUseAnthropic: boolean }>
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
export async function getDeveloperList(): Promise<{ totalCount: number; failedCount: number; pendingCount: number; entries: DeveloperInvocationSummary[] }> {
  return httpGet('/api/admin/developer/invocations/list')
}
export async function getDeveloperDetail(traceId: string, summarize = false): Promise<unknown> {
  return httpGet(`/api/admin/developer/invocations/${traceId}?summarize=${summarize}`)
}
export async function getDeveloperConcurrency(): Promise<{ refreshedAt: string; items: DeveloperConcurrencyItem[] }> {
  return httpGet('/api/admin/developer/invocations/concurrency')
}

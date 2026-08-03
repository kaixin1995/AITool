import { httpGet } from './http'

export interface UsageLogFilters {
  sites: { id: string; name: string }[]
  accessKeys: { id: string; name: string }[]
}
// 与后端 UsageLogListItemDto 对齐。后端列表不返回 accessKeyId/targetSiteId/forwardingMode/errorMessage/reasoningEffort，
// 而是返回解析后的 siteName/accessKeyName/siteModelName。
export interface UsageLogItem {
  id: string
  requestId: string
  protocolType: string
  requestModel: string
  attemptedModel: string
  siteModelName: string
  status: string
  source: string
  siteName: string
  accessKeyName: string
  retryCount: number
  attemptIndex: number
  isFinalResult: boolean
  fallbackTriggered: boolean
  errorMessage: string
  inputTokens: number
  cachedTokens: number
  outputTokens: number
  totalTokens: number
  isStreaming: boolean
  isStreamInterrupted: boolean
  firstTokenLatencyMs: number
  streamDurationMs: number
  totalDurationMs: number
  reasoningEffort: string
  requestedAt: string
}

export interface UsageLogSummary {
  totalRequests: number
  failedRequests: number
  successRate: number
  totalTokens: number
  maxDurationMs: number
}

export interface UsageLogRequestDetail {
  requestId: string
  requestModel: string
  routeEntry: string
  protocolType: string
  forwardingMode: string
  reasoningEffort: string
  attempts: UsageLogItem[]
}

export async function getUsageLogFilters(): Promise<UsageLogFilters> {
  return httpGet<UsageLogFilters>('/api/admin/usage-logs/filters')
}

function buildQuery(params: Record<string, unknown>): string {
  const query = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== '') query.append(k, String(v))
  }
  return query.toString()
}

export async function listUsageLogs(params: Record<string, unknown>): Promise<{ items: UsageLogItem[]; page: number; pageSize: number; totalCount: number; totalPages: number }> {
  return httpGet(`/api/admin/usage-logs/list?${buildQuery(params)}`)
}

export async function getUsageLogSummary(params: Record<string, unknown>): Promise<UsageLogSummary> {
  return httpGet(`/api/admin/usage-logs/summary?${buildQuery(params)}`)
}

export async function getUsageLogRequestDetail(requestId: string): Promise<UsageLogRequestDetail> {
  return httpGet(`/api/admin/usage-logs/request-detail/${encodeURIComponent(requestId)}`)
}

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
  inputTokens: number
  cachedTokens: number
  outputTokens: number
  totalTokens: number
  isStreaming: boolean
  isStreamInterrupted: boolean
  firstTokenLatencyMs: number
  streamDurationMs: number
  totalDurationMs: number
  requestedAt: string
}

export async function getUsageLogFilters(): Promise<UsageLogFilters> {
  return httpGet<UsageLogFilters>('/api/admin/usage-logs/filters')
}

export async function listUsageLogs(params: Record<string, unknown>): Promise<{ items: UsageLogItem[]; totalCount: number }> {
  const query = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== '') query.append(k, String(v))
  }
  return httpGet(`/api/admin/usage-logs/list?${query.toString()}`)
}

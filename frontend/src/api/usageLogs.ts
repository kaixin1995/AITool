import { httpGet } from './http'

export interface UsageLogFilters {
  sites: { id: string; name: string }[]
  accessKeys: { id: string; name: string }[]
}
export interface UsageLogItem {
  id: string
  requestId: string
  accessKeyId: string
  protocolType: string
  forwardingMode: string
  requestModel: string
  attemptedModel: string
  targetSiteId: string
  status: string
  source: string
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

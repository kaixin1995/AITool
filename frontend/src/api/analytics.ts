import { httpGet } from './http'

// 与后端 AnalyticsApiController 的 DTO 完全对齐。
export interface AnalyticsSummary {
  totalRequests: number
  successRequests: number
  failedRequests: number
  totalInputTokens: number
  totalOutputTokens: number
  totalCachedTokens: number
  totalTokens?: number
  averageTotalDurationMs?: number
  averageFirstTokenLatencyMs?: number
  successRate?: number
  failureRate?: number
  fallbackRequestCount?: number
}
export interface AnalyticsTrendPoint {
  label: string
  requestCount: number
}
export interface AnalyticsResultTrendPoint {
  label: string
  successCount: number
  failCount: number
  successRate?: number
  failureRate?: number
}
export interface AnalyticsTokenTrendPoint {
  label: string
  inputTokens: number
  cachedTokens: number
  outputTokens: number
  totalTokens: number
}
export interface AnalyticsDurationTrendPoint {
  label: string
  averageTotalDurationMs: number
  averageFirstTokenLatencyMs: number
}
export interface AnalyticsFallbackTrendPoint {
  label: string
  fallbackCount: number
  fallbackRate: number
}
export interface AnalyticsDistributionPoint {
  label: string
  requestCount: number
  successCount?: number
  failedCount?: number
  totalTokens?: number
  inputTokens?: number
  cachedTokens?: number
  outputTokens?: number
  averageTotalDurationMs?: number
}
export interface AnalyticsCacheRatioPoint {
  label: string
  inputTokens: number
  cachedTokens: number
  totalInputScope: number
  cacheHitRate: number
}
export interface AnalyticsAppliedFilter {
  startTime: string
  endTime: string
  rangeType: string
  bucketType: string
  protocolType: string
  modelName: string
  siteId: string | null
  accessKeyId: string | null
}

export interface AnalyticsDashboard {
  appliedFilter: AnalyticsAppliedFilter
  summary: AnalyticsSummary
  requestTrend: AnalyticsTrendPoint[]
  resultTrend?: AnalyticsResultTrendPoint[]
  tokenTrend?: AnalyticsTokenTrendPoint[]
  durationTrend?: AnalyticsDurationTrendPoint[]
  fallbackTrend?: AnalyticsFallbackTrendPoint[]
  modelDistribution: AnalyticsDistributionPoint[]
  siteDistribution: AnalyticsDistributionPoint[]
  modelCacheRatioDistribution?: AnalyticsCacheRatioPoint[]
}

export interface AnalyticsFilterOptions {
  sites: { siteId: string; siteName: string }[]
  models: { modelName: string }[]
  accessKeys: { accessKeyId: string; accessKeyLabel: string }[]
}

export interface AnalyticsPendingResult {
  status: 'pending'
  retryAfterMs?: number
  message?: string
}

export interface AnalyticsBusyResult {
  status: 'busy'
  retryAfterMs?: number
  message?: string
}

export type AnalyticsDashboardResponse =
  | AnalyticsDashboard
  | AnalyticsPendingResult
  | AnalyticsBusyResult

export async function getAnalyticsDashboard(
  params: Record<string, unknown>,
  signal?: AbortSignal
): Promise<AnalyticsDashboardResponse> {
  const query = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== '') query.append(k, String(v))
  }
  return httpGet<AnalyticsDashboardResponse>(
    `/api/admin/analytics/dashboard?${query.toString()}`,
    {
      signal,
      // 队列满属于可重试状态，由页面按后端建议的间隔继续等待。
      validateStatus: status => (status >= 200 && status < 300) || status === 429
    }
  )
}

export async function getAnalyticsOptions(): Promise<AnalyticsFilterOptions> {
  return httpGet<AnalyticsFilterOptions>('/api/admin/analytics/options')
}

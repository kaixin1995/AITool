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
export interface AnalyticsDashboard {
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

export async function getAnalyticsDashboard(params: Record<string, unknown>): Promise<AnalyticsDashboard> {
  const query = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== '') query.append(k, String(v))
  }
  return httpGet<AnalyticsDashboard>(`/api/admin/analytics/dashboard?${query.toString()}`)
}

export async function getAnalyticsOptions(): Promise<AnalyticsFilterOptions> {
  return httpGet<AnalyticsFilterOptions>('/api/admin/analytics/options')
}

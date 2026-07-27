import { httpGet } from './http'

// 与后端 AnalyticsApiController 的 DTO 完全对齐。
export interface AnalyticsSummary {
  totalRequests: number
  successRequests: number
  failedRequests: number
  totalInputTokens: number
  totalOutputTokens: number
  totalCachedTokens: number
  averageDurationMs?: number
  averageFirstTokenLatencyMs?: number
  successRate?: number
}
export interface AnalyticsTrendPoint {
  label: string
  requestCount: number
}
export interface AnalyticsResultTrendPoint {
  label: string
  successCount: number
  failedCount: number
}
export interface AnalyticsDistributionPoint {
  label: string
  requestCount: number
  successCount?: number
  failedCount?: number
  totalTokens?: number
}
export interface AnalyticsDashboard {
  summary: AnalyticsSummary
  requestTrend: AnalyticsTrendPoint[]
  resultTrend?: AnalyticsResultTrendPoint[]
  modelDistribution: AnalyticsDistributionPoint[]
  siteDistribution: AnalyticsDistributionPoint[]
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

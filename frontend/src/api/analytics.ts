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
  key: string
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
export interface AnalyticsBreakdownPoint {
  key: string
  label: string
  requestCount: number
  successCount: number
  failedCount: number
  successRate: number
  totalTokens: number
  averageTotalDurationMs: number
  fallbackRequestCount: number
}

export interface AnalyticsFallbackChainPoint {
  firstSiteKey: string
  firstSiteLabel: string
  finalSiteKey: string
  finalSiteLabel: string
  requestCount: number
  successCount: number
  successRate: number
  averageAttemptCount: number
}

export interface AnalyticsLatencyPercentileValues {
  p50: number
  p95: number
  p99: number
  sampleCount: number
}

export interface AnalyticsLatencyPercentiles {
  totalDuration: AnalyticsLatencyPercentileValues
  firstTokenLatency: AnalyticsLatencyPercentileValues
}

export type AnalyticsAnalysisDimension =
  | 'source'
  | 'accessKey'
  | 'protocol'
  | 'failureReason'
  | 'statusCode'
  | 'fallbackChain'
  | 'latencyPercentiles'

export interface AnalyticsAppliedFilter {
  startTime: string
  endTime: string
  rangeType: string
  bucketType: string
  protocolType: string
  modelName: string
  source: string | null
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
  sourceBreakdown?: AnalyticsBreakdownPoint[]
  accessKeyBreakdown?: AnalyticsBreakdownPoint[]
  protocolBreakdown?: AnalyticsBreakdownPoint[]
  failureReasonBreakdown?: AnalyticsBreakdownPoint[]
  statusCodeBreakdown?: AnalyticsBreakdownPoint[]
  fallbackChainDistribution?: AnalyticsFallbackChainPoint[]
  latencyPercentiles?: AnalyticsLatencyPercentiles
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

// 旧后端响应缺少新增分析字段时，统一补为空数组；延迟分位数继续保留 undefined。
export function normalizeAnalyticsDashboardResponse(
  response: AnalyticsDashboardResponse
): AnalyticsDashboardResponse {
  if ('status' in response) return response

  return {
    ...response,
    resultTrend: response.resultTrend ?? [],
    tokenTrend: response.tokenTrend ?? [],
    durationTrend: response.durationTrend ?? [],
    fallbackTrend: response.fallbackTrend ?? [],
    modelCacheRatioDistribution: response.modelCacheRatioDistribution ?? [],
    sourceBreakdown: response.sourceBreakdown ?? [],
    accessKeyBreakdown: response.accessKeyBreakdown ?? [],
    protocolBreakdown: response.protocolBreakdown ?? [],
    failureReasonBreakdown: response.failureReasonBreakdown ?? [],
    statusCodeBreakdown: response.statusCodeBreakdown ?? [],
    fallbackChainDistribution: response.fallbackChainDistribution ?? []
  }
}

export async function getAnalyticsDashboard(
  params: Record<string, unknown>,
  signal?: AbortSignal
): Promise<AnalyticsDashboardResponse> {
  const query = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== '') query.append(k, String(v))
  }
  const response = await httpGet<AnalyticsDashboardResponse>(
    `/api/admin/analytics/dashboard?${query.toString()}`,
    {
      signal,
      // 队列满属于可重试状态，由页面按后端建议的间隔继续等待。
      validateStatus: status => (status >= 200 && status < 300) || status === 429
    }
  )
  return normalizeAnalyticsDashboardResponse(response)
}

export async function getAnalyticsOptions(): Promise<AnalyticsFilterOptions> {
  return httpGet<AnalyticsFilterOptions>('/api/admin/analytics/options')
}

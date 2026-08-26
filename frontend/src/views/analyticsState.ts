import type { AnalyticsAnalysisDimension } from '@/api/analytics'

interface AnalyticsTokenSummary {
  totalTokens?: number
  totalInputTokens?: number
  totalCachedTokens?: number
  totalOutputTokens?: number
}

export function calculateAnalyticsTotalTokens(summary: AnalyticsTokenSummary | undefined): number {
  return summary?.totalTokens
    ?? (summary?.totalInputTokens ?? 0) + (summary?.totalOutputTokens ?? 0)
}

export function formatAnalyticsTokenSplit(
  summary: AnalyticsTokenSummary | undefined,
  formatter: (val: number) => string
): string {
  return `${formatter(summary?.totalInputTokens ?? 0)} / ${formatter(summary?.totalCachedTokens ?? 0)} / ${formatter(summary?.totalOutputTokens ?? 0)}`
}

export function shouldAutoLoadAnalytics(rangeType: string): boolean {
  return rangeType !== 'custom'
}

export interface AnalyticsQueryState {
  rangeType: string
  bucketType: string
  protocolType: string
  modelName: string
  source: string | null
  siteId: string | null
  accessKeyId: string | null
  startTime: number | null
  endTime: number | null
}

export function buildAnalyticsQuery(state: Partial<AnalyticsQueryState> = {}): Record<string, unknown> {
  const rangeType = state.rangeType ?? 'week'
  const params: Record<string, unknown> = {
    rangeType,
    bucketType: state.bucketType ?? 'auto',
    protocolType: state.protocolType ?? 'all',
    modelName: state.modelName ?? 'all'
  }

  if (state.source) params.source = state.source
  if (state.siteId) params.siteId = state.siteId
  if (state.accessKeyId) params.accessKeyId = state.accessKeyId
  if (rangeType === 'custom') {
    if (state.startTime) params.startTime = new Date(state.startTime).toISOString()
    if (state.endTime) params.endTime = new Date(state.endTime).toISOString()
  }

  return params
}

export type AnalyticsFilterKey =
  | 'source'
  | 'protocolType'
  | 'modelName'
  | 'siteId'
  | 'accessKeyId'

export type AnalyticsFilterState = Partial<Pick<AnalyticsQueryState, AnalyticsFilterKey>>

export const DEFAULT_ANALYTICS_ANALYSIS_DIMENSION: AnalyticsAnalysisDimension = 'source'

export const ANALYTICS_ANALYSIS_TABS: ReadonlyArray<{
  key: AnalyticsAnalysisDimension
  label: string
}> = [
  { key: 'source', label: '来源' },
  { key: 'accessKey', label: 'Access Key' },
  { key: 'protocol', label: '协议' },
  { key: 'failureReason', label: '失败原因' },
  { key: 'statusCode', label: 'HTTP 状态码' },
  { key: 'fallbackChain', label: '回退链路' },
  { key: 'latencyPercentiles', label: '延迟分位数' }
]

type AnalyticsDimensionFilter = AnalyticsAnalysisDimension | 'site' | 'model'

function getAnalyticsDimensionFilterKey(
  dimension: AnalyticsDimensionFilter
): AnalyticsFilterKey | null {
  switch (dimension) {
    case 'source': return 'source'
    case 'accessKey': return 'accessKeyId'
    case 'protocol': return 'protocolType'
    case 'site': return 'siteId'
    case 'model': return 'modelName'
    default: return null
  }
}

export function toggleAnalyticsDimensionFilter(
  filters: AnalyticsFilterState,
  dimension: AnalyticsDimensionFilter,
  value: string
): AnalyticsFilterState {
  const key = getAnalyticsDimensionFilterKey(dimension)
  return key ? toggleDimensionFilter(filters, key, value) : filters
}

export function toggleDimensionFilter(
  filters: AnalyticsFilterState,
  key: AnalyticsFilterKey,
  value: string
): AnalyticsFilterState {
  if (filters[key] === value) {
    return removeAnalyticsFilter(filters, key)
  }

  return { ...filters, [key]: value }
}

export function removeAnalyticsFilter(
  filters: AnalyticsFilterState,
  key: AnalyticsFilterKey
): AnalyticsFilterState {
  const next = { ...filters }
  delete next[key]
  return next
}

export function resetAnalyticsFilters(_filters: AnalyticsFilterState = {}): AnalyticsFilterState {
  return {}
}

export function sortAnalyticsBreakdown<T extends { key: string }>(
  points: readonly T[],
  sortBy: keyof T = 'requestCount' as keyof T,
  direction: 'asc' | 'desc' = 'desc'
): T[] {
  return points
    .map((point, index) => ({ point, index }))
    .sort((left, right) => {
      const leftValue = left.point[sortBy]
      const rightValue = right.point[sortBy]
      let comparison = 0

      if (typeof leftValue === 'number' && typeof rightValue === 'number') {
        comparison = leftValue - rightValue
      } else {
        comparison = String(leftValue ?? '').localeCompare(String(rightValue ?? ''))
      }

      if (comparison === 0) return left.index - right.index
      return direction === 'asc' ? comparison : -comparison
    })
    .map(({ point }) => point)
}

export function buildAnalyticsDefaultCustomRange(now = new Date()): {
  startTime: number
  endTime: number
} {
  const end = new Date(now)
  end.setHours(23, 59, 0, 0)

  const start = new Date(end)
  start.setDate(end.getDate() - ((7 + end.getDay() - 1) % 7))
  start.setHours(0, 0, 0, 0)

  return {
    startTime: start.getTime(),
    endTime: end.getTime()
  }
}

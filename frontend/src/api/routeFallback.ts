import { httpGet } from './http'

export interface RouteFallbackEvent {
  requestId: string
  requestModel: string
  fromSiteId: string
  fromSiteName: string
  fromSiteModelName: string
  toSiteId: string
  toSiteName: string
  toSiteModelName: string
  reason: string
  occurredAt: string
}

export interface RouteFallbackListResponse {
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
  items: RouteFallbackEvent[]
  summary: RouteFallbackSummary
  sampleLogLimit: number
  isTruncated: boolean
  sampleOldestRequestedAt: string | null
}

export interface RouteFallbackSummary {
  totalCount: number
  uniqueFromSites: number
  uniqueToSites: number
  latestOccurredAt: string | null
}

function buildQuery(params: Record<string, unknown>): string {
  const query = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') query.append(key, String(value))
  }
  return query.toString()
}

export async function listRouteFallbackEvents(params: Record<string, unknown>): Promise<RouteFallbackListResponse> {
  return httpGet<RouteFallbackListResponse>(`/api/admin/route-fallback/list?${buildQuery(params)}`)
}

export async function getRouteFallbackSummary(params: Record<string, unknown> = {}): Promise<RouteFallbackSummary> {
  const query = buildQuery(params)
  return httpGet<RouteFallbackSummary>(`/api/admin/route-fallback/summary${query ? `?${query}` : ''}`)
}

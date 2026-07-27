import { httpGet } from './http'

export interface AnalyticsDashboard {
  summary?: {
    totalRequests: number
    totalInputTokens: number
    totalOutputTokens: number
    totalCachedTokens: number
    successCount: number
    failCount: number
  }
  trends?: Array<{ date: string; count: number }>
  modelDistribution?: Array<{ model: string; count: number }>
  siteDistribution?: Array<{ site: string; count: number }>
}

export async function getAnalyticsDashboard(params: Record<string, unknown>): Promise<AnalyticsDashboard> {
  const query = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== '') query.append(k, String(v))
  }
  return httpGet<AnalyticsDashboard>(`/api/admin/analytics/dashboard?${query.toString()}`)
}

export async function getAnalyticsOptions(): Promise<{ sites: { id: string; name: string }[]; models: { name: string; displayName: string }[] }> {
  return httpGet('/api/admin/analytics/options')
}

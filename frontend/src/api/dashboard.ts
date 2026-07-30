import { httpGet } from './http'

export interface DashboardStats {
  siteCount: number
  modelCount: number
  mappingCount: number
  routeCount: number
  accessKeyCount: number
  detectionTaskCount: number
  coreBaseUrl: string
  coreStatusText: string
  coreSyncStatusText: string
  coreSyncDetailText: string
}

export async function getDashboardStats(): Promise<DashboardStats> {
  return httpGet<DashboardStats>('/api/admin/dashboard/stats')
}

import { httpGet, httpPost, httpDelete } from './http'

export interface ModelHealthTimelineSegment {
  status: string
  count: number
  successCount: number
  failureCount: number
  startAt: string
  endAt: string
}
export interface ModelHealthMonitoredModel {
  modelLibraryItemId: string
  displayName: string
  siteCount: number
  healthySiteCount: number
  unhealthySiteCount: number
  lastCheckedAt: string | null
  averageDurationMs: number | null
  successCount: number
  failureCount: number
  totalRequestCount: number
  averageSuccessRate: number
  timelineSegments: ModelHealthTimelineSegment[]
}
export interface ModelHealthDashboard {
  monitoredModels: ModelHealthMonitoredModel[]
  availableModels: { id: string; displayName: string }[]
  healthData: Record<string, Array<{
    siteName: string
    remoteModelName: string
    lastStatus: string
    lastCheckedAt: string | null
    lastDurationMs: number | null
    successRate: number
    successCount: number
    failureCount: number
    totalRequestCount: number
    timelineSegments: ModelHealthTimelineSegment[]
  }>>
  rangeOptions: Array<{ value: string; label: string }>
}

export async function getModelHealthDashboard(range?: string): Promise<ModelHealthDashboard> {
  const q = range ? `?range=${encodeURIComponent(range)}` : ''
  return httpGet<ModelHealthDashboard>(`/api/admin/model-health${q}`)
}
export async function addMonitor(modelId: string): Promise<void> {
  await httpPost(`/api/admin/model-health/${modelId}/monitor`)
}
export async function removeMonitor(modelId: string): Promise<void> {
  await httpDelete(`/api/admin/model-health/${modelId}/monitor`)
}

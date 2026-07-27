import { httpGet, httpPost } from './http'

export interface DetectionSiteStatus {
  mappingId: string
  siteName: string
  remoteModelName: string
  lastStatus: string
  lastCheckedAt: string | null
  lastDurationMs: number | null
}
export interface DetectionModelGroup {
  modelLibraryItemId: string
  modelName: string
  displayName: string
  sites: DetectionSiteStatus[]
}
export interface DetectionMatrix {
  modelGroups: DetectionModelGroup[]
  filterModels: { id: string; displayName: string }[]
}

export async function getDetectionMatrix(): Promise<DetectionMatrix> {
  return httpGet<DetectionMatrix>('/api/admin/detection/matrix')
}

export async function probeMapping(mappingId: string): Promise<{ status: string; durationMs: number; errorMessage?: string }> {
  return httpPost(`/api/admin/detection/probe/${mappingId}`)
}

// 异步批量探测（probe-model / probe-all），返回 taskId 用于轮询
export async function probeModel(modelId: string): Promise<{ taskId: string }> {
  return httpPost<{ taskId: string }>(`/api/admin/detection/probe-model/${modelId}`)
}
export async function probeAll(): Promise<{ taskId: string }> {
  return httpPost<{ taskId: string }>('/api/admin/detection/probe-all')
}
export async function getProbeProgress(taskId: string): Promise<{ isCompleted: boolean; allResults: Array<{ mappingId: string; siteName: string; status: string }> }> {
  return httpGet(`/api/admin/detection/progress/${taskId}`)
}

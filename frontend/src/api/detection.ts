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

export interface ProbeResultItem {
  mappingId: string
  siteName: string
  remoteModelName: string
  status: string
  durationMs: number | null
  error?: string | null
}

export interface ProbeProgress {
  taskId: string
  total: number
  completed: number
  isCompleted: boolean
  newResults: ProbeResultItem[]
}

export async function getDetectionMatrix(): Promise<DetectionMatrix> {
  return httpGet<DetectionMatrix>('/api/admin/detection/matrix')
}

export async function probeMapping(mappingId: string): Promise<ProbeResultItem> {
  return httpPost<ProbeResultItem>(
    `/api/admin/detection/probe/${mappingId}`,
    undefined,
    { skipErrorNotify: true }
  )
}

export async function probeModel(modelId: string): Promise<{ taskId: string }> {
  return httpPost<{ taskId: string }>(
    `/api/admin/detection/probe-model/${modelId}`,
    undefined,
    { skipErrorNotify: true }
  )
}

export async function probeAll(): Promise<{ taskId: string }> {
  return httpPost<{ taskId: string }>(
    '/api/admin/detection/probe-all',
    undefined,
    { skipErrorNotify: true }
  )
}

export async function getProbeProgress(taskId: string): Promise<ProbeProgress> {
  // 轮询失败由页面退避重试，避免每次临时失败都弹出全局错误。
  return httpGet<ProbeProgress>(
    `/api/admin/detection/progress/${taskId}`,
    { skipErrorNotify: true }
  )
}

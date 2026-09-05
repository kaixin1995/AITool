import { httpGet, httpPost, httpDelete } from './http'

export interface DetectionTaskItem {
  id: string
  name: string
  /** 执行间隔（秒），最小 10；调度时自动附加随机抖动。 */
  intervalSeconds: number
  isEnabled: boolean
  /** 绑定的站点模型映射（站点 × 模型粒度）；null 表示检测全部。 */
  siteModelMappingId: string | null
  siteName: string | null
  remoteModelName: string | null
  /** 遗留字段：按模型检测的旧任务。 */
  modelLibraryItemId: string | null
  modelName: string | null
  createdAt: string
  lastExecutionSummary: string | null
  lastExecutionStatus: string | null
  lastExecutionStartedAt: string | null
  lastExecutionFinishedAt: string | null
  executionHistory: Array<{ startedAt: string; finishedAt: string | null; status: string; summary: string | null }>
}
export interface CreateDetectionTaskRequest {
  name: string
  intervalSeconds: number
  /** 绑定的站点模型映射；null 表示检测全部。 */
  siteModelMappingId?: string | null
  /** 遗留字段：未绑定映射时可指定按模型检测。 */
  modelLibraryItemId?: string | null
}
/** 可选站点模型目标（与聊天页同源：站点 × 模型映射，仅启用项）。 */
export interface DetectionTargetOption {
  mappingId: string
  siteId: string
  siteName: string
  remoteModelName: string
  modelLibraryItemId: string
  modelName: string
}

export async function listDetectionTasks(): Promise<{ tasks: DetectionTaskItem[]; availableTargets: DetectionTargetOption[] }> {
  return httpGet('/api/admin/detection-tasks')
}
export async function createDetectionTask(payload: CreateDetectionTaskRequest): Promise<{ id: string }> {
  return httpPost<{ id: string }>('/api/admin/detection-tasks', payload)
}
export async function toggleDetectionTask(id: string): Promise<{ isEnabled: boolean }> {
  return httpPost<{ isEnabled: boolean }>(`/api/admin/detection-tasks/${id}/toggle`)
}
export async function executeDetectionTask(id: string): Promise<void> {
  await httpPost(`/api/admin/detection-tasks/${id}/execute`)
}
export async function deleteDetectionTask(id: string): Promise<void> {
  await httpDelete(`/api/admin/detection-tasks/${id}`)
}

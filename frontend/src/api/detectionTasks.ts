import { httpGet, httpPost, httpDelete } from './http'

export interface DetectionTaskItem {
  id: string
  name: string
  cronExpression: string
  isEnabled: boolean
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
  cronExpression: string
  modelLibraryItemId?: string | null
}

export async function listDetectionTasks(): Promise<{ tasks: DetectionTaskItem[]; availableModels: { id: string; displayName: string }[] }> {
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

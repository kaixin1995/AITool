import { httpGet, httpPost, httpDelete, httpPut } from './http'

export interface CodexAccount {
  id: string
  displayName: string
  email: string | null
  planType: string | null
  isEnabled: boolean
  isQuotaCooling: boolean
  quotaCoolingUntil: string | null
  lastQuotaCheckedAt: string | null
}
export interface CodexInspectionStatus {
  isRunning: boolean
  nextScheduledAt: string | null
  lastRun: { finishedAt: string; totalAccounts: number; disabledAccounts: number } | null
  logs: Array<{ time: string; level: string; message: string }>
}

export async function listCodexAccounts(): Promise<CodexAccount[]> {
  return httpGet<CodexAccount[]>('/api/admin/codex/accounts')
}
export async function toggleCodexAccount(id: string): Promise<void> {
  await httpPost(`/api/admin/codex/accounts/${id}/toggle`)
}
export async function refreshCodexQuota(id: string): Promise<void> {
  await httpPost(`/api/admin/codex/accounts/${id}/refresh-quota`)
}
export async function resetCodexQuota(id: string): Promise<void> {
  await httpPost(`/api/admin/codex/accounts/${id}/reset-quota`)
}
export async function updateCodexAccount(id: string, displayName: string): Promise<void> {
  await httpPut(`/api/admin/codex/accounts/${id}`, { displayName })
}
export async function deleteCodexAccount(id: string): Promise<void> {
  await httpDelete(`/api/admin/codex/accounts/${id}`)
}
export async function importCredential(jsonText: string): Promise<void> {
  // 后端接受 raw JSON 字符串体（单文件导入）
  await httpPost('/api/admin/codex/import-credential', JSON.parse(jsonText))
}
export async function exportCredentials(accountIds: string[]): Promise<void> {
  // 后端返回 { credentials: [...] }，前端触发下载
  const resp = await httpPost<{ credentials: unknown[] }>('/api/admin/codex/accounts/export-credentials', { accountIds })
  const blob = new Blob([JSON.stringify(resp.credentials, null, 2)], { type: 'application/json' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = `codex-credentials-${Date.now()}.json`
  a.click()
  URL.revokeObjectURL(url)
}
export async function startCodexOAuth(): Promise<{ url: string; state: string }> {
  // 后端返回字段名为 url（不是 authorizeUrl）
  return httpPost<{ url: string; state: string }>('/api/admin/codex/start-oauth')
}
export async function completeCodexOAuth(callbackUrl: string): Promise<void> {
  await httpPost('/api/admin/codex/complete-oauth', { callbackUrl })
}
export async function runCodexInspection(): Promise<void> {
  await httpPost('/api/admin/codex/inspection/run')
}
export async function getCodexInspectionStatus(): Promise<CodexInspectionStatus> {
  // 巡检是可选功能：未启用时后端返回 404，这里静默处理（不弹全局错误提示）。
  return httpGet<CodexInspectionStatus>('/api/admin/codex/inspection/status', { skipErrorNotify: true })
}

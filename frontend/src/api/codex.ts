import { httpGet, httpPost, httpDelete, httpPut } from './http'

// 额度窗口（进度条数据），与后端 CodexUsageParser 解析结果对齐。
export interface CodexQuotaWindow {
  id: string
  label: string
  usedPercent: number
  resetLabel: string | null
}

export interface CodexAccount {
  id: string
  displayName: string
  email: string | null
  accountId: string | null
  planType: string | null
  isEnabled: boolean
  isQuotaCooling: boolean
  quotaCoolingUntil: string | null
  lastQuotaCheckedAt: string | null
  tokenExpiresAt: string | null
  createdAt: string | null
  // 额度明细（从最近一次额度查询解析）
  windows?: CodexQuotaWindow[] | null
  fiveHourUsedPercent?: number | null
  weeklyUsedPercent?: number | null
  resetCreditsAvailableCount?: number | null
  autoDisableThreshold?: number | null
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
export async function refreshCodexToken(id: string): Promise<void> {
  await httpPost(`/api/admin/codex/accounts/${id}/refresh-token`)
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
export async function completeCodexOAuth(callbackUrl: string, displayName?: string): Promise<void> {
  await httpPost('/api/admin/codex/complete-oauth', { callbackUrl, displayName })
}
export async function runCodexInspection(): Promise<void> {
  await httpPost('/api/admin/codex/inspection/run')
}
export async function getCodexInspectionStatus(): Promise<CodexInspectionStatus> {
  // 巡检是可选功能：未启用时后端返回 404，这里静默处理（不弹全局错误提示）。
  return httpGet<CodexInspectionStatus>('/api/admin/codex/inspection/status', { skipErrorNotify: true })
}
// 拉取上游 Codex 模型目录（供选择导入）。
export async function fetchCodexModels(id: string): Promise<Array<{ id: string; name: string }>> {
  return httpGet<Array<{ id: string; name: string }>>(`/api/admin/codex/accounts/${id}/fetch-models`)
}
export async function importSelectedCodexModels(id: string, modelIds: string[]): Promise<void> {
  await httpPost(`/api/admin/codex/accounts/${id}/import-selected-models`, { modelIds })
}
// 重置额度信用（rate_limit_reset_credits）。
export async function getResetCredits(id: string): Promise<{ availableCount: number; items: Array<{ count: number; expiresAt: string }> }> {
  return httpGet(`/api/admin/codex/accounts/${id}/reset-credits`)
}
export async function consumeResetCredit(id: string): Promise<void> {
  await httpPost(`/api/admin/codex/accounts/${id}/consume-reset-credit`)
}

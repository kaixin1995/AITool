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
  lastFinishedAt: string | null
}

export interface CodexInspectionAccountResult {
  accountId: string
  displayName: string
  action: 'keep' | 'disable' | 'enable'
  reason: string
  fromCache: boolean
  weeklyUsedPercent: number | null
  fiveHourUsedPercent: number | null
  checkedAt: string
}

export interface CodexInspectionRunResult {
  isRunning: boolean
  forcedRefresh: boolean
  startedAt: string | null
  finishedAt: string | null
  accounts: CodexInspectionAccountResult[]
  keepCount: number
  disableCount: number
  enableCount: number
  cacheCount: number
  realRefreshCount: number
  autoTriggered: boolean
}

export interface CodexInspectionLog {
  at: string
  category: string
  message: string
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
export interface CodexCredentialImportFailure {
  fileName: string | null
  error: string
}

export interface CodexCredentialImportResult {
  successes: CodexAccount[]
  failures: CodexCredentialImportFailure[]
}

export async function importCredential(
  jsonText: string
): Promise<CodexCredentialImportResult> {
  // 文本导入提供稳定文件名，保持旧页面的默认账号名称。
  const result = await httpPost<Partial<CodexCredentialImportResult>>(
    '/api/admin/codex/import-credential?name=imported.json',
    JSON.parse(jsonText)
  )
  return {
    successes: result.successes ?? [],
    failures: result.failures ?? []
  }
}

export async function importCredentialFiles(
  files: File[]
): Promise<CodexCredentialImportResult> {
  const form = new FormData()
  files.forEach(file => form.append('files', file))
  const result = await httpPost<Partial<CodexCredentialImportResult>>(
    '/api/admin/codex/import-credential',
    form,
    { headers: { 'Content-Type': 'multipart/form-data' } }
  )
  return {
    successes: result.successes ?? [],
    failures: result.failures ?? []
  }
}

export interface CodexExportCredential {
  account_id?: string | null
  email?: string | null
  display_name?: string | null
  plan_type?: string | null
  access_token?: string
  refresh_token?: string
  id_token?: string
  token_expires_at?: string | null
  created_at?: string | null
  [key: string]: unknown
}

export interface CodexExportResult {
  credentials: CodexExportCredential[]
}

export async function exportCredentials(
  accountIds: string[]
): Promise<CodexExportResult> {
  return httpPost<CodexExportResult>(
    '/api/admin/codex/accounts/export-credentials',
    { accountIds }
  )
}
export async function startCodexOAuth(): Promise<{ url: string; state: string }> {
  // 后端返回字段名为 url（不是 authorizeUrl）
  return httpPost<{ url: string; state: string }>('/api/admin/codex/start-oauth', {})
}
export async function completeCodexOAuth(callbackUrl: string, displayName?: string): Promise<void> {
  await httpPost('/api/admin/codex/complete-oauth', { callbackUrl, displayName })
}
export async function runCodexInspection(
  force = false
): Promise<CodexInspectionRunResult> {
  return httpPost<CodexInspectionRunResult>(
    `/api/admin/codex/inspection/run?force=${force}`
  )
}
export async function getCodexInspectionStatus(): Promise<CodexInspectionStatus> {
  // 巡检是可选功能：未启用时后端返回 404，这里静默处理（不弹全局错误提示）。
  return httpGet<CodexInspectionStatus>('/api/admin/codex/inspection/status', { skipErrorNotify: true })
}
export async function getCodexInspectionLastRun(): Promise<CodexInspectionRunResult | null> {
  return httpGet<CodexInspectionRunResult | null>(
    '/api/admin/codex/inspection/last-run',
    { skipErrorNotify: true }
  )
}
export async function getCodexInspectionLogs(): Promise<CodexInspectionLog[]> {
  return httpGet<CodexInspectionLog[]>(
    '/api/admin/codex/inspection/logs',
    { skipErrorNotify: true }
  )
}
export interface CodexRemoteModelItem {
  remoteModelName: string
  displayName: string
  existingMappingId: string | null
  isEnabled: boolean
  existingDisplayName: string | null
}

export interface CodexModelSelection {
  remoteModelName: string
  displayName: string
  selected: boolean
}

export interface CodexResetCredit {
  id: string
  status: string
  grantedAt: string | null
  expiresAt: string | null
}

export interface CodexResetCreditsInfo {
  availableCount: number
  credits: CodexResetCredit[]
  success: boolean
  error: string | null
  rawJson: string | null
}

// 拉取上游 Codex 模型目录（供选择导入）。
export async function fetchCodexModels(id: string): Promise<CodexRemoteModelItem[]> {
  return httpGet<CodexRemoteModelItem[]>(`/api/admin/codex/accounts/${id}/fetch-models`)
}
export async function importSelectedCodexModels(id: string, selections: CodexModelSelection[]): Promise<void> {
  await httpPost(`/api/admin/codex/accounts/${id}/import-selected-models`, { selections })
}
// 重置额度信用（rate_limit_reset_credits）。
export async function getResetCredits(id: string): Promise<CodexResetCreditsInfo> {
  return httpGet<CodexResetCreditsInfo>(`/api/admin/codex/accounts/${id}/reset-credits`)
}
export async function consumeResetCredit(id: string): Promise<void> {
  await httpPost(`/api/admin/codex/accounts/${id}/consume-reset-credit`)
}

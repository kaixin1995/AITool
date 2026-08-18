import { httpGet, httpPost, httpDelete, httpPut } from './http'

// 通用额度窗口（进度条数据），与后端 OAuth 账号额度提供程序返回结果对齐。
export interface OAuthQuotaWindow {
  id: string
  label: string
  usedPercent: number | null
  resetLabel: string | null
}

export interface OAuthAccount {
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
  windows?: OAuthQuotaWindow[] | null
  fiveHourUsedPercent?: number | null
  weeklyUsedPercent?: number | null
  resetCreditsAvailableCount?: number | null
  autoDisableThreshold?: number | null
}
export interface OAuthInspectionStatus {
  isRunning: boolean
  nextScheduledAt: string | null
  lastFinishedAt: string | null
}

export interface OAuthInspectionAccountResult {
  providerKey?: string
  accountId: string
  displayName: string
  action: 'keep' | 'disable' | 'enable'
  reason: string
  fromCache: boolean
  windows?: OAuthQuotaWindow[] | null
  weeklyUsedPercent?: number | null
  fiveHourUsedPercent?: number | null
  checkedAt: string
}

export interface OAuthInspectionRunResult {
  isRunning: boolean
  forcedRefresh: boolean
  startedAt: string | null
  finishedAt: string | null
  accounts: OAuthInspectionAccountResult[]
  keepCount: number
  disableCount: number
  enableCount: number
  cacheCount: number
  realRefreshCount: number
  autoTriggered: boolean
}

export interface OAuthInspectionLog {
  at: string
  category: string
  message: string
}

export async function listOAuthAccounts(): Promise<OAuthAccount[]> {
  return httpGet<OAuthAccount[]>('/api/admin/oauth/accounts')
}
export async function toggleOAuthAccount(id: string): Promise<void> {
  await httpPost(`/api/admin/oauth/accounts/${id}/toggle`)
}
export async function refreshOAuthQuota(id: string): Promise<void> {
  await httpPost(`/api/admin/oauth/accounts/${id}/refresh-quota`)
}
export async function resetOAuthQuota(id: string): Promise<void> {
  await httpPost(`/api/admin/oauth/accounts/${id}/reset-quota`)
}
export async function updateOAuthAccount(id: string, displayName: string, refreshToken?: string): Promise<OAuthAccount & { message?: string | null }> {
  const body: Record<string, string> = { displayName }
  if (refreshToken && refreshToken.trim()) body.refreshToken = refreshToken.trim()
  return httpPut(`/api/admin/oauth/accounts/${id}`, body)
}
export async function refreshOAuthToken(id: string): Promise<void> {
  await httpPost(`/api/admin/oauth/accounts/${id}/refresh-token`)
}
export async function deleteOAuthAccount(id: string): Promise<void> {
  await httpDelete(`/api/admin/oauth/accounts/${id}`)
}
export interface OAuthCredentialImportFailure {
  fileName: string | null
  error: string
}

export interface OAuthCredentialImportResult {
  successes: OAuthAccount[]
  failures: OAuthCredentialImportFailure[]
}

export async function importCredential(
  jsonText: string
): Promise<OAuthCredentialImportResult> {
  // 文本导入提供稳定文件名，保持旧页面的默认账号名称。
  const result = await httpPost<Partial<OAuthCredentialImportResult>>(
    '/api/admin/oauth/import-credential?name=imported.json',
    JSON.parse(jsonText)
  )
  return {
    successes: result.successes ?? [],
    failures: result.failures ?? []
  }
}

export async function importCredentialFiles(
  files: File[]
): Promise<OAuthCredentialImportResult> {
  const form = new FormData()
  files.forEach(file => form.append('files', file))
  const result = await httpPost<Partial<OAuthCredentialImportResult>>(
    '/api/admin/oauth/import-credential',
    form,
    { headers: { 'Content-Type': 'multipart/form-data' } }
  )
  return {
    successes: result.successes ?? [],
    failures: result.failures ?? []
  }
}

export interface OAuthExportCredential {
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

export interface OAuthExportResult {
  credentials: OAuthExportCredential[]
}

export async function exportCredentials(
  accountIds: string[]
): Promise<OAuthExportResult> {
  return httpPost<OAuthExportResult>(
    '/api/admin/oauth/accounts/export-credentials',
    { accountIds }
  )
}
export async function startOAuth(): Promise<{ url: string; state: string }> {
  // 后端返回字段名为 url（不是 authorizeUrl）
  return httpPost<{ url: string; state: string }>('/api/admin/oauth/start-oauth', {})
}
export async function completeOAuth(callbackUrl: string, displayName?: string): Promise<void> {
  await httpPost('/api/admin/oauth/complete-oauth', { callbackUrl, displayName })
}
export async function runOAuthInspection(
  force = false
): Promise<OAuthInspectionRunResult> {
  return httpPost<OAuthInspectionRunResult>(
    `/api/admin/oauth/inspection/run?force=${force}`
  )
}
export async function getOAuthInspectionStatus(): Promise<OAuthInspectionStatus> {
  // 巡检是可选功能：未启用时后端返回 404，这里静默处理（不弹全局错误提示）。
  return httpGet<OAuthInspectionStatus>('/api/admin/oauth/inspection/status', { skipErrorNotify: true })
}
export async function getOAuthInspectionLastRun(): Promise<OAuthInspectionRunResult | null> {
  return httpGet<OAuthInspectionRunResult | null>(
    '/api/admin/oauth/inspection/last-run',
    { skipErrorNotify: true }
  )
}
export async function getOAuthInspectionLogs(): Promise<OAuthInspectionLog[]> {
  return httpGet<OAuthInspectionLog[]>(
    '/api/admin/oauth/inspection/logs',
    { skipErrorNotify: true }
  )
}
export interface OAuthRemoteModelItem {
  remoteModelName: string
  displayName: string
  existingMappingId: string | null
  isEnabled: boolean
  existingDisplayName: string | null
}

export interface OAuthModelSelection {
  remoteModelName: string
  displayName: string
  selected: boolean
}

export interface OAuthResetCredit {
  id: string
  status: string
  grantedAt: string | null
  expiresAt: string | null
}

export interface OAuthResetCreditsInfo {
  availableCount: number
  credits: OAuthResetCredit[]
  success: boolean
  error: string | null
  rawJson: string | null
}

// 拉取账号提供程序的上游模型目录（供选择导入）。
export async function fetchOAuthModels(id: string): Promise<OAuthRemoteModelItem[]> {
  return httpGet<OAuthRemoteModelItem[]>(`/api/admin/oauth/accounts/${id}/fetch-models`)
}
export async function importSelectedOAuthModels(id: string, selections: OAuthModelSelection[]): Promise<void> {
  await httpPost(`/api/admin/oauth/accounts/${id}/import-selected-models`, { selections })
}
// 重置额度信用（rate_limit_reset_credits）。
export async function getResetCredits(id: string): Promise<OAuthResetCreditsInfo> {
  return httpGet<OAuthResetCreditsInfo>(`/api/admin/oauth/accounts/${id}/reset-credits`)
}
export async function consumeResetCredit(id: string): Promise<void> {
  await httpPost(`/api/admin/oauth/accounts/${id}/consume-reset-credit`)
}

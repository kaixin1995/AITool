import { httpGet, httpPut, httpPost } from './http'

export interface SystemSettings {
  proxyRequestTimeoutSeconds: number
  proxyStreamIdleTimeoutSeconds: number
  proxyRetryCount: number
  rateLimitRetryCount: number
  detectionRequestTimeoutSeconds: number
  detectionRetryCount: number
  detectionConcurrency: number
  circuitBreakerFailureThreshold: number
  circuitBreakerRecoveryMinutes: number
  usageLogRetentionDays: number
  usageLogAutoCleanupEnabled: boolean
  developerFeaturesEnabled: boolean
  developerTraceEnabled: boolean
  developerFailureDumpEnabled: boolean
  developerSimulatorEnabled: boolean
  developerProtocolDiagnosticsEnabled: boolean
  developerSqlMigrationsEnabled: boolean
  concurrencyMode: number
  concurrencyQueueTimeoutSeconds: number
  oauthFeaturesEnabled: boolean
  oauthInspectionEnabled: boolean
  oauthInspectionIntervalSeconds: number
  oauthQuotaMaxCacheHours: number
  oauthAutoDisableThresholdPercent: number
  oauthInspectionCacheEnabled: boolean
  lastUsageLogPrunedAt: string | null
  lastUsageLogPrunedCount: number
}

export async function getSystemSettings(): Promise<SystemSettings> {
  return httpGet<SystemSettings>('/api/admin/system/settings')
}

export async function updateSystemSettings(payload: SystemSettings): Promise<void> {
  await httpPut('/api/admin/system/settings', payload)
}

export interface ClearUsageLogsPayload {
  source?: string
  startTime?: string
  endTime?: string
}

export async function clearUsageLogs(clearAll: boolean, payload: ClearUsageLogsPayload = {}): Promise<{ deletedCount: number }> {
  return httpPost<{ deletedCount: number }>(`/api/admin/system/clear-usage-logs?clearAll=${clearAll}`, payload)
}

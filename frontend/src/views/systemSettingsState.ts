const numericFieldLabels: Record<string, string> = {
  proxyRequestTimeoutSeconds: '代理超时时间',
  proxyStreamIdleTimeoutSeconds: '流式空闲超时',
  proxyRetryCount: '代理重试次数',
  rateLimitRetryCount: '429 重试次数',
  detectionRequestTimeoutSeconds: '检测超时时间',
  detectionRetryCount: '检测重试次数',
  detectionConcurrency: '检测并发数',
  circuitBreakerFailureThreshold: '熔断失败阈值',
  circuitBreakerRecoveryMinutes: '熔断恢复时间',
  usageLogRetentionDays: 'UsageLogs 保留天数',
  concurrencyMode: '并发打满策略',
  concurrencyQueueTimeoutSeconds: '排队等待超时',
  oauthInspectionIntervalSeconds: '账号巡检周期',
  oauthQuotaMaxCacheHours: '额度缓存最大小时数',
  oauthAutoDisableThresholdPercent: '自动禁用阈值'
}

export function validateSystemSettingsNumbers(settings: object): string | null {
  const values = settings as Record<string, unknown>
  for (const [key, label] of Object.entries(numericFieldLabels)) {
    if (!(key in values)) continue
    const value = values[key]
    if (typeof value !== 'number' || !Number.isFinite(value) || !Number.isInteger(value)) {
      return `${label}必须填写有效整数`
    }
  }
  return null
}

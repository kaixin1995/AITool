import { describe, expect, it } from 'vitest'
import { validateSystemSettingsNumbers } from './systemSettingsState'

describe('系统设置数值校验', () => {
  it('拒绝空值和非整数，避免向后端 int 字段提交 null', () => {
    expect(validateSystemSettingsNumbers({ proxyRetryCount: null })).toContain('代理重试次数')
    expect(validateSystemSettingsNumbers({ proxyRetryCount: 1.5 })).toContain('代理重试次数')
  })

  it('接受后端允许的无上限整数', () => {
    expect(validateSystemSettingsNumbers({
      proxyRetryCount: 8,
      detectionRetryCount: 8,
      detectionConcurrency: 50,
      usageLogRetentionDays: 730,
      codexQuotaMaxCacheHours: 168
    })).toBeNull()
  })
})

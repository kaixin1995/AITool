import { describe, expect, it } from 'vitest'
import {
  buildAnalyticsDefaultCustomRange,
  calculateAnalyticsTotalTokens,
  shouldAutoLoadAnalytics
} from './analyticsState'

describe('Analytics 汇总', () => {
  it('输入 Token 已含缓存时不重复累计缓存 Token', () => {
    expect(calculateAnalyticsTotalTokens({
      totalInputTokens: 120,
      totalCachedTokens: 20,
      totalOutputTokens: 30
    })).toBe(150)
  })

  it('优先使用后端返回的总 Token', () => {
    expect(calculateAnalyticsTotalTokens({
      totalTokens: 180,
      totalInputTokens: 120,
      totalCachedTokens: 20,
      totalOutputTokens: 30
    })).toBe(180)
  })
})

describe('Analytics 时间筛选', () => {
  it('自定义时间等待用户点击应用，预设范围自动查询', () => {
    expect(shouldAutoLoadAnalytics('custom')).toBe(false)
    expect(shouldAutoLoadAnalytics('week')).toBe(true)
  })

  it('自定义范围默认填充本周一到当天结束', () => {
    const { startTime, endTime } = buildAnalyticsDefaultCustomRange(new Date('2026-07-30T12:34:56'))

    const start = new Date(startTime)
    const end = new Date(endTime)
    expect([start.getFullYear(), start.getMonth(), start.getDate(), start.getHours(), start.getMinutes()])
      .toEqual([2026, 6, 27, 0, 0])
    expect([end.getFullYear(), end.getMonth(), end.getDate(), end.getHours(), end.getMinutes()])
      .toEqual([2026, 6, 30, 23, 59])
  })
})

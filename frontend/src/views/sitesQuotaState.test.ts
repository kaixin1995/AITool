import { describe, expect, it } from 'vitest'
import {
  formatCheckedAtAgo,
  formatResetCountdown,
  isStaleQuota,
  quotaBarColor,
  quotaStatusChip,
  remainingPercent
} from './sitesQuotaState'

describe('站点额度 - 剩余百分比', () => {
  it('常规值换算并四舍五入', () => {
    expect(remainingPercent(0)).toBe(100)
    expect(remainingPercent(42.4)).toBe(58)
    expect(remainingPercent(100)).toBe(0)
  })

  it('上游异常值按边界裁剪', () => {
    expect(remainingPercent(-5)).toBe(100)
    expect(remainingPercent(150)).toBe(0)
  })

  it('null/undefined/非数值按 0 已用处理', () => {
    expect(remainingPercent(null)).toBe(100)
    expect(remainingPercent(undefined)).toBe(100)
    expect(remainingPercent(Number.NaN)).toBe(100)
  })
})

describe('站点额度 - 进度条颜色', () => {
  it('阈值与 OAuthView.quotaColor 一致（剩余 <20 红、<50 橙、其余绿）', () => {
    expect(quotaBarColor(95)).toBe('error') // 剩 5%
    expect(quotaBarColor(85)).toBe('error') // 剩 15%，仍 < 20
    expect(quotaBarColor(70)).toBe('warning') // 剩 30%
  })

  it('边界值判定', () => {
    expect(quotaBarColor(0)).toBe('success') // 剩 100%
    expect(quotaBarColor(50)).toBe('success') // 剩 50%，不小于 50
    expect(quotaBarColor(51)).toBe('warning') // 剩 49%
    expect(quotaBarColor(80)).toBe('warning') // 剩 20%，不小于 20
    expect(quotaBarColor(81)).toBe('error') // 剩 19%
    expect(quotaBarColor(null)).toBe('success')
  })
})

describe('站点额度 - 重置倒计时', () => {
  const now = Date.parse('2026-10-09T12:00:00Z')

  it('无重置时间或非法时间显示占位符', () => {
    expect(formatResetCountdown(null, now)).toBe('—')
    expect(formatResetCountdown(undefined, now)).toBe('—')
    expect(formatResetCountdown('not-a-date', now)).toBe('—')
  })

  it('已到期、临界与不足一分钟显示即将重置', () => {
    expect(formatResetCountdown('2026-10-09T11:59:59Z', now)).toBe('即将重置')
    expect(formatResetCountdown('2026-10-09T12:00:00Z', now)).toBe('即将重置')
    expect(formatResetCountdown('2026-10-09T12:00:59Z', now)).toBe('即将重置')
  })

  it('分级拼装相对时间', () => {
    expect(formatResetCountdown('2026-10-09T12:01:00Z', now)).toBe('1 分后')
    expect(formatResetCountdown('2026-10-09T12:04:59Z', now)).toBe('4 分后')
    expect(formatResetCountdown('2026-10-09T15:12:00Z', now)).toBe('3 小时 12 分后')
    expect(formatResetCountdown('2026-10-11T14:00:00Z', now)).toBe('2 天 2 小时后')
  })
})

describe('站点额度 - 上次查询相对时间', () => {
  const now = Date.parse('2026-10-09T12:00:00Z')

  it('未查询或非法时间为空串', () => {
    expect(formatCheckedAtAgo(null, now)).toBe('')
    expect(formatCheckedAtAgo('bad', now)).toBe('')
  })

  it('分级显示', () => {
    expect(formatCheckedAtAgo('2026-10-09T11:59:30Z', now)).toBe('刚刚')
    expect(formatCheckedAtAgo('2026-10-09T11:30:00Z', now)).toBe('30 分钟前')
    expect(formatCheckedAtAgo('2026-10-09T08:00:00Z', now)).toBe('4 小时前')
    expect(formatCheckedAtAgo('2026-10-07T12:00:00Z', now)).toBe('2 天前')
  })
})

describe('站点额度 - 状态徽章', () => {
  it('四态映射', () => {
    expect(quotaStatusChip('ok')).toEqual({ text: '正常', type: 'success' })
    expect(quotaStatusChip('invalid_credential')).toEqual({ text: '密钥失效', type: 'error' })
    expect(quotaStatusChip('error')).toEqual({ text: '查询失败', type: 'error' })
    expect(quotaStatusChip('never')).toEqual({ text: '未查询', type: 'default' })
  })

  it('失败态视为过期缓存需置灰', () => {
    expect(isStaleQuota('error')).toBe(true)
    expect(isStaleQuota('invalid_credential')).toBe(true)
    expect(isStaleQuota('ok')).toBe(false)
    expect(isStaleQuota('never')).toBe(false)
  })
})

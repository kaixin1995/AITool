import { describe, expect, it } from 'vitest'
import { formatCompact, formatDuration, formatPercentage } from '../views/analyticsFormat'

describe('统计格式化', () => {
  it('以与原统计页一致的单位展示 Token 数值', () => {
    expect(formatCompact(12_345)).toBe('12.35K')
    expect(formatCompact(1_234_567_890_123)).toBe('1.23T')
  })

  it('以与原统计页一致的单位展示耗时', () => {
    expect(formatDuration(60_000)).toBe('1.00 min')
    expect(formatDuration(3_600_000)).toBe('1.00 h')
  })

  it('固定两位小数展示百分比', () => {
    expect(formatPercentage(50)).toBe('50.00%')
    expect(formatPercentage(null)).toBe('0.00%')
  })
})

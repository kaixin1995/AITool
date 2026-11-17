import { describe, expect, it } from 'vitest'
import type { CodexQuotaPeriod } from '@/api/oauth'
import { previousQuotaPeriod, projectQuotaPeriod } from './quotaPeriodDisplay'

const now = Date.parse('2026-11-08T12:00:00Z')
function period(changes: Partial<CodexQuotaPeriod> = {}): CodexQuotaPeriod {
  return { id: 'current', windowId: 'weekly', label: '周限额', startAt: '2026-11-06T00:00:00Z', resetAt: '2026-11-13T00:00:00Z', endAt: null, firstObservedAt: '2026-11-06T00:00:00Z', lastObservedAt: '2026-11-08T11:59:00Z', lastUsageAt: '2026-11-08T11:58:00Z', aggregatedAt: '2026-11-08T12:00:00Z', usedPercent: 25, boundaryUncertain: false, isCurrent: true, inputTokens: 50, cachedTokens: 30, outputTokens: 20, totalTokens: 100, requestCount: 10, successCount: 9, unknownCount: 0, estimatedCostUsd: 2, unpricedCount: 0, models: [], ...changes }
}

describe('quota period comparison', () => {
  it('matches only the latest preceding period of the same window', () => {
    const current = period()
    const previous = period({ id: 'previous', isCurrent: false, endAt: current.startAt })
    const other = period({ id: 'other', windowId: 'five-hour', isCurrent: false, endAt: current.startAt })
    expect(previousQuotaPeriod([other, previous, current], current)?.id).toBe('previous')
    expect(previousQuotaPeriod([period({ id: 'gap', isCurrent: false, endAt: '2026-11-01T00:00:00Z' })], current)).toBeUndefined()
  })
  it('uses a server-verified historical link across an idle gap', () => {
    const current = period({ historyNormalized: true, previousPeriodId: 'previous' })
    const previous = period({ id: 'previous', isCurrent: false, endAt: '2026-11-05T00:00:00Z' })
    const fragment = period({ id: 'fragment', isCurrent: false, endAt: current.startAt })
    expect(previousQuotaPeriod([fragment, previous, current], current)?.id).toBe('previous')
    expect(previousQuotaPeriod([fragment, current], current)).toBeUndefined()
    expect(previousQuotaPeriod([fragment, previous, { ...current, historyNormalized: false }], { ...current, historyNormalized: false })?.id).toBe('fragment')
  })
  it('projects from the current period usage at the quota observation, with transparent warnings', () => {
    const basis = { observedAt: '2026-11-08T11:59:00Z', requestCount: 10, totalTokens: 100, unknownCount: 1, estimatedCostUsd: 2, unpricedCount: 0 }
    const current = period({ projectionBasis: basis, boundaryUncertain: true, requestCount: 20, totalTokens: 900, lastUsageAt: '2026-11-08T11:59:30Z' })
    const result = projectQuotaPeriod(current, now)!
    expect(result).toMatchObject({ requests: 40, tokens: 400, costUsd: 8 })
    expect(result.warnings.join(' ')).toContain('周期边界不确定')
    expect(result.warnings.join(' ')).toContain('1 次用量未知')
    expect(projectQuotaPeriod({ ...current, usedPercent: 2 }, now)?.warnings.join(' ')).toContain('比例较低')
    expect(projectQuotaPeriod({ ...current, projectionBasis: { ...basis, unpricedCount: 1 } }, now)?.costUsd).toBeNull()
    expect(projectQuotaPeriod({ ...current, projectionBasis: null }, now)).toBeNull()
    expect(projectQuotaPeriod({ ...current, usedPercent: 0 }, now)).toBeNull()
    expect(projectQuotaPeriod({ ...current, lastObservedAt: '2026-11-08T11:00:00Z' }, now)).toBeNull()
    expect(projectQuotaPeriod({ ...current, projectionBasis: { ...basis, requestCount: 0 } }, now)).toBeNull()
    expect(projectQuotaPeriod(current, now + 60 * 60_000)?.warnings.join(' ')).toContain('已过时')
    expect(projectQuotaPeriod(current, now - 90_000)?.warnings.join(' ')).toContain('时间存在偏差')
    expect(projectQuotaPeriod(current, now - 180_000)).toBeNull()
    expect(projectQuotaPeriod({ ...current, usedPercent: Number.MIN_VALUE }, now)).toBeNull()
    expect(projectQuotaPeriod({ ...current, projectionBasis: { ...basis, estimatedCostUsd: Number.MAX_VALUE } }, now)?.costUsd).toBeNull()
    expect(projectQuotaPeriod({ ...current, usedPercent: 50 }, now)).toMatchObject({ requests: 20, tokens: 200, costUsd: 4 })
  })
})

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
  it('projects from consumed percentage only when the inputs are usable', () => {
    expect(projectQuotaPeriod(period(), now)).toEqual({ requests: 40, tokens: 400, costUsd: 8 })
    expect(projectQuotaPeriod(period({ unknownCount: 1 }), now)).toBeNull()
    expect(projectQuotaPeriod(period({ boundaryUncertain: true }), now)).toBeNull()
    expect(projectQuotaPeriod(period({ usedPercent: 2 }), now)).toBeNull()
    expect(projectQuotaPeriod(period({ requestCount: 0, totalTokens: 0 }), now)).toBeNull()
    expect(projectQuotaPeriod(period({ lastObservedAt: '2026-11-08T11:00:00Z' }), now)).toBeNull()
    expect(projectQuotaPeriod(period({ lastUsageAt: '2026-11-08T11:59:30Z' }), now)).toBeNull()
    expect(projectQuotaPeriod(period({ unpricedCount: 1 }), now)?.costUsd).toBeNull()
  })
})

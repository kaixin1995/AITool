import type { CodexQuotaPeriod } from '@/api/oauth'

export function previousQuotaPeriod(periods: CodexQuotaPeriod[], current: CodexQuotaPeriod): CodexQuotaPeriod | undefined {
  return periods.filter(p => p.id !== current.id && p.windowId === current.windowId && p.endAt && Math.abs(Date.parse(p.endAt) - Date.parse(current.startAt)) <= 120_000)
    .sort((a, b) => Date.parse(b.endAt!) - Date.parse(a.endAt!))[0]
}

/** 仅在真实额度读数新鲜、周期边界可信且比例足够大时，按已用额度比例粗略推算。 */
export function projectQuotaPeriod(period: CodexQuotaPeriod, now = Date.now()): { requests: number; tokens: number; costUsd: number | null } | null {
  const used = period.usedPercent
  const observed = Date.parse(period.lastObservedAt)
  const lastUsage = period.lastUsageAt ? Date.parse(period.lastUsageAt) : NaN
  if (!period.isCurrent || period.boundaryUncertain || used == null || !Number.isFinite(used) || used < 5 || used > 100 || !Number.isFinite(observed) || now - observed > 15 * 60_000 || observed > now + 60_000 || !Number.isFinite(lastUsage) || lastUsage > observed || period.unknownCount > 0 || period.requestCount === 0 || period.totalTokens === 0) return null
  const multiplier = 100 / used
  return {
    requests: Math.round(period.requestCount * multiplier),
    tokens: Math.round(period.totalTokens * multiplier),
    costUsd: period.estimatedCostUsd == null || period.unpricedCount == null || period.unpricedCount > 0 ? null : period.estimatedCostUsd * multiplier
  }
}

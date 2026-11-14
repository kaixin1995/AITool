import type { CodexQuotaPeriod } from '@/api/oauth'

export function previousQuotaPeriod(periods: CodexQuotaPeriod[], current: CodexQuotaPeriod): CodexQuotaPeriod | undefined {
  if (current.historyNormalized && current.previousPeriodId) {
    return periods.find(p => p.id === current.previousPeriodId && p.windowId === current.windowId && !!p.endAt)
  }
  return periods.filter(p => p.id !== current.id && p.windowId === current.windowId && p.endAt && Math.abs(Date.parse(p.endAt) - Date.parse(current.startAt)) <= 120_000)
    .sort((a, b) => Date.parse(b.endAt!) - Date.parse(a.endAt!))[0]
}

/** 使用与当前额度读数同一时刻的日志，粗略估算额度用尽时的本周期总量。 */
export function projectQuotaPeriod(period: CodexQuotaPeriod, now = Date.now()): { requests: number; tokens: number; costUsd: number | null; warnings: string[] } | null {
  const used = period.usedPercent
  const observed = Date.parse(period.lastObservedAt)
  const basis = period.projectionBasis
  if (!period.isCurrent || !basis || used == null || !Number.isFinite(used) || used <= 0 || used > 100
    || !Number.isFinite(observed) || Date.parse(basis.observedAt) !== observed || observed > now
    || !Number.isFinite(basis.requestCount) || basis.requestCount <= 0
    || !Number.isFinite(basis.totalTokens) || basis.totalTokens <= 0) return null
  const multiplier = 100 / used
  const warnings = ['按额度刷新前记录的调用估算，假设后续用量结构不变']
  if (period.boundaryUncertain) warnings.push('周期边界不确定，估算可能偏差较大')
  if (basis.unknownCount > 0) warnings.push(`${basis.unknownCount} 次用量未知，估算可能偏低`)
  if (basis.unpricedCount == null || basis.unpricedCount > 0) warnings.push('部分用量未定价，金额不可估算')
  if (now - observed > 15 * 60_000) warnings.push('额度读数超过 15 分钟，可能已过时')
  if (used < 5) warnings.push('已用比例较低，估算波动较大')
  return {
    requests: Math.round(basis.requestCount * multiplier),
    tokens: Math.round(basis.totalTokens * multiplier),
    costUsd: basis.estimatedCostUsd == null || basis.unpricedCount == null || basis.unpricedCount > 0 ? null : basis.estimatedCostUsd * multiplier,
    warnings
  }
}

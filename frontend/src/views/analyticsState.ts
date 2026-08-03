interface AnalyticsTokenSummary {
  totalTokens?: number
  totalInputTokens?: number
  totalCachedTokens?: number
  totalOutputTokens?: number
}

export function calculateAnalyticsTotalTokens(summary: AnalyticsTokenSummary | undefined): number {
  return summary?.totalTokens
    ?? (summary?.totalInputTokens ?? 0) + (summary?.totalOutputTokens ?? 0)
}

export function shouldAutoLoadAnalytics(rangeType: string): boolean {
  return rangeType !== 'custom'
}

export function buildAnalyticsDefaultCustomRange(now = new Date()): {
  startTime: number
  endTime: number
} {
  const end = new Date(now)
  end.setHours(23, 59, 0, 0)

  const start = new Date(end)
  start.setDate(end.getDate() - ((7 + end.getDay() - 1) % 7))
  start.setHours(0, 0, 0, 0)

  return {
    startTime: start.getTime(),
    endTime: end.getTime()
  }
}

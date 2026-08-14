// 仅格式化日志列表的显示文本，不修改接口返回的原始模型字段。
// 只保留对外名（路由入口/客户端请求名）；上游站点模型名不在列表展示，避免 `对外名->站点名` 拼接。
export function formatUsageLogModel(routeEntry: string | null | undefined, upstreamModel: string | null | undefined): string {
  const entry = routeEntry?.trim() ?? ''
  const upstream = upstreamModel?.trim() || entry
  if (!entry) return upstream || '-'
  return entry
}

export function buildUsageLogsDefaultCustomRange(now = new Date()): {
  startTime: number
  endTime: number
} {
  const start = new Date(now)
  start.setHours(0, 0, 0, 0)

  return {
    startTime: start.getTime(),
    endTime: now.getTime()
  }
}

export function canAutoLoadUsageLogs(
  rangeType: string,
  startTime: number | null,
  endTime: number | null
): boolean {
  return rangeType !== 'custom'
    || (startTime !== null && endTime !== null)
}

export function buildVisibleUsageLogPages(currentPage: number, totalPages: number): number[] {
  if (totalPages <= 0) return []
  if (totalPages <= 5) {
    return Array.from({ length: totalPages }, (_, index) => index + 1)
  }

  const current = Math.min(Math.max(1, currentPage), totalPages)
  let start = Math.max(1, current - 2)
  const end = Math.min(totalPages, start + 4)
  start = Math.max(1, end - 4)
  return Array.from({ length: end - start + 1 }, (_, index) => start + index)
}

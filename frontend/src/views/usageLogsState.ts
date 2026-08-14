// 仅格式化日志列表的显示文本，不修改接口返回的原始模型字段。
// 路由调用（proxy/检测等）：显示 "路由入口名 -> 对外模型"，如 "chat-prod -> gpt-5.5"；
// chat 页等非路由调用：只显示对外模型（客户端请求的模型名）。
// 上游模型名与入口名相同或缺失时不重复拼接。
export function formatUsageLogModel(
  routeEntry: string | null | undefined,
  upstreamModel: string | null | undefined,
  source?: string | null
): string {
  const entry = routeEntry?.trim() ?? ''
  const upstream = upstreamModel?.trim() ?? ''
  if (!entry && !upstream) return '-'
  if (source === 'chat') return entry || upstream
  if (!entry) return upstream
  if (!upstream || upstream === entry) return entry
  return entry + ' -> ' + upstream
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

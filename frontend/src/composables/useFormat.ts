// 数字紧凑格式化：>999 显示 1.2K / 3.4M / 1.1B，用于统计卡片、表格 token 列、图表 Y 轴。
export function formatCompact(n: number | null | undefined): string {
  if (n == null || isNaN(n)) return '0'
  const abs = Math.abs(n)
  if (abs < 1000) return String(n)
  if (abs < 1_000_000) return (n / 1000).toFixed(1).replace(/\.0$/, '') + 'K'
  if (abs < 1_000_000_000) return (n / 1_000_000).toFixed(1).replace(/\.0$/, '') + 'M'
  return (n / 1_000_000_000).toFixed(1).replace(/\.0$/, '') + 'B'
}

// 毫秒 → 人类可读（如 1.2s / 350ms）
export function formatDuration(ms: number | null | undefined): string {
  if (ms == null) return '-'
  if (ms < 1000) return `${Math.round(ms)}ms`
  return `${(ms / 1000).toFixed(1)}s`
}

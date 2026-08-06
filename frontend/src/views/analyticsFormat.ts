// 分析页沿用旧统计看板的数值精度与单位，避免影响其他页面的紧凑展示。
export function formatCompact(value: number | null | undefined): string {
  if (value == null || Number.isNaN(value)) return '0'

  const units = [
    { threshold: 1_000_000_000_000_000_000, suffix: 'E' },
    { threshold: 1_000_000_000_000_000, suffix: 'P' },
    { threshold: 1_000_000_000_000, suffix: 'T' },
    { threshold: 1_000_000_000, suffix: 'B' },
    { threshold: 1_000_000, suffix: 'M' },
    { threshold: 1_000, suffix: 'K' }
  ]
  const unit = units.find(({ threshold }) => Math.abs(value) >= threshold)

  return unit ? `${(value / unit.threshold).toFixed(2)}${unit.suffix}` : String(value)
}

// 耗时按毫秒、秒、分钟、小时分级显示，保持图表轴和提示层一致。
export function formatDuration(value: number | null | undefined): string {
  if (value == null || Number.isNaN(value)) return '-'
  if (value < 1_000) return `${Math.round(value)}ms`
  if (value < 60_000) return `${(value / 1_000).toFixed(2)}s`
  if (value < 3_600_000) return `${(value / 60_000).toFixed(2)} min`
  return `${(value / 3_600_000).toFixed(2)} h`
}

// 百分比统一两位精度，防止 KPI 与图表提示层显示不一致。
export function formatPercentage(value: number | null | undefined): string {
  return `${(value ?? 0).toFixed(2)}%`
}

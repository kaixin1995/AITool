import type { DetectionModelGroup, ProbeResultItem } from '@/api/detection'

export function applyDetectionProbeResult(
  groups: DetectionModelGroup[],
  result: ProbeResultItem,
  checkedAt = new Date().toISOString()
): boolean {
  for (const group of groups) {
    const site = group.sites.find(item => item.mappingId === result.mappingId)
    if (!site) continue

    site.lastStatus = result.status
    site.lastCheckedAt = checkedAt
    site.lastDurationMs = result.durationMs
    return true
  }
  return false
}

export function formatDetectionDateTime(value: string | null): string {
  if (!value) return '-'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return '-'

  const pad = (part: number) => String(part).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
}

import type { SelectOption } from 'naive-ui'

export type UsageSourceTagType = 'default' | 'success' | 'info' | 'warning'

// Usage Logs 现有的来源值、中文标签和筛选顺序由此处统一维护。
export const usageSourceOptions: SelectOption[] = [
  { label: '全部', value: '' },
  { label: '代理', value: 'proxy' },
  { label: '对话测试', value: 'chat' },
  { label: 'Claude Code', value: 'claude-code' },
  { label: 'Codex', value: 'codex' },
  { label: 'Open Code', value: 'open-code' },
  { label: 'ZCode', value: 'zcode' },
  { label: '手动检测', value: 'detection-manual' },
  { label: '定时检测', value: 'detection-task' }
]

const usageSourceLabels: Record<string, string> = {
  proxy: '代理',
  chat: '对话测试',
  'claude-code': 'Claude Code',
  codex: 'Codex',
  'open-code': 'Open Code',
  zcode: 'ZCode',
  'detection-manual': '手动检测',
  'detection-task': '定时检测'
}

export function getUsageSourceMeta(source: string | null | undefined): {
  label: string
  type: UsageSourceTagType
} {
  const value = source?.trim() ?? ''
  const normalized = value.toLowerCase()
  const type = normalized === 'chat'
    ? 'info'
    : normalized.startsWith('detection')
      ? 'warning'
      : normalized === 'proxy'
        ? 'default'
        : 'success'

  return {
    // 未知来源保留原始值；空值才使用占位符，避免误显示为代理。
    label: (usageSourceLabels[normalized] ?? value) || '-',
    type
  }
}

export function getUsageSourceLabel(source: string | null | undefined): string {
  return getUsageSourceMeta(source).label
}

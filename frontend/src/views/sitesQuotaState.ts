import type { SiteQuotaKey } from '@/api/siteQuota'

/**
 * 站点额度查询页（SitesQuotaTab）的纯展示逻辑，便于单测。
 * 交互状态（加载/刷新编排）在组件内维护。
 */

/** 剩余百分比（0-100，两端裁剪）：已用 -5% 或 150% 这类上游异常值按边界处理。 */
export function remainingPercent(usedPercent: number | null | undefined): number {
  const used = Number(usedPercent ?? 0)
  if (!Number.isFinite(used)) return 100
  return Math.max(0, Math.min(100, Math.round(100 - used)))
}

/**
 * 额度进度条颜色（按剩余量），阈值与 OAuthView.quotaColor 完全一致：
 * 剩余 <20 红、<50 橙、其余绿。
 */
export function quotaBarColor(usedPercent: number | null | undefined): 'success' | 'warning' | 'error' {
  const remaining = remainingPercent(usedPercent)
  if (remaining < 20) return 'error'
  if (remaining < 50) return 'warning'
  return 'success'
}

/** 重置倒计时（相对时间）：无重置时间为「—」，已到期为「即将重置」。 */
export function formatResetCountdown(resetAtUtc: string | null | undefined, now: number = Date.now()): string {
  if (!resetAtUtc) return '—'
  const resetAt = Date.parse(resetAtUtc)
  if (!Number.isFinite(resetAt)) return '—'

  const diffMs = resetAt - now
  const totalMinutes = Math.floor(diffMs / 60_000)
  if (totalMinutes < 1) return '即将重置'

  const days = Math.floor(totalMinutes / 1440)
  const hours = Math.floor((totalMinutes % 1440) / 60)
  const minutes = totalMinutes % 60
  if (days >= 1) return `${days} 天 ${hours} 小时后`
  if (hours >= 1) return `${hours} 小时 ${minutes} 分后`
  return `${minutes} 分后`
}

/** 上次查询的相对时间：null（从未查询）为空串。 */
export function formatCheckedAtAgo(checkedAtUtc: string | null | undefined, now: number = Date.now()): string {
  if (!checkedAtUtc) return ''
  const checkedAt = Date.parse(checkedAtUtc)
  if (!Number.isFinite(checkedAt)) return ''

  const diffMs = now - checkedAt
  if (diffMs < 60_000) return '刚刚'
  const totalMinutes = Math.floor(diffMs / 60_000)
  if (totalMinutes < 60) return `${totalMinutes} 分钟前`
  const hours = Math.floor(totalMinutes / 60)
  if (hours < 24) return `${hours} 小时前`
  return `${Math.floor(hours / 24)} 天前`
}

export interface QuotaStatusChip {
  text: string
  type: 'success' | 'error' | 'default'
}

/** 查询状态徽章（仅反映额度查询结果；密钥启停单独展示）。 */
export function quotaStatusChip(status: SiteQuotaKey['status']): QuotaStatusChip {
  switch (status) {
    case 'ok':
      return { text: '正常', type: 'success' }
    case 'invalid_credential':
      return { text: '密钥失效', type: 'error' }
    case 'error':
      return { text: '查询失败', type: 'error' }
    default:
      return { text: '未查询', type: 'default' }
  }
}

/** 窗口是否应置灰：上次查询失败（密钥失效/其他错误）时展示的是旧缓存值。 */
export function isStaleQuota(status: SiteQuotaKey['status']): boolean {
  return status === 'invalid_credential' || status === 'error'
}

// 常见币种符号；未收录的币种直接展示代码（如 "AUD "）。
const CURRENCY_SYMBOLS: Record<string, string> = {
  CNY: '¥',
  USD: '$',
  EUR: '€',
  GBP: '£',
  JPY: '¥'
}

export function currencySymbol(currency: string): string {
  return CURRENCY_SYMBOLS[currency] ?? `${currency} `
}

/** 余额金额展示：币种符号 + 固定两位小数（后端 decimal 序列化可能丢尾零）。 */
export function formatBalance(currency: string, amount: number | null | undefined): string {
  const value = Number(amount ?? 0)
  return `${currencySymbol(currency)}${(Number.isFinite(value) ? value : 0).toFixed(2)}`
}

/** 余额明细行（赠送/充值）：两者都无时返回 null（不渲染明细行）。 */
export function formatBalanceDetail(
  balance: Pick<SiteQuotaKey['balances'][number], 'currency' | 'grantedBalance' | 'toppedUpBalance'>
): string | null {
  const parts: string[] = []
  if (balance.grantedBalance != null) parts.push(`赠送 ${formatBalance(balance.currency, balance.grantedBalance)}`)
  if (balance.toppedUpBalance != null) parts.push(`充值 ${formatBalance(balance.currency, balance.toppedUpBalance)}`)
  return parts.length > 0 ? parts.join(' · ') : null
}

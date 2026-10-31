import type {
  RouteEntry,
  RouteReferenceTarget,
  RouteRuleItem,
  SaveRuleItem,
  SiteInstanceItem
} from '@/api/routes'

export function normalizeSearchText(value: string): string {
  return value.trim().toLowerCase()
}

export function filterSiteInstances<T extends SiteInstanceItem>(instances: T[], searchText: string): T[] {
  const search = normalizeSearchText(searchText)
  if (!search) return instances

  return instances.filter((instance) =>
    normalizeSearchText(instance.siteName).includes(search)
    || normalizeSearchText(instance.siteModelName).includes(search)
  )
}

export function chooseSelectedEntryAfterReload(
  entries: RouteEntry[],
  current: string | null,
  preferred?: string | null
): string | null {
  if (preferred && entries.some((entry) => entry.entryName === preferred)) return preferred
  if (current && entries.some((entry) => entry.entryName === current)) return current
  return entries[0]?.entryName ?? null
}

export function appendCandidate(
  rules: RouteRuleItem[],
  instance: SiteInstanceItem
): RouteRuleItem[] {
  return [...rules, {
    ruleId: '',
    targetKind: 'Site',
    targetRouteEntryId: null,
    targetRouteEntryName: null,
    siteId: instance.siteId,
    siteName: instance.siteName,
    siteEnabled: instance.siteEnabled,
    upstreamModelName: instance.siteModelName,
    siteModelName: instance.siteModelName,
    priority: rules.length,
    modelPriority: 0,
    instancePriority: 0,
    isEnabled: true,
    availabilityMode: 'AllDay',
    timeRangesJson: ''
  }]
}

export function isRouteReference(rule: RouteRuleItem): boolean {
  return rule.targetKind === 'RouteEntry'
}

export function getReferenceUnavailableReason(rule: RouteRuleItem): string | null {
  if (!isRouteReference(rule)) return null
  switch (rule.referenceStatus?.toLowerCase()) {
    case 'valid':
    case 'available': return null
    case 'missing': return '目标入口已删除或不存在'
    case 'nested': return '目标入口含有路由引用，不支持嵌套引用'
    case 'empty': return '目标入口没有可用候选'
    case 'invalid': return '路由引用配置无效'
    default: return rule.referenceStatus || '目标入口状态未知，请刷新确认'
  }
}

export function appendReferenceCandidate(
  rules: RouteRuleItem[],
  target: RouteReferenceTarget
): RouteRuleItem[] {
  if (!target.canSelect) return rules
  return [...rules, {
    ruleId: '',
    targetKind: 'RouteEntry',
    targetRouteEntryId: target.entryId,
    targetRouteEntryName: target.entryName,
    referenceStatus: 'available',
    // UI placeholders only; site fields are never sent for references.
    siteId: '',
    siteName: '',
    siteEnabled: false,
    siteModelName: '',
    upstreamModelName: '',
    priority: rules.length,
    modelPriority: 0,
    instancePriority: 0,
    isEnabled: true,
    availabilityMode: 'AllDay',
    timeRangesJson: ''
  }]
}

export function createRuleKeyResolver(): (rule: RouteRuleItem) => string {
  const draftKeys = new WeakMap<RouteRuleItem, string>()
  let nextDraftKey = 0

  return (rule: RouteRuleItem): string => {
    if (rule.ruleId) return rule.ruleId

    const existingKey = draftKeys.get(rule)
    if (existingKey) return existingKey

    const key = `draft-${nextDraftKey++}`
    draftKeys.set(rule, key)
    return key
  }
}

export function getDeleteEntryConfirmation(dirty: boolean): string {
  return dirty
    ? '删除主入口会同时删除其全部候选规则，并放弃当前未保存的候选队列修改，确定继续？'
    : '删除主入口会同时删除其全部候选规则，确定继续？'
}

export function moveCandidate(
  rules: RouteRuleItem[],
  index: number,
  direction: -1 | 1
): RouteRuleItem[] {
  const target = index + direction
  if (index < 0 || index >= rules.length || target < 0 || target >= rules.length) return rules

  const moved = [...rules]
  ;[moved[index], moved[target]] = [moved[target], moved[index]]
  return moved
}

export function buildSaveRules(rules: RouteRuleItem[]): SaveRuleItem[] {
  return rules.map((rule): SaveRuleItem => {
    const availability = {
      isEnabled: rule.isEnabled,
      availabilityMode: rule.availabilityMode,
      timeRangesJson: rule.timeRangesJson
    }
    return isRouteReference(rule) ? {
      ...availability,
      targetKind: 'RouteEntry',
      targetRouteEntryId: rule.targetRouteEntryId ?? null,
      targetRouteEntryName: rule.targetRouteEntryName ?? null
    } : {
      ...availability,
      targetKind: 'Site',
      targetRouteEntryId: null,
      targetRouteEntryName: null,
      siteId: rule.siteId,
      siteModelName: rule.siteModelName,
      upstreamModelName: rule.upstreamModelName
    }
  })
}

export function isLatestRouteLoad(token: number, latestToken: number): boolean {
  return token === latestToken
}

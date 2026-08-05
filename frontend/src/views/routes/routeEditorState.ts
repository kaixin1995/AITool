import type {
  RouteEntry,
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
  return rules.map((rule) => ({
    siteId: rule.siteId,
    siteModelName: rule.siteModelName,
    upstreamModelName: rule.upstreamModelName,
    isEnabled: rule.isEnabled,
    availabilityMode: rule.availabilityMode,
    timeRangesJson: rule.timeRangesJson
  }))
}

export function isLatestRouteLoad(token: number, latestToken: number): boolean {
  return token === latestToken
}

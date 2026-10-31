import { describe, expect, it } from 'vitest'
import type {
  RouteEntry,
  RouteReferenceTarget,
  RouteRuleItem,
  SiteInstanceItem
} from '@/api/routes'
import {
  appendCandidate,
  appendReferenceCandidate,
  getReferenceUnavailableReason,
  isRouteReference,
  buildSaveRules,
  chooseSelectedEntryAfterReload,
  filterSiteInstances,
  isLatestRouteLoad,
  createRuleKeyResolver,
  getDeleteEntryConfirmation,
  moveCandidate,
  normalizeSearchText
} from './routeEditorState'

const entries: RouteEntry[] = [
  { entryName: 'public-model', candidateCount: 2 },
  { entryName: 'backup-model', candidateCount: 1 }
]

const instances: SiteInstanceItem[] = [
  {
    siteId: 'site-1',
    siteName: 'OpenAI Primary',
    siteModelName: 'GPT-4o',
    protocolType: 'OpenAI',
    siteEnabled: true
  },
  {
    siteId: 'site-2',
    siteName: 'Claude Backup',
    siteModelName: 'Sonnet-4',
    protocolType: 'Anthropic',
    siteEnabled: false
  }
]

function makeRule(overrides: Partial<RouteRuleItem> = {}): RouteRuleItem {
  return {
    ruleId: 'rule-1',
    siteId: 'site-1',
    siteName: 'Primary',
    siteEnabled: true,
    upstreamModelName: 'upstream-model',
    siteModelName: 'site-model',
    priority: 0,
    modelPriority: 1,
    instancePriority: 2,
    isEnabled: true,
    availabilityMode: 'AvailableOnly',
    timeRangesJson: '[{"start":"09:00","end":"18:00"}]',
    ...overrides
  }
}

describe('route editor state helpers', () => {
  it('normalizes search text by trimming and lowercasing it', () => {
    expect(normalizeSearchText('  GPT-4O  ')).toBe('gpt-4o')
  })

  it('filters site instances by site name or model name without case sensitivity', () => {
    expect(filterSiteInstances(instances, ' openai ')).toEqual([instances[0]])
    expect(filterSiteInstances(instances, 'SONNET')).toEqual([instances[1]])
  })

  it('returns every site instance for a blank search', () => {
    expect(filterSiteInstances(instances, '   ')).toEqual(instances)
  })

  it('chooses preferred, then current, then first entry after reload', () => {
    expect(chooseSelectedEntryAfterReload(entries, 'public-model', 'backup-model')).toBe('backup-model')
    expect(chooseSelectedEntryAfterReload(entries, 'public-model', 'missing')).toBe('public-model')
    expect(chooseSelectedEntryAfterReload(entries, 'missing', 'also-missing')).toBe('public-model')
    expect(chooseSelectedEntryAfterReload([], 'public-model', 'backup-model')).toBeNull()
  })

  it('appends an enabled all-day candidate using the instance model name as upstream', () => {
    const original = [makeRule()]
    const result = appendCandidate(original, instances[1])

    expect(result).toHaveLength(2)
    expect(result[1]).toEqual({
      ruleId: '',
      targetKind: 'Site',
      targetRouteEntryId: null,
      targetRouteEntryName: null,
      siteId: 'site-2',
      siteName: 'Claude Backup',
      siteEnabled: false,
      upstreamModelName: 'Sonnet-4',
      siteModelName: 'Sonnet-4',
      priority: 1,
      modelPriority: 0,
      instancePriority: 0,
      isEnabled: true,
      availabilityMode: 'AllDay',
      timeRangesJson: ''
    })
    expect(original).toHaveLength(1)
  })

  it('allows appending the same site instance more than once', () => {
    const once = appendCandidate([], instances[0])
    const twice = appendCandidate(once, instances[0])

    expect(twice).toHaveLength(2)
    expect(twice[0].siteId).toBe(twice[1].siteId)
    expect(twice[0].siteModelName).toBe(twice[1].siteModelName)
  })

  it('moves a candidate without mutating the input array', () => {
    const input = [makeRule({ ruleId: 'a' }), makeRule({ ruleId: 'b' }), makeRule({ ruleId: 'c' })]
    const result = moveCandidate(input, 1, -1)

    expect(result.map((rule) => rule.ruleId)).toEqual(['b', 'a', 'c'])
    expect(input.map((rule) => rule.ruleId)).toEqual(['a', 'b', 'c'])
    expect(result).not.toBe(input)
  })

  it('leaves candidates unchanged for out-of-bounds moves', () => {
    const input = [makeRule({ ruleId: 'a' }), makeRule({ ruleId: 'b' })]

    expect(moveCandidate(input, 0, -1)).toEqual(input)
    expect(moveCandidate(input, 1, 1)).toEqual(input)
  })

  it('keeps duplicate draft rule keys stable when candidates move', () => {
    const resolveRuleKey = createRuleKeyResolver()
    const first = makeRule({ ruleId: '', siteId: 'site-1' })
    const second = makeRule({ ruleId: '', siteId: 'site-1' })
    const firstKey = resolveRuleKey(first)
    const secondKey = resolveRuleKey(second)

    expect(firstKey).not.toBe(secondKey)
    expect(resolveRuleKey(second)).toBe(secondKey)
    expect(resolveRuleKey(first)).toBe(firstKey)
  })

  it('explains that deleting a dirty entry also discards its draft', () => {
    expect(getDeleteEntryConfirmation(false)).toBe(
      '删除主入口会同时删除其全部候选规则，确定继续？'
    )
    expect(getDeleteEntryConfirmation(true)).toBe(
      '删除主入口会同时删除其全部候选规则，并放弃当前未保存的候选队列修改，确定继续？'
    )
  })

  it('builds save rules with only fields required by the save contract', () => {
    expect(buildSaveRules([makeRule()])).toEqual([{
      targetKind: 'Site',
      targetRouteEntryId: null,
      targetRouteEntryName: null,
      siteId: 'site-1',
      siteModelName: 'site-model',
      upstreamModelName: 'upstream-model',
      isEnabled: true,
      availabilityMode: 'AvailableOnly',
      timeRangesJson: '[{"start":"09:00","end":"18:00"}]'
    }])
  })

  const referenceTarget: RouteReferenceTarget = {
    entryId: 'target-id', entryName: 'pool/model', managedByPoolId: 'pool-id',
    candidateCount: 2, canSelect: true, reason: null
  }

  it('appends a reference as one enabled all-day group without expanding its children', () => {
    const original = [makeRule()]
    const result = appendReferenceCandidate(original, referenceTarget)
    expect(original).toHaveLength(1)
    expect(result).toHaveLength(2)
    expect(result[1]).toMatchObject({
      targetKind: 'RouteEntry', targetRouteEntryId: 'target-id', targetRouteEntryName: 'pool/model',
      priority: 1, isEnabled: true, availabilityMode: 'AllDay', timeRangesJson: ''
    })
    expect(buildSaveRules(result)[1]).toEqual({
      targetKind: 'RouteEntry', targetRouteEntryId: 'target-id', targetRouteEntryName: 'pool/model',
      isEnabled: true, availabilityMode: 'AllDay', timeRangesJson: ''
    })
  })

  it('does not append a target rejected by the reference picker', () => {
    const original = [makeRule()]
    expect(appendReferenceCandidate(original, { ...referenceTarget, canSelect: false, reason: 'nested' })).toBe(original)
  })

  it('keeps mixed group order and group availability in save payloads without site sentinels', () => {
    const rules = appendReferenceCandidate([makeRule({ ruleId: 'site' })], referenceTarget)
    rules[1].isEnabled = false
    rules[1].availabilityMode = 'Unavailable'
    rules[1].timeRangesJson = '[{"start":"10:00","end":"11:00"}]'
    const moved = moveCandidate(rules, 1, -1)
    const payload = buildSaveRules(moved)
    expect(payload.map((rule) => rule.targetKind)).toEqual(['RouteEntry', 'Site'])
    expect(payload[0]).toMatchObject({ isEnabled: false, availabilityMode: 'Unavailable', timeRangesJson: rules[1].timeRangesJson })
    expect(payload[0]).not.toHaveProperty('siteId')
    expect(payload[0]).not.toHaveProperty('siteModelName')
    expect(payload[0]).not.toHaveProperty('upstreamModelName')
    expect(payload[0]).not.toHaveProperty('referenceStatus')
  })

  it('preserves a missing reference id/name rather than converting it to a site', () => {
    const rule = makeRule({ targetKind: 'RouteEntry', targetRouteEntryId: 'deleted-id', targetRouteEntryName: 'deleted', referenceStatus: 'Missing' })
    expect(buildSaveRules([rule])[0]).toMatchObject({ targetKind: 'RouteEntry', targetRouteEntryId: 'deleted-id', targetRouteEntryName: 'deleted' })
    expect(getReferenceUnavailableReason(rule)).toContain('不存在')
    expect(buildSaveRules([makeRule({ targetKind: 'RouteEntry' })])[0]).toMatchObject({ targetRouteEntryId: null, targetRouteEntryName: null })
  })

  it('treats legacy rules without targetKind as site targets', () => {
    const legacy = makeRule()
    expect(isRouteReference(legacy)).toBe(false)
    expect(getReferenceUnavailableReason(legacy)).toBeNull()
    expect(buildSaveRules([legacy])[0]).toMatchObject({ targetKind: 'Site', siteId: legacy.siteId, targetRouteEntryId: null })
  })

  it.each([
    ['Valid', null], ['AVAILABLE', null], ['Missing', '目标入口已删除或不存在'],
    ['Nested', '目标入口含有路由引用，不支持嵌套引用'], ['empty', '目标入口没有可用候选'],
    ['Invalid', '路由引用配置无效']
  ])('explains reference status %s', (referenceStatus, reason) => {
    expect(getReferenceUnavailableReason(makeRule({ targetKind: 'RouteEntry', referenceStatus }))).toBe(reason)
  })

  it('accepts only the latest route load token', () => {
    expect(isLatestRouteLoad(3, 3)).toBe(true)
    expect(isLatestRouteLoad(2, 3)).toBe(false)
  })
})

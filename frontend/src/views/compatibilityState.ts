export type CompatibilityRuleOperation = 'strip' | 'rename' | 'default' | 'keep_reasoning'
export type CompatibilityRuleScope = 'all' | 'passthrough' | 'bridge'

export interface CompatibilityRuleForm {
  op: CompatibilityRuleOperation
  target?: string
  from?: string
  to?: string
  key?: string
  value?: string
  scope: CompatibilityRuleScope
}

export function parseCompatibilityRules(rulesJson: string): CompatibilityRuleForm[] {
  try {
    const raw = JSON.parse(rulesJson || '[]')
    if (!Array.isArray(raw)) return []
    return raw.map((item): CompatibilityRuleForm => ({
      op: item?.op === 'rename' || item?.op === 'default' || item?.op === 'keep_reasoning' ? item.op : 'strip',
      target: item?.target ?? '',
      from: item?.from ?? '',
      to: item?.to ?? '',
      key: item?.key ?? '',
      value: item?.value ?? '',
      scope: item?.scope === 'passthrough' || item?.scope === 'bridge' ? item.scope : 'all'
    }))
  } catch {
    return []
  }
}

export function serializeCompatibilityRules(rules: CompatibilityRuleForm[]): string {
  return JSON.stringify(rules.map((rule) => {
    if (rule.op === 'strip') {
      return { op: rule.op, target: rule.target ?? '', scope: rule.scope }
    }
    if (rule.op === 'rename') {
      return { op: rule.op, from: rule.from ?? '', to: rule.to ?? '', scope: rule.scope }
    }
    // keep_reasoning 无额外参数，只保留 op 和 scope
    if (rule.op === 'keep_reasoning') {
      return { op: rule.op, scope: rule.scope }
    }
    return { op: rule.op, key: rule.key ?? '', value: rule.value ?? '', scope: rule.scope }
  }))
}

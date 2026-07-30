import { describe, expect, it } from 'vitest'
import { parseCompatibilityRules, serializeCompatibilityRules } from './compatibilityState'

describe('Compatibility 页面状态', () => {
  it('只接受规则数组 JSON', () => {
    expect(parseCompatibilityRules('{"op":"strip"}')).toEqual([])
    expect(parseCompatibilityRules('invalid')).toEqual([])
    expect(parseCompatibilityRules('[{"op":"strip","target":"metadata"}]')).toEqual([
      expect.objectContaining({ op: 'strip', target: 'metadata', scope: 'all' })
    ])
  })

  it('序列化结构化规则并保留操作字段', () => {
    expect(JSON.parse(serializeCompatibilityRules([{
      op: 'rename',
      from: 'old',
      to: 'new',
      scope: 'bridge'
    }]))).toEqual([{ op: 'rename', from: 'old', to: 'new', scope: 'bridge' }])
  })
})

import { describe, expect, it } from 'vitest'
import { countJsonDiffs, diffJson } from './jsonDiff'

describe('jsonDiff 字段级对比', () => {
  it('对象键的新增/移除/修改', () => {
    const nodes = diffJson(
      { model: 'a', keep: 1, gone: true },
      { model: 'b', keep: 1, fresh: 'x' }
    )
    expect(nodes).toContainEqual({ kind: 'changed', key: 'model', before: 'a', after: 'b' })
    expect(nodes).toContainEqual({ kind: 'same', key: 'keep', value: 1 })
    expect(nodes).toContainEqual({ kind: 'removed', key: 'gone', value: true })
    expect(nodes).toContainEqual({ kind: 'added', key: 'fresh', value: 'x' })
  })

  it('嵌套对象递归为 object 节点', () => {
    const nodes = diffJson(
      { a: { x: 1, y: 2 } },
      { a: { x: 1, y: 3 } }
    )
    expect(nodes).toHaveLength(1)
    expect(nodes[0]?.kind).toBe('object')
    if (nodes[0]?.kind === 'object') {
      expect(nodes[0].children).toContainEqual({ kind: 'same', key: 'x', value: 1 })
      expect(nodes[0].children).toContainEqual({ kind: 'changed', key: 'y', before: 2, after: 3 })
    }
  })

  it('数组按索引对比，长度差标记 added/removed', () => {
    const nodes = diffJson([1, 2], [1, 3, 4])
    expect(nodes[0]).toEqual({ kind: 'same', key: '0', value: 1 })
    expect(nodes[1]).toEqual({ kind: 'changed', key: '1', before: 2, after: 3 })
    expect(nodes[2]).toEqual({ kind: 'added', key: '2', value: 4 })
  })

  it('无差异时返回空列表', () => {
    expect(diffJson({ a: 1, b: [1, 2] }, { a: 1, b: [1, 2] })).toHaveLength(0)
  })

  it('差异计数', () => {
    const nodes = diffJson({ a: 1, b: 2 }, { a: 3, c: 4 })
    expect(countJsonDiffs(nodes)).toEqual({ added: 1, removed: 1, changed: 1 })
  })
})

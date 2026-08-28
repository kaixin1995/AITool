// JSON 字段级差异对比（纯函数，供协议诊断台“转换前后字段对比”使用）。

export type JsonDiffNode =
  | { kind: 'same'; key: string; value: unknown }
  | { kind: 'added'; key: string; value: unknown }
  | { kind: 'removed'; key: string; value: unknown }
  | { kind: 'changed'; key: string; before: unknown; after: unknown }
  | { kind: 'object'; key: string; children: JsonDiffNode[] }
  | { kind: 'array'; key: string; items: JsonDiffNode[] }

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isScalar(value: unknown): boolean {
  return value === null || typeof value !== 'object'
}

/**
 * 递归对比两个 JSON 值，返回按层级组织的差异树。
 * 顶层为对象时返回键列表；顶层为标量/数组时返回单元素列表。
 */
export function diffJson(a: unknown, b: unknown, key = '(root)'): JsonDiffNode[] {
  if (isPlainObject(a) && isPlainObject(b)) {
    const keys = new Set([...Object.keys(a), ...Object.keys(b)])
    const children: JsonDiffNode[] = []
    for (const k of keys) {
      const hasA = Object.prototype.hasOwnProperty.call(a, k)
      const hasB = Object.prototype.hasOwnProperty.call(b, k)
      if (hasA && !hasB) {
        children.push({ kind: 'removed', key: k, value: a[k] })
      } else if (!hasA && hasB) {
        children.push({ kind: 'added', key: k, value: b[k] })
      } else if (isPlainObject(a[k]) && isPlainObject(b[k])) {
        const sub = diffJson(a[k], b[k], k)
        if (sub.length === 0) {
          children.push({ kind: 'same', key: k, value: a[k] })
        } else {
          children.push({ kind: 'object', key: k, children: sub })
        }
      } else if (Array.isArray(a[k]) && Array.isArray(b[k])) {
        const sub = diffJson(a[k], b[k], k)
        children.push(sub.length === 0
          ? { kind: 'same', key: k, value: a[k] }
          : { kind: 'array', key: k, items: sub })
      } else if (isScalar(a[k]) && isScalar(b[k]) && a[k] === b[k]) {
        children.push({ kind: 'same', key: k, value: a[k] })
      } else {
        children.push({ kind: 'changed', key: k, before: a[k], after: b[k] })
      }
    }
    // 对象完全相同时视为无差异（供上层压缩为 same）。
    if (children.every(node => node.kind === 'same')) {
      return []
    }
    return children
  }

  if (Array.isArray(a) && Array.isArray(b)) {
    const length = Math.max(a.length, b.length)
    const items: JsonDiffNode[] = []
    for (let i = 0; i < length; i++) {
      if (i >= a.length) {
        items.push({ kind: 'added', key: String(i), value: b[i] })
      } else if (i >= b.length) {
        items.push({ kind: 'removed', key: String(i), value: a[i] })
      } else if (isPlainObject(a[i]) && isPlainObject(b[i])) {
        const sub = diffJson(a[i], b[i], String(i))
        items.push(sub.length === 0
          ? { kind: 'same', key: String(i), value: a[i] }
          : { kind: 'object', key: String(i), children: sub })
      } else if (Array.isArray(a[i]) && Array.isArray(b[i])) {
        const sub = diffJson(a[i], b[i], String(i))
        items.push(sub.length === 0
          ? { kind: 'same', key: String(i), value: a[i] }
          : { kind: 'array', key: String(i), items: sub })
      } else if (isScalar(a[i]) && isScalar(b[i]) && a[i] === b[i]) {
        items.push({ kind: 'same', key: String(i), value: a[i] })
      } else {
        items.push({ kind: 'changed', key: String(i), before: a[i], after: b[i] })
      }
    }
    // 数组完全相同时视为无差异（供上层压缩为 same）。
    if (items.every(node => node.kind === 'same')) {
      return []
    }
    return items
  }

  // 顶层标量或类型不同的值
  if (isScalar(a) && isScalar(b) && a === b) {
    return [{ kind: 'same', key, value: a }]
  }
  return [{ kind: 'changed', key, before: a, after: b }]
}

/** 统计差异数量（不含 same），用于界面摘要。 */
export function countJsonDiffs(nodes: JsonDiffNode[]): { added: number; removed: number; changed: number } {
  let added = 0
  let removed = 0
  let changed = 0
  const walk = (list: JsonDiffNode[]): void => {
    for (const node of list) {
      if (node.kind === 'added') added += 1
      else if (node.kind === 'removed') removed += 1
      else if (node.kind === 'changed') changed += 1
      else if (node.kind === 'object') walk(node.children)
      else if (node.kind === 'array') walk(node.items)
    }
  }
  walk(nodes)
  return { added, removed, changed }
}

<script setup lang="ts">
import { computed, ref } from 'vue'
import { NButton } from 'naive-ui'
import {
  countJsonDiffs,
  diffJson,
  type JsonDiffNode
} from '@/utils/jsonDiff'

const props = defineProps<{
  before: string
  after: string
}>()

// 扁平化 diff 树：DFS 生成带缩进深度的行，避免递归组件在大 JSON 上的组件实例爆炸。
interface DiffRow {
  depth: number
  node: JsonDiffNode
}

function flatten(nodes: JsonDiffNode[], depth = 0, out: DiffRow[] = []): DiffRow[] {
  for (const node of nodes) {
    out.push({ depth, node })
    if (node.kind === 'object') flatten(node.children, depth + 1, out)
    else if (node.kind === 'array') flatten(node.items, depth + 1, out)
  }
  return out
}

const MAX_RENDER_ROWS = 800

interface ParsedPair {
  ok: boolean
  rows: DiffRow[]
  added: number
  removed: number
  changed: number
}

const parsed = computed<ParsedPair>(() => {
  try {
    const beforeValue = JSON.parse(props.before)
    const afterValue = JSON.parse(props.after)
    const nodes = diffJson(beforeValue, afterValue)
    const counts = countJsonDiffs(nodes)
    return { ok: true, rows: flatten(nodes), ...counts }
  } catch {
    return { ok: false, rows: [], added: 0, removed: 0, changed: 0 }
  }
})

const hasAnyDiff = computed(() =>
  parsed.value.added + parsed.value.removed + parsed.value.changed > 0
)

// 默认只显示差异行（same 行会淹没数千行无变化内容）；可展开显示全部。
const showAllRows = ref(false)

const visibleRows = computed(() => {
  const source = showAllRows.value ? parsed.value.rows : parsed.value.rows.filter(row => row.node.kind !== 'same')
  return source.slice(0, MAX_RENDER_ROWS)
})

const hiddenRowCount = computed(() => {
  const total = showAllRows.value ? parsed.value.rows.length : parsed.value.rows.filter(row => row.node.kind !== 'same').length
  return Math.max(0, total - MAX_RENDER_ROWS)
})

function renderValue(value: unknown): string {
  if (typeof value === 'string') return JSON.stringify(value)
  if (value === null) return 'null'
  if (Array.isArray(value)) return '[…]'
  if (typeof value === 'object') return '{…}'
  return String(value)
}
</script>

<template>
  <div v-if="parsed.ok && hasAnyDiff" class="json-diff-view">
    <div class="json-diff-summary">
      <span class="json-diff-count json-diff-added">+{{ parsed.added }} 新增</span>
      <span class="json-diff-count json-diff-removed">-{{ parsed.removed }} 移除</span>
      <span class="json-diff-count json-diff-changed">~{{ parsed.changed }} 修改</span>
      <NButton v-if="!showAllRows" size="tiny" quaternary @click="showAllRows = true">显示无变化字段</NButton>
      <NButton v-else size="tiny" quaternary @click="showAllRows = false">只显示差异</NButton>
      <span class="json-diff-note">（嵌套差异已展开到最深层）</span>
    </div>
    <div class="json-diff-tree">
      <template v-for="(row, index) in visibleRows" :key="index">
        <div
          v-if="row.node.kind === 'same'"
          class="json-diff-line"
          :style="{ paddingLeft: (row.depth * 16 + 8) + 'px' }"
        >
          <span class="json-diff-key">{{ row.node.key }}</span>
          <span class="json-diff-value">{{ renderValue(row.node.value) }}</span>
        </div>
        <div
          v-else-if="row.node.kind === 'added'"
          class="json-diff-line json-diff-added"
          :style="{ paddingLeft: (row.depth * 16 + 8) + 'px' }"
        >
          <span class="json-diff-marker">+</span>
          <span class="json-diff-key">{{ row.node.key }}</span>
          <span class="json-diff-value">{{ renderValue(row.node.value) }}</span>
          <span class="json-diff-tag">转换后新增</span>
        </div>
        <div
          v-else-if="row.node.kind === 'removed'"
          class="json-diff-line json-diff-removed"
          :style="{ paddingLeft: (row.depth * 16 + 8) + 'px' }"
        >
          <span class="json-diff-marker">-</span>
          <span class="json-diff-key">{{ row.node.key }}</span>
          <span class="json-diff-value">{{ renderValue(row.node.value) }}</span>
          <span class="json-diff-tag">转换后移除</span>
        </div>
        <div
          v-else-if="row.node.kind === 'changed'"
          class="json-diff-line json-diff-changed"
          :style="{ paddingLeft: (row.depth * 16 + 8) + 'px' }"
        >
          <span class="json-diff-marker">~</span>
          <span class="json-diff-key">{{ row.node.key }}</span>
          <span class="json-diff-value">{{ renderValue(row.node.before) }} → {{ renderValue(row.node.after) }}</span>
          <span class="json-diff-tag">值变化</span>
        </div>
        <div
          v-else-if="row.node.kind === 'object'"
          class="json-diff-line"
          :style="{ paddingLeft: (row.depth * 16 + 8) + 'px' }"
        >
          <span class="json-diff-key">{{ row.node.key }}</span>
          <span class="json-diff-brace">{</span>
        </div>
        <div
          v-else-if="row.node.kind === 'array'"
          class="json-diff-line"
          :style="{ paddingLeft: (row.depth * 16 + 8) + 'px' }"
        >
          <span class="json-diff-key">{{ row.node.key }}</span>
          <span class="json-diff-brace">[</span>
        </div>
      </template>
      <div v-if="hiddenRowCount > 0" class="json-diff-more">
        还有 {{ hiddenRowCount }} 行未显示，<button type="button" class="json-diff-more-btn" @click="showAllRows = true">展开全部</button>
      </div>
    </div>
  </div>
  <div v-else-if="parsed.ok" class="json-diff-empty">
    转换前后字段无差异（或均为透传保留）
  </div>
</template>

<style scoped>
.json-diff-view {
  margin-top: 10px;
  border: 1px solid var(--border-color-global);
  border-radius: 8px;
  overflow: hidden;
  background: var(--bg-input);
}
.json-diff-summary {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 6px 12px;
  font-size: 12px;
  border-bottom: 1px solid var(--border-color-global);
}
.json-diff-note { color: var(--text-color-secondary); }
.json-diff-count { font-weight: 600; }
.json-diff-tree { padding: 8px 0; font-size: 12px; max-height: 420px; overflow: auto; }
.json-diff-line {
  display: flex;
  align-items: baseline;
  gap: 8px;
  padding: 1px 12px 1px 8px;
  line-height: 1.7;
  white-space: nowrap;
}
.json-diff-key { font-weight: 600; color: var(--text-primary); }
.json-diff-value {
  color: var(--text-primary);
  font-family: var(--n-font-family-mono, monospace);
  overflow: hidden;
  text-overflow: ellipsis;
}
.json-diff-brace { color: var(--text-color-secondary); }
.json-diff-marker { width: 12px; text-align: center; font-weight: 700; }
.json-diff-tag { font-size: 11px; border-radius: 4px; padding: 0 6px; flex: none; }
.json-diff-added { background: rgba(22, 163, 74, 0.08); }
.json-diff-added .json-diff-marker,
.json-diff-added .json-diff-key { color: #166534; }
.json-diff-added .json-diff-tag { background: rgba(22, 163, 74, 0.12); color: #166534; }
.json-diff-removed { background: rgba(220, 38, 38, 0.07); }
.json-diff-removed .json-diff-marker,
.json-diff-removed .json-diff-key { color: #B91C1C; }
.json-diff-removed .json-diff-tag { background: rgba(220, 38, 38, 0.1); color: #B91C1C; }
.json-diff-changed { background: rgba(217, 119, 6, 0.07); }
.json-diff-changed .json-diff-marker,
.json-diff-changed .json-diff-key { color: #B45309; }
.json-diff-changed .json-diff-tag { background: rgba(217, 119, 6, 0.12); color: #B45309; }
[data-theme='dark'] .json-diff-added .json-diff-marker,
[data-theme='dark'] .json-diff-added .json-diff-key { color: #86EFAC; }
[data-theme='dark'] .json-diff-removed .json-diff-marker,
[data-theme='dark'] .json-diff-removed .json-diff-key { color: #FCA5A5; }
[data-theme='dark'] .json-diff-changed .json-diff-marker,
[data-theme='dark'] .json-diff-changed .json-diff-key { color: #FCD34D; }
.json-diff-more { padding: 4px 16px; font-size: 12px; color: var(--text-color-secondary); }
.json-diff-more-btn {
  color: var(--n-primary-color, #2080f0);
  background: transparent;
  border: none;
  cursor: pointer;
  font-size: 12px;
  padding: 0;
}
.json-diff-empty {
  margin-top: 10px;
  padding: 8px 12px;
  font-size: 12px;
  color: var(--text-color-secondary);
  border: 1px dashed var(--border-color-global);
  border-radius: 8px;
}
</style>

<script setup lang="ts">
import { computed, ref } from 'vue'

// 递归自引用组件：渲染任意 JSON 值为可折叠树。
// 性能约束：单节点最多渲染 CHILD_RENDER_LIMIT 个子项，超出部分折叠为“展开全部”，
// 避免超大数组/超大对象一次性渲染数万 DOM 节点卡死页面。
defineOptions({ name: 'JsonTreeView' })

const CHILD_RENDER_LIMIT = 100

const props = withDefaults(defineProps<{
  value?: unknown
  name?: string
  depth?: number
  defaultExpandDepth?: number
}>(), {
  depth: 0,
  defaultExpandDepth: 2
})

const isObject = computed(() => typeof props.value === 'object' && props.value !== null && !Array.isArray(props.value))
const isArray = computed(() => Array.isArray(props.value))
const isExpandable = computed(() => isObject.value || isArray.value)

const expanded = ref(props.depth < props.defaultExpandDepth)
const showAllChildren = ref(false)

const childEntries = computed<{ key: string; value: unknown }[]>(() => {
  const v = props.value
  if (isObject.value && v !== null && typeof v === 'object') {
    return Object.entries(v as Record<string, unknown>).map(([key, value]) => ({ key, value }))
  }
  if (isArray.value && Array.isArray(v)) {
    return v.map((value, index) => ({ key: String(index), value }))
  }
  return []
})

const childCount = computed(() => childEntries.value.length)

// 实际渲染的子项：默认截断到上限，点“展开全部”后渲染剩余部分。
const visibleEntries = computed(() =>
  showAllChildren.value ? childEntries.value : childEntries.value.slice(0, CHILD_RENDER_LIMIT)
)

const hiddenCount = computed(() => Math.max(0, childEntries.value.length - CHILD_RENDER_LIMIT))

function toggleExpanded(): void {
  expanded.value = !expanded.value
  if (!expanded.value) showAllChildren.value = false
}

function expandAll(): void {
  showAllChildren.value = true
}

const summary = computed(() => {
  if (isObject.value) return '{ ' + childCount.value + ' 个字段 }'
  if (isArray.value) return '[ ' + childCount.value + ' 项 ]'
  return ''
})

const scalarText = computed(() => {
  const v = props.value
  if (typeof v === 'string') return v
  if (v === null) return 'null'
  if (typeof v === 'number' || typeof v === 'boolean') return String(v)
  return ''
})

const scalarClass = computed(() => {
  const v = props.value
  if (typeof v === 'string') return 'jsonv-string'
  if (typeof v === 'number') return 'jsonv-number'
  if (typeof v === 'boolean') return 'jsonv-boolean'
  return 'jsonv-null'
})

const isArrayIndex = computed(() => props.depth > 0 && /^\d+$/.test(props.name ?? ''))

// 悬停提示只保留前 500 字符，避免 19K 字符超长字符串生成超大 title。
const hoverTitle = computed(() =>
  typeof props.value === 'string' && props.value.length > 500
    ? props.value.slice(0, 500) + '…'
    : (typeof props.value === 'string' ? props.value : undefined)
)
</script>

<template>
  <div class="jsonv-row" :class="{ 'jsonv-collapsible': isExpandable }">
    <span v-if="isExpandable" class="jsonv-toggle" @click="toggleExpanded">
      {{ expanded ? '▾' : '▸' }}
    </span>
    <span v-else class="jsonv-toggle jsonv-toggle-empty"></span>
    <span v-if="depth > 0" class="jsonv-key" :class="{ 'jsonv-index': isArrayIndex }">{{ props.name }}</span>
    <span v-if="depth > 0" class="jsonv-colon">:</span>
    <template v-if="isExpandable">
      <span class="jsonv-brace" @click="toggleExpanded">{{ expanded ? (isArray ? '[' : '{') : (isArray ? '[ ' : '{ ') }}{{ expanded ? '' : summary }}</span>
      <span v-if="expanded && childEntries.length" class="jsonv-close-brace" @click="toggleExpanded">{{ isArray ? ']' : '}' }}</span>
    </template>
    <span v-else class="jsonv-scalar" :class="scalarClass" :title="hoverTitle">
      <template v-if="typeof props.value === 'string'">"{{ scalarText }}"</template>
      <template v-else>{{ scalarText }}</template>
    </span>
  </div>
  <div v-if="expanded && isExpandable" class="jsonv-children">
    <JsonTreeView
      v-for="(entry, index) in visibleEntries"
      :key="index"
      :name="entry.key"
      :value="entry.value"
      :depth="depth + 1"
      :default-expand-depth="defaultExpandDepth"
    />
    <button
      v-if="hiddenCount > 0 && !showAllChildren"
      type="button"
      class="jsonv-more"
      @click="expandAll"
    >
      … 还有 {{ hiddenCount }} 项未显示，点击展开全部
    </button>
  </div>
</template>

<style scoped>
.jsonv-row {
  display: flex;
  align-items: baseline;
  gap: 6px;
  font-size: 12px;
  line-height: 1.9;
  white-space: nowrap;
  min-height: 1.9em;
}
.jsonv-collapsible { cursor: pointer; }
.jsonv-toggle {
  width: 14px;
  flex: none;
  text-align: center;
  color: var(--text-color-secondary);
  user-select: none;
  font-size: 11px;
}
.jsonv-toggle-empty { visibility: hidden; }
.jsonv-key { font-weight: 600; color: var(--text-primary); }
.jsonv-index { color: var(--text-color-secondary); font-weight: 400; }
.jsonv-colon { color: var(--text-color-secondary); }
.jsonv-brace { color: var(--text-color-secondary); }
.jsonv-close-brace { color: var(--text-color-secondary); }
.jsonv-scalar {
  font-family: var(--n-font-family-mono, monospace);
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 70vw;
}
.jsonv-string { color: #166534; }
.jsonv-number { color: #1D4ED8; }
.jsonv-boolean { color: #B45309; }
.jsonv-null { color: var(--text-color-secondary); font-style: italic; }
[data-theme='dark'] .jsonv-string { color: #86EFAC; }
[data-theme='dark'] .jsonv-number { color: #A9C4FF; }
[data-theme='dark'] .jsonv-boolean { color: #FCD34D; }
.jsonv-children {
  padding-left: 22px;
  border-left: 1px dashed var(--border-color-global);
  margin-left: 6px;
}
.jsonv-more {
  display: block;
  margin: 4px 0;
  padding: 2px 8px;
  font-size: 11px;
  color: var(--n-primary-color, #2080f0);
  background: transparent;
  border: 1px dashed var(--border-color-global);
  border-radius: 4px;
  cursor: pointer;
}
.jsonv-more:hover { background: var(--bg-surface-soft); }
</style>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import {
  NButton,
  NInput,
  NInputNumber,
  NModal,
  NPagination,
  NPopconfirm,
  NTag,
  NTooltip,
  useMessage
} from 'naive-ui'
import {
  getModelPricing,
  listModels,
  saveModelPricing,
  type ModelPriceEntry
} from '@/api/models'

/**
 * 模型价格管理面板（Models 页“模型价格”Tab）。
 * 价格表保存到服务器本地 model-pricing.json（非数据库），保存后计价缓存立即生效；
 * 峰谷条目（如 DeepSeek）在高峰窗口外自动使用低峰价。
 * 大数据量优化：按厂商筛选（厂商来自厂商规则页的匹配规则）+ 客户端分页，
 * 一次最多渲染一页（20 行）的输入控件，避免全量渲染卡顿。
 */
const message = useMessage()

/** 客户端分页每页行数：控制同时渲染的输入控件数量。 */
const PAGE_SIZE = 20

const loading = ref(false)
const saving = ref(false)
const search = ref('')
const usdToCny = ref(6.74)
const entries = ref<ModelPriceEntry[]>([])
/** 未在价格表中的模型库模型名（提示补录）。 */
const unpricedModelNames = ref<string[]>([])

/** 当前厂商筛选；null = 全部。 */
const activeVendor = ref<string | null>(null)
const page = ref(1)

const peakEditorVisible = ref(false)
const peakEditorIndex = ref(-1)
const peakDraft = ref({
  input: 0,
  output: 0,
  cacheRead: 0,
  windows: ''
})

/** 厂商选项（含计数），按条目数降序；无厂商归属的条目归入「未分类」。 */
const vendorOptions = computed(() => {
  const counts = new Map<string, number>()
  for (const entry of entries.value) {
    const vendor = entry.vendorName?.trim() || '未分类'
    counts.set(vendor, (counts.get(vendor) ?? 0) + 1)
  }
  return [...counts.entries()]
    .map(([name, count]) => ({ name, count }))
    .sort((a, b) => b.count - a.count || a.name.localeCompare(b.name))
})

const totalCount = computed(() => entries.value.length)

/** 厂商 + 搜索过滤后的条目。 */
const filteredEntries = computed(() => {
  const vendor = activeVendor.value
  const keyword = search.value.trim().toLowerCase()
  return entries.value.filter((entry) => {
    if (vendor !== null) {
      const entryVendor = entry.vendorName?.trim() || '未分类'
      if (entryVendor !== vendor) return false
    }
    if (!keyword) return true
    return entry.id.toLowerCase().includes(keyword) || (entry.displayName || '').toLowerCase().includes(keyword)
  })
})

const totalPages = computed(() => Math.max(1, Math.ceil(filteredEntries.value.length / PAGE_SIZE)))

/** 当前页条目（真实数组下标随过滤/翻页变化）。 */
const pagedEntries = computed(() => {
  const start = (page.value - 1) * PAGE_SIZE
  return filteredEntries.value.slice(start, start + PAGE_SIZE)
})

// 过滤条件变化后回到第一页，避免停留在超出总页数的页码。
watch([activeVendor, search], () => {
  page.value = 1
})

const peakEntryCount = computed(() => entries.value.filter((e) => e.offPeak).length)

async function load(): Promise<void> {
  loading.value = true
  try {
    const [catalog, models] = await Promise.all([getModelPricing(), listModels().catch(() => null)])
    usdToCny.value = catalog.usdToCny || 6.74
    entries.value = catalog.models.map((entry) => normalizeEntry(entry))
    if (models) {
      const known = new Set(entries.value.map((e) => e.id.toLowerCase()))
      unpricedModelNames.value = (models.vendorGroups ?? [])
        .flatMap((g) => g.models.map((m) => m.modelName))
        .filter((name) => !known.has(name.toLowerCase()))
    }
    page.value = 1
  } catch (error) {
    message.error(`加载价格表失败：${(error as Error)?.message ?? '未知错误'}`)
  } finally {
    loading.value = false
  }
}

function normalizeEntry(entry: ModelPriceEntry): ModelPriceEntry {
  return {
    ...entry,
    input: Number(entry.input) || 0,
    output: Number(entry.output) || 0,
    cacheRead: Number(entry.cacheRead) || 0,
    cacheWrite: Number(entry.cacheWrite) || 0,
    peakWindows: entry.peakWindows ?? (entry.offPeak ? ['09:00-12:00', '14:00-18:00'] : undefined)
  }
}

function addEntry(name?: string): void {
  entries.value.unshift({
    id: name ?? '',
    displayName: name ?? '',
    input: 0,
    output: 0,
    cacheRead: 0,
    cacheWrite: 0
  })
  // 新增行插在最前：回到第一页并清除厂商筛选，保证立即可见可编辑。
  activeVendor.value = null
  search.value = ''
  page.value = 1
  if (name) unpricedModelNames.value = unpricedModelNames.value.filter((n) => n.toLowerCase() !== name.toLowerCase())
}

function removeEntry(index: number): void {
  const entry = pagedEntries.value[index]
  const actual = entries.value.indexOf(entry)
  if (actual >= 0) entries.value.splice(actual, 1)
  // 删除尾页最后一行后页码可能越界，钳回实际总页数。
  page.value = Math.min(page.value, totalPages.value)
}

function openPeakEditor(index: number): void {
  const entry = pagedEntries.value[index]
  peakEditorIndex.value = entries.value.indexOf(entry)
  peakDraft.value = {
    input: entry.offPeak?.input ?? entry.input,
    output: entry.offPeak?.output ?? entry.output,
    cacheRead: entry.offPeak?.cacheRead ?? entry.cacheRead,
    windows: (entry.peakWindows ?? []).join(', ')
  }
  peakEditorVisible.value = true
}

function applyPeakEditor(): void {
  const entry = entries.value[peakEditorIndex.value]
  if (!entry) return
  const windows = peakDraft.value.windows
    .split(/[,，;]/)
    .map((w) => w.trim())
    .filter(Boolean)
  if (windows.length === 0) {
    entry.offPeak = null
    entry.peakWindows = null
  } else {
    entry.offPeak = {
      input: peakDraft.value.input,
      output: peakDraft.value.output,
      cacheRead: peakDraft.value.cacheRead
    }
    entry.peakWindows = windows
    entry.peakTimeZoneOffsetMinutes = entry.peakTimeZoneOffsetMinutes || 480
  }
  peakEditorVisible.value = false
}

async function handleSave(): Promise<void> {
  const seen = new Set<string>()
  for (const entry of entries.value) {
    const id = entry.id.trim()
    if (!id) {
      message.warning('存在未填写模型 ID 的价格条目')
      return
    }
    if (seen.has(id.toLowerCase())) {
      message.warning(`模型 ID 重复：${id}`)
      return
    }
    seen.add(id.toLowerCase())
  }

  saving.value = true
  try {
    // vendorName 是服务端标注的派生字段，保存时剔除。
    const payload = {
      usdToCny: usdToCny.value,
      models: entries.value.map((entry) => ({
        id: entry.id.trim(),
        displayName: entry.displayName?.trim() || entry.id.trim(),
        input: entry.input,
        output: entry.output,
        cacheRead: entry.cacheRead,
        cacheWrite: entry.cacheWrite,
        offPeak: entry.offPeak,
        peakWindows: entry.peakWindows,
        peakTimeZoneOffsetMinutes: entry.peakTimeZoneOffsetMinutes
      }))
    }
    await saveModelPricing(payload)
    message.success('模型价格已保存，统计页与日志页的消耗金额已实时更新')
    await load()
  } catch (error) {
    message.error(`保存失败：${(error as Error)?.message ?? '未知错误'}`)
  } finally {
    saving.value = false
  }
}

function peakWindowCount(entry: { peakWindows?: string[] | null }): number {
  return entry.peakWindows?.length ?? 0
}

/** 行 key：按对象身份分配稳定序号。key 不能包含 entry.id——
 *  id 输入框 v-model 绑定 entry.id，key 随每次按键变化会导致行重建、输入框失焦。 */
let rowKeySeq = 0
const rowKeys = new WeakMap<object, number>()

function keyOf(entry: ModelPriceEntry): number {
  let key = rowKeys.get(entry)
  if (key === undefined) {
    key = ++rowKeySeq
    rowKeys.set(entry, key)
  }
  return key
}

onMounted(load)
</script>

<template>
  <div class="pricing-panel">
    <div class="pricing-toolbar">
      <div class="pricing-toolbar-left">
        <span class="pricing-title">模型价格</span>
        <NTooltip trigger="hover" placement="bottom-start" style="max-width: 420px">
          <template #trigger><span class="tip-icon">?</span></template>
          价格单位为 USD / 百万 tokens；保存到服务器本地 model-pricing.json（不入数据库），保存后统计立即生效。<br />
          峰谷条目在高峰时段窗口外自动使用低峰价（默认北京时间 09:00-12:00、14:00-18:00 为高峰）。<br />
          厂商分组来自「厂商规则」页的匹配规则；汇率用于统计页人民币展示换算。
        </NTooltip>
      </div>
      <div class="pricing-toolbar-actions">
        <div class="pricing-rate">
          <span>汇率 1$ =</span>
          <NInputNumber v-model:value="usdToCny" size="small" :min="0.01" :max="100" :step="0.01" style="width: 100px" />
          <span>¥</span>
        </div>
        <NInput v-model:value="search" size="small" clearable placeholder="搜索模型" style="width: 180px" />
        <NButton size="small" secondary type="primary" @click="addEntry()">新增价格</NButton>
        <NButton size="small" type="primary" :loading="saving" @click="handleSave">保存价格表</NButton>
      </div>
    </div>

    <div class="pricing-vendor-row">
      <button
        type="button"
        class="pricing-vendor-chip"
        :class="{ active: activeVendor === null }"
        @click="activeVendor = null"
      >
        全部 <span class="pricing-vendor-count">{{ totalCount }}</span>
      </button>
      <button
        v-for="vendor in vendorOptions"
        :key="vendor.name"
        type="button"
        class="pricing-vendor-chip"
        :class="{ active: activeVendor === vendor.name }"
        @click="activeVendor = vendor.name"
      >
        {{ vendor.name }} <span class="pricing-vendor-count">{{ vendor.count }}</span>
      </button>
    </div>

    <div v-if="unpricedModelNames.length" class="pricing-unpriced">
      <span>模型库中有 {{ unpricedModelNames.length }} 个模型未定价（其请求成本将按 0 统计）：</span>
      <NTag
        v-for="name in unpricedModelNames.slice(0, 12)"
        :key="name"
        size="small"
        :bordered="false"
        class="pricing-unpriced-tag"
        @click="addEntry(name)"
      >
        ＋ {{ name }}
      </NTag>
      <span v-if="unpricedModelNames.length > 12" class="pricing-unpriced-more">等 {{ unpricedModelNames.length }} 个</span>
    </div>

    <div v-if="loading" class="pricing-empty">加载中...</div>
    <div v-else-if="filteredEntries.length === 0" class="pricing-empty">
      {{ totalCount === 0 ? '价格表为空，点击“新增价格”添加第一条' : '没有匹配的模型' }}
    </div>
    <template v-else>
      <div class="table-wrapper pricing-table-wrapper">
        <table class="table pricing-table">
          <thead>
            <tr>
              <th style="width: 220px">模型 ID</th>
              <th style="width: 170px">显示名</th>
              <th>输入 $/M</th>
              <th>输出 $/M</th>
              <th>缓存读 $/M</th>
              <th style="width: 120px">峰谷</th>
              <th style="width: 70px">操作</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(entry, index) in pagedEntries" :key="keyOf(entry)">
              <td><input v-model="entry.id" class="pricing-input pricing-input-id" placeholder="model-id" /></td>
              <td><input v-model="entry.displayName" class="pricing-input" placeholder="显示名" /></td>
              <td><NInputNumber v-model:value="entry.input" size="tiny" :min="0" :step="0.05" class="pricing-number" /></td>
              <td><NInputNumber v-model:value="entry.output" size="tiny" :min="0" :step="0.05" class="pricing-number" /></td>
              <td><NInputNumber v-model:value="entry.cacheRead" size="tiny" :min="0" :step="0.01" class="pricing-number" /></td>
              <td>
                <NTag v-if="entry.offPeak" size="small" type="warning" :bordered="false" class="pricing-peak-tag" @click="openPeakEditor(index)">
                  峰谷 ×{{ peakWindowCount(entry) }}
                </NTag>
                <NTag v-else size="small" :bordered="false" class="pricing-peak-tag" @click="openPeakEditor(index)">固定价</NTag>
              </td>
              <td>
                <NPopconfirm @positive-click="removeEntry(index)">
                  <template #trigger><NButton size="tiny" secondary type="error">删除</NButton></template>
                  删除「{{ entry.id || '未命名' }}」的价格条目？
                </NPopconfirm>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <div class="pricing-footer">
        <span>共 {{ totalCount }} 条价格，其中 {{ peakEntryCount }} 条峰谷计价；当前 {{ filteredEntries.length }} 条匹配</span>
        <NPagination
          v-if="filteredEntries.length > PAGE_SIZE"
          v-model:page="page"
          :page-count="totalPages"
          :page-size="PAGE_SIZE"
          size="small"
        />
      </div>
    </template>

    <NModal
      v-model:show="peakEditorVisible"
      title="峰谷价格设置"
      preset="card"
      style="width: min(520px, 94vw)"
      :mask-closable="false"
    >
      <div class="peak-editor">
        <p class="peak-editor-help">
          基准价（表格中的输入/输出/缓存读）即<b>高峰时段价格</b>；此处填写<b>低峰时段</b>价格与高峰时间窗口。
          窗口格式 HH:mm-HH:mm，多个用逗号分隔（支持跨午夜，如 22:00-06:00）。清空窗口即恢复固定价。
        </p>
        <div class="peak-editor-grid">
          <label>低峰输入 $/M<NInputNumber v-model:value="peakDraft.input" size="small" :min="0" :step="0.05" /></label>
          <label>低峰输出 $/M<NInputNumber v-model:value="peakDraft.output" size="small" :min="0" :step="0.05" /></label>
          <label>低峰缓存读 $/M<NInputNumber v-model:value="peakDraft.cacheRead" size="small" :min="0" :step="0.01" /></label>
          <label class="peak-editor-window">高峰窗口（北京时间）<NInput v-model:value="peakDraft.windows" placeholder="09:00-12:00, 14:00-18:00" /></label>
        </div>
        <div class="peak-editor-actions">
          <NButton size="small" @click="peakEditorVisible = false">取消</NButton>
          <NButton size="small" type="primary" @click="applyPeakEditor">确定</NButton>
        </div>
      </div>
    </NModal>
  </div>
</template>

<script lang="ts">
export default { name: 'ModelPricingPanel' }
</script>

<style scoped>
.pricing-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.pricing-toolbar {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  align-items: center;
  justify-content: space-between;
  padding: 10px 12px;
  background: var(--bg-secondary, rgba(127, 127, 127, 0.06));
  border-radius: 8px;
}

.pricing-toolbar-left {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 14px;
  font-weight: 600;
}

.tip-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  border: 1px solid var(--border-color-global, rgba(127, 127, 127, 0.4));
  border-radius: 50%;
  font-size: 11px;
  font-weight: 600;
  cursor: help;
  opacity: 0.65;
  flex-shrink: 0;
}

.pricing-toolbar-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.pricing-rate {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
}

.pricing-vendor-row {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  padding: 0 12px;
}

.pricing-vendor-chip {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 3px 10px;
  border: 1px solid var(--border-color-global, rgba(127, 127, 127, 0.35));
  border-radius: 999px;
  background: transparent;
  color: var(--text-primary);
  font-size: 12px;
  cursor: pointer;
  transition: border-color 0.15s, background 0.15s;
}

.pricing-vendor-chip:hover {
  border-color: rgba(99, 148, 255, 0.6);
}

.pricing-vendor-chip.active {
  border-color: rgba(99, 148, 255, 0.8);
  background: rgba(99, 148, 255, 0.14);
  font-weight: 600;
}

.pricing-vendor-count {
  font-size: 11px;
  opacity: 0.55;
}

.pricing-unpriced {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  font-size: 12px;
  /* 内容内边距 6px 12px + 外侧留白 12px，与厂商筛选行对齐 */
  margin: 0 12px;
  padding: 6px 12px;
  border: 1px dashed rgba(240, 160, 32, 0.5);
  border-radius: 8px;
}

.pricing-unpriced-tag {
  cursor: pointer;
}

.pricing-unpriced-more {
  opacity: 0.6;
}

.pricing-empty {
  padding: 40px 0;
  text-align: center;
  opacity: 0.6;
}

.pricing-table-wrapper {
  overflow-x: auto;
}

.pricing-table th,
.pricing-table td {
  padding: 6px 8px;
  font-size: 12px;
  white-space: nowrap;
}

.pricing-input {
  width: 100%;
  min-width: 120px;
  padding: 4px 8px;
  border: 1px solid transparent;
  border-radius: 6px;
  background: transparent;
  color: inherit;
  font-size: 12px;
}

.pricing-input:focus {
  border-color: rgba(99, 148, 255, 0.5);
  outline: none;
  background: var(--bg-secondary, rgba(127, 127, 127, 0.08));
}

.pricing-input-id {
  font-family: var(--font-mono, monospace);
}

.pricing-number {
  width: 110px;
}

.pricing-peak-tag {
  cursor: pointer;
}

.pricing-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 8px;
  padding: 4px 2px;
  font-size: 12px;
  opacity: 0.7;
}

.peak-editor-help {
  font-size: 12px;
  opacity: 0.75;
  margin: 0 0 12px;
}

.peak-editor-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px;
}

.peak-editor-grid label {
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: 12px;
}

.peak-editor-window {
  grid-column: 1 / -1;
}

.peak-editor-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  margin-top: 14px;
}
</style>

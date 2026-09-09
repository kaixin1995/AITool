<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import {
  NButton,
  NCheckbox,
  NInput,
  NInputNumber,
  NModal,
  NPagination,
  NPopconfirm,
  NProgress,
  NSelect,
  NSpace,
  NTag,
  NTooltip,
  useMessage
} from 'naive-ui'
import {
  getModelPricing,
  listModels,
  saveModelPricing,
  aiPlanPricing,
  aiQueryPricing,
  sourceFetchPricing,
  type ModelPriceEntry,
  type AiPlanTarget,
  type AiPriceSnapshot
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
/** 本地模型库全部模型名（小写，含禁用；供「清理无效条目」判据使用）。 */
const libraryModelNames = ref<Set<string>>(new Set())

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

// —— 模型价格查询（首选公开价格源，AI 仅补漏）——
type AiRowKind = 'added' | 'updated' | 'unchanged' | 'missing' | 'failed'

/** 单个模型的查询结果行。 */
interface AiRow {
  id: string
  displayName: string
  kind: AiRowKind
  before: AiPriceSnapshot | null
  after?: AiPriceSnapshot
  error?: string
}

const aiScope = ref<'missing' | 'all'>('missing')
const aiLoading = ref(false)
const showAiModal = ref(false)
/** 查询计划全量目标。 */
const aiPlan = ref<AiPlanTarget[]>([])
const aiTruncated = ref(false)
/** 弹窗内状态：source=查公开价格源，ai=AI 补漏，done=全部结束。 */
const aiPhase = ref<'idle' | 'source' | 'ai' | 'done'>('idle')
const aiSourceError = ref('')
const aiSourceNote = ref('')
const aiBatchSize = ref(5)
const aiRunning = ref(false)
const aiStopRequested = ref(false)
const aiProcessed = ref(0)
const aiAiTotal = ref(0)
const aiRows = ref<AiRow[]>([])
const aiCurrentBatch = ref<string[]>([])
const aiApplying = ref(false)

const aiScopeOptions = [
  { label: '仅缺失模型', value: 'missing' },
  { label: '全部模型', value: 'all' }
]

const aiBatchSizeOptions = [
  { label: '每批 1 个', value: 1 },
  { label: '每批 5 个', value: 5 },
  { label: '每批 10 个', value: 10 }
]

const aiAddedCount = computed(() => aiRows.value.filter((r) => r.kind === 'added').length)
const aiUpdatedCount = computed(() => aiRows.value.filter((r) => r.kind === 'updated').length)
const aiUnchangedCount = computed(() => aiRows.value.filter((r) => r.kind === 'unchanged').length)
const aiMissingCount = computed(() => aiRows.value.filter((r) => r.kind === 'missing').length)
const aiFailedCount = computed(() => aiRows.value.filter((r) => r.kind === 'failed').length)
const aiHasChanges = computed(() => aiAddedCount.value + aiUpdatedCount.value > 0)
const aiProgressPercent = computed(() =>
  aiAiTotal.value === 0 ? 0 : Math.round((aiProcessed.value / aiAiTotal.value) * 100)
)
/** AI 补漏目标数：公开源失败时兜底查全部计划目标，否则只查未收录/失败的。 */
const aiFillCount = computed(() =>
  aiSourceError.value ? aiPlan.value.length : aiMissingCount.value + aiFailedCount.value
)

// 查询中关闭弹窗视为停止（不再发起后续批次）。
watch(showAiModal, (show) => {
  if (!show) aiStopRequested.value = true
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
      const allNames = new Set<string>()
      unpricedModelNames.value = (models.vendorGroups ?? [])
        .flatMap((g) => g.models.map((m) => m.modelName))
        .filter((name) => {
          allNames.add(name.toLowerCase())
          return !known.has(name.toLowerCase())
        })
      libraryModelNames.value = allNames
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

async function handleSave(silentSuccess = false): Promise<boolean> {
  const seen = new Set<string>()
  for (const entry of entries.value) {
    const id = entry.id.trim()
    if (!id) {
      message.warning('存在未填写模型 ID 的价格条目')
      return false
    }
    if (seen.has(id.toLowerCase())) {
      message.warning(`模型 ID 重复：${id}`)
      return false
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
    if (!silentSuccess) {
      message.success('模型价格已保存，统计页与日志页的消耗金额已实时更新')
    }
    await load()
    return true
  } catch (error) {
    message.error(`保存失败：${(error as Error)?.message ?? '未知错误'}`)
    return false
  } finally {
    saving.value = false
  }
}

async function handleAiQueryPricing(): Promise<void> {
  aiLoading.value = true
  try {
    // 第一步只生成查询计划（不调任何查询，秒回），随后弹窗内先查公开价格源。
    const plan = await aiPlanPricing(aiScope.value === 'all')
    if (!plan.targets.length) {
      message.info('没有需要查询的模型：价格表无缺失，也未选择「全部模型」')
      return
    }
    aiPlan.value = plan.targets
    aiTruncated.value = plan.truncated
    aiRows.value = []
    aiPhase.value = 'idle'
    aiSourceError.value = ''
    aiSourceNote.value = ''
    aiProcessed.value = 0
    aiAiTotal.value = 0
    aiCurrentBatch.value = []
    aiStopRequested.value = false
    showAiModal.value = true
    void runSourceFetch()
  } catch (error) {
    message.error(`查询价格失败：${(error as Error)?.message ?? '未知错误'}`)
  } finally {
    aiLoading.value = false
  }
}

/** 第二步（首选）：一次请求匹配公开价格源（models.dev / LiteLLM），秒级返回。 */
async function runSourceFetch(): Promise<void> {
  aiPhase.value = 'source'
  aiSourceError.value = ''
  try {
    const res = await sourceFetchPricing(aiPlan.value.map((t) => t.id))
    if (!res.success) {
      aiSourceError.value = res.error || '公开价格源查询失败'
      return
    }
    const hitById = new Map(res.entries.map((e) => [e.id.toLowerCase(), e]))
    aiRows.value = aiPlan.value.map((target) => {
      const hit = hitById.get(target.id.toLowerCase())
      if (!hit) {
        return {
          id: target.id,
          displayName: target.id,
          kind: 'missing' as AiRowKind,
          before: target.current,
          error: '公开价格源未收录'
        }
      }
      return buildPriceRow(target, {
        id: hit.id,
        displayName: hit.displayName,
        input: hit.input,
        output: hit.output,
        cacheRead: hit.cacheRead,
        cacheWrite: hit.cacheWrite
      })
    })
    aiSourceNote.value = `价格源：${res.sources.join('、')}｜已匹配 ${res.entries.length} / ${aiPlan.value.length}`
  } catch (error) {
    aiSourceError.value = (error as Error)?.message ?? '公开价格源查询失败'
  } finally {
    aiPhase.value = aiRows.value.length ? 'done' : 'idle'
  }
}

/** 第三步（可选）：对「未收录/失败」的模型跑 AI 分批补漏，结果原位更新到行上；
 *  公开源整体失败（无行）时兜底查全部计划目标。 */
async function handleAiFillMissing(): Promise<void> {
  let pending = aiRows.value
    .filter((r) => r.kind === 'missing' || r.kind === 'failed')
    .map((row) => aiPlan.value.find((t) => t.id.toLowerCase() === row.id.toLowerCase()))
    .filter((t): t is AiPlanTarget => Boolean(t))
  if (!pending.length && (aiSourceError.value || aiRows.value.length === 0)) {
    pending = [...aiPlan.value]
  }
  if (!pending.length) return

  // 每次补漏重新允许停止（上一次点过停止/关过弹窗都会置位）。
  aiStopRequested.value = false
  aiRunning.value = true
  aiPhase.value = 'ai'
  aiProcessed.value = 0
  aiAiTotal.value = pending.length
  const size = Math.max(1, aiBatchSize.value)
  try {
    for (let i = 0; i < pending.length; i += size) {
      if (aiStopRequested.value) break
      const batch = pending.slice(i, i + size)
      aiCurrentBatch.value = batch.map((t) => t.id)
      try {
        const res = await aiQueryPricing(batch)
        mergeAiBatchIntoRows(batch, res)
      } catch (error) {
        for (const target of batch) {
          markRowFailed(target.id, (error as Error)?.message ?? '请求失败')
        }
      }
      aiProcessed.value = Math.min(i + size, pending.length)
      aiCurrentBatch.value = []
    }
  } finally {
    aiRunning.value = false
    aiCurrentBatch.value = []
    aiPhase.value = 'done'
  }
}

/** 按目标 + 新价生成结果行（对比当前价判定新增/更新/无变化）。 */
function buildPriceRow(
  target: AiPlanTarget,
  hit: { id: string; displayName: string; input: number; output: number; cacheRead: number; cacheWrite: number }
): AiRow {
  const after: AiPriceSnapshot = {
    input: hit.input,
    output: hit.output,
    cacheRead: hit.cacheRead,
    cacheWrite: hit.cacheWrite
  }
  const before = target.current ?? null
  const changed = !before
    || before.input !== after.input
    || before.output !== after.output
    || before.cacheRead !== after.cacheRead
    || before.cacheWrite !== after.cacheWrite
  return {
    id: target.id,
    displayName: hit.displayName || target.id,
    kind: !before ? 'added' : changed ? 'updated' : 'unchanged',
    before,
    after
  }
}

/** AI 补漏：把一个批次的响应原位合并到已有行上（命中的更新，未命中的标失败）。 */
function mergeAiBatchIntoRows(batch: AiPlanTarget[], res: { success: boolean; error?: string | null; entries: Array<{ id: string; displayName: string; input: number; output: number; cacheRead: number; cacheWrite: number }> }): void {
  for (const target of batch) {
    if (!res.success) {
      markRowFailed(target.id, res.error || 'AI 查询失败')
      continue
    }
    const hit = res.entries.find((e) => e.id.toLowerCase() === target.id.toLowerCase())
    if (!hit) {
      markRowFailed(target.id, 'AI 未提供该模型的定价')
      continue
    }
    const row = findRow(target.id)
    if (!row) continue
    const built = buildPriceRow(target, hit)
    row.displayName = built.displayName
    row.kind = built.kind
    row.before = built.before
    row.after = built.after
    row.error = ''
  }
}

function findRow(id: string): AiRow | undefined {
  return aiRows.value.find((r) => r.id.toLowerCase() === id.toLowerCase())
}

function markRowFailed(id: string, error: string): void {
  const row = findRow(id)
  if (!row) return
  row.kind = 'failed'
  row.error = error
}

/** 应用：命中已有条目只覆盖四个基准价（保留低峰档/高峰窗等配置），新条目追加，走既有整表保存。 */
async function handleApplyAiPricing(): Promise<void> {
  if (!aiHasChanges.value) return

  aiApplying.value = true
  try {
    const byId = new Map(entries.value.map((e) => [e.id.trim().toLowerCase(), e]))
    for (const row of aiRows.value) {
      if (!row.after) continue
      const existing = byId.get(row.id.toLowerCase())
      if (existing) {
        existing.input = row.after.input
        existing.output = row.after.output
        existing.cacheRead = row.after.cacheRead
        existing.cacheWrite = row.after.cacheWrite
      } else if (row.kind === 'added') {
        const entry: ModelPriceEntry = {
          id: row.id,
          displayName: row.displayName || row.id,
          input: row.after.input,
          output: row.after.output,
          cacheRead: row.after.cacheRead,
          cacheWrite: row.after.cacheWrite
        }
        entries.value.push(entry)
        byId.set(row.id.toLowerCase(), entry)
      }
    }
    const saved = await handleSave(true)
    if (saved) {
      message.success(`已应用 AI 查询结果（新增 ${aiAddedCount.value} 条，更新 ${aiUpdatedCount.value} 条）`)
      showAiModal.value = false
    }
  } finally {
    aiApplying.value = false
  }
}

function kindLabel(kind: AiRowKind): string {
  return kind === 'added' ? '新增'
    : kind === 'updated' ? '更新'
    : kind === 'failed' ? '失败'
    : kind === 'missing' ? '未收录'
    : '无变化'
}

function aiKindTagType(kind: AiRowKind): 'success' | 'warning' | 'error' | 'default' {
  return kind === 'added' ? 'success'
    : kind === 'updated' ? 'warning'
    : kind === 'failed' ? 'error'
    : 'default'
}

function formatPriceChange(before: number | undefined, after: number): string {
  if (before === undefined || before === after) return String(after)
  return `${before} → ${after}`
}

// —— 清理无效条目（价格表有、本地模型库没有的模型）——
const showCleanupModal = ref(false)
const cleanupPreview = ref<ModelPriceEntry[]>([])
/** 本轮确认弹窗中勾选待删的模型 ID（小写）。默认全选，可逐行取消。 */
const cleanupSelectedIds = ref<Set<string>>(new Set())
const cleaning = ref(false)

/** 价格表中不在本地模型库（含禁用）的条目。 */
const orphanedEntries = computed(() =>
  entries.value.filter((e) => {
    const id = e.id.trim()
    return id.length > 0 && !libraryModelNames.value.has(id.toLowerCase())
  })
)

const cleanupSelectedCount = computed(() => cleanupSelectedIds.value.size)

function toggleCleanupSelected(id: string, checked: boolean): void {
  const next = new Set(cleanupSelectedIds.value)
  if (checked) next.add(id.toLowerCase())
  else next.delete(id.toLowerCase())
  cleanupSelectedIds.value = next
}

function openCleanupModal(): void {
  cleanupPreview.value = [...orphanedEntries.value]
  cleanupSelectedIds.value = new Set(cleanupPreview.value.map((e) => e.id.trim().toLowerCase()))
  showCleanupModal.value = true
}

async function handleCleanupConfirm(): Promise<void> {
  if (!cleanupSelectedIds.value.size) return
  entries.value = entries.value.filter((e) => !cleanupSelectedIds.value.has(e.id.trim().toLowerCase()))

  cleaning.value = true
  try {
    const saved = await handleSave(true)
    if (saved) {
      message.success(`已清理 ${cleanupSelectedIds.value.size} 条无效价格条目并保存`)
      showCleanupModal.value = false
    } else {
      await load()
    }
  } finally {
    cleaning.value = false
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
        <NTooltip trigger="hover" placement="bottom-start" style="max-width: 380px">
          <template #trigger>
            <NButton
              size="small"
              secondary
              :loading="aiLoading"
              :disabled="aiRunning || showAiModal"
              @click="handleAiQueryPricing"
            >
              查价格
            </NButton>
          </template>
          首选公开价格源（models.dev / LiteLLM）一次匹配全部模型，秒级返回；未收录的可一键 AI 补漏。结果确认后应用。
        </NTooltip>
        <NSelect
          v-model:value="aiScope"
          size="small"
          :options="aiScopeOptions"
          :disabled="aiLoading"
          style="width: 120px"
        />
        <NTooltip trigger="hover" placement="bottom-start" style="max-width: 360px">
          <template #trigger>
            <NButton size="small" secondary :disabled="loading" @click="() => openCleanupModal()">清理无效条目</NButton>
          </template>
          删除价格表中不在本地模型库（含已禁用）的模型条目，如 gpt-4o 等早已不用的老模型；清理后这些模型的历史用量金额将按 0 统计。
        </NTooltip>
        <NButton size="small" type="primary" :loading="saving" @click="() => handleSave()">保存价格表</NButton>
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

    <!-- 清理无效条目确认弹窗 -->
    <NModal
      v-model:show="showCleanupModal"
      title="清理无效价格条目"
      preset="card"
      style="width: min(640px, 94vw)"
      :mask-closable="false"
    >
      <p class="cleanup-help">
        以下 {{ cleanupPreview.length }} 个条目在本地模型库中不存在（gpt-4o 等早已不用的老模型），
        已默认全选，<b>可取消勾选保留想留的模型</b>；清理后这些模型的历史使用日志消耗金额将按 0 统计（后续重新导入模型可再查价格补回）。
      </p>
      <div class="ai-pricing-table-wrap">
        <table class="table ai-pricing-table">
          <thead>
            <tr>
              <th style="width: 40px"></th>
              <th>模型 ID</th>
              <th>显示名</th>
              <th>输入 $/M</th>
              <th>输出 $/M</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="entry in cleanupPreview.slice(0, 60)" :key="entry.id">
              <td>
                <NCheckbox
                  :checked="cleanupSelectedIds.has(entry.id.trim().toLowerCase())"
                  @update:checked="(checked: boolean) => toggleCleanupSelected(entry.id, checked)"
                />
              </td>
              <td><span class="ai-pricing-id">{{ entry.id }}</span></td>
              <td>{{ entry.displayName || '—' }}</td>
              <td>{{ entry.input }}</td>
              <td>{{ entry.output }}</td>
            </tr>
            <tr v-if="cleanupPreview.length > 60">
              <td colspan="5" class="ai-pricing-empty">…等共 {{ cleanupPreview.length }} 个</td>
            </tr>
          </tbody>
        </table>
      </div>
      <template #footer>
        <NSpace justify="end" class="ai-pricing-actions">
          <NButton @click="showCleanupModal = false">取消</NButton>
          <NButton
            type="error"
            :loading="cleaning"
            :disabled="cleanupSelectedCount === 0"
            @click="handleCleanupConfirm"
          >
            清理 {{ cleanupSelectedCount }} 条并保存
          </NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- AI 查价格：分批查询弹窗（进度实时刷新，失败行常驻展示，可随时停止） -->
    <NModal
      v-model:show="showAiModal"
      title="查价格（公开价格源）"
      preset="card"
      style="width: min(880px, 96vw)"
      :mask-closable="false"
    >
      <div v-if="aiPhase === 'source'" class="ai-pricing-current">正在查询公开价格源（models.dev / LiteLLM）…</div>
      <div v-if="aiPhase === 'ai' || aiRunning" class="ai-pricing-progress">
        <NProgress
          type="line"
          :percentage="aiProgressPercent"
          :height="10"
          :show-indicator="false"
          style="flex: 1; min-width: 120px"
        />
        <span class="ai-pricing-progress-text">AI 补漏 {{ aiProcessed }} / {{ aiAiTotal }}</span>
        <NSelect
          v-model:value="aiBatchSize"
          size="small"
          :options="aiBatchSizeOptions"
          :disabled="aiRunning"
          style="width: 104px"
        />
        <NButton size="small" secondary type="warning" @click="aiStopRequested = true">
          停止查询
        </NButton>
      </div>
      <div v-if="aiCurrentBatch.length" class="ai-pricing-current">正在查询：{{ aiCurrentBatch.join('、') }}</div>
      <div v-if="aiSourceError" class="ai-pricing-source-error">{{ aiSourceError }}</div>
      <div class="ai-pricing-summary">
        <NTag type="success" :bordered="false">新增 {{ aiAddedCount }}</NTag>
        <NTag type="warning" :bordered="false">更新 {{ aiUpdatedCount }}</NTag>
        <NTag :bordered="false">无变化 {{ aiUnchangedCount }}</NTag>
        <NTag v-if="aiMissingCount" :bordered="false">未收录 {{ aiMissingCount }}</NTag>
        <NTag type="error" :bordered="false">失败 {{ aiFailedCount }}</NTag>
        <span v-if="aiSourceNote" class="ai-pricing-note">{{ aiSourceNote }}</span>
        <span v-if="aiTruncated" class="ai-pricing-note">目标模型较多，本次仅查询前 80 个。</span>
        <span v-if="!aiRunning && aiPhase === 'done' && aiProcessed < aiAiTotal" class="ai-pricing-note">
          已停止，剩余 {{ aiAiTotal - aiProcessed }} 个未查询，可再次点击 AI 补漏重试。
        </span>
      </div>
      <div class="ai-pricing-table-wrap">
        <table class="table ai-pricing-table">
          <thead>
            <tr>
              <th>模型</th>
              <th style="width: 64px">类型</th>
              <th>输入 $/M（旧 → 新）</th>
              <th>输出 $/M（旧 → 新）</th>
              <th>缓存读 $/M（旧 → 新）</th>
              <th>备注</th>
            </tr>
          </thead>
          <tbody>
            <tr v-if="aiRows.length === 0">
              <td colspan="6" class="ai-pricing-empty">
                {{ aiRunning ? '正在查询第一批…' : '尚未开始' }}
              </td>
            </tr>
            <tr v-for="row in aiRows" :key="row.id">
              <td>
                <span class="ai-pricing-id">{{ row.id }}</span>
              </td>
              <td>
                <NTag size="tiny" :type="aiKindTagType(row.kind)" :bordered="false">
                  {{ kindLabel(row.kind) }}
                </NTag>
              </td>
              <td>{{ row.after ? formatPriceChange(row.before?.input, row.after.input) : '—' }}</td>
              <td>{{ row.after ? formatPriceChange(row.before?.output, row.after.output) : '—' }}</td>
              <td>{{ row.after ? formatPriceChange(row.before?.cacheRead, row.after.cacheRead) : '—' }}</td>
              <td class="ai-pricing-error-cell">{{ row.error || '' }}</td>
            </tr>
          </tbody>
        </table>
      </div>

      <template #footer>
        <NSpace justify="end" class="ai-pricing-actions">
          <NButton :disabled="aiRunning" @click="showAiModal = false">关闭</NButton>
          <NButton
            v-if="aiFillCount > 0 && !aiRunning && aiPhase !== 'source'"
            secondary
            type="warning"
            @click="handleAiFillMissing"
          >
            {{ aiSourceError ? 'AI 兜底查询（全部 ' + aiPlan.length + ' 个）' : 'AI 补漏（' + aiFillCount + ' 个）' }}
          </NButton>
          <NButton
            v-if="aiHasChanges"
            type="primary"
            :loading="aiApplying"
            :disabled="aiRunning"
            @click="handleApplyAiPricing"
          >
            应用到价格表（新增 {{ aiAddedCount }} / 更新 {{ aiUpdatedCount }}）
          </NButton>
        </NSpace>
      </template>
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

.ai-pricing-summary {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 10px;
}

.ai-pricing-progress {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 10px;
}

.ai-pricing-progress-text {
  font-size: 12px;
  white-space: nowrap;
  opacity: 0.75;
}

.ai-pricing-current {
  font-size: 12px;
  color: var(--n-text-color-3, inherit);
  opacity: 0.85;
  margin-bottom: 8px;
}

.ai-pricing-empty {
  text-align: center;
  opacity: 0.6;
  padding: 18px 0;
}

.ai-pricing-error-cell {
  max-width: 240px;
  overflow: hidden;
  text-overflow: ellipsis;
  color: rgba(255, 120, 120, 0.9);
}

.ai-pricing-source-error {
  margin-bottom: 8px;
  padding: 6px 10px;
  border: 1px solid rgba(255, 120, 120, 0.45);
  border-radius: 6px;
  font-size: 12px;
  color: rgba(255, 120, 120, 0.95);
}

.ai-pricing-note {
  font-size: 12px;
  opacity: 0.65;
}

.ai-pricing-table-wrap {
  max-height: 55vh;
  overflow-y: auto;
  border: 1px solid var(--border-color-global, rgba(127, 127, 127, 0.25));
  border-radius: 6px;
}

.ai-pricing-table th,
.ai-pricing-table td {
  padding: 6px 10px;
  font-size: 12px;
  text-align: left;
  white-space: nowrap;
}

.ai-pricing-id {
  font-family: var(--font-mono, monospace);
}

.ai-pricing-actions {
  margin-top: 12px;
}

.cleanup-help {
  margin: 0 0 10px;
  font-size: 12px;
  opacity: 0.85;
}
</style>

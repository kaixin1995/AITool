<script setup lang="ts">
import { computed, h, onBeforeUnmount, onMounted, ref } from 'vue'
import { NButton, NCard, NDataTable, NEmpty, NGrid, NGi, NInput, NPagination, NSpace, NSpin, NStatistic, NSwitch, NTag, type DataTableColumns } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import { listRouteFallbackEvents, type RouteFallbackEvent, type RouteFallbackSummary } from '@/api/routeFallback'

const page = ref(1)
const pageSize = 20
const modelKeyword = ref('')
const reasonKeyword = ref('')
const autoRefresh = ref(true)
const loading = ref(false)
const error = ref('')
const rows = ref<RouteFallbackEvent[]>([])
const totalCount = ref(0)
const totalPages = ref(0)
const summary = ref<RouteFallbackSummary>({ totalCount: 0, uniqueFromSites: 0, uniqueToSites: 0, latestOccurredAt: null })
const sampleLogLimit = ref(5000)
const sampleOldestRequestedAt = ref<string | null>(null)
const sampleTruncated = ref(false)
let refreshTimer: number | undefined
let refreshRunning = false

const summaryCards = computed(() => [
  { label: '样本内回退数', value: summary.value.totalCount },
  { label: '涉及源站点', value: summary.value.uniqueFromSites },
  { label: '涉及目标站点', value: summary.value.uniqueToSites },
  { label: '最近回退时间', value: formatDateTime(summary.value.latestOccurredAt) }
])

const sampleDescription = computed(() => {
  const oldest = sampleOldestRequestedAt.value
    ? `，最早采样至 ${formatDateTime(sampleOldestRequestedAt.value)}`
    : ''
  const truncated = sampleTruncated.value ? '，已达到采样上限，并非完整历史统计' : ''
  return `基于最近 ${sampleLogLimit.value} 条 UsageLogs 重建${oldest}${truncated}。摘要与表格均按当前筛选条件统计。`
})

const paginationText = computed(() => {
  if (totalCount.value === 0) return '共 0 条记录'
  const start = (page.value - 1) * pageSize + 1
  const end = Math.min(page.value * pageSize, totalCount.value)
  return `第 ${start}-${end} 条，共 ${totalCount.value} 条`
})

const columns: DataTableColumns<RouteFallbackEvent> = [
  { title: '发生时间', key: 'occurredAt', width: 180, render: (row) => h('span', { class: 'route-fallback-time' }, formatDateTime(row.occurredAt)) },
  { title: '请求模型', key: 'requestModel', width: 150, render: (row) => h('code', row.requestModel || '-') },
  { title: '源站点', key: 'fromSite', width: 210, render: (row) => renderSite(row.fromSiteName, row.fromSiteModelName, row.fromSiteId) },
  { title: '', key: 'arrow', width: 48, align: 'center', render: () => h('span', { class: 'route-fallback-arrow' }, '→') },
  { title: '目标站点', key: 'toSite', width: 210, render: (row) => renderSite(row.toSiteName, row.toSiteModelName, row.toSiteId) },
  { title: '回退原因', key: 'reason', minWidth: 260, ellipsis: { tooltip: true } }
]

function renderSite(siteName: string, modelName: string, siteId: string) {
  return h('div', { class: 'route-fallback-site' }, [
    h('span', { class: 'route-fallback-site-name' }, siteName || '-'),
    h('span', { class: 'route-fallback-site-model' }, modelName || '-'),
    h('small', { class: 'route-fallback-site-id', title: siteId }, `${siteId.slice(0, 8)}…`)
  ])
}

function formatDateTime(value: string | null | undefined): string {
  if (!value) return '-'
  return new Date(value).toLocaleString('zh-CN', {
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false
  })
}

async function loadList(targetPage = page.value): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    page.value = targetPage
    const data = await listRouteFallbackEvents({
      page: page.value,
      pageSize,
      modelKeyword: modelKeyword.value.trim(),
      reasonKeyword: reasonKeyword.value.trim()
    })
    rows.value = data.items
    page.value = data.page
    totalCount.value = data.totalCount
    totalPages.value = data.totalPages
    summary.value = data.summary
    sampleLogLimit.value = data.sampleLogLimit
    sampleTruncated.value = data.isTruncated
    sampleOldestRequestedAt.value = data.sampleOldestRequestedAt
  } catch (e) {
    error.value = (e as Error).message
    rows.value = []
    totalCount.value = 0
    totalPages.value = 0
  } finally {
    loading.value = false
  }
}

async function refreshAll(targetPage = page.value): Promise<void> {
  if (refreshRunning) return
  refreshRunning = true
  try {
    await loadList(targetPage)
  } finally {
    refreshRunning = false
  }
}

function handleQuery(): void {
  void refreshAll(1)
}

function handleReset(): void {
  modelKeyword.value = ''
  reasonKeyword.value = ''
  void refreshAll(1)
}

function restartAutoRefresh(): void {
  if (refreshTimer) {
    window.clearInterval(refreshTimer)
    refreshTimer = undefined
  }
  if (autoRefresh.value) {
    refreshTimer = window.setInterval(() => { void refreshAll() }, 5000)
  }
}

onMounted(() => {
  void refreshAll(1)
  restartAutoRefresh()
})

onBeforeUnmount(() => {
  if (refreshTimer) window.clearInterval(refreshTimer)
})
</script>

<template>
  <div class="page-container route-fallback-page">
    <PageHeader title="路由回退" subtitle="基于近期调用日志样本重建路由回退事件，诊断模型站点故障与降级情况" />
    <div class="route-fallback-sample-note">{{ sampleDescription }}</div>

    <NCard class="route-fallback-filter" title="筛选条件">
      <template #header-extra>
        <NSpace align="center" :wrap="false">
          <span class="route-fallback-auto-label">自动刷新</span>
          <NSwitch v-model:value="autoRefresh" @update:value="restartAutoRefresh" />
        </NSpace>
      </template>
      <div class="route-fallback-filter-grid">
        <label class="route-fallback-field">
          <span>模型关键字</span>
          <NInput v-model:value="modelKeyword" clearable placeholder="按模型名筛选" @keyup.enter="handleQuery" />
        </label>
        <label class="route-fallback-field">
          <span>回退原因</span>
          <NInput v-model:value="reasonKeyword" clearable placeholder="按原因筛选" @keyup.enter="handleQuery" />
        </label>
        <NSpace class="route-fallback-actions" align="end" :wrap="false">
          <NButton type="primary" @click="handleQuery">查询</NButton>
          <NButton @click="handleReset">重置</NButton>
        </NSpace>
      </div>
    </NCard>

    <NGrid :cols="4" :x-gap="16" :y-gap="16" responsive="screen" item-responsive class="route-fallback-summary">
      <NGi v-for="card in summaryCards" :key="card.label" span="4 m:2 l:1">
        <NCard class="route-fallback-summary-card">
          <NStatistic :label="card.label" :value="card.value" />
        </NCard>
      </NGi>
    </NGrid>

    <NCard content-style="padding: 0;" class="route-fallback-table-card">
      <NSpin :show="loading">
        <NDataTable
          :columns="columns"
          :data="rows"
          :bordered="false"
          :single-line="false"
          :scroll-x="1060"
          size="small"
          remote
        >
          <template #empty>
            <NEmpty :description="error || '暂无回退事件记录'" />
          </template>
        </NDataTable>
      </NSpin>
      <div class="route-fallback-table-footer">
        <span class="route-fallback-pagination-info">{{ paginationText }}</span>
        <NPagination v-if="totalPages > 1" v-model:page="page" :page-count="totalPages" size="small" @update:page="refreshAll" />
        <NTag v-else size="small" :bordered="false">第 1 页</NTag>
      </div>
    </NCard>
  </div>
</template>

<style scoped>
.route-fallback-page {
  min-width: 0;
}

.route-fallback-sample-note {
  margin: -6px 0 16px;
  color: var(--text-color-secondary, #64748b);
  font-size: 13px;
}

.route-fallback-filter {
  margin-bottom: 16px;
}

.route-fallback-filter-grid {
  display: grid;
  grid-template-columns: minmax(180px, 1fr) minmax(180px, 1fr) auto;
  gap: 16px;
  align-items: end;
}

.route-fallback-field {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
  font-size: 13px;
  color: var(--text-color-secondary);
}

.route-fallback-actions {
  padding-bottom: 1px;
}

.route-fallback-auto-label,
.route-fallback-pagination-info,
.route-fallback-time,
.route-fallback-site-id {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.route-fallback-summary {
  margin-bottom: 16px;
}

.route-fallback-summary-card {
  height: 100%;
}

.route-fallback-table-card {
  overflow: hidden;
}

.route-fallback-site {
  display: grid;
  gap: 2px;
  min-width: 0;
}

.route-fallback-site-name,
.route-fallback-site-model {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.route-fallback-site-name {
  font-weight: 600;
  color: var(--text-primary);
}

.route-fallback-site-model {
  font-size: 12px;
  color: var(--text-color-secondary);
}

.route-fallback-arrow {
  color: var(--text-color-secondary);
  font-size: 16px;
}

.route-fallback-table-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 12px 16px;
  border-top: 1px solid var(--border-color-global);
}

@media (max-width: 768px) {
  .route-fallback-filter-grid {
    grid-template-columns: 1fr;
  }

  .route-fallback-actions,
  .route-fallback-actions :deep(.n-space) {
    width: 100%;
  }

  .route-fallback-table-footer {
    align-items: flex-start;
    flex-direction: column;
  }
}
</style>

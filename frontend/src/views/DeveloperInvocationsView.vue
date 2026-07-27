<script setup lang="ts">
import { computed, h, onMounted, onUnmounted, ref } from 'vue'
import { NCard, NSpace, NButton, NDataTable, NTag, NStatistic, NModal, NTabs, NTabPane, useMessage, type DataTableColumns } from 'naive-ui'
import * as api from '@/api/developer'
import type { DeveloperInvocationSummary, DeveloperConcurrencyItem } from '@/api/developer'

const message = useMessage()
const loading = ref(false)
const entries = ref<DeveloperInvocationSummary[]>([])
const totalCount = ref(0)
const failedCount = ref(0)
const pendingCount = ref(0)
const concurrency = ref<DeveloperConcurrencyItem[]>([])
const detailVisible = ref(false)
const detail = ref<unknown>(null)
let pollTimer: ReturnType<typeof setInterval> | null = null

async function load(): Promise<void> {
  loading.value = true
  try {
    const [listResp, concResp] = await Promise.all([api.getDeveloperList(), api.getDeveloperConcurrency()])
    entries.value = listResp.entries ?? []
    totalCount.value = listResp.totalCount
    failedCount.value = listResp.failedCount
    pendingCount.value = listResp.pendingCount
    concurrency.value = concResp.items ?? []
  } catch {
    // 功能开关关闭时会 404，忽略。
  } finally { loading.value = false }
}

async function openDetail(traceId: string): Promise<void> {
  detail.value = await api.getDeveloperDetail(traceId, true)
  detailVisible.value = true
}

const columns = computed<DataTableColumns<DeveloperInvocationSummary>>(() => [
  { title: '时间', key: 'createdAt', width: 160, render: (r) => new Date(r.createdAt).toLocaleString('zh-CN') },
  { title: '来源', key: 'source', width: 90 },
  { title: '路径', key: 'requestPath', minWidth: 140, ellipsis: { tooltip: true } },
  { title: '模型', key: 'requestModel', minWidth: 120, ellipsis: { tooltip: true } },
  { title: '站点', key: 'targetSiteName', minWidth: 100 },
  { title: '状态', key: 'status', width: 90, render: (r) => h(NTag, { size: 'small', type: r.status === 'success' ? 'success' : r.status === 'pending' ? 'warning' : 'error', bordered: false }, () => r.status) },
  { title: '耗时(ms)', key: 'totalDurationMs', width: 100 },
  { title: '操作', key: 'actions', width: 80, render: (r) => h(NButton, { size: 'tiny', quaternary: true, onClick: () => openDetail(r.traceId) }, () => '详情') }
])

const concColumns = computed<DataTableColumns<DeveloperConcurrencyItem>>(() => [
  { title: '站点', key: 'siteName', minWidth: 120 },
  { title: '模型', key: 'modelName', minWidth: 120 },
  { title: '活跃', key: 'activeCount', width: 80, render: (r) => h(NTag, { size: 'small', type: r.activeCount > 0 ? 'info' : 'default', bordered: false }, () => r.activeCount) },
  { title: '排队', key: 'queueCount', width: 80, render: (r) => h(NTag, { size: 'small', type: r.queueCount > 0 ? 'warning' : 'default', bordered: false }, () => r.queueCount) },
  { title: '上限', key: 'maxConcurrency', width: 80, render: (r) => r.maxConcurrency ?? '不限' }
])

onMounted(() => {
  load()
  // 5 秒轮询刷新（页面激活时）。
  pollTimer = setInterval(() => {
    if (document.visibilityState === 'visible') load()
  }, 5000)
})
onUnmounted(() => { if (pollTimer) clearInterval(pollTimer) })
</script>

<template>
  <div class="page-container">
    <NTabs type="line" animated>
      <NTabPane name="invocations" tab="调用记录">
        <NCard>
          <template #header>
            <NSpace justify="space-between" align="center">
              <NSpace :size="24">
                <NStatistic label="总数" :value="totalCount" />
                <NStatistic label="失败" :value="failedCount" />
                <NStatistic label="等待" :value="pendingCount" />
              </NSpace>
              <NButton size="small" @click="load">刷新</NButton>
            </NSpace>
          </template>
          <NDataTable :columns="columns" :data="entries" :loading="loading" :row-key="(r: DeveloperInvocationSummary) => r.traceId" size="small" />
        </NCard>
      </NTabPane>
      <NTabPane name="concurrency" tab="并发面板">
        <NCard>
          <NDataTable :columns="concColumns" :data="concurrency" :row-key="(r: DeveloperConcurrencyItem) => r.siteId + r.modelName" size="small" />
        </NCard>
      </NTabPane>
    </NTabs>

    <NModal v-model:show="detailVisible" title="调用详情" preset="card" style="width: 800px; max-width: 95vw">
      <pre style="max-height: 60vh; overflow: auto; font-size: 12px">{{ JSON.stringify(detail, null, 2) }}</pre>
    </NModal>
  </div>
</template>

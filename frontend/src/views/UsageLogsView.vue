<script setup lang="ts">
import { computed, h, onMounted, reactive, ref } from 'vue'
import { NCard, NSpace, NDataTable, NTag, NSelect, NDatePicker, NButton, useMessage, type DataTableColumns } from 'naive-ui'
import * as api from '@/api/usageLogs'
import type { UsageLogItem } from '@/api/usageLogs'

const message = useMessage()
const loading = ref(false)
const items = ref<UsageLogItem[]>([])
const totalCount = ref(0)
const filters = ref<api.UsageLogFilters>({ sites: [], accessKeys: [] })

const query = reactive({
  page: 1, pageSize: 20, rangeType: 'day',
  siteId: null as string | null, accessKeyId: null as string | null,
  source: null as string | null, status: null as string | null,
  startTime: null as number | null, endTime: null as number | null
})

const siteOptions = computed(() => filters.value.sites.map((s) => ({ label: s.name, value: s.id })))
const keyOptions = computed(() => filters.value.accessKeys.map((k) => ({ label: k.name, value: k.id })))
const sourceOptions = [
  { label: '全部', value: null }, { label: '代理', value: 'proxy' }, { label: '对话', value: 'chat' }, { label: '检测', value: 'detection-task' }
]
const statusOptions = [{ label: '全部', value: null }, { label: '成功', value: 'success' }, { label: '失败', value: 'fail' }]
const rangeOptions = [{ label: '今天', value: 'day' }, { label: '本周', value: 'week' }, { label: '本月', value: 'month' }, { label: '自定义', value: 'custom' }, { label: '全部', value: 'all' }]

async function loadFilters(): Promise<void> {
  filters.value = await api.getUsageLogFilters()
}

async function load(): Promise<void> {
  loading.value = true
  try {
    const params: Record<string, unknown> = { page: query.page, pageSize: query.pageSize, rangeType: query.rangeType }
    if (query.siteId) params.siteId = query.siteId
    if (query.accessKeyId) params.accessKeyId = query.accessKeyId
    if (query.source) params.source = query.source
    if (query.status) params.status = query.status
    if (query.rangeType === 'custom' && query.startTime) params.startTime = new Date(query.startTime).toISOString()
    if (query.rangeType === 'custom' && query.endTime) params.endTime = new Date(query.endTime).toISOString()
    const resp = await api.listUsageLogs(params)
    items.value = resp.items ?? []
    totalCount.value = resp.totalCount ?? 0
  } catch (e) {
    message.error((e as Error).message)
  } finally { loading.value = false }
}

const columns = computed<DataTableColumns<UsageLogItem>>(() => [
  { title: '时间', key: 'requestedAt', width: 160, render: (r) => new Date(r.requestedAt).toLocaleString('zh-CN') },
  { title: '模型', key: 'requestModel', minWidth: 120, ellipsis: { tooltip: true } },
  { title: '尝试模型', key: 'attemptedModel', minWidth: 120, ellipsis: { tooltip: true } },
  { title: '来源', key: 'source', width: 90 },
  { title: '状态', key: 'status', width: 80, render: (r) => h(NTag, { size: 'small', type: r.status === 'success' ? 'success' : 'error', bordered: false }, () => r.status === 'success' ? '成功' : '失败') },
  { title: '重试', key: 'retryCount', width: 60 },
  { title: '输入', key: 'inputTokens', width: 80 },
  { title: '缓存', key: 'cachedTokens', width: 80 },
  { title: '输出', key: 'outputTokens', width: 80 },
  { title: '总耗时(ms)', key: 'totalDurationMs', width: 100 },
  { title: '流式', key: 'isStreaming', width: 70, render: (r) => r.isStreaming ? (r.isStreamInterrupted ? h(NTag, { size: 'tiny', type: 'warning', bordered: false }, () => '中断') : '是') : '否' }
])

onMounted(async () => {
  await loadFilters()
  await load()
})
</script>

<template>
  <div class="page-container">
    <NCard>
      <template #header>使用日志（共 {{ totalCount }} 条）</template>
      <NSpace :size="12" style="margin-bottom: 16px" wrap>
        <NSelect v-model:value="query.rangeType" :options="rangeOptions" placeholder="时间范围" style="width: 120px" />
        <template v-if="query.rangeType === 'custom'">
          <NDatePicker v-model:value="query.startTime" type="datetime" placeholder="开始时间" />
          <NDatePicker v-model:value="query.endTime" type="datetime" placeholder="结束时间" />
        </template>
        <NSelect v-model:value="query.siteId" :options="siteOptions" placeholder="站点" clearable style="width: 160px" />
        <NSelect v-model:value="query.accessKeyId" :options="keyOptions" placeholder="密钥" clearable style="width: 160px" />
        <NSelect v-model:value="query.source" :options="sourceOptions" placeholder="来源" clearable style="width: 120px" />
        <NSelect v-model:value="query.status" :options="statusOptions" placeholder="状态" clearable style="width: 120px" />
        <NButton type="primary" @click="query.page = 1; load()">查询</NButton>
      </NSpace>
      <NDataTable :columns="columns" :data="items" :loading="loading" :row-key="(r: UsageLogItem) => r.id" :scroll-x="1200" remote :pagination="{
        page: query.page, pageSize: query.pageSize, itemCount: totalCount,
        onUpdatePage: (p: number) => { query.page = p; load() }
      }" striped size="small" />
    </NCard>
  </div>
</template>

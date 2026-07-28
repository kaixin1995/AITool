<script setup lang="ts">
import { computed, h, onMounted, reactive, ref } from 'vue'
import { NCard, NSpace, NDataTable, NTag, NSelect, NDatePicker, NButton, NStatistic, NGrid, NGi, useMessage, type DataTableColumns, type SelectOption } from 'naive-ui'
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
const sourceOptions: SelectOption[] = [
  { label: '全部', value: '' }, { label: '代理', value: 'proxy' }, { label: '对话', value: 'chat' }, { label: '检测', value: 'detection-task' }
]
const statusOptions: SelectOption[] = [{ label: '全部', value: '' }, { label: '成功', value: 'success' }, { label: '失败', value: 'fail' }]
const rangeOptions: SelectOption[] = [{ label: '今天', value: 'day' }, { label: '本周', value: 'week' }, { label: '本月', value: 'month' }, { label: '自定义', value: 'custom' }, { label: '全部', value: 'all' }]

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

// 本页汇总（与原 Razor Pages 的 4 个汇总卡片对齐）。
const summary = computed(() => {
  const list = items.value
  const success = list.filter((x) => x.status === 'success').length
  const failed = list.length - success
  const successRate = list.length === 0 ? 0 : Math.round((success * 100) / list.length)
  const totalTokens = list.reduce((s, x) => s + x.totalTokens, 0)
  return { total: totalCount.value, successRate, totalTokens, failed }
})

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
    <!-- 汇总卡片（对齐原设计：总请求/成功率/总Tokens/失败请求） -->
    <NGrid :cols="4" :x-gap="12" :y-gap="12" responsive="screen" item-responsive style="margin-bottom: 16px">
      <NGi span="4 m:2 l:1">
        <NCard size="small"><NStatistic label="总请求" :value="summary.total" /></NCard>
      </NGi>
      <NGi span="4 m:2 l:1">
        <NCard size="small"><NStatistic label="成功率" :value="`${summary.successRate}%`" /></NCard>
      </NGi>
      <NGi span="4 m:2 l:1">
        <NCard size="small"><NStatistic label="总 Tokens（本页）" :value="summary.totalTokens" /></NCard>
      </NGi>
      <NGi span="4 m:2 l:1">
        <NCard size="small"><NStatistic label="失败请求（本页）" :value="summary.failed" /></NCard>
      </NGi>
    </NGrid>

    <NCard>
      <template #header>
        <NSpace align="center" :size="12" wrap>
          <span>使用日志</span>
          <NSelect v-model:value="query.rangeType" :options="rangeOptions" placeholder="时间范围" size="small" style="width: 110px" />
          <template v-if="query.rangeType === 'custom'">
            <NDatePicker v-model:value="query.startTime" type="datetime" placeholder="开始时间" size="small" />
            <NDatePicker v-model:value="query.endTime" type="datetime" placeholder="结束时间" size="small" />
          </template>
          <NSelect v-model:value="query.siteId" :options="siteOptions" placeholder="站点" clearable size="small" style="width: 150px" />
          <NSelect v-model:value="query.accessKeyId" :options="keyOptions" placeholder="密钥" clearable size="small" style="width: 150px" />
          <NSelect v-model:value="query.source" :options="sourceOptions" placeholder="来源" clearable size="small" style="width: 110px" />
          <NSelect v-model:value="query.status" :options="statusOptions" placeholder="状态" clearable size="small" style="width: 110px" />
          <NButton type="primary" size="small" @click="query.page = 1; load()">查询</NButton>
        </NSpace>
      </template>
      <NDataTable :columns="columns" :data="items" :loading="loading" :row-key="(r: UsageLogItem) => r.id" :scroll-x="1200" remote :pagination="{
        page: query.page, pageSize: query.pageSize, itemCount: totalCount,
        onUpdatePage: (p: number) => { query.page = p; load() }
      }" striped size="small" />
    </NCard>
  </div>
</template>

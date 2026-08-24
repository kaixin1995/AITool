<script setup lang="ts">
import { computed, h, onMounted, onUnmounted, ref } from 'vue'
import {
  NAlert,
  NButton,
  NCard,
  NDataTable,
  NSpace,
  NTooltip,
  type DataTableColumns
} from 'naive-ui'
import { getDeveloperConcurrency, type DeveloperConcurrencyItem } from '@/api/developer'

const props = defineProps<{
  embedded?: boolean
}>()

const concurrency = ref<DeveloperConcurrencyItem[]>([])
const loading = ref(false)
const error = ref('')
const refreshedAt = ref('')
let timer: ReturnType<typeof setInterval> | null = null

async function loadConcurrency(): Promise<void> {
  if (loading.value) return
  loading.value = true
  error.value = ''
  try {
    const data = await getDeveloperConcurrency()
    concurrency.value = Array.isArray(data.items) ? data.items : []
    refreshedAt.value = data.refreshedAt || new Date().toLocaleTimeString('zh-CN')
  } catch (err: any) {
    error.value = err?.message || '获取并发监控数据失败'
  } finally {
    loading.value = false
  }
}

const columns = computed<DataTableColumns<DeveloperConcurrencyItem>>(() => [
  { title: '模型名称', key: 'modelName', minWidth: 180, ellipsis: { tooltip: true } },
  { title: '目标站点', key: 'siteName', minWidth: 160, ellipsis: { tooltip: true } },
  {
    title: '活跃并发',
    key: 'activeCount',
    width: 110,
    align: 'right',
    render: (r) =>
      h(
        'span',
        {
          class: [
            'concurrency-badge',
            r.activeCount > 0 ? 'is-active' : 'is-idle'
          ]
        },
        r.activeCount
      )
  },
  {
    title: '最大并发限制',
    key: 'maxConcurrency',
    width: 130,
    align: 'right',
    render: (r) => (r.maxConcurrency ? `${r.maxConcurrency}` : '不限')
  },
  {
    title: '排队等待数',
    key: 'queueCount',
    width: 110,
    align: 'right',
    render: (r) =>
      h(
        'span',
        {
          class: [
            'concurrency-badge',
            r.queueCount > 0 ? 'is-queued' : 'is-idle'
          ]
        },
        r.queueCount
      )
  }
])

function handleVisibilityChange(): void {
  if (document.visibilityState === 'visible') {
    void loadConcurrency()
  }
}

onMounted(() => {
  void loadConcurrency()
  timer = setInterval(() => {
    if (document.visibilityState === 'visible') {
      void loadConcurrency()
    }
  }, 4000)
  document.addEventListener('visibilitychange', handleVisibilityChange)
})

onUnmounted(() => {
  if (timer) {
    clearInterval(timer)
    timer = null
  }
  document.removeEventListener('visibilitychange', handleVisibilityChange)
})
</script>

<template>
  <div class="concurrency-monitor-tab flex flex-col gap-4">
    <div class="flex items-center justify-between gap-4 p-4 rounded-xl border border-slate-200/80 dark:border-slate-800 bg-gradient-to-r from-slate-50 to-blue-50/40 dark:from-slate-900 dark:to-slate-800/60 flex-wrap">
      <div class="flex flex-col gap-1">
        <div class="flex items-center gap-2">
          <span class="font-bold text-base text-slate-800 dark:text-slate-100">站点模型实时并发与排队监控</span>
          <NTooltip trigger="hover">
            <template #trigger>
              <span class="inline-flex items-center justify-center w-4 h-4 rounded-full bg-slate-200 dark:bg-slate-700 text-slate-500 text-xs cursor-help">?</span>
            </template>
            展示最近 6 小时内发生过请求调用的站点模型，实时同步显示活跃并发数、最大并发配额与排队等待中的请求数。
          </NTooltip>
        </div>
        <div class="text-xs text-slate-500">
          每 4 秒自动轮询刷新 · 最近更新时间：{{ refreshedAt || '-' }}
        </div>
      </div>

      <div class="flex items-center gap-2.5">
        <NButton size="small" secondary :loading="loading" @click="loadConcurrency">
          立即刷新
        </NButton>
      </div>
    </div>

    <NAlert v-if="error" type="error" :show-icon="false">
      {{ error }}
    </NAlert>

    <NCard :content-style="{ padding: 0 }">
      <NDataTable
        :columns="columns"
        :data="concurrency"
        :loading="loading"
        :row-key="(r: DeveloperConcurrencyItem) => r.concurrencyKey || `${r.siteId}:${r.modelName}`"
        :pagination="{ pageSize: 20 }"
        :scroll-x="760"
        size="small"
      />
    </NCard>
  </div>
</template>

<style scoped>
.concurrency-monitor-tab {
  min-width: 0;
}

.concurrency-badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 46px;
  min-height: 26px;
  padding: 4px 10px;
  border-radius: 999px;
  font-weight: 700;
  font-size: 12px;
}

.concurrency-badge.is-idle {
  background: var(--bg-surface-soft, rgba(0, 0, 0, 0.04));
  color: var(--text-color-secondary, #94a3b8);
}

.concurrency-badge.is-active {
  background: #ecfdf5;
  color: #059669;
}

[data-theme='dark'] .concurrency-badge.is-active {
  background: rgba(5, 150, 105, 0.2);
  color: #34d399;
}

.concurrency-badge.is-queued {
  background: #fffbeb;
  color: #d97706;
}

[data-theme='dark'] .concurrency-badge.is-queued {
  background: rgba(217, 119, 6, 0.2);
  color: #fbbf24;
}
</style>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { NAlert, NButton, NCard, NDataTable, NEmpty, NSpace, type DataTableColumns } from 'naive-ui'
import { h } from 'vue'
import { httpGet } from '@/api/http'
import { gatewayError } from './developerGatewaySettings'

interface SessionUsage {
  session: string
  parent: string | null
  requests: number
  attempts: number
  inputTokens: number
  cachedTokens: number
  outputTokens: number
  costUsd: number
  unpricedAttempts: number
  requestBytes: number
  responseBytes: number
  switches: number
}
interface SessionDetail {
  requestId: string
  attemptIndex: number
  requestedAt: string
  attemptedModel: string
  status: string
  errorCategory: string | null
  contextTokens: number
  cacheShare: number
  outputTokens: number
  totalDurationMs: number
}
const loading = ref(false), detailLoading = ref(false), error = ref(''), detailError = ref('')
const metricsEnabled = ref<boolean | null>(null)
const coverage = ref<{ totalRequests: number; unidentifiedRequests: number } | null>(null)
const sessions = ref<SessionUsage[]>([])
const details = ref<SessionDetail[]>([])
const selectedSession = ref('')
const format = (value: number) => value.toLocaleString()
function bytes(value: number) {
  if (value < 1024) return `${format(value)} B`
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`
  return `${(value / 1024 / 1024).toFixed(1)} MB`
}
const columns: DataTableColumns<SessionUsage> = [
  { title: '会话摘要', key: 'session', width: 160, render: row => h(NButton, { text: true, type: 'primary', title: row.session, onClick: () => inspect(row.session) }, { default: () => row.session.slice(0, 12) }) },
  { title: '请求 / 尝试', key: 'requests', width: 130, render: row => `${format(row.requests)} / ${format(row.attempts)}` },
  { title: '输入 / 缓存 / 输出 Token', key: 'tokens', width: 230, render: row => `${format(row.inputTokens)} / ${format(row.cachedTokens)} / ${format(row.outputTokens)}` },
  { title: '参考金额', key: 'costUsd', width: 180, render: row => `$${row.costUsd.toFixed(4)}${row.unpricedAttempts ? '（有未定价）' : ''}` },
  { title: '上传 / 下载', key: 'requestBytes', width: 180, render: row => `${bytes(row.requestBytes)} / ${bytes(row.responseBytes)}` },
  { title: '站点切换', key: 'switches', width: 100 }
]
const detailColumns: DataTableColumns<SessionDetail> = [
  { title: '时间', key: 'requestedAt', width: 180, render: row => new Date(row.requestedAt).toLocaleString() },
  { title: '模型', key: 'attemptedModel', width: 160 },
  { title: '状态', key: 'status', width: 180, render: row => [row.status, row.errorCategory].filter(Boolean).join(' · ') },
  { title: '上下文 Token', key: 'contextTokens', width: 140, render: row => format(row.contextTokens) },
  { title: '缓存占比', key: 'cacheShare', width: 110, render: row => `${(row.cacheShare * 100).toFixed(1)}%` },
  { title: '输出 Token', key: 'outputTokens', width: 120, render: row => format(row.outputTokens) },
  { title: '耗时 ms', key: 'totalDurationMs', width: 120, render: row => format(row.totalDurationMs) }
]
async function load() {
  loading.value = true
  error.value = ''
  try {
    const [rows, settings, coverageResult] = await Promise.all([
      httpGet<SessionUsage[]>('/api/admin/gateway-enhancements/sessions', { skipErrorNotify: true }),
      httpGet<{ sessionMetricsEnabled: boolean }>('/api/admin/gateway-enhancements', { skipErrorNotify: true }),
      httpGet<{ totalRequests: number; unidentifiedRequests: number }>('/api/admin/gateway-enhancements/sessions/coverage', { skipErrorNotify: true })
    ])
    sessions.value = rows
    metricsEnabled.value = settings.sessionMetricsEnabled
    coverage.value = coverageResult
  } catch (e) { error.value = gatewayError(e) } finally { loading.value = false }
}
let detailVersion = 0
async function inspect(digest: string) {
  const version = ++detailVersion
  selectedSession.value = digest
  details.value = []
  detailLoading.value = true
  detailError.value = ''
  try {
    const rows = await httpGet<SessionDetail[]>(`/api/admin/gateway-enhancements/sessions/${encodeURIComponent(digest)}`, { skipErrorNotify: true })
    if (version === detailVersion) details.value = rows
  } catch (e) { if (version === detailVersion) detailError.value = gatewayError(e) } finally { if (version === detailVersion) detailLoading.value = false }
}
onMounted(load)
</script>

<template>
  <NSpace vertical :size="16">
    <NAlert type="info">汇总最近 7 天带明确会话标识的请求与重试。开启“系统设置 → 稳定性与会话统计”后开始记录；支持常见会话请求头和 Codex / Claude 请求体中的会话标识，只保存标识摘要，不保存对话正文。没有标识的请求仍可在使用日志中查看，不会被猜测合并。</NAlert>
    <NAlert v-if="metricsEnabled === false" type="warning">会话与流量统计当前已关闭，可在系统设置中开启。已有记录仍可查看。</NAlert>
    <NAlert v-if="coverage && coverage.unidentifiedRequests > 0" type="warning">最近 7 天记录了 {{ coverage.totalRequests }} 次请求，其中 {{ coverage.unidentifiedRequests }} 次未识别到会话标识，无法归入下方会话。历史请求不会自动补录。</NAlert>
    <NAlert v-if="error" type="error">{{ error }}</NAlert>
    <NSpace justify="space-between" align="center"><span>请求数为客户端请求，尝试数包括失败重试；参考金额按模型价格表估算。</span><NButton :loading="loading" @click="load">刷新会话用量</NButton></NSpace>
    <NDataTable v-if="sessions.length || loading" :columns="columns" :data="sessions" :loading="loading" :row-key="row => row.session" :scroll-x="1080" :pagination="{ pageSize: 20 }" />
    <NEmpty v-else description="暂无会话记录；请确认统计已开启且客户端传入了会话标识" />
    <NCard v-if="selectedSession" title="会话调用明细" size="small">
      <NSpace vertical :size="12">
        <div class="session-digest">{{ selectedSession }}</div>
        <NAlert v-if="detailError" type="error">{{ detailError }}</NAlert>
        <NDataTable :columns="detailColumns" :data="details" :loading="detailLoading" :row-key="row => `${row.requestId}:${row.attemptIndex}`" :scroll-x="1110" :pagination="{ pageSize: 20 }" />
      </NSpace>
    </NCard>
  </NSpace>
</template>

<style scoped>
.session-digest { color: var(--text-color-secondary); font-size: 12px; overflow-wrap: anywhere; }
</style>

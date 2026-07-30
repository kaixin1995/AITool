<script setup lang="ts">
import { computed, h, onMounted, onUnmounted, ref } from 'vue'
import {
  NButton,
  NCard,
  NDataTable,
  NEmpty,
  NSpace,
  NSwitch,
  NTabPane,
  NTabs,
  NTag,
  NTooltip,
  NPagination,
  useMessage,
  type DataTableColumns
} from 'naive-ui'
import * as api from '@/api/developer'
import type { DeveloperInvocationSummary, DeveloperConcurrencyItem } from '@/api/developer'
import PageHeader from '@/components/PageHeader.vue'
import ClientSimulator from './ClientSimulator.vue'

interface DeveloperInvocationAttempt {
  attemptId: string
  targetSiteName: string
  attemptedModel: string
  forwardingMode: string
  upstreamProtocolType: string
  status: string
  statusCode: number
  errorMessage: string
  preparedRequestBody: string
  responseBody: string
  responseContentType: string
  isStreaming: boolean
  inputTokens: number
  cachedTokens: number
  outputTokens: number
  totalDurationMs: number
}

interface DeveloperInvocationDetail {
  traceId: string
  requestId: string
  createdAt: string
  updatedAt: string
  source: string
  userAgent: string
  clientIp: string
  protocolType: string
  requestPath: string
  requestModel: string
  requestHeaders: Record<string, string>
  requestBody: string
  targetSiteName: string
  attemptedModel: string
  status: string
  statusCode: number
  errorMessage: string
  responseBody: string
  responseContentType: string
  isStreaming: boolean
  totalDurationMs: number
  inputTokens: number
  cachedTokens: number
  outputTokens: number
  attempts: DeveloperInvocationAttempt[]
}

const message = useMessage()
const loading = ref(false)
const autoRefresh = ref(false)
const summarizeDetail = ref(true)
const entries = ref<DeveloperInvocationSummary[]>([])
const totalCount = ref(0)
const failedCount = ref(0)
const pendingCount = ref(0)
const concurrency = ref<DeveloperConcurrencyItem[]>([])
const page = ref(1)
const pageSize = 40
const totalPages = ref(1)
const expandedTraceIds = ref<Set<string>>(new Set())
const details = ref<Record<string, DeveloperInvocationDetail>>({})
const detailLoading = ref<Record<string, boolean>>({})
let pollTimer: ReturnType<typeof setInterval> | null = null

function isPending(status: string): boolean {
  return status?.toLowerCase() === 'pending'
}

function isSuccess(status: string): boolean {
  const normalized = status?.toLowerCase()
  return normalized === 'success' || normalized === 'ok'
}

function statusLabel(status: string): string {
  if (isPending(status)) return '等待'
  if (isSuccess(status)) return '成功'
  return '失败'
}

function statusClass(status: string): string {
  if (isPending(status)) return 'pending'
  if (isSuccess(status)) return 'success'
  return 'danger'
}

function formatDateTime(value: string | null | undefined): string {
  if (!value) return '-'
  return new Date(value).toLocaleString('zh-CN')
}

function formatDuration(value: number | null | undefined): string {
  const number = Number(value ?? 0)
  if (!Number.isFinite(number) || number <= 0) return '-'
  if (number >= 1000) {
    const seconds = number / 1000
    return `${Math.abs(seconds - Math.round(seconds)) < 0.05 ? Math.round(seconds) : seconds.toFixed(1)}s`
  }
  return `${Math.round(number)}ms`
}

function formatNumber(value: number | null | undefined): string {
  const number = Number(value ?? 0)
  return Number.isFinite(number) ? number.toLocaleString('zh-CN') : '-'
}

function formatForwardingMode(value: string): string {
  const normalized = value?.trim().toLowerCase()
  if (normalized === 'direct') return '直接透传'
  if (normalized === 'bridge') return '兼容中转'
  return value || '-'
}

function bodyText(value: unknown): string {
  if (value === null || value === undefined || value === '') return '无'
  if (typeof value === 'string') return value
  return JSON.stringify(value, null, 2)
}

function configureAutoRefresh(): void {
  if (pollTimer) {
    clearInterval(pollTimer)
    pollTimer = null
  }
  if (autoRefresh.value) {
    pollTimer = setInterval(() => {
      if (document.visibilityState === 'visible') void load(false)
    }, 5000)
  }
}

async function load(showSpinner = true, targetPage = page.value): Promise<void> {
  if (showSpinner) loading.value = true
  try {
    const [listResp, concResp] = await Promise.all([api.getDeveloperList(targetPage, pageSize), api.getDeveloperConcurrency()])
    entries.value = listResp.entries ?? []
    page.value = listResp.page
    totalPages.value = listResp.totalPages || 1
    totalCount.value = listResp.totalCount
    failedCount.value = listResp.failedCount
    pendingCount.value = listResp.pendingCount
    concurrency.value = concResp.items ?? []
  } catch {
    // 功能开关关闭时会 404，忽略。
  } finally {
    if (showSpinner) loading.value = false
  }
}

function handleSummarizeChange(): void {
  details.value = {}
  expandedTraceIds.value = new Set()
}

async function toggleDetail(traceId: string): Promise<void> {
  const next = new Set(expandedTraceIds.value)
  if (next.has(traceId)) {
    next.delete(traceId)
    expandedTraceIds.value = next
    return
  }
  next.add(traceId)
  expandedTraceIds.value = next
  if (details.value[traceId]) return

  detailLoading.value = { ...detailLoading.value, [traceId]: true }
  try {
    const detail = await api.getDeveloperDetail(traceId, summarizeDetail.value) as DeveloperInvocationDetail
    details.value = { ...details.value, [traceId]: detail }
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    detailLoading.value = { ...detailLoading.value, [traceId]: false }
  }
}

async function copyText(text: string): Promise<void> {
  if (!text) return
  try {
    await navigator.clipboard.writeText(text)
    message.success('已复制到剪贴板')
  } catch {
    const textarea = document.createElement('textarea')
    textarea.value = text
    textarea.style.position = 'fixed'
    textarea.style.opacity = '0'
    document.body.appendChild(textarea)
    textarea.select()
    try {
      document.execCommand('copy')
      message.success('已复制到剪贴板')
    } catch {
      message.error('复制失败，请手动复制')
    } finally {
      document.body.removeChild(textarea)
    }
  }
}

const displayedEntries = computed(() => entries.value)
const paginationSummary = computed(() => {
  if (totalCount.value === 0) return '共 0 条记录'
  const start = (page.value - 1) * pageSize + 1
  const end = Math.min(page.value * pageSize, totalCount.value)
  return `显示第 ${formatNumber(start)}-${formatNumber(end)} 条，共 ${formatNumber(totalCount.value)} 条，一页最多 40 条`
})

const concColumns = computed<DataTableColumns<DeveloperConcurrencyItem>>(() => [
  { title: '模型名', key: 'modelName', minWidth: 180, ellipsis: { tooltip: true } },
  { title: '站点', key: 'siteName', minWidth: 160, ellipsis: { tooltip: true } },
  {
    title: '并发数',
    key: 'activeCount',
    width: 100,
    align: 'right',
    render: (r) => h('span', { class: ['concurrency-count-badge', r.activeCount > 0 ? 'is-active' : ''] }, r.activeCount)
  },
  { title: '最大并发', key: 'maxConcurrency', width: 110, align: 'right', render: (r) => r.maxConcurrency ?? '不限' },
  {
    title: '排队数',
    key: 'queueCount',
    width: 100,
    align: 'right',
    render: (r) => h('span', { class: ['concurrency-count-badge', r.queueCount > 0 ? 'is-queued' : ''] }, r.queueCount)
  }
])

onMounted(() => {
  void load()
  configureAutoRefresh()
})

onUnmounted(() => {
  if (pollTimer) clearInterval(pollTimer)
})
</script>

<template>
  <div class="page-container developer-invocations-page">
    <PageHeader title="调试工具" subtitle="调用调试、客户端模拟和开发者追踪" />

    <NCard class="developer-tools-card" :content-style="{ padding: '16px' }">
      <NTabs type="line" animated>
        <NTabPane name="invocations" tab="调用调试">
          <div class="trace-page-header">
            <div>
              <h2 class="pane-title">调用调试</h2>
              <p class="pane-subtitle">内存中最多保留最近 40 条记录，且仅保留 20 分钟；一页全部展示，不分页</p>
            </div>
            <div class="trace-toolbar">
              <label class="trace-refresh-switch">
                <NSwitch v-model:value="autoRefresh" size="small" @update:value="configureAutoRefresh" />
                <span>自动刷新（5 秒）</span>
              </label>
              <label class="trace-refresh-switch">
                <NSwitch v-model:value="summarizeDetail" size="small" @update:value="handleSummarizeChange" />
                <span>精简显示</span>
              </label>
              <NButton type="primary" :loading="loading" @click="load()">立即刷新</NButton>
            </div>
          </div>

          <div class="trace-overview-grid">
            <article class="trace-overview-card trace-overview-card-primary">
              <span class="trace-overview-label">当前记录数</span>
              <strong class="trace-overview-value">{{ formatNumber(totalCount) }}</strong>
              <span class="trace-overview-hint">最多 40 条，且仅保留 20 分钟</span>
            </article>
            <article class="trace-overview-card trace-overview-card-danger">
              <span class="trace-overview-label">失败 / 异常</span>
              <strong class="trace-overview-value">{{ formatNumber(failedCount) }}</strong>
              <span class="trace-overview-hint">优先检查红色告警记录</span>
            </article>
            <article class="trace-overview-card trace-overview-card-warning">
              <span class="trace-overview-label">等待返回</span>
              <strong class="trace-overview-value">{{ formatNumber(pendingCount) }}</strong>
              <span class="trace-overview-hint">请求已到达，但返回尚未补齐</span>
            </article>
          </div>

          <div v-if="loading && entries.length === 0" class="trace-loading-card">
            <div class="trace-loading-title">调用记录按需加载中</div>
            <div class="trace-loading-text">首次进入页面时先展示统计信息，详细列表会在后台懒加载，一页全部展示。</div>
          </div>

          <div v-if="displayedEntries.length" class="trace-accordion">
            <article
              v-for="entry in displayedEntries"
              :key="entry.traceId"
              class="trace-card"
              :class="`trace-card-${statusClass(entry.status)}`"
            >
              <button type="button" class="trace-card-toggle" @click="toggleDetail(entry.traceId)">
                <div class="trace-card-main">
                  <div class="trace-card-topline">
                    <div class="trace-title-group">
                      <span class="trace-status-pill" :class="`trace-status-pill-${statusClass(entry.status)}`">{{ statusLabel(entry.status) }}</span>
                      <span class="trace-protocol-pill">{{ entry.protocolType || '-' }}</span>
                      <span class="trace-source-pill">{{ entry.source || 'proxy' }}</span>
                    </div>
                    <span class="trace-summary-time">{{ formatDateTime(entry.createdAt) }}</span>
                  </div>
                  <div class="trace-card-title-row">
                    <div class="trace-model-pair">
                      <span class="trace-request-model">{{ entry.requestModel || '-' }}</span>
                      <span class="trace-model-arrow">→</span>
                      <span class="trace-attempted-model">{{ entry.attemptedModel || '-' }}</span>
                    </div>
                    <span class="trace-summary-time">{{ expandedTraceIds.has(entry.traceId) ? '收起详情' : '展开详情' }}</span>
                  </div>
                  <div class="trace-meta-grid">
                    <div class="trace-meta-chip"><span class="trace-meta-label">路径</span><strong>{{ entry.requestPath || '-' }}</strong></div>
                    <div class="trace-meta-chip"><span class="trace-meta-label">站点</span><strong>{{ entry.targetSiteName || '-' }}</strong></div>
                    <div class="trace-meta-chip"><span class="trace-meta-label">HTTP</span><strong>{{ entry.statusCode || '-' }}</strong></div>
                    <div class="trace-meta-chip"><span class="trace-meta-label">耗时</span><strong>{{ formatDuration(entry.totalDurationMs) }}</strong></div>
                    <div class="trace-meta-chip"><span class="trace-meta-label">尝试次数</span><strong>{{ entry.attemptCount }}</strong></div>
                  </div>
                </div>
              </button>

              <div v-if="expandedTraceIds.has(entry.traceId)" class="trace-card-body">
                <div v-if="detailLoading[entry.traceId]" class="trace-loading-text">调用详情加载中...</div>
                <template v-else-if="details[entry.traceId]">
                  <div class="trace-chain-tip">TraceId: {{ details[entry.traceId].traceId }} · RequestId: {{ details[entry.traceId].requestId || '-' }}</div>
                  <div class="trace-attempt-list">
                    <article
                      v-for="(attempt, index) in details[entry.traceId].attempts"
                      :key="attempt.attemptId || index"
                      class="trace-attempt-card"
                      :class="`trace-attempt-card-${statusClass(attempt.status)}`"
                    >
                      <div class="trace-attempt-head">
                        <span class="trace-attempt-order">第 {{ index + 1 }} 次尝试</span>
                        <span class="trace-status-pill" :class="`trace-status-pill-${statusClass(attempt.status)}`">{{ statusLabel(attempt.status) }}</span>
                      </div>
                      <div class="trace-attempt-route">
                        <strong>{{ attempt.attemptedModel || '-' }}</strong>
                        <span class="trace-attempt-site">{{ attempt.targetSiteName || '-' }}</span>
                      </div>
                      <div class="trace-meta-grid trace-meta-grid-attempt">
                        <div class="trace-meta-chip"><span class="trace-meta-label">转发方式</span><strong>{{ formatForwardingMode(attempt.forwardingMode) }}</strong></div>
                        <div class="trace-meta-chip"><span class="trace-meta-label">协议</span><strong>{{ attempt.upstreamProtocolType || '-' }}</strong></div>
                        <div class="trace-meta-chip"><span class="trace-meta-label">HTTP</span><strong>{{ attempt.statusCode || '-' }}</strong></div>
                        <div class="trace-meta-chip"><span class="trace-meta-label">耗时</span><strong>{{ formatDuration(attempt.totalDurationMs) }}</strong></div>
                        <div class="trace-meta-chip"><span class="trace-meta-label">输入/缓存/输出</span><strong>{{ formatNumber(attempt.inputTokens) }} / {{ formatNumber(attempt.cachedTokens) }} / {{ formatNumber(attempt.outputTokens) }}</strong></div>
                      </div>
                      <div v-if="attempt.errorMessage" class="trace-code-panel trace-code-panel-danger">
                        <div class="trace-section-title trace-section-title-danger">错误信息</div>
                        <pre class="trace-pre">{{ attempt.errorMessage }}</pre>
                      </div>
                    </article>
                  </div>

                  <div class="trace-error-stack">
                    <div class="trace-code-panel">
                      <div class="trace-panel-header">
                        <div class="trace-section-title">请求体</div>
                        <NButton size="tiny" secondary class="trace-copy-btn" @click="copyText(bodyText(details[entry.traceId].requestBody))">复制</NButton>
                      </div>
                      <pre class="trace-pre">{{ bodyText(details[entry.traceId].requestBody) }}</pre>
                    </div>
                    <div class="trace-code-panel">
                      <div class="trace-panel-header">
                        <div class="trace-section-title">响应体</div>
                        <NButton size="tiny" secondary class="trace-copy-btn" @click="copyText(bodyText(details[entry.traceId].responseBody))">复制</NButton>
                      </div>
                      <pre class="trace-pre">{{ bodyText(details[entry.traceId].responseBody) }}</pre>
                    </div>
                  </div>
                </template>
              </div>
            </article>
          </div>
          <NEmpty v-else-if="!loading" description="暂无调用记录" />

          <div v-if="totalPages > 1" class="trace-pagination-bar">
            <div class="trace-pagination-summary">{{ paginationSummary }}</div>
            <NPagination v-model:page="page" :page-count="totalPages" size="small" @update:page="(p) => load(true, p)" />
          </div>
        </NTabPane>

        <NTabPane name="simulator" tab="客户端模拟">
          <ClientSimulator />
        </NTabPane>

        <NTabPane name="concurrency" tab="当前模型并发数检测">
          <div class="concurrency-header">
            <h2 class="pane-title concurrency-title">
              <span>当前模型并发数检测</span>
              <NTooltip trigger="hover">
                <template #trigger><span class="concurrency-help-trigger">?</span></template>
                仅展示最近 6 小时内出现过的站点模型，并同步显示当前并发数、最大并发和排队数。
              </NTooltip>
            </h2>
            <div class="concurrency-refresh-tip">进入此页后自动刷新</div>
          </div>
          <NCard class="concurrency-table-card" :content-style="{ padding: 0 }">
            <NDataTable
              :columns="concColumns"
              :data="concurrency"
              :row-key="(r: DeveloperConcurrencyItem) => r.siteId + r.modelName"
              :pagination="{ pageSize: 20 }"
              :scroll-x="760"
              size="small"
            />
          </NCard>
        </NTabPane>
      </NTabs>
    </NCard>
  </div>
</template>

<style scoped>
.developer-invocations-page {
  min-width: 0;
}

.developer-tools-card {
  min-width: 0;
  overflow: hidden;
}

.developer-tools-card :deep(.n-card__content),
.concurrency-table-card :deep(.n-card__content) {
  min-width: 0;
  overflow: hidden;
}

.pane-title {
  margin: 0 0 4px;
  color: var(--text-primary);
  font-size: 20px;
  font-weight: 700;
}

.pane-subtitle,
.concurrency-refresh-tip,
.trace-loading-text,
.trace-overview-label,
.trace-overview-hint,
.trace-summary-time,
.trace-meta-label,
.trace-chain-tip,
.trace-attempt-site,
.trace-pagination-summary {
  color: var(--text-color-secondary);
}

.pane-subtitle {
  margin: 0;
  font-size: 13px;
}

.trace-page-header,
.concurrency-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  flex-wrap: wrap;
  margin-bottom: 16px;
}

.trace-toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
  justify-content: flex-end;
}

.trace-refresh-switch {
  display: inline-flex;
  align-items: center;
  gap: 10px;
  min-height: 42px;
  padding: 0 14px;
  border-radius: 999px;
  background: var(--bg-card);
  border: 1px solid rgba(148, 163, 184, 0.2);
  box-shadow: 0 8px 24px rgba(15, 23, 42, 0.05);
  margin: 0;
  cursor: pointer;
  user-select: none;
  font-size: 14px;
  color: var(--text-primary);
}

.trace-overview-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 16px;
  margin-bottom: 16px;
}

.trace-overview-card {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 20px 22px;
  background: var(--bg-card);
  border-radius: 18px;
  border: 1px solid rgba(226, 232, 240, 0.9);
  box-shadow: 0 10px 28px rgba(15, 23, 42, 0.05);
}

.trace-overview-card-primary {
  border-color: rgba(59, 130, 246, 0.18);
  background: linear-gradient(135deg, rgba(239, 246, 255, 0.92), var(--bg-card));
}

.trace-overview-card-danger {
  border-color: rgba(239, 68, 68, 0.2);
  background: linear-gradient(135deg, rgba(254, 242, 242, 0.95), var(--bg-card));
}

.trace-overview-card-warning {
  border-color: rgba(245, 158, 11, 0.2);
  background: linear-gradient(135deg, rgba(255, 251, 235, 0.95), var(--bg-card));
}

.trace-overview-value {
  color: var(--text-primary);
  font-size: 30px;
  line-height: 1;
  font-weight: 800;
}

.trace-loading-card {
  margin-bottom: 16px;
  padding: 16px 18px;
  border-radius: 18px;
  border: 1px solid rgba(59, 130, 246, 0.14);
  background: linear-gradient(135deg, rgba(239, 246, 255, 0.9), var(--bg-card));
  box-shadow: 0 10px 28px rgba(15, 23, 42, 0.05);
}

.trace-loading-title {
  margin-bottom: 6px;
  color: #1d4ed8;
  font-size: 14px;
  font-weight: 700;
}

.trace-accordion {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.trace-card {
  border: 1px solid rgba(226, 232, 240, 0.9);
  border-radius: 20px;
  overflow: hidden;
  box-shadow: 0 12px 32px rgba(15, 23, 42, 0.05);
}

.trace-card-success { border-left: 6px solid #22c55e; }
.trace-card-pending { border-left: 6px solid #f59e0b; background: linear-gradient(180deg, rgba(255, 251, 235, 0.4), var(--bg-card) 42%); }
.trace-card-danger { border-left: 6px solid #ef4444; background: linear-gradient(180deg, rgba(254, 242, 242, 0.55), var(--bg-card) 42%); box-shadow: 0 16px 36px rgba(239, 68, 68, 0.12); }

.trace-card-toggle {
  width: 100%;
  border: 0;
  background: transparent;
  padding: 18px 20px;
  text-align: left;
  cursor: pointer;
}

.trace-card-toggle:hover {
  background: rgba(248, 250, 252, 0.72);
}

.trace-card-main,
.trace-attempt-list {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.trace-card-topline,
.trace-card-title-row,
.trace-attempt-head,
.trace-attempt-route,
.trace-panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.trace-title-group,
.trace-model-pair {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.trace-status-pill,
.trace-protocol-pill,
.trace-source-pill,
.trace-attempt-order {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-height: 28px;
  padding: 6px 12px;
  border-radius: 999px;
  font-size: 12px;
  font-weight: 700;
  line-height: 1;
  white-space: nowrap;
}

.trace-status-pill-success { background: #e8f7ea; color: #166534; }
.trace-status-pill-pending { background: #fff5d6; color: #b45309; }
.trace-status-pill-danger { background: #fee2e2; color: #b91c1c; box-shadow: 0 0 0 3px rgba(239, 68, 68, 0.08); }
.trace-protocol-pill { background: #eff6ff; color: #1d4ed8; }
.trace-source-pill { background: #f8fafc; color: var(--text-color-secondary); }

.trace-request-model,
.trace-attempted-model {
  color: var(--text-primary);
  font-size: 18px;
  font-weight: 800;
  word-break: break-word;
}

.trace-attempted-model {
  color: #1d4ed8;
}

.trace-model-arrow {
  color: #94a3b8;
  font-size: 18px;
  font-weight: 700;
}

.trace-meta-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
  gap: 10px;
}

.trace-meta-chip {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
  padding: 12px 14px;
  border-radius: 14px;
  background: #f8fafc;
  border: 1px solid rgba(226, 232, 240, 0.95);
}

.trace-meta-chip strong {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.trace-card-body {
  padding: 0 20px 18px;
}

.trace-chain-tip {
  margin-bottom: 12px;
  font-size: 12px;
}

.trace-attempt-card {
  padding: 14px;
  border-radius: 16px;
  border: 1px solid rgba(226, 232, 240, 0.9);
  background: var(--bg-card);
}

.trace-attempt-card-success { border-left: 5px solid #22c55e; }
.trace-attempt-card-pending { border-left: 5px solid #f59e0b; background: #fffdf6; }
.trace-attempt-card-danger { border-left: 5px solid #ef4444; background: #fff7f7; }

.trace-attempt-order {
  background: #f1f5f9;
  color: #334155;
}

.trace-error-stack {
  display: grid;
  gap: 12px;
  min-width: 0;
  margin-top: 12px;
}

.trace-code-panel {
  min-width: 0;
  overflow: hidden;
  padding: 16px;
  border-radius: 16px;
  border: 1px solid rgba(226, 232, 240, 0.9);
  background: #f8fafc;
}

.trace-code-panel-danger {
  margin-top: 12px;
  background: #fff5f5;
  border-color: rgba(239, 68, 68, 0.22);
}

.trace-section-title {
  color: var(--text-color-secondary);
  font-size: 13px;
  font-weight: 700;
}

.trace-section-title-danger {
  color: #b91c1c;
}

.trace-copy-btn {
  min-width: 96px;
}

.trace-pre {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 360px;
  overflow: auto;
  font-size: 12px;
  line-height: 1.6;
}

.trace-pagination-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
  margin-top: 16px;
  padding: 14px 16px;
  border-radius: 18px;
  border: 1px solid rgba(226, 232, 240, 0.9);
  background: var(--bg-card);
  box-shadow: 0 10px 28px rgba(15, 23, 42, 0.05);
}

.trace-pagination-actions {
  display: flex;
  gap: 8px;
}

.concurrency-title {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.concurrency-help-trigger {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  border: 0;
  border-radius: 999px;
  background: #e2e8f0;
  color: #475569;
  font-size: 12px;
  font-weight: 800;
  cursor: help;
}

.concurrency-table-card,
.concurrency-table-card :deep(.n-data-table) {
  max-width: 100%;
}

.concurrency-count-badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 54px;
  min-height: 30px;
  padding: 6px 12px;
  border-radius: 999px;
  background: #eff6ff;
  color: #1d4ed8;
  font-weight: 700;
}

.concurrency-count-badge.is-active {
  background: #e8f7ea;
  color: #166534;
}

.concurrency-count-badge.is-queued {
  background: #fff5d6;
  color: #b45309;
}

[data-theme='dark'] .trace-overview-card-primary,
[data-theme='dark'] .trace-overview-card-danger,
[data-theme='dark'] .trace-overview-card-warning,
[data-theme='dark'] .trace-loading-card,
[data-theme='dark'] .trace-card-pending,
[data-theme='dark'] .trace-card-danger {
  background: var(--bg-card);
}

[data-theme='dark'] .trace-meta-chip,
[data-theme='dark'] .trace-code-panel,
[data-theme='dark'] .trace-source-pill,
[data-theme='dark'] .trace-attempt-order {
  background: rgba(255, 255, 255, 0.05);
}

@media (max-width: 640px) {
  .trace-page-header,
  .concurrency-header {
    align-items: stretch;
    flex-direction: column;
  }

  .trace-toolbar {
    justify-content: flex-start;
  }

  .trace-refresh-switch,
  .trace-toolbar .n-button {
    width: 100%;
  }

  .trace-card-toggle,
  .trace-card-body {
    padding-left: 14px;
    padding-right: 14px;
  }

  .trace-request-model,
  .trace-attempted-model {
    font-size: 15px;
  }
}
</style>

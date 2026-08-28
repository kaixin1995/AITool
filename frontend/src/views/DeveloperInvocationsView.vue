<script setup lang="ts">
import { computed, h, onMounted, onUnmounted, provide, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  NAlert,
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
import type { DeveloperInvocationSummary } from '@/api/developer'
import { isRequestCanceled } from '@/api/http'
import PageHeader from '@/components/PageHeader.vue'
import ClientSimulator from './ClientSimulator.vue'
import ProtocolDiagnosticsTab from './ProtocolDiagnosticsTab.vue'
import DiagnosticDumpsTab from './DiagnosticDumpsTab.vue'
import HeaderProfilesTab from './HeaderProfilesTab.vue'
import ProxyProfilesTab from './ProxyProfilesTab.vue'
import SqlMigrationsTab from './SqlMigrationsTab.vue'
import DeveloperAiDiagnosisDrawer from './DeveloperAiDiagnosisDrawer.vue'
import JsonDiffView from '@/components/JsonDiffView.vue'
import { analyzeProtocolError } from '@/utils/protocolErrorAnalyzer'
import {
  developerHashForTab,
  developerTabFromHash,
  setProtocolDiagnosticsPrefill,
  hasRewrittenHeaders,
  getRewrittenHeaders,
  getCurrentDisplayHeaders,
  hasConvertedRequestBody,
  getCurrentDisplayRequestBody,
  hasConvertedResponseBody,
  getCurrentDisplayResponseBody,
  type DeveloperToolTab,
  type ProtocolDiagnosticsPrefill
} from './developerInvocationsState'

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
  preparedRequestHeaders?: Record<string, string>
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
  preparedRequestHeaders?: Record<string, string>
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
const route = useRoute()
const router = useRouter()
const activeTab = ref<DeveloperToolTab>(developerTabFromHash(route.hash))
const prefillSignal = ref(0)
provide('protocol-diagnostics-prefill', prefillSignal)
const loading = ref(false)
const autoRefresh = ref(false)
const summarizeDetail = ref(true)
const entries = ref<DeveloperInvocationSummary[]>([])
const totalCount = ref(0)
const failedCount = ref(0)
const pendingCount = ref(0)
const page = ref(1)
const pageSize = 40
const totalPages = ref(1)
const expandedTraceIds = ref<Set<string>>(new Set())
const details = ref<Record<string, DeveloperInvocationDetail>>({})
const detailLoading = ref<Record<string, boolean>>({})
let pollTimer: ReturnType<typeof setInterval> | null = null
let invocationRequestRunning = false
const detailAbortControllers = new Map<string, AbortController>()

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

function pruneInvocationDetails(nextEntries: DeveloperInvocationSummary[]): void {
  const validTraceIds = new Set(nextEntries.map((entry) => entry.traceId))
  for (const [traceId, controller] of detailAbortControllers) {
    if (!validTraceIds.has(traceId)) {
      controller.abort()
      detailAbortControllers.delete(traceId)
    }
  }

  const nextDetails: Record<string, DeveloperInvocationDetail> = {}
  for (const [traceId, detail] of Object.entries(details.value)) {
    if (validTraceIds.has(traceId)) nextDetails[traceId] = detail
  }
  details.value = nextDetails

  const nextLoading: Record<string, boolean> = {}
  for (const [traceId, isLoading] of Object.entries(detailLoading.value)) {
    if (validTraceIds.has(traceId)) nextLoading[traceId] = isLoading
  }
  detailLoading.value = nextLoading
  expandedTraceIds.value = new Set([...expandedTraceIds.value].filter((traceId) => validTraceIds.has(traceId)))
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

function headersText(headers: Record<string, string> | null | undefined): string {
  if (!headers || Object.keys(headers).length === 0) return '无'
  return Object.entries(headers).map(([key, value]) => `${key}: ${value}`).join('\n')
}

const headersTab = ref<Record<string, 'original' | 'rewritten'>>({})
const requestBodyTabs = ref<Record<string, 'original' | 'prepared'>>({})
const responseBodyTabs = ref<Record<string, 'final' | 'upstream'>>({})

function getAttemptKey(traceId: string, attempt: DeveloperInvocationAttempt, index: number): string {
  return `${traceId}-${attempt.attemptId || index}`
}

function getActiveRequestBodyMode(traceId: string, attempt: DeveloperInvocationAttempt, index: number): 'original' | 'prepared' {
  const key = getAttemptKey(traceId, attempt, index)
  return requestBodyTabs.value[key] || 'original'
}

function setRequestBodyMode(traceId: string, attempt: DeveloperInvocationAttempt, index: number, mode: 'original' | 'prepared'): void {
  const key = getAttemptKey(traceId, attempt, index)
  requestBodyTabs.value[key] = mode
}

function getActiveResponseBodyMode(traceId: string, attempt: DeveloperInvocationAttempt, index: number): 'final' | 'upstream' {
  const key = getAttemptKey(traceId, attempt, index)
  return responseBodyTabs.value[key] || 'final'
}

function setResponseBodyMode(traceId: string, attempt: DeveloperInvocationAttempt, index: number, mode: 'final' | 'upstream'): void {
  const key = getAttemptKey(traceId, attempt, index)
  responseBodyTabs.value[key] = mode
}

function getActiveHeadersMode(traceId: string, detail: DeveloperInvocationDetail): 'original' | 'rewritten' {
  if (headersTab.value[traceId]) {
    return headersTab.value[traceId]
  }
  return hasRewrittenHeaders(detail) ? 'rewritten' : 'original'
}

function attemptStats(entry: DeveloperInvocationSummary): string {
  const success = entry.successAttemptCount ?? 0
  const failed = entry.failedAttemptCount ?? 0
  const pending = entry.pendingAttemptCount ?? 0
  return `成功 ${success} / 失败 ${failed} / 等待 ${pending}`
}

function canShowDetailBody(value: unknown): boolean {
  return bodyText(value) !== '无'
}

// ── 调用记录 → 协议诊断台 联动 ──────────────────────────────
function clientProtocolFromPath(path: string | undefined): string {
  if (path?.includes('/v1/messages')) return 'Anthropic'
  if (path?.includes('/v1/responses')) return 'Responses'
  return 'OpenAI'
}

// 响应方向流式离线转换支持矩阵（与后端 IsSupportedStreamingDirection 的 response 分支一致）。
// 键 = 上游协议，值 = 支持流式转换的客户端协议列表。
const STREAM_RESPONSE_SUPPORTED: Record<string, readonly string[]> = {
  OpenAI: ['Anthropic'],
  Anthropic: ['OpenAI'],
  Responses: ['OpenAI', 'Anthropic']
}

function isStreamResponseSupported(upstream: string, client: string): boolean {
  if (upstream === client) return true
  return STREAM_RESPONSE_SUPPORTED[upstream]?.includes(client) ?? false
}

function openProtocolDiagnostics(prefill: ProtocolDiagnosticsPrefill): void {
  setProtocolDiagnosticsPrefill(prefill)
  prefillSignal.value += 1
  if (activeTab.value !== 'protocol-diagnostics') {
    activeTab.value = 'protocol-diagnostics'
  }
}

function diagnoseEntryRequest(entry: DeveloperInvocationDetail): void {
  // 请求方向永远是整体转换（PrepareRequestBody 一次改写），与客户端是否流式无关。
  openProtocolDiagnostics({
    direction: 'request',
    sourceProtocol: clientProtocolFromPath(entry.requestPath),
    targetProtocol: entry.attempts[0]?.upstreamProtocolType || entry.protocolType || 'OpenAI',
    streaming: false,
    modelName: entry.requestModel,
    payload: entry.requestBody
  })
}

function diagnoseEntryResponse(entry: DeveloperInvocationDetail): void {
  const upstream = entry.protocolType || 'OpenAI'
  const client = clientProtocolFromPath(entry.requestPath)
  openProtocolDiagnostics({
    direction: 'response',
    sourceProtocol: upstream,
    targetProtocol: client,
    // 流式响应转换只支持部分组合；不支持的组合退化为非流式（保留完整 SSE 便于手动调整）。
    streaming: entry.isStreaming && isStreamResponseSupported(upstream, client),
    modelName: entry.requestModel,
    payload: entry.responseBody,
    inputTokens: entry.inputTokens,
    cachedTokens: entry.cachedTokens,
    outputTokens: entry.outputTokens
  })
}

// ── AI 智能诊断抽屉与对比状态 ──
const showAiDrawer = ref(false)
const aiDrawerContext = ref<api.DeveloperAiDiagnosePayload | null>(null)
const expandedDiffAttemptIds = ref<Set<string>>(new Set())

function toggleAttemptDiff(attemptId: string): void {
  if (expandedDiffAttemptIds.value.has(attemptId)) {
    expandedDiffAttemptIds.value.delete(attemptId)
  } else {
    expandedDiffAttemptIds.value.add(attemptId)
  }
}

function openAiDiagnose(entry: DeveloperInvocationDetail, attempt: DeveloperInvocationAttempt): void {
  aiDrawerContext.value = {
    modelId: '',
    clientProtocol: clientProtocolFromPath(entry.requestPath),
    requestPath: entry.requestPath,
    requestModel: entry.requestModel,
    attemptedModel: attempt.attemptedModel,
    targetSiteName: attempt.targetSiteName,
    upstreamProtocolType: attempt.upstreamProtocolType,
    forwardingMode: attempt.forwardingMode,
    statusCode: attempt.statusCode,
    errorMessage: attempt.errorMessage || details.value[entry.traceId]?.errorMessage || '',
    originalRequestBody: entry.requestBody,
    preparedRequestBody: attempt.preparedRequestBody
  }
  showAiDrawer.value = true
}

function openProtocolDiagnosticsWithAttempt(entry: DeveloperInvocationDetail, attempt: DeveloperInvocationAttempt): void {
  const diag = analyzeProtocolError(attempt.errorMessage, attempt.statusCode)
  openProtocolDiagnostics({
    direction: 'request',
    sourceProtocol: clientProtocolFromPath(entry.requestPath),
    targetProtocol: attempt.upstreamProtocolType || 'OpenAI',
    streaming: false,
    modelName: entry.requestModel,
    payload: entry.requestBody,
    targetSiteName: attempt.targetSiteName,
    attemptedModel: attempt.attemptedModel,
    statusCode: attempt.statusCode,
    errorMessage: attempt.errorMessage,
    trialRules: diag?.recommendedRule ? [diag.recommendedRule] : []
  })
}

function diagnoseAttemptResponse(entry: DeveloperInvocationDetail, attempt: DeveloperInvocationAttempt): void {
  const upstream = attempt.upstreamProtocolType || 'OpenAI'
  const client = clientProtocolFromPath(entry.requestPath)
  openProtocolDiagnostics({
    direction: 'response',
    sourceProtocol: upstream,
    targetProtocol: client,
    streaming: attempt.isStreaming && isStreamResponseSupported(upstream, client),
    modelName: entry.requestModel,
    payload: attempt.responseBody,
    inputTokens: attempt.inputTokens,
    cachedTokens: attempt.cachedTokens,
    outputTokens: attempt.outputTokens
  })
}

function configureAutoRefresh(): void {
  if (pollTimer) {
    clearInterval(pollTimer)
    pollTimer = null
  }
  if (!(activeTab.value === 'invocations' && autoRefresh.value)) return

  pollTimer = setInterval(() => {
    if (document.visibilityState !== 'visible') return
    if (activeTab.value === 'invocations' && autoRefresh.value) void loadInvocations(false)
  }, 5000)
}

async function loadInvocations(showSpinner = true, targetPage = page.value): Promise<void> {
  if (invocationRequestRunning) return
  invocationRequestRunning = true
  if (showSpinner) loading.value = true
  try {
    const listResp = await api.getDeveloperList(targetPage, pageSize)
    entries.value = listResp.entries ?? []
    pruneInvocationDetails(entries.value)
    page.value = listResp.page
    totalPages.value = listResp.totalPages || 1
    totalCount.value = listResp.totalCount
    failedCount.value = listResp.failedCount
    pendingCount.value = listResp.pendingCount
  } catch (error) {
    if (showSpinner && (error as { status?: number }).status !== 404) {
      message.error((error as Error).message)
    }
  } finally {
    invocationRequestRunning = false
    if (showSpinner) loading.value = false
  }
}

function refreshActiveTab(): void {
  if (activeTab.value === 'invocations') void loadInvocations(false)
}

function handleVisibilityChange(): void {
  if (document.visibilityState === 'visible' && autoRefresh.value) {
    refreshActiveTab()
  }
}

function handleSummarizeChange(): void {
  const traceIds = [...expandedTraceIds.value]
  if (traceIds.length === 0) return

  // 切换精简模式后保持详情展开，并以新模式原地重新加载正文。
  const nextDetails = { ...details.value }
  traceIds.forEach((traceId) => delete nextDetails[traceId])
  details.value = nextDetails
  traceIds.forEach((traceId) => { void loadDetail(traceId) })
}

async function loadDetail(traceId: string): Promise<void> {
  detailAbortControllers.get(traceId)?.abort()
  const controller = new AbortController()
  detailAbortControllers.set(traceId, controller)
  detailLoading.value = { ...detailLoading.value, [traceId]: true }
  try {
    const detail = await api.getDeveloperDetail(traceId, summarizeDetail.value, controller.signal) as DeveloperInvocationDetail
    if (!controller.signal.aborted && expandedTraceIds.value.has(traceId)) {
      details.value = { ...details.value, [traceId]: detail }
    }
  } catch (e) {
    if (!isRequestCanceled(e)) message.error((e as Error).message)
  } finally {
    if (detailAbortControllers.get(traceId) === controller) {
      detailAbortControllers.delete(traceId)
      detailLoading.value = { ...detailLoading.value, [traceId]: false }
    }
  }
}

async function toggleDetail(traceId: string): Promise<void> {
  const next = new Set(expandedTraceIds.value)
  if (next.has(traceId)) {
    next.delete(traceId)
    expandedTraceIds.value = next
    detailAbortControllers.get(traceId)?.abort()
    const nextDetails = { ...details.value }
    delete nextDetails[traceId]
    details.value = nextDetails
    detailLoading.value = { ...detailLoading.value, [traceId]: false }
    return
  }
  next.add(traceId)
  expandedTraceIds.value = next
  if (details.value[traceId]) return

  await loadDetail(traceId)
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

watch(activeTab, (tab) => {
  const hash = developerHashForTab(tab)
  if (route.hash !== hash) void router.replace({ hash })
  if (tab === 'invocations') void loadInvocations()
  configureAutoRefresh()
})

watch(() => route.hash, (hash) => {
  const tab = developerTabFromHash(hash)
  if (activeTab.value !== tab) activeTab.value = tab
})

onMounted(() => {
  if (activeTab.value === 'invocations') void loadInvocations()
  configureAutoRefresh()
  document.addEventListener('visibilitychange', handleVisibilityChange)
})

onUnmounted(() => {
  if (pollTimer) clearInterval(pollTimer)
  document.removeEventListener('visibilitychange', handleVisibilityChange)
  for (const controller of detailAbortControllers.values()) controller.abort()
  detailAbortControllers.clear()
})
</script>

<template>
  <div class="page-container developer-invocations-page">
    <PageHeader title="调试工具" subtitle="调用调试、客户端模拟和开发者追踪" />

    <NCard class="developer-tools-card" :content-style="{ padding: '16px' }">
      <NTabs v-model:value="activeTab" type="line" animated>
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
              <NButton type="primary" :loading="loading" @click="loadInvocations()">立即刷新</NButton>
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
                    <div class="trace-meta-chip"><span class="trace-meta-label">尝试统计</span><strong>{{ attemptStats(entry) }}</strong></div>
                  </div>
                </div>
              </button>

              <div v-if="expandedTraceIds.has(entry.traceId)" class="trace-card-body">
                <div v-if="detailLoading[entry.traceId]" class="trace-loading-text">调用详情加载中...</div>
                <template v-else-if="details[entry.traceId]">
                  <div class="trace-chain-tip">TraceId: {{ details[entry.traceId].traceId }} · RequestId: {{ details[entry.traceId].requestId || '-' }}</div>
                  <div class="trace-detail-grid">
                    <div class="trace-code-panel">
                      <div class="trace-section-title">基础信息</div>
                      <div class="trace-basic-grid">
                        <div><span>来源</span><strong>{{ details[entry.traceId].source || '-' }}</strong></div>
                        <div><span>客户端 IP</span><strong>{{ details[entry.traceId].clientIp || '-' }}</strong></div>
                        <div><span>协议</span><strong>{{ details[entry.traceId].protocolType || '-' }}</strong></div>
                        <div><span>路径</span><strong>{{ details[entry.traceId].requestPath || '-' }}</strong></div>
                        <div><span>模型</span><strong>{{ details[entry.traceId].requestModel || '-' }}</strong></div>
                        <div><span>User-Agent</span><strong>{{ details[entry.traceId].userAgent || '-' }}</strong></div>
                      </div>
                    </div>
                    <div class="trace-code-panel">
                      <div class="trace-panel-header">
                        <div class="trace-header-toggle-wrap">
                          <button
                            type="button"
                            class="trace-header-toggle-btn"
                            :class="{ active: getActiveHeadersMode(entry.traceId, details[entry.traceId]) === 'original' }"
                            @click="headersTab[entry.traceId] = 'original'"
                          >
                            原始请求头
                          </button>
                          <button
                            type="button"
                            class="trace-header-toggle-btn"
                            :class="{ active: getActiveHeadersMode(entry.traceId, details[entry.traceId]) === 'rewritten' }"
                            @click="headersTab[entry.traceId] = 'rewritten'"
                          >
                            ⚡ 重写后请求头
                            <span v-if="hasRewrittenHeaders(details[entry.traceId])" class="trace-header-badge-tag">已改写</span>
                            <span v-else class="trace-header-badge-tag-muted">无改写</span>
                          </button>
                        </div>
                        <NButton
                          size="tiny"
                          secondary
                          class="trace-copy-btn"
                          @click="copyText(headersText(getCurrentDisplayHeaders(details[entry.traceId], getActiveHeadersMode(entry.traceId, details[entry.traceId]))))"
                        >
                          复制
                        </NButton>
                      </div>
                      <pre class="trace-pre trace-pre-compact">{{ headersText(getCurrentDisplayHeaders(details[entry.traceId], getActiveHeadersMode(entry.traceId, details[entry.traceId]))) }}</pre>
                    </div>
                  </div>
                  <div v-if="details[entry.traceId].errorMessage" class="trace-code-panel trace-code-panel-danger trace-final-error">
                    <div class="trace-section-title trace-section-title-danger">最终错误信息</div>
                    <pre class="trace-pre">{{ details[entry.traceId].errorMessage }}</pre>
                  </div>
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

                      <!-- 智能错误归因与修复建议条 -->
                      <div v-if="analyzeProtocolError(attempt.errorMessage, attempt.statusCode)" class="trace-smart-diagnosis-card">
                        <div class="smart-diag-header">
                          <span class="smart-diag-title">💡 智能诊断与归因</span>
                          <span class="smart-diag-badge">{{ analyzeProtocolError(attempt.errorMessage, attempt.statusCode)?.title }}</span>
                        </div>
                        <p class="smart-diag-detail">{{ analyzeProtocolError(attempt.errorMessage, attempt.statusCode)?.detail }}</p>
                        <p class="smart-diag-action">{{ analyzeProtocolError(attempt.errorMessage, attempt.statusCode)?.suggestedAction }}</p>
                        <div class="smart-diag-actions">
                          <NButton
                            size="tiny"
                            type="primary"
                            @click="openProtocolDiagnosticsWithAttempt(details[entry.traceId], attempt)"
                          >
                            ⚡ 载入规则并测试
                          </NButton>
                          <NButton
                            size="tiny"
                            type="warning"
                            ghost
                            @click="openAiDiagnose(details[entry.traceId], attempt)"
                          >
                            🤖 AI 深度诊断
                          </NButton>
                        </div>
                      </div>

                      <div class="trace-attempt-body-grid">
                        <!-- 请求体面板 -->
                        <div
                          v-if="canShowDetailBody(details[entry.traceId].requestBody) || canShowDetailBody(attempt.preparedRequestBody)"
                          class="trace-code-panel"
                        >
                          <div class="trace-panel-header">
                            <!-- 若有协议转换：显示原始 ↔ 转换后请求体切换 Tab；若透传/无改写：仅显示标题 -->
                            <div v-if="hasConvertedRequestBody(details[entry.traceId], attempt)" class="trace-header-toggle-wrap">
                              <button
                                type="button"
                                class="trace-header-toggle-btn"
                                :class="{ active: getActiveRequestBodyMode(entry.traceId, attempt, index) === 'original' }"
                                @click="setRequestBodyMode(entry.traceId, attempt, index, 'original')"
                              >
                                原始请求体
                              </button>
                              <button
                                type="button"
                                class="trace-header-toggle-btn"
                                :class="{ active: getActiveRequestBodyMode(entry.traceId, attempt, index) === 'prepared' }"
                                @click="setRequestBodyMode(entry.traceId, attempt, index, 'prepared')"
                              >
                                ⚡ 转换后请求体
                                <span class="trace-header-badge-tag">发往上游</span>
                              </button>
                            </div>
                            <div v-else class="trace-section-title">
                              请求体
                              <span class="trace-header-badge-tag-muted">透传</span>
                            </div>

                            <div class="trace-panel-actions">
                              <NButton
                                v-if="hasConvertedRequestBody(details[entry.traceId], attempt)"
                                size="tiny"
                                :type="expandedDiffAttemptIds.has(attempt.attemptId || String(index)) ? 'primary' : 'default'"
                                secondary
                                class="trace-copy-btn"
                                @click="toggleAttemptDiff(attempt.attemptId || String(index))"
                              >
                                {{ expandedDiffAttemptIds.has(attempt.attemptId || String(index)) ? '收起 Diff' : '对比 Diff' }}
                              </NButton>
                              <NButton size="tiny" type="primary" ghost class="trace-copy-btn" @click="openProtocolDiagnosticsWithAttempt(details[entry.traceId], attempt)">诊断</NButton>
                              <NButton
                                size="tiny"
                                secondary
                                class="trace-copy-btn"
                                @click="copyText(bodyText(getCurrentDisplayRequestBody(details[entry.traceId], attempt, getActiveRequestBodyMode(entry.traceId, attempt, index))))"
                              >
                                复制
                              </NButton>
                            </div>
                          </div>

                          <!-- 就地 Diff 对比视图 -->
                          <div
                            v-if="expandedDiffAttemptIds.has(attempt.attemptId || String(index)) && hasConvertedRequestBody(details[entry.traceId], attempt)"
                            class="attempt-diff-container mb-2"
                          >
                            <div class="text-xs font-bold text-slate-500 mb-1">原始请求 (before) ➔ 发往上游 (after) 差异对比：</div>
                            <JsonDiffView
                              :before="bodyText(details[entry.traceId].requestBody)"
                              :after="bodyText(attempt.preparedRequestBody)"
                            />
                          </div>
                          <pre v-else class="trace-pre">{{ bodyText(getCurrentDisplayRequestBody(details[entry.traceId], attempt, getActiveRequestBodyMode(entry.traceId, attempt, index))) }}</pre>
                        </div>

                        <!-- 响应体面板 -->
                        <div
                          v-if="canShowDetailBody(details[entry.traceId].responseBody) || canShowDetailBody(attempt.responseBody)"
                          class="trace-code-panel"
                        >
                          <div class="trace-panel-header">
                            <!-- 若响应发生格式转换：显示客户端响应体 ↔ 上游返回体切换 Tab；若透传/无转换：仅显示标题 -->
                            <div v-if="hasConvertedResponseBody(details[entry.traceId], attempt)" class="trace-header-toggle-wrap">
                              <button
                                type="button"
                                class="trace-header-toggle-btn"
                                :class="{ active: getActiveResponseBodyMode(entry.traceId, attempt, index) === 'final' }"
                                @click="setResponseBodyMode(entry.traceId, attempt, index, 'final')"
                              >
                                客户端响应体
                              </button>
                              <button
                                type="button"
                                class="trace-header-toggle-btn"
                                :class="{ active: getActiveResponseBodyMode(entry.traceId, attempt, index) === 'upstream' }"
                                @click="setResponseBodyMode(entry.traceId, attempt, index, 'upstream')"
                              >
                                ⚡ 上游返回体
                                <span class="trace-header-badge-tag">原始</span>
                              </button>
                            </div>
                            <div v-else class="trace-section-title">
                              响应体
                            </div>

                            <div class="trace-panel-actions">
                              <NButton size="tiny" type="primary" ghost class="trace-copy-btn" @click="diagnoseAttemptResponse(details[entry.traceId], attempt)">诊断</NButton>
                              <NButton
                                size="tiny"
                                secondary
                                class="trace-copy-btn"
                                @click="copyText(bodyText(getCurrentDisplayResponseBody(details[entry.traceId], attempt, getActiveResponseBodyMode(entry.traceId, attempt, index))))"
                              >
                                复制
                              </NButton>
                            </div>
                          </div>
                          <pre class="trace-pre">{{ bodyText(getCurrentDisplayResponseBody(details[entry.traceId], attempt, getActiveResponseBodyMode(entry.traceId, attempt, index))) }}</pre>
                        </div>
                      </div>
                    </article>
                  </div>

                  <!-- 无尝试时的兜底请求/响应展示 -->
                  <div v-if="!details[entry.traceId].attempts || details[entry.traceId].attempts.length === 0" class="trace-attempt-body-grid">
                    <div v-if="canShowDetailBody(details[entry.traceId].requestBody)" class="trace-code-panel">
                      <div class="trace-panel-header">
                        <div class="trace-section-title">请求体</div>
                        <div class="trace-panel-actions">
                          <NButton size="tiny" type="primary" ghost class="trace-copy-btn" @click="diagnoseEntryRequest(details[entry.traceId])">诊断此请求</NButton>
                          <NButton size="tiny" secondary class="trace-copy-btn" @click="copyText(bodyText(details[entry.traceId].requestBody))">复制</NButton>
                        </div>
                      </div>
                      <pre class="trace-pre">{{ bodyText(details[entry.traceId].requestBody) }}</pre>
                    </div>
                    <div v-if="canShowDetailBody(details[entry.traceId].responseBody)" class="trace-code-panel">
                      <div class="trace-panel-header">
                        <div class="trace-section-title">响应体</div>
                        <div class="trace-panel-actions">
                          <NButton size="tiny" type="primary" ghost class="trace-copy-btn" @click="diagnoseEntryResponse(details[entry.traceId])">诊断此响应</NButton>
                          <NButton size="tiny" secondary class="trace-copy-btn" @click="copyText(bodyText(details[entry.traceId].responseBody))">复制</NButton>
                        </div>
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
            <NPagination v-model:page="page" :page-count="totalPages" size="small" @update:page="(p) => loadInvocations(true, p)" />
          </div>
        </NTabPane>

        <NTabPane name="diagnostic-dumps" tab="诊断抓包与样本">
          <DiagnosticDumpsTab />
        </NTabPane>

        <NTabPane name="protocol-diagnostics" tab="协议自愈">
          <ProtocolDiagnosticsTab />
        </NTabPane>

        <NTabPane name="simulator" tab="客户端模拟">
          <ClientSimulator />
        </NTabPane>

        <NTabPane name="header-presets" tab="请求头模板库">
          <HeaderProfilesTab />
        </NTabPane>

        <NTabPane name="proxy-profiles" tab="网络代理池">
          <ProxyProfilesTab />
        </NTabPane>

        <NTabPane name="sql-migrations" tab="SQL 迁移">
          <SqlMigrationsTab />
        </NTabPane>
      </NTabs>
    </NCard>

    <!-- AI 智能故障诊断抽屉 -->
    <DeveloperAiDiagnosisDrawer
      v-model:show="showAiDrawer"
      :context="aiDrawerContext"
      @open-diagnostics="activeTab = 'protocol-diagnostics'"
    />
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
  border: 1px solid var(--border-color-soft);
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
  border: 1px solid var(--border-color-soft);
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
  border: 1px solid color-mix(in srgb, var(--status-info-text) 24%, transparent);
  background: linear-gradient(135deg, rgba(239, 246, 255, 0.9), var(--bg-card));
  box-shadow: 0 10px 28px rgba(15, 23, 42, 0.05);
}

.trace-loading-title {
  margin-bottom: 6px;
  color: var(--status-info-text);
  font-size: 14px;
  font-weight: 700;
}

.trace-accordion {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.trace-card {
  border: 1px solid var(--border-color-soft);
  border-radius: 20px;
  overflow: hidden;
  box-shadow: 0 12px 32px rgba(15, 23, 42, 0.05);
}

.trace-card-success { border-left: 6px solid var(--status-success-text); }
.trace-card-pending { border-left: 6px solid var(--status-warning-text); background: linear-gradient(180deg, rgba(255, 251, 235, 0.4), var(--bg-card) 42%); }
.trace-card-danger { border-left: 6px solid var(--status-danger-text); background: linear-gradient(180deg, rgba(254, 242, 242, 0.55), var(--bg-card) 42%); box-shadow: 0 16px 36px rgba(239, 68, 68, 0.12); }

.trace-card-toggle {
  width: 100%;
  border: 0;
  background: transparent;
  padding: 18px 20px;
  text-align: left;
  cursor: pointer;
}

.trace-card-toggle:hover {
  background: var(--bg-surface-soft);
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

.trace-header-toggle-wrap {
  display: inline-flex;
  align-items: center;
  background: var(--bg-surface-soft);
  padding: 2px;
  border-radius: 6px;
  border: 1px solid var(--border-color-soft);
  gap: 2px;
}

.trace-header-toggle-btn {
  background: transparent;
  border: none;
  font-size: 12px;
  font-weight: 500;
  color: var(--text-color-secondary);
  padding: 2px 8px;
  border-radius: 4px;
  cursor: pointer;
  transition: all 0.15s ease;
}

.trace-header-toggle-btn:hover {
  color: var(--text-color-primary);
}

.trace-header-toggle-btn.active {
  background: var(--primary-color, #10b981);
  color: #ffffff;
  font-weight: 600;
}

.trace-header-badge-tag {
  display: inline-block;
  margin-left: 4px;
  padding: 1px 5px;
  font-size: 10px;
  border-radius: 4px;
  background: rgba(16, 185, 129, 0.2);
  color: #10b981;
  font-weight: 600;
}

.trace-header-toggle-btn.active .trace-header-badge-tag {
  background: rgba(255, 255, 255, 0.25);
  color: #ffffff;
}

.trace-header-badge-tag-muted {
  display: inline-block;
  margin-left: 4px;
  padding: 1px 5px;
  font-size: 10px;
  border-radius: 4px;
  background: var(--bg-surface-soft);
  color: var(--text-color-tertiary, #94a3b8);
  font-weight: 400;
}

.trace-header-toggle-btn.active .trace-header-badge-tag-muted {
  background: rgba(255, 255, 255, 0.2);
  color: rgba(255, 255, 255, 0.85);
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

.trace-status-pill-success { background: var(--status-success-bg); color: var(--status-success-text); }
.trace-status-pill-pending { background: var(--status-warning-bg); color: var(--status-warning-text); }
.trace-status-pill-danger { background: var(--status-danger-bg); color: var(--status-danger-text); box-shadow: 0 0 0 3px color-mix(in srgb, var(--status-danger-text) 14%, transparent); }
.trace-protocol-pill { background: var(--status-info-bg); color: var(--status-info-text); }
.trace-source-pill { background: var(--bg-surface-soft); color: var(--text-color-secondary); }

.trace-request-model,
.trace-attempted-model {
  color: var(--text-primary);
  font-size: 18px;
  font-weight: 800;
  word-break: break-word;
}

.trace-attempted-model {
  color: var(--status-info-text);
}

.trace-model-arrow {
  color: var(--text-color-disabled);
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
  background: var(--bg-surface-soft);
  border: 1px solid var(--border-color-soft);
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
  border: 1px solid var(--border-color-soft);
  background: var(--bg-card);
}

.trace-attempt-card-success { border-left: 5px solid var(--status-success-text); }
.trace-attempt-card-pending { border-left: 5px solid var(--status-warning-text); background: color-mix(in srgb, var(--status-warning-bg) 55%, var(--bg-card)); }
.trace-attempt-card-danger { border-left: 5px solid var(--status-danger-text); background: color-mix(in srgb, var(--status-danger-bg) 55%, var(--bg-card)); }

.trace-attempt-order {
  background: var(--bg-surface-soft);
  color: var(--text-primary);
}

.trace-detail-grid,
.trace-attempt-body-grid {
  display: grid;
  gap: 12px;
  min-width: 0;
  margin-top: 12px;
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

.trace-final-error {
  margin: 12px 0;
}

.trace-smart-diagnosis-card {
  margin-top: 12px;
  padding: 12px 14px;
  border-radius: 12px;
  background: rgba(245, 158, 11, 0.08);
  border: 1px solid rgba(245, 158, 11, 0.3);
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.smart-diag-header {
  display: flex;
  align-items: center;
  gap: 8px;
}

.smart-diag-title {
  font-size: 13px;
  font-weight: 700;
  color: #d97706;
}

.smart-diag-badge {
  font-size: 11px;
  padding: 2px 6px;
  border-radius: 4px;
  background: rgba(245, 158, 11, 0.15);
  color: #b45309;
  font-weight: 600;
}

.smart-diag-detail {
  margin: 0;
  font-size: 12px;
  color: var(--text-primary);
  line-height: 1.5;
}

.smart-diag-action {
  margin: 0;
  font-size: 12px;
  font-weight: 600;
  color: #0284c7;
}

.smart-diag-actions {
  display: flex;
  gap: 8px;
  margin-top: 4px;
}

.attempt-diff-container {
  padding: 8px;
  background: var(--bg-surface-soft);
  border: 1px solid var(--border-color-soft);
  border-radius: 8px;
}

.trace-basic-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 10px;
  margin-top: 10px;
}

.trace-basic-grid div {
  min-width: 0;
}

.trace-basic-grid span {
  display: block;
  margin-bottom: 4px;
  color: var(--text-color-secondary);
  font-size: 12px;
}

.trace-basic-grid strong {
  display: block;
  overflow: hidden;
  color: var(--text-primary);
  font-size: 13px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.trace-pre-compact {
  max-height: 180px;
}

.trace-code-panel {
  min-width: 0;
  overflow: hidden;
  padding: 16px;
  border-radius: 16px;
  border: 1px solid var(--border-color-soft);
  background: var(--bg-surface-soft);
}

.trace-code-panel-danger {
  margin-top: 12px;
  background: var(--status-danger-bg);
  border-color: color-mix(in srgb, var(--status-danger-text) 30%, transparent);
}

.trace-section-title {
  color: var(--text-color-secondary);
  font-size: 13px;
  font-weight: 700;
}

.trace-section-title-danger {
  color: var(--status-danger-text);
}

.trace-copy-btn {
  min-width: 96px;
}

.trace-panel-actions {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.trace-panel-header .trace-panel-actions .trace-copy-btn {
  min-width: auto;
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
  border: 1px solid var(--border-color-soft);
  background: var(--bg-card);
  box-shadow: 0 10px 28px rgba(15, 23, 42, 0.05);
}

.trace-pagination-actions {
  display: flex;
  gap: 8px;
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

@media (max-width: 900px) {
  .trace-detail-grid,
  .trace-attempt-body-grid,
  .trace-basic-grid {
    grid-template-columns: 1fr;
  }
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

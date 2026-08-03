<script setup lang="ts">
import {
  computed,
  nextTick,
  onBeforeUnmount,
  onMounted,
  ref
} from 'vue'
import {
  NCard, NSelect, NInput, NButton, NTag, NEmpty, NSpin, NPopconfirm, NPagination,
  NModal, NForm, NFormItem, useMessage, type SelectOption
} from 'naive-ui'
import * as api from '@/api/conversations'
import * as routesApi from '@/api/routes'
import type { ConversationSession, ConversationTurn } from '@/api/conversations'
import { renderSafeMarkdown } from './conversationsMarkdown'
import {
  buildConversationAssistantMeta,
  buildConversationUserMeta,
  buildInitialConversationWindow,
  buildPreviousConversationWindow,
  type ConversationRenderWindow
} from './conversationsState'

const message = useMessage()
const loading = ref(false)
const sessions = ref<ConversationSession[]>([])
const routeEntries = ref<routesApi.RouteEntry[]>([])
const totalCount = ref(0)
const selectedGroupKey = ref<string | null>(null)
const selectedSessionTitle = ref('对话详情')
const turns = ref<ConversationTurn[]>([])
const turnsLoading = ref(false)
const truncated = ref(false)
const turnsContainer = ref<HTMLElement | null>(null)
const turnsSentinel = ref<HTMLElement | null>(null)
const renderWindow = ref<ConversationRenderWindow>({
  start: 0,
  end: 0
})
let turnsObserver: IntersectionObserver | null = null
let turnsPrepending = false
const copyFeedbackTimers = new Set<ReturnType<typeof setTimeout>>()

function formatDateTimeLocal(date: Date): string {
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`
}

const todayStart = new Date()
todayStart.setHours(0, 0, 0, 0)
const todayEnd = new Date()
todayEnd.setHours(23, 59, 0, 0)

const rangeType = ref('day')
const startTime = ref(formatDateTimeLocal(todayStart))
const endTime = ref(formatDateTimeLocal(todayEnd))
const sourceTool = ref<string | null>(null)
const requestModel = ref<string | null>(null)
const roleFilter = ref('all')
const keyword = ref('')
const page = ref(1)
const pageSize = ref(30)

const renameModal = ref(false)
const renaming = ref(false)
const renameGroupKey = ref('')
const renameTitle = ref('')
const renamePlaceholder = ref('')

const sourceOptions: SelectOption[] = [
  { label: '全部来源', value: '' },
  { label: '代理', value: 'proxy' },
  { label: '对话测试', value: 'chat' },
  { label: 'Claude Code', value: 'claude-code' },
  { label: 'ZCode', value: 'zcode' },
  { label: 'Codex', value: 'codex' },
  { label: 'Open Code', value: 'open-code' }
]
const rangeOptions: SelectOption[] = [
  { label: '按天', value: 'day' },
  { label: '按周', value: 'week' },
  { label: '按月', value: 'month' },
  { label: '指定范围', value: 'custom' }
]
const roleOptions: SelectOption[] = [
  { label: '全部', value: 'all' },
  { label: '我', value: 'user' },
  { label: 'AI', value: 'assistant' }
]

const requestModelOptions = computed<SelectOption[]>(() => [
  { label: '全部路由入口', value: '' },
  ...routeEntries.value.map((entry) => ({ label: entry.entryName, value: entry.entryName }))
])

const visibleTurns = computed(() => turns.value.slice(
  renderWindow.value.start,
  renderWindow.value.end
))
const hasOlderTurns = computed(() => renderWindow.value.start > 0)

function disconnectTurnsObserver(): void {
  turnsObserver?.disconnect()
  turnsObserver = null
}

async function revealPreviousTurns(): Promise<void> {
  if (turnsPrepending) return
  if (!hasOlderTurns.value) {
    disconnectTurnsObserver()
    return
  }

  turnsPrepending = true
  try {
    const container = turnsContainer.value
    const previousHeight = container?.scrollHeight ?? 0
    const previousTop = container?.scrollTop ?? 0
    renderWindow.value = buildPreviousConversationWindow(
      renderWindow.value.start,
      turns.value.length
    )
    await nextTick()

    if (container) {
      container.scrollTop = previousTop
        + container.scrollHeight
        - previousHeight
    }
    if (!hasOlderTurns.value) disconnectTurnsObserver()
  } finally {
    turnsPrepending = false
  }
}

async function setupTurnsObserver(): Promise<void> {
  disconnectTurnsObserver()
  await nextTick()
  if (
    !hasOlderTurns.value
    || !turnsContainer.value
    || !turnsSentinel.value
    || typeof IntersectionObserver === 'undefined'
  ) return

  turnsObserver = new IntersectionObserver(
    entries => {
      if (entries.some(entry => entry.isIntersecting)) {
        void revealPreviousTurns()
      }
    },
    {
      root: turnsContainer.value,
      rootMargin: '200px 0px 0px 0px',
      threshold: 0
    }
  )
  turnsObserver.observe(turnsSentinel.value)
}

function buildRangeParams(): Record<string, unknown> {
  const params: Record<string, unknown> = { rangeType: rangeType.value }
  if (rangeType.value === 'custom') {
    if (startTime.value) params.startTime = startTime.value
    if (endTime.value) params.endTime = endTime.value
  }
  return params
}

async function loadSessions(resetPage = false): Promise<void> {
  if (resetPage) page.value = 1
  loading.value = true
  try {
    const params: Record<string, unknown> = {
      ...buildRangeParams(),
      page: page.value,
      pageSize: pageSize.value
    }
    if (sourceTool.value) params.sourceTool = sourceTool.value
    if (requestModel.value) params.requestModel = requestModel.value
    if (keyword.value) params.sessionKeyword = keyword.value
    const resp = await api.listSessions(params)
    sessions.value = resp.items ?? []
    page.value = resp.page ?? page.value
    pageSize.value = resp.pageSize ?? pageSize.value
    totalCount.value = resp.totalCount ?? 0
    if (selectedGroupKey.value && !sessions.value.some((s) => s.groupKey === selectedGroupKey.value)) {
      selectedGroupKey.value = null
      selectedSessionTitle.value = '对话详情'
      turns.value = []
      renderWindow.value = { start: 0, end: 0 }
      disconnectTurnsObserver()
    }
    if (!selectedGroupKey.value && sessions.value.length > 0) {
      await loadTurns(sessions.value[0].groupKey, sessions.value[0].title)
    }
  } finally { loading.value = false }
}

async function loadTurns(
  groupKey: string,
  title?: string,
  scrollToBottom = false
): Promise<void> {
  disconnectTurnsObserver()
  selectedGroupKey.value = groupKey
  selectedSessionTitle.value = title || sessions.value.find((s) => s.groupKey === groupKey)?.title || '对话详情'
  turnsLoading.value = true
  try {
    const resp = await api.getTurns(groupKey, buildRangeParams())
    turns.value = resp.items ?? []
    renderWindow.value = buildInitialConversationWindow(
      turns.value.length
    )
    truncated.value = resp.truncated === true
    await setupTurnsObserver()
    if (scrollToBottom && turnsContainer.value) {
      await nextTick()
      turnsContainer.value.scrollTop =
        turnsContainer.value.scrollHeight
    }
  } finally { turnsLoading.value = false }
}

async function refreshCurrentSession(): Promise<void> {
  await loadSessions()
  if (selectedGroupKey.value) {
    await loadTurns(selectedGroupKey.value, undefined, true)
  }
}

async function handleDelete(groupKey: string): Promise<void> {
  await api.deleteSession(groupKey)
  message.success('已删除会话')
  if (selectedGroupKey.value === groupKey) {
    selectedGroupKey.value = null
    selectedSessionTitle.value = '对话详情'
    turns.value = []
    renderWindow.value = { start: 0, end: 0 }
    disconnectTurnsObserver()
  }
  await loadSessions()
}

function openRename(session: ConversationSession): void {
  renameGroupKey.value = session.groupKey
  renameTitle.value = session.isCustomTitle ? session.title : ''
  renamePlaceholder.value = session.defaultTitle || session.title
  renameModal.value = true
}

async function handleRename(): Promise<void> {
  if (!renameGroupKey.value) return
  renaming.value = true
  try {
    await api.updateSessionTitle(renameGroupKey.value, renameTitle.value)
    message.success('会话标题已更新')
    renameModal.value = false
    await loadSessions()
    if (selectedGroupKey.value === renameGroupKey.value) {
      selectedSessionTitle.value = renameTitle.value.trim() || renamePlaceholder.value || '对话详情'
    }
  } finally {
    renaming.value = false
  }
}

async function copyText(text: string): Promise<boolean> {
  if (window.isSecureContext && navigator.clipboard) {
    try {
      await navigator.clipboard.writeText(text)
      return true
    } catch {
      // 浏览器权限受限时继续使用传统复制路径。
    }
  }

  const textarea = document.createElement('textarea')
  textarea.value = text
  textarea.style.position = 'fixed'
  textarea.style.left = '-9999px'
  textarea.style.opacity = '0'
  document.body.appendChild(textarea)
  textarea.focus()
  textarea.select()
  try {
    return document.execCommand('copy')
  } finally {
    document.body.removeChild(textarea)
  }
}

async function handleTurnsClick(event: MouseEvent): Promise<void> {
  const target = event.target
  if (!(target instanceof Element)) return
  const button = target.closest<HTMLButtonElement>(
    '[data-conversation-copy-code]'
  )
  if (!button) return

  const code = button
    .closest('.conversation-code-block')
    ?.querySelector('code')
    ?.textContent
  if (!code) return

  try {
    if (!await copyText(code)) throw new Error('copy failed')
    button.textContent = '已复制'
    const timer = setTimeout(() => {
      button.textContent = '复制'
      copyFeedbackTimers.delete(timer)
    }, 1500)
    copyFeedbackTimers.add(timer)
  } catch {
    message.error('复制失败，请手动复制')
  }
}

onMounted(async () => {
  try {
    routeEntries.value = await routesApi.getRouteEntries()
  } catch {
    routeEntries.value = []
  }
  await loadSessions()
})

onBeforeUnmount(() => {
  disconnectTurnsObserver()
  copyFeedbackTimers.forEach(timer => clearTimeout(timer))
  copyFeedbackTimers.clear()
})
</script>

<template>
  <div class="conversation-page-shell">
    <div class="conversation-log-filter-bar">
      <label class="conversation-filter-item">
        <span class="conversation-filter-label">时间范围:</span>
        <NSelect v-model:value="rangeType" :options="rangeOptions" size="small" class="range-select" />
      </label>
      <label v-if="rangeType === 'custom'" class="conversation-filter-item conversation-custom-range">
        <span class="conversation-filter-label">开始时间:</span>
        <input v-model="startTime" type="datetime-local" class="conversation-time-input time-input" />
      </label>
      <label v-if="rangeType === 'custom'" class="conversation-filter-item conversation-custom-range">
        <span class="conversation-filter-label">结束时间:</span>
        <input v-model="endTime" type="datetime-local" class="conversation-time-input time-input" />
      </label>
      <label class="conversation-filter-item">
        <span class="conversation-filter-label">来源:</span>
        <NSelect v-model:value="sourceTool" :options="sourceOptions" size="small" class="source-select" />
      </label>
      <label class="conversation-filter-item">
        <span class="conversation-filter-label">路由入口:</span>
        <NSelect v-model:value="requestModel" :options="requestModelOptions" size="small" class="model-select" filterable />
      </label>
      <label class="conversation-filter-item">
        <span class="conversation-filter-label">对话角色:</span>
        <NSelect v-model:value="roleFilter" :options="roleOptions" size="small" class="role-select" />
      </label>
      <label class="conversation-filter-item keyword-filter">
        <span class="conversation-filter-label">关键词:</span>
        <NInput v-model:value="keyword" placeholder="关键词" size="small" @keyup.enter="loadSessions(true)" />
      </label>
      <NButton type="primary" size="small" @click="loadSessions(true)">查询</NButton>
      <NButton secondary size="small" @click="refreshCurrentSession">刷新</NButton>
    </div>

    <div class="conversation-log-shell">
      <aside class="conversation-log-sidebar">
        <div class="conversation-log-brand">会话列表</div>
        <NSpin :show="loading" class="conversation-session-spin">
          <NEmpty v-if="sessions.length === 0" description="暂无会话" />
          <div v-else class="conversation-log-session-list">
            <article
              v-for="s in sessions"
              :key="s.groupKey"
              :class="['conversation-session-card', { active: selectedGroupKey === s.groupKey }]"
            >
              <button type="button" class="conversation-session-item" @click="loadTurns(s.groupKey, s.title)">
                <div class="conversation-session-title-row">
                  <NTag v-if="s.sourceTool" size="tiny" :bordered="false">{{ s.sourceToolText || s.sourceTool }}</NTag>
                  <span class="conversation-session-title">{{ s.title }}</span>
                </div>
                <div class="conversation-session-meta">{{ s.lastActivityAtText }} · {{ s.turnCount }} 轮</div>
                <div class="conversation-session-meta conversation-session-tokens">{{ s.totalTokensText || `${s.totalTokens} tokens` }}</div>
                <div v-if="s.preview" class="conversation-session-preview">{{ s.preview }}</div>
              </button>
              <div class="conversation-session-actions">
                <NButton size="tiny" text type="primary" class="conversation-session-rename" @click="openRename(s)">改名</NButton>
                <NPopconfirm @positive-click="handleDelete(s.groupKey)">
                  <template #trigger><NButton size="tiny" text type="error">删除</NButton></template>
                  删除整个会话？
                </NPopconfirm>
              </div>
            </article>
          </div>
        </NSpin>
        <NPagination
          v-if="totalCount > pageSize"
          v-model:page="page"
          :page-size="pageSize"
          :item-count="totalCount"
          :page-slot="5"
          class="conversation-pagination"
          @update:page="loadSessions()"
        />
      </aside>

      <section class="conversation-log-main">
        <div class="conversation-log-title">{{ selectedSessionTitle }}</div>
        <NSpin :show="turnsLoading" class="conversation-turns-spin">
          <div v-if="!selectedGroupKey" class="conversation-log-content-empty text-muted">请选择左侧会话查看内容。</div>
          <div v-else-if="turns.length === 0" class="conversation-log-content-empty text-muted">该会话无轮次记录。</div>
          <div v-else id="conversationTurns" ref="turnsContainer" class="conversation-turns-content" @click="handleTurnsClick">
            <div
              v-if="hasOlderTurns"
              ref="turnsSentinel"
              class="conversation-turns-sentinel"
              aria-hidden="true"
            />
            <NTag v-if="truncated" type="warning" :bordered="false" class="conversation-truncated-tip">记录过多，仅展示最近一部分，请缩小时间范围查看完整内容。</NTag>
            <div v-for="turn in visibleTurns" :key="turn.id" class="conversation-turn">
              <div v-if="turn.userInputText && (roleFilter === 'all' || roleFilter === 'user')" class="conversation-msg conversation-msg-user">
                <div class="conversation-avatar conversation-avatar-user">我</div>
                <div class="conversation-msg-body">
                  <div class="conversation-msg-meta">{{ buildConversationUserMeta(turn) }}</div>
                  <div class="conversation-bubble conversation-bubble-user">{{ turn.userInputText }}</div>
                </div>
              </div>

              <div v-if="turn.assistantOutputMarkdown && (roleFilter === 'all' || roleFilter === 'assistant')" class="conversation-msg conversation-msg-assistant">
                <div class="conversation-avatar conversation-avatar-assistant">AI</div>
                <div class="conversation-msg-body">
                  <div class="conversation-msg-meta">{{ buildConversationAssistantMeta(turn) }}</div>
                  <div class="conversation-bubble conversation-bubble-assistant conversation-markdown" v-html="renderSafeMarkdown(turn.assistantOutputMarkdown)" />
                </div>
              </div>
            </div>
          </div>
        </NSpin>
      </section>
    </div>

    <NModal v-model:show="renameModal" preset="card" title="修改会话标题" style="width: 420px; max-width: 92vw" :mask-closable="false">
      <NForm label-placement="top">
        <NFormItem label="会话标题">
          <NInput v-model:value="renameTitle" maxlength="200" clearable :placeholder="renamePlaceholder" />
          <template #feedback>留空后保存会恢复默认标题。</template>
        </NFormItem>
      </NForm>
      <template #footer>
        <div class="conversation-modal-footer">
          <NButton @click="renameModal = false">取消</NButton>
          <NButton type="primary" :loading="renaming" @click="handleRename">保存</NButton>
        </div>
      </template>
    </NModal>
  </div>
</template>

<style scoped>
.conversation-page-shell {
  display: flex;
  flex-direction: column;
  gap: 16px;
  min-width: 0;
  height: 100%;
  min-height: 0;
}

.conversation-log-filter-bar {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 12px;
  padding: 14px 20px;
  border: 1px solid var(--border-color-global);
  border-radius: 18px;
  background: var(--bg-card);
  flex-shrink: 0;
}

.conversation-filter-item {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.conversation-filter-label {
  color: var(--text-color-secondary);
  font-size: 13px;
  white-space: nowrap;
}

.range-select { width: 130px; }
.source-select { width: 140px; }
.model-select { width: 160px; }
.role-select { width: 120px; }
.time-input { width: 190px; }
.conversation-time-input {
  height: 28px;
  min-width: 0;
  padding: 0 10px;
  border: 1px solid var(--border-color-global);
  border-radius: 6px;
  background: var(--bg-card);
  color: var(--text-primary);
  font: inherit;
  font-size: 13px;
}
.keyword-filter { width: 190px; }
.keyword-filter :deep(.n-input) { min-width: 0; }

.conversation-log-shell {
  display: grid;
  grid-template-columns: 280px minmax(0, 1fr);
  gap: 20px;
  flex: 1;
  min-height: 0;
}

.conversation-log-sidebar,
.conversation-log-main {
  min-height: 0;
  overflow: hidden;
  border: 1px solid var(--border-color-global);
  border-radius: 18px;
  background: var(--bg-card);
}

.conversation-log-sidebar {
  display: flex;
  flex-direction: column;
}

.conversation-log-brand {
  padding: 18px 20px 12px;
  color: #2563eb;
  font-size: 22px;
  font-weight: 700;
}

.conversation-session-spin {
  flex: 1;
  min-height: 0;
}

.conversation-session-spin :deep(.n-spin-content) {
  height: 100%;
  min-height: 0;
}

.conversation-log-session-list {
  height: 100%;
  min-height: 0;
  overflow-y: auto;
  padding: 0 10px 12px;
}

.conversation-session-card {
  border-radius: 14px;
  margin-bottom: 6px;
  overflow: hidden;
}

.conversation-session-card:hover {
  background: rgba(108, 158, 255, 0.08);
}

.conversation-session-card.active {
  background: rgba(108, 158, 255, 0.18);
}

.conversation-session-item {
  width: 100%;
  border: 0;
  background: transparent;
  text-align: left;
  border-radius: 14px;
  padding: 12px;
  color: inherit;
  cursor: pointer;
  overflow: hidden;
}

.conversation-session-title-row {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.conversation-session-title {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-weight: 600;
}

.conversation-session-meta {
  margin-top: 6px;
  color: var(--text-color-secondary);
  font-size: 12px;
  line-height: 1.45;
  white-space: nowrap;
}

.conversation-session-tokens {
  margin-top: 2px;
}

.conversation-session-preview {
  margin-top: 6px;
  color: var(--text-color-secondary);
  font-size: 12px;
  line-height: 1.5;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.conversation-session-actions {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
  padding: 0 12px 10px;
}

.conversation-pagination {
  justify-content: center;
  padding: 10px 8px 12px;
  flex-shrink: 0;
}

.conversation-log-main {
  display: flex;
  flex-direction: column;
}

.conversation-log-title {
  padding: 18px 24px;
  border-bottom: 1px solid var(--border-color-global);
  font-size: 26px;
  font-weight: 700;
  flex-shrink: 0;
}

.conversation-turns-spin {
  flex: 1;
  min-height: 0;
}

.conversation-turns-spin :deep(.n-spin-content) {
  height: 100%;
  min-height: 0;
}

.conversation-turns-content,
.conversation-log-content-empty {
  height: 100%;
  min-height: 0;
  overflow: auto;
  padding: 24px;
}

.conversation-turns-sentinel {
  width: 100%;
  height: 1px;
}

.conversation-truncated-tip {
  margin-bottom: 18px;
}

.conversation-turn {
  margin-bottom: 32px;
}

.conversation-msg {
  display: flex;
  gap: 12px;
  margin-bottom: 14px;
  align-items: flex-start;
}

.conversation-msg-user {
  flex-direction: row-reverse;
}

.conversation-avatar {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
  border-radius: 50%;
  flex-shrink: 0;
  font-size: 13px;
  font-weight: 700;
  line-height: 1;
}

.conversation-avatar-user {
  background: #dbeafe;
  color: #1d4ed8;
}

.conversation-avatar-assistant {
  background: #f0fdf4;
  color: #15803d;
}

.conversation-msg-body {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
  max-width: min(80%, 1100px);
}

.conversation-msg-user .conversation-msg-body {
  align-items: flex-end;
}

.conversation-msg-meta {
  color: #9ca3af;
  font-size: 12px;
  line-height: 1.5;
}

.conversation-bubble {
  max-width: 100%;
  min-width: 0;
  padding: 12px 16px;
  border-radius: 16px;
  overflow-wrap: anywhere;
  word-break: break-word;
  line-height: 1.7;
  font-size: 15px;
}

.conversation-bubble-user {
  background: #2563eb;
  color: #fff;
  border-bottom-right-radius: 4px;
  white-space: pre-wrap;
}

.conversation-bubble-assistant {
  background: var(--bg-input);
  border: 1px solid var(--border-color-global);
  border-bottom-left-radius: 4px;
}

.conversation-markdown :deep(h1),
.conversation-markdown :deep(h2),
.conversation-markdown :deep(h3),
.conversation-markdown :deep(h4) {
  margin-top: 20px;
  margin-bottom: 10px;
  font-weight: 700;
  line-height: 1.35;
}

.conversation-markdown :deep(h3) {
  font-size: 16px;
}

.conversation-markdown :deep(p) {
  margin: 0 0 10px;
}

.conversation-markdown :deep(ul),
.conversation-markdown :deep(ol) {
  margin: 0 0 10px 20px;
  padding: 0;
}

.conversation-markdown :deep(li) {
  margin: 4px 0;
}

.conversation-markdown :deep(blockquote) {
  margin: 10px 0;
  padding: 8px 12px;
  border-left: 3px solid #6c9eff;
  background: rgba(108, 158, 255, 0.1);
  color: var(--text-primary);
}

.conversation-markdown :deep(hr) {
  margin: 14px 0;
  border: 0;
  border-top: 1px solid var(--border-color-global);
}

.conversation-markdown :deep(a) {
  color: #2563eb;
  text-decoration: none;
}

.conversation-markdown :deep(a:hover) {
  text-decoration: underline;
}

.conversation-markdown :deep(table) {
  display: block;
  width: 100%;
  margin: 12px 0;
  overflow-x: auto;
  border-collapse: collapse;
}

.conversation-markdown :deep(th),
.conversation-markdown :deep(td) {
  padding: 8px 10px;
  border: 1px solid var(--border-color-global);
  text-align: left;
}

.conversation-markdown :deep(th) {
  background: var(--bg-input);
  font-weight: 600;
}

.conversation-markdown :deep(p:last-child),
.conversation-markdown :deep(> *:last-child) {
  margin-bottom: 0;
}

.conversation-markdown :deep(> *:first-child) {
  margin-top: 0;
}

.conversation-markdown :deep(code) {
  background: rgba(108, 158, 255, 0.12);
  padding: 2px 6px;
  border-radius: 4px;
  font-size: 14px;
}

.conversation-markdown :deep(.conversation-code-block) {
  position: relative;
  margin: 12px 0;
  border: 1px solid var(--border-color-global);
  border-radius: 10px;
  overflow: hidden;
}

.conversation-markdown :deep(.conversation-code-header) {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 6px 12px;
  background: var(--bg-input);
  color: var(--text-color-secondary);
  font-size: 12px;
}

.conversation-markdown :deep(.conversation-code-copy) {
  padding: 2px 8px;
  border: 1px solid var(--border-color-global);
  border-radius: 5px;
  background: var(--bg-card);
  color: var(--text-primary);
  cursor: pointer;
  font: inherit;
}

.conversation-markdown :deep(.conversation-code-copy:hover) {
  border-color: #6c9eff;
  color: #6c9eff;
}

.conversation-markdown :deep(.conversation-code-block pre) {
  margin: 0;
  max-height: 520px;
  overflow-x: auto;
  padding: 14px 16px;
  background: var(--bg-card);
  color: var(--text-primary);
  white-space: pre;
}

.conversation-markdown :deep(.conversation-code-block pre code) {
  background: transparent;
  padding: 0;
  font-size: 13px;
  line-height: 1.6;
}

.conversation-markdown :deep(.hljs-comment),
.conversation-markdown :deep(.hljs-quote) { color: #6a737d; }
.conversation-markdown :deep(.hljs-keyword),
.conversation-markdown :deep(.hljs-selector-tag),
.conversation-markdown :deep(.hljs-subst) { color: #d73a49; }
.conversation-markdown :deep(.hljs-string),
.conversation-markdown :deep(.hljs-doctag),
.conversation-markdown :deep(.hljs-regexp) { color: #0a7a31; }
.conversation-markdown :deep(.hljs-number),
.conversation-markdown :deep(.hljs-literal),
.conversation-markdown :deep(.hljs-variable),
.conversation-markdown :deep(.hljs-template-variable) { color: #005cc5; }
.conversation-markdown :deep(.hljs-title),
.conversation-markdown :deep(.hljs-section),
.conversation-markdown :deep(.hljs-selector-id) { color: #6f42c1; }
.conversation-markdown :deep(.hljs-built_in),
.conversation-markdown :deep(.hljs-type),
.conversation-markdown :deep(.hljs-attribute) { color: #b35c00; }

:global([data-theme='dark']) .conversation-markdown :deep(.hljs-comment),
:global([data-theme='dark']) .conversation-markdown :deep(.hljs-quote) { color: #9ca3af; }
:global([data-theme='dark']) .conversation-markdown :deep(.hljs-keyword),
:global([data-theme='dark']) .conversation-markdown :deep(.hljs-selector-tag),
:global([data-theme='dark']) .conversation-markdown :deep(.hljs-subst) { color: #ff7b72; }
:global([data-theme='dark']) .conversation-markdown :deep(.hljs-string),
:global([data-theme='dark']) .conversation-markdown :deep(.hljs-doctag),
:global([data-theme='dark']) .conversation-markdown :deep(.hljs-regexp) { color: #a5d6ff; }
:global([data-theme='dark']) .conversation-markdown :deep(.hljs-number),
:global([data-theme='dark']) .conversation-markdown :deep(.hljs-literal),
:global([data-theme='dark']) .conversation-markdown :deep(.hljs-variable),
:global([data-theme='dark']) .conversation-markdown :deep(.hljs-template-variable) { color: #79c0ff; }
:global([data-theme='dark']) .conversation-markdown :deep(.hljs-title),
:global([data-theme='dark']) .conversation-markdown :deep(.hljs-section),
:global([data-theme='dark']) .conversation-markdown :deep(.hljs-selector-id) { color: #d2a8ff; }
:global([data-theme='dark']) .conversation-markdown :deep(.hljs-built_in),
:global([data-theme='dark']) .conversation-markdown :deep(.hljs-type),
:global([data-theme='dark']) .conversation-markdown :deep(.hljs-attribute) { color: #ffa657; }

.conversation-modal-footer {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
}

@media (max-width: 900px) {
  .conversation-log-shell {
    grid-template-columns: 1fr;
    overflow: visible;
  }

  .conversation-log-sidebar {
    max-height: 320px;
  }

  .conversation-log-main {
    min-height: 520px;
  }
}

@media (max-width: 640px) {
  .conversation-log-filter-bar {
    align-items: stretch;
    flex-direction: column;
  }

  .conversation-filter-item,
  .range-select,
  .source-select,
  .model-select,
  .role-select,
  .time-input,
  .keyword-filter {
    width: 100%;
  }

  .conversation-msg-body {
    max-width: calc(100% - 48px);
  }
}
</style>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { NCard, NSelect, NInput, NButton, NSwitch, useMessage, type SelectOption } from 'naive-ui'
import * as chatApi from '@/api/chat'
import type { ChatModelTarget, ChatAttemptResult, ChatSendResult } from '@/api/chat'

interface Message { role: 'user' | 'assistant'; content: string; reasoning?: string; meta?: ChatSendResult; error?: boolean; createdAt: number; streaming?: boolean; reasoningEnabled?: boolean; durationMs?: number }

const message = useMessage()
const selectedModelId = ref<string | null>(null)
const targets = ref<ChatModelTarget[]>([])
const selectedMappingId = ref<string | null>(null)
const input = ref('')
const sending = ref(false)
const messages = ref<Message[]>([])
const streamingContent = ref('')
const enableReasoning = ref(false)
const reasoningEffort = ref('high')
const enableStreaming = ref(false)
const reasoningOptions: SelectOption[] = [
  { label: '低', value: 'low' },
  { label: '中', value: 'medium' },
  { label: '高', value: 'high' },
  { label: '超高', value: 'xhigh' },
  { label: '最大', value: 'max' }
]
const streamingReasoning = ref('')
// 最近一次的调用链路（meta 事件携带的路由尝试明细）
const lastAttempts = ref<ChatAttemptResult[] | null>(null)
const expandedAttemptIndexes = ref<Set<number>>(new Set())
const messagesContainer = ref<HTMLElement | null>(null)
let abortController: AbortController | null = null

async function loadModels(): Promise<void> {
  await loadTargets()
}

async function loadTargets(): Promise<void> {
  lastAttempts.value = null
  targets.value = await chatApi.getChatTargets()
  // 刷新后若原映射已被删除或禁用，清空旧 value，避免选择框回退显示 mappingId。
  const nextTarget = targets.value.find((item) => item.mappingId === selectedMappingId.value) ?? targets.value[0]
  selectedMappingId.value = nextTarget?.mappingId ?? null
  selectedModelId.value = nextTarget?.modelId ?? null
}

watch(selectedMappingId, (mappingId) => {
  const target = targets.value.find((item) => item.mappingId === mappingId)
  selectedModelId.value = target?.modelId ?? null
})

function formatTargetLabel(target: ChatModelTarget): string {
  const siteName = target.siteName?.trim() || '未命名站点'
  const remoteModelName = target.siteModelName?.trim()
  const displayName = target.modelDisplayName?.trim()
  const modelLabel = remoteModelName || displayName || '未知模型'
  const aliasLabel = remoteModelName && displayName && remoteModelName !== displayName
    ? `（${displayName}）`
    : ''
  return `${siteName} / ${modelLabel}${aliasLabel}`
}

const targetOptionsComputed = computed<SelectOption[]>(() => {
  // 不再用 modelSearch 过滤 options：选中项被过滤掉时 NSelect 会回退显示 value（乱码）。
  // 搜索交由 NSelect 自身的 filterable 处理，保证选中项始终在 options 中。
  // 同一站点模型会按多个 Key 展开为多个调度候选；选择值仍是 mappingId，界面只保留一项，避免重复展示。
  const uniqueTargets = new Map<string, ChatModelTarget>()
  for (const target of targets.value) {
    if (!uniqueTargets.has(target.mappingId)) uniqueTargets.set(target.mappingId, target)
  }

  return [...uniqueTargets.values()]
    .map((target) => ({ label: formatTargetLabel(target), value: target.mappingId }))
})

const currentReasoning = computed(() => streamingReasoning.value || [...messages.value].reverse().find((m) => m.reasoning)?.reasoning || '')

async function scrollToBottom(): Promise<void> {
  await nextTick()
  if (messagesContainer.value) messagesContainer.value.scrollTop = messagesContainer.value.scrollHeight
}

async function handleSend(): Promise<void> {
  const text = input.value.trim()
  const target = targets.value.find((item) => item.mappingId === selectedMappingId.value)
  if (!text || !target || sending.value) {
    if (!target) message.warning('请先选择站点模型')
    return
  }
  input.value = ''
  const startedAt = Date.now()
  messages.value.push({ role: 'user', content: text, createdAt: startedAt })
  messages.value.push({ role: 'assistant', content: '', createdAt: startedAt, streaming: enableStreaming.value, reasoningEnabled: enableReasoning.value })
  const assistantIdx = messages.value.length - 1
  streamingContent.value = ''
  streamingReasoning.value = ''
  lastAttempts.value = null
  expandedAttemptIndexes.value = new Set()
  sending.value = true
  const controller = new AbortController()
  abortController = controller
  await scrollToBottom()

  const commonOpts = {
    modelId: target.modelId,
    message: text,
    mappingId: target.mappingId,
    enableReasoning: enableReasoning.value,
    reasoningEffort: reasoningEffort.value,
    signal: controller.signal
  }

  try {
    if (enableStreaming.value) {
      await chatApi.sendChatStream(commonOpts, {
        onToken: (delta) => {
          streamingContent.value += delta
          messages.value[assistantIdx].content = streamingContent.value
          scrollToBottom()
        },
        onReasoning: (delta) => {
          streamingReasoning.value += delta
          messages.value[assistantIdx].reasoning = streamingReasoning.value
        },
        onMeta: (meta) => {
          // meta 事件携带路由尝试明细（每段尝试的站点/模型/状态/耗时）
          const m = meta as ChatSendResult
          if (m?.attempts) {
            lastAttempts.value = m.attempts
            messages.value[assistantIdx].meta = m
          }
        },
        onDone: () => {
          messages.value[assistantIdx].durationMs = Date.now() - startedAt
          if (!streamingContent.value) {
            messages.value[assistantIdx].content = '(空回复)'
          }
        },
        onError: (err) => {
          const streamError = err as Error & { attempts?: ChatAttemptResult[] }
          if (streamError.attempts) {
            lastAttempts.value = streamError.attempts
            messages.value[assistantIdx].meta = { success: false, content: '', attempts: streamError.attempts }
          }
          messages.value[assistantIdx].error = true
          messages.value[assistantIdx].content = `(错误：${err.message})`
          message.error(err.message)
        }
      })
      if (controller.signal.aborted && !messages.value[assistantIdx].content) {
        messages.value[assistantIdx].content = '(已停止)'
      }
    } else {
      const result = await chatApi.sendChat(commonOpts)
      messages.value[assistantIdx].meta = result
      lastAttempts.value = result.attempts ?? []
      messages.value[assistantIdx].durationMs = result.totalDurationMs || result.durationMs || (Date.now() - startedAt)
      if (result.success) {
        messages.value[assistantIdx].content = result.content || '(空回复)'
        if (result.reasoningContent) messages.value[assistantIdx].reasoning = result.reasoningContent
      } else {
        messages.value[assistantIdx].error = true
        messages.value[assistantIdx].content = `(错误：${result.error || '未知错误'})`
      }
    }
  } catch (e) {
    const error = e as Error
    if (error.name === 'AbortError') {
      messages.value[assistantIdx].content = '(已停止)'
    } else {
      messages.value[assistantIdx].error = true
      messages.value[assistantIdx].content = `(错误：${error.message})`
      message.error(error.message)
    }
  } finally {
    if (abortController === controller) abortController = null
    sending.value = false
    await scrollToBottom()
  }
}

function handleStop(): void {
  abortController?.abort()
}

function handleClear(): void {
  messages.value = []
  streamingContent.value = ''
  streamingReasoning.value = ''
  lastAttempts.value = null
  expandedAttemptIndexes.value = new Set()
}

function formatNumber(value: number | null | undefined): string {
  const number = Number(value ?? 0)
  return Number.isFinite(number) ? number.toLocaleString('zh-CN') : '-'
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

function attemptStatusClass(att: ChatAttemptResult): string {
  const status = String(att.status ?? '').toLowerCase()
  return status === 'success' || status === 'ok' ? 'success' : 'fail'
}

function attemptStatusLabel(att: ChatAttemptResult): string {
  const status = String(att.status ?? '')
  return status || '未知'
}

function attemptError(att: ChatAttemptResult): string {
  return att.errorMessage || ''
}

function formatTime(value: number | null | undefined): string {
  if (!value) return '-'
  return new Date(value).toLocaleTimeString('zh-CN', { hour12: false })
}

function messageMeta(msg: Message): string {
  if (msg.role === 'user') return `我 · ${formatTime(msg.createdAt)}`
  const parts = ['AI', formatTime(msg.createdAt)]
  if (msg.durationMs) parts.push(`耗时 ${formatDuration(msg.durationMs)}`)
  parts.push(msg.streaming ? '流式' : '非流式')
  parts.push(msg.reasoningEnabled ? '思考开启' : '思考关闭')
  return parts.join(' · ')
}

function toggleAttemptDetail(index: number): void {
  const next = new Set(expandedAttemptIndexes.value)
  if (next.has(index)) next.delete(index)
  else next.add(index)
  expandedAttemptIndexes.value = next
}

onMounted(loadModels)
// 组件卸载（切换路由）时中止进行中的流式请求：fetch 循环会持续回调写入已卸载组件的状态，
// 长回复可拖数分钟，连接与内存都无法回收。
onUnmounted(() => {
  abortController?.abort()
})
</script>

<template>
  <div class="chat-admin-shell">
    <div class="chat-admin-stage">
      <div class="chat-admin-page">
      <div class="chat-admin-main">
        <NCard class="chat-card" :content-style="{ padding: '0', flex: 1, display: 'flex', flexDirection: 'column', minHeight: 0 }">
          <div class="chat-toolbar">
            <label class="chat-toolbar-field chat-toolbar-field-target">
              <span class="chat-toolbar-field-label">站点 / 模型</span>
              <NSelect v-model:value="selectedMappingId" :options="targetOptionsComputed" placeholder="-- 请选择站点模型 --" filterable />
            </label>
            <div class="chat-toolbar-toggle-group">
              <label class="chat-toolbar-switch">
                <NSwitch v-model:value="enableStreaming" size="small" />
                <span>启用流式</span>
              </label>
              <label class="chat-toolbar-switch">
                <NSwitch v-model:value="enableReasoning" size="small" />
                <span>开启思考</span>
              </label>
              <label v-if="enableReasoning" class="chat-reasoning-effort">
                <span class="chat-reasoning-effort-label">思考等级</span>
                <NSelect v-model:value="reasoningEffort" :options="reasoningOptions" size="small" class="chat-reasoning-effort-select" />
              </label>
            </div>
            <div class="chat-toolbar-actions">
              <NButton secondary @click="loadModels">刷新候选</NButton>
              <NButton secondary :disabled="sending" @click="handleClear">清空</NButton>
            </div>
          </div>

          <div ref="messagesContainer" class="chat-messages">
            <div v-if="messages.length === 0" class="chat-empty">
              <div class="chat-empty-icon">💬</div>
              <div>选择站点模型后输入问题开始对话</div>
            </div>
            <div v-for="(msg, idx) in messages" :key="idx" :class="['chat-msg', msg.role]">
              <div :class="['chat-bubble', msg.role === 'user' ? 'chat-bubble-user' : msg.error ? 'chat-bubble-error' : 'chat-bubble-ai']">
                <div class="chat-text">{{ msg.content || (sending && idx === messages.length - 1 ? '正在输入...' : '') }}</div>
                <div class="chat-bubble-meta">{{ messageMeta(msg) }}</div>
              </div>
            </div>
          </div>

          <template #footer>
            <div class="chat-input-area">
              <div class="chat-input-wrapper">
                <NInput
                  v-model:value="input"
                  class="chat-input"
                  type="textarea"
                  :autosize="{ minRows: 3, maxRows: 6 }"
                  placeholder="输入消息...（Enter 发送，Shift+Enter 换行）"
                  @keydown.enter.exact.prevent="handleSend"
                />
                <NButton v-if="!sending" class="chat-send-btn" type="primary" :disabled="!input.trim() || !selectedMappingId" @click="handleSend">发送</NButton>
                <NButton v-else class="chat-send-btn" type="error" @click="handleStop">停止</NButton>
              </div>
            </div>
          </template>
        </NCard>
      </div>

      <div class="chat-admin-side">
        <NCard class="chat-side-card" :bordered="false">
          <template #header>思考内容</template>
          <div v-if="!currentReasoning" class="chat-side-empty">开启思考模式并且上游返回思考内容后，这里会展示。</div>
          <pre v-else class="chat-side-pre">{{ currentReasoning }}</pre>
        </NCard>

        <NCard class="chat-side-card chat-attempts-card" :bordered="false">
          <template #header>调用详细过程</template>
          <div v-if="!lastAttempts || (lastAttempts as unknown[]).length === 0" class="chat-side-empty">发送一条消息后显示本次请求的每次尝试。</div>
          <div v-else class="chat-attempt-list">
            <article v-for="(att, aIdx) in lastAttempts" :key="aIdx" class="chat-attempt-card">
              <div class="chat-attempt-head">
                <div>
                  <div class="chat-attempt-title">第 {{ att.attemptIndex ?? (aIdx + 1) }} 次尝试 · {{ att.siteName || '未知站点' }}</div>
                  <div class="chat-attempt-meta">{{ att.attemptedModel || '未知模型' }} / {{ att.siteModelName || '-' }}</div>
                </div>
                <span :class="['chat-attempt-status', `chat-attempt-status-${attemptStatusClass(att)}`]">{{ attemptStatusLabel(att) }}</span>
              </div>
              <div class="chat-attempt-tokens">
                <span class="chat-attempt-token-chip">耗时 {{ formatDuration(att.totalDurationMs) }}</span>
                <span class="chat-attempt-token-chip">首字 {{ formatDuration(att.firstTokenLatencyMs) }}</span>
                <span class="chat-attempt-token-chip">流式 {{ att.isStreaming ? '是' : '否' }}</span>
                <span class="chat-attempt-token-chip">转发 {{ att.forwardingMode || '-' }}</span>
                <span class="chat-attempt-token-chip">协议 {{ att.upstreamProtocolType || '-' }}</span>
                <span class="chat-attempt-token-chip">输入 {{ formatNumber(att.inputTokens) }}</span>
                <span class="chat-attempt-token-chip">缓存 {{ formatNumber(att.cachedTokens) }}</span>
                <span class="chat-attempt-token-chip">输出 {{ formatNumber(att.outputTokens) }}</span>
                <span class="chat-attempt-token-chip">总计 {{ formatNumber(att.totalTokens) }}</span>
              </div>
              <div v-if="attemptError(att)" class="chat-attempt-error">{{ attemptError(att) }}</div>
              <button type="button" class="chat-attempt-detail-toggle" @click="toggleAttemptDetail(aIdx)">
                {{ expandedAttemptIndexes.has(aIdx) ? '收起请求/响应' : '展开请求/响应' }}
              </button>
              <div :class="['chat-attempt-detail-body', { show: expandedAttemptIndexes.has(aIdx) }]">
                <div class="chat-attempt-detail-title">请求体</div>
                <pre class="chat-attempt-detail-pre">{{ att.requestBody || '无' }}</pre>
                <div class="chat-attempt-detail-title">响应体</div>
                <pre class="chat-attempt-detail-pre">{{ att.responseBody || '无' }}</pre>
              </div>
            </article>
          </div>
        </NCard>
      </div>
    </div>
  </div>
</div>
</template>

<style scoped>
.chat-admin-shell {
  width: 100%;
  height: 100%;
  min-width: 0;
  min-height: 0;
  overflow-x: auto;
  overflow-y: hidden;
  padding-bottom: 8px;
}

.chat-admin-stage {
  width: 100%;
  height: 100%;
  min-width: 0;
  min-height: 0;
  transform-origin: top left;
}

.chat-admin-page {
  --chat-admin-design-width: 1360;
  display: grid;
  grid-template-columns: minmax(0, 1.8fr) minmax(300px, 1fr);
  gap: 16px;
  align-items: stretch;
  width: 100%;
  min-width: 1080px;
  height: 100%;
  min-height: 620px;
  overflow: hidden;
}

.chat-admin-main,
.chat-admin-side,
.chat-card {
  min-height: 0;
  min-width: 0;
}

.chat-admin-main,
.chat-admin-side {
  overflow: hidden;
}

.chat-admin-side {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.chat-card {
  height: 100%;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.chat-toolbar {
  display: flex;
  align-items: flex-end;
  gap: 14px;
  flex-wrap: wrap;
  padding: 16px 20px;
  border-bottom: 1px solid var(--border-color-global);
  flex-shrink: 0;
}

.chat-toolbar-field {
  display: flex;
  flex-direction: column;
  justify-content: flex-end;
  min-width: 220px;
  gap: 8px;
}

.chat-toolbar-field-model {
  min-width: 220px;
}

.chat-toolbar-field-target {
  min-width: 280px;
  flex: 1 1 280px;
}

.chat-toolbar-field-label {
  margin: 0;
  font-size: 12px;
  font-weight: 600;
  line-height: 1.4;
  color: var(--text-primary);
}

.chat-toolbar-toggle-group,
.chat-toolbar-actions {
  display: flex;
  align-items: center;
  gap: 12px;
  height: 40px;
  flex-wrap: nowrap;
}

.chat-toolbar-switch {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--text-primary);
  font-size: 13px;
  white-space: nowrap;
}

.chat-reasoning-effort {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  min-height: 34px;
  padding: 0 10px;
  border: 1px solid var(--border-color-global);
  border-radius: 10px;
  background: var(--bg-input, #f8fafc);
  flex: 0 0 auto;
}

.chat-reasoning-effort-label {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-color-secondary);
  white-space: nowrap;
}

.chat-reasoning-effort-select {
  width: 82px;
}

.chat-messages {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 16px 20px 0;
  display: flex;
  flex-direction: column;
  gap: 12px;
  background: var(--bg-card);
  position: relative;
}

.chat-empty {
  position: absolute;
  inset: 16px 0 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  color: var(--text-color-secondary);
  pointer-events: none;
}

.chat-empty-icon {
  font-size: 32px;
  margin-bottom: 8px;
}

.chat-msg {
  display: flex;
}

.chat-msg.user {
  justify-content: flex-end;
}

.chat-msg.assistant {
  justify-content: flex-start;
}

.chat-bubble {
  max-width: 82%;
  padding: 12px 14px;
  border-radius: 14px;
  font-size: 14px;
  line-height: 1.7;
  word-break: break-word;
  white-space: pre-wrap;
  box-shadow: 0 1px 2px rgba(15, 23, 42, 0.06);
}

.chat-bubble-user {
  background: #0d6efd;
  color: #fff;
  border-bottom-right-radius: 4px;
}

.chat-bubble-ai {
  background: #f8fafc;
  color: #1f2937;
  border: 1px solid #e5e7eb;
  border-bottom-left-radius: 4px;
}

.chat-bubble-error {
  background: #fff1f2;
  color: #be123c;
  border: 1px solid #fecdd3;
  border-bottom-left-radius: 4px;
}

.chat-bubble-meta {
  margin-top: 6px;
  color: rgba(100, 116, 139, 0.9);
  font-size: 11px;
}

.chat-bubble-user .chat-bubble-meta {
  color: rgba(255, 255, 255, 0.75);
}

.chat-text {
  white-space: pre-wrap;
  word-break: break-word;
}

.chat-input-area {
  padding: 12px 0 4px;
  background: var(--bg-card);
  flex-shrink: 0;
}

.chat-input-wrapper {
  display: flex;
  gap: 8px;
  align-items: stretch;
}

.chat-input {
  flex: 1;
}

.chat-send-btn {
  white-space: nowrap;
  min-width: 82px;
  min-height: 88px;
}

.chat-side-card {
  min-height: 0;
  overflow: hidden;
  border: 1px solid var(--border-color-global);
  border-radius: 14px;
  background: var(--bg-card);
}

.chat-attempts-card {
  flex: 1;
}

.chat-attempts-card :deep(.n-card__content),
.chat-side-card :deep(.n-card__content) {
  min-height: 0;
  overflow: auto;
}

.chat-side-empty {
  color: var(--text-color-secondary);
  font-size: 13px;
  line-height: 1.7;
}

.chat-side-pre {
  white-space: pre-wrap;
  word-break: break-word;
  overflow-wrap: anywhere;
  margin: 0;
  font-size: 13px;
  line-height: 1.65;
  max-height: 260px;
  overflow: auto;
  background: #f8fafc;
  border: 1px solid #e5e7eb;
  border-radius: 10px;
  padding: 12px;
}

.chat-attempt-list {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.chat-attempt-card {
  border: 1px solid #e5e7eb;
  border-radius: 10px;
  padding: 12px;
  background: #fff;
  min-width: 0;
}

.chat-attempt-head {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  align-items: flex-start;
  margin-bottom: 8px;
}

.chat-attempt-title {
  color: #0f172a;
  font-size: 14px;
  font-weight: 600;
  word-break: break-word;
}

.chat-attempt-meta {
  color: #64748b;
  font-size: 12px;
  line-height: 1.7;
  overflow-wrap: anywhere;
  word-break: break-word;
}

.chat-attempt-status {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 52px;
  min-height: 24px;
  padding: 4px 8px;
  border-radius: 999px;
  font-size: 12px;
  font-weight: 600;
}

.chat-attempt-status-success {
  background: #dcfce7;
  color: #166534;
}

.chat-attempt-status-fail {
  background: #fee2e2;
  color: #b91c1c;
}

.chat-attempt-tokens {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 8px;
}

.chat-attempt-token-chip {
  display: inline-flex;
  align-items: center;
  padding: 4px 8px;
  border-radius: 999px;
  background: #f1f5f9;
  color: #334155;
  font-size: 12px;
}

.chat-attempt-error {
  margin-top: 8px;
  color: #b91c1c;
  background: #fff1f2;
  border-radius: 8px;
  padding: 8px 10px;
  font-size: 12px;
  line-height: 1.6;
}

.chat-attempt-detail-toggle {
  display: inline-block;
  margin-top: 8px;
  padding: 0;
  border: 0;
  background: transparent;
  color: #6366f1;
  cursor: pointer;
  font-size: 12px;
}

.chat-attempt-detail-toggle:hover {
  text-decoration: underline;
}

.chat-attempt-detail-body {
  display: none;
  margin-top: 6px;
}

.chat-attempt-detail-body.show {
  display: block;
}

.chat-attempt-detail-title {
  margin: 6px 0;
  color: #64748b;
  font-size: 12px;
  font-weight: 600;
}

.chat-attempt-detail-pre {
  white-space: pre-wrap;
  word-break: break-word;
  margin: 0 0 6px;
  font-size: 11px;
  line-height: 1.5;
  max-height: 200px;
  overflow: auto;
  background: #f8fafc;
  border: 1px solid #e5e7eb;
  border-radius: 6px;
  padding: 8px;
}

[data-theme='dark'] .chat-bubble-ai,
[data-theme='dark'] .chat-side-pre,
[data-theme='dark'] .chat-attempt-card {
  background: rgba(255, 255, 255, 0.08);
  color: var(--text-primary);
}

@media (max-width: 1400px) {
  .chat-admin-page {
    grid-template-columns: minmax(0, 1.5fr) minmax(280px, 0.9fr);
  }
}

@media (max-width: 1199.98px) {
  .chat-admin-shell {
    overflow: visible;
    padding-bottom: 0;
  }

  .chat-admin-stage {
    min-width: 0;
  }

  .chat-admin-page {
    grid-template-columns: minmax(0, 1fr);
    height: auto;
    min-width: 0;
    min-height: 0;
    overflow: visible;
  }

  .chat-admin-main {
    min-height: 620px;
    overflow: visible;
  }

  .chat-admin-side {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    overflow: visible;
  }

  .chat-side-card,
  .chat-attempts-card {
    min-height: 280px;
  }
}

@media (max-width: 767.98px) {
  .chat-admin-main {
    min-height: 560px;
  }

  .chat-admin-side {
    grid-template-columns: minmax(0, 1fr);
  }

  .chat-toolbar {
    align-items: stretch;
    flex-direction: column;
  }

  .chat-toolbar-field,
  .chat-toolbar-field-model,
  .chat-toolbar-field-target {
    min-width: 0;
    width: 100%;
  }

  .chat-toolbar-toggle-group,
  .chat-toolbar-actions {
    flex-wrap: wrap;
  }

  .chat-bubble {
    max-width: 100%;
  }

  .chat-input-wrapper {
    flex-direction: column;
  }

  .chat-send-btn {
    min-height: 40px;
  }
}
</style>

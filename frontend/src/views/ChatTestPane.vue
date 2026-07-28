<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { NCard, NSpace, NSelect, NInput, NButton, NTag, NSwitch, NCollapse, NCollapseItem, useMessage, type SelectOption } from 'naive-ui'
import * as chatApi from '@/api/chat'
import type { ChatModel, ChatModelTarget } from '@/api/chat'

interface Message { role: 'user' | 'assistant'; content: string; reasoning?: string; meta?: unknown }

const message = useMessage()
const models = ref<ChatModel[]>([])
const selectedModelId = ref<string | null>(null)
// 指定具体站点+模型组合（mappingId）。空=走完整 fallback 链。
const targets = ref<ChatModelTarget[]>([])
const selectedMappingId = ref<string | null>(null)
const input = ref('')
const sending = ref(false)
const messages = ref<Message[]>([])
const streamingContent = ref('')
const enableReasoning = ref(false)
const reasoningEffort = ref('high')
const enableStreaming = ref(true)
const reasoningOptions: SelectOption[] = [
  { label: '低 (low)', value: 'low' },
  { label: '中 (medium)', value: 'medium' },
  { label: '高 (high)', value: 'high' },
  { label: '超高 (xhigh)', value: 'xhigh' },
  { label: '最大 (max)', value: 'max' }
]
const streamingReasoning = ref('')
// 最近一次的调用链路（meta 事件携带的路由尝试明细）
const lastAttempts = ref<unknown[] | null>(null)
const messagesContainer = ref<HTMLElement | null>(null)
let abortController: AbortController | null = null

async function loadModels(): Promise<void> {
  models.value = await chatApi.getChatModels()
  if (models.value.length > 0) selectedModelId.value = models.value[0].modelId
}

// 模型变化时加载该模型的可用站点+模型目标列表。
watch(selectedModelId, async (id) => {
  selectedMappingId.value = null
  targets.value = []
  lastAttempts.value = null
  if (!id) return
  try {
    targets.value = await chatApi.getChatTargets(id)
  } catch {
    // 目标列表加载失败不阻塞对话（退化为走完整 fallback 链）
  }
}, { immediate: false })

// targets 是 ref，需要用 computed 保证选项随 targets 变化更新
const targetOptionsComputed = computed<SelectOption[]>(() => [
  { label: '自动（走完整 fallback 链）', value: '' },
  ...targets.value.map((t) => ({ label: `${t.siteName} · ${t.siteModelName}`, value: t.mappingId }))
])

async function scrollToBottom(): Promise<void> {
  await nextTick()
  if (messagesContainer.value) messagesContainer.value.scrollTop = messagesContainer.value.scrollHeight
}

async function handleSend(): Promise<void> {
  const text = input.value.trim()
  if (!text || !selectedModelId.value || sending.value) return
  input.value = ''
  messages.value.push({ role: 'user', content: text })
  messages.value.push({ role: 'assistant', content: '' })
  const assistantIdx = messages.value.length - 1
  streamingContent.value = ''
  streamingReasoning.value = ''
  lastAttempts.value = null
  sending.value = true
  abortController = new AbortController()

  // mappingId：空字符串表示不指定（走完整 fallback 链）
  const mappingId = selectedMappingId.value || undefined
  const commonOpts = {
    modelId: selectedModelId.value,
    message: text,
    mappingId,
    enableReasoning: enableReasoning.value,
    reasoningEffort: reasoningEffort.value,
    signal: abortController.signal
  }

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
        const m = meta as { attempts?: unknown[] }
        if (m?.attempts) {
          lastAttempts.value = m.attempts
          messages.value[assistantIdx].meta = m
        }
      },
      onDone: () => {
        sending.value = false
        if (!streamingContent.value) {
          messages.value[assistantIdx].content = '(空回复)'
        }
      },
      onError: (err) => {
        sending.value = false
        messages.value[assistantIdx].content = `(错误：${err.message})`
        message.error(err.message)
      }
    })
  } else {
    // 非流式
    try {
      const result = await chatApi.sendChat(commonOpts)
      sending.value = false
      if (result.success) {
        messages.value[assistantIdx].content = result.content || '(空回复)'
        if (result.reasoningContent) messages.value[assistantIdx].reasoning = result.reasoningContent
      } else {
        messages.value[assistantIdx].content = `(错误：${result.error || '未知错误'})`
      }
    } catch (e) {
      sending.value = false
      messages.value[assistantIdx].content = `(错误：${(e as Error).message})`
      message.error((e as Error).message)
    }
  }
}

function handleStop(): void {
  abortController?.abort()
  sending.value = false
}

function handleClear(): void {
  messages.value = []
  streamingContent.value = ''
  streamingReasoning.value = ''
  lastAttempts.value = null
}

onMounted(loadModels)
</script>

<template>
  <NCard style="flex: 1; display: flex; flex-direction: column" content-style="flex: 1; display: flex; flex-direction: column; padding: 0">
    <template #header>
      <NSpace justify="space-between" align="center" wrap>
        <NSpace align="center" :size="12" wrap>
          <NSelect
            v-model:value="selectedModelId"
            :options="models.map(m => ({ label: `${m.displayName} (${m.availableSiteCount}站点)`, value: m.modelId }))"
            placeholder="选择模型"
            style="width: 240px"
          />
          <NSelect
            v-model:value="selectedMappingId"
            :options="targetOptionsComputed"
            placeholder="目标站点"
            style="width: 220px"
          />
          <NSpace align="center" :size="4">
            <NSwitch v-model:value="enableReasoning" size="small" />
            <span style="font-size: 13px">思考</span>
          </NSpace>
          <NSelect
            v-if="enableReasoning"
            v-model:value="reasoningEffort"
            :options="reasoningOptions"
            size="small"
            style="width: 120px"
          />
          <NSpace align="center" :size="4">
            <NSwitch v-model:value="enableStreaming" size="small" />
            <span style="font-size: 13px">流式</span>
          </NSpace>
        </NSpace>
        <NButton size="small" quaternary @click="handleClear">清空</NButton>
      </NSpace>
    </template>

    <div ref="messagesContainer" class="chat-messages">
      <div v-if="messages.length === 0" class="chat-empty">
        <NTag size="large" :bordered="false">输入消息开始对话（走代理链路，含故障转移）</NTag>
      </div>
      <div v-for="(msg, idx) in messages" :key="idx" :class="['chat-msg', msg.role]">
        <div class="chat-bubble">
          <div class="chat-role">{{ msg.role === 'user' ? '我' : 'AI' }}</div>
          <div v-if="msg.reasoning" class="chat-reasoning">{{ msg.reasoning }}</div>
          <div class="chat-text">{{ msg.content || (sending && idx === messages.length - 1 ? '正在输入...' : '') }}</div>
        </div>
      </div>
    </div>

    <!-- 调用链路明细（meta 事件的路由尝试） -->
    <NCollapse v-if="lastAttempts && (lastAttempts as unknown[]).length > 0" class="attempts-panel" :default-expanded-names="[]">
      <NCollapseItem title="调用详细过程（路由尝试链路）" name="attempts">
        <div v-for="(att, aIdx) in (lastAttempts as any[])" :key="aIdx" class="attempt-row">
          <NTag size="tiny" :type="att.status === 'success' ? 'success' : att.status === 'fail' ? 'error' : 'warning'" :bordered="false">
            {{ att.status || '未知' }}
          </NTag>
          <span>{{ att.siteName || att.site }} · {{ att.modelName || att.model }}</span>
          <span v-if="att.durationMs" style="font-size: 12px; color: var(--text-color-secondary)">{{ att.durationMs }}ms</span>
          <span v-if="att.error" style="font-size: 12px; color: var(--text-color-secondary)">{{ att.error }}</span>
        </div>
      </NCollapseItem>
    </NCollapse>

    <div class="chat-input-area">
      <NSpace :size="8" align="flex-end">
        <NInput
          v-model:value="input"
          type="textarea"
          :autosize="{ minRows: 1, maxRows: 4 }"
          placeholder="输入消息，Enter 发送，Shift+Enter 换行"
          style="width: 100%; max-width: 600px"
          @keydown.enter.exact.prevent="handleSend"
        />
        <NButton v-if="!sending" type="primary" :disabled="!input.trim()" @click="handleSend">发送</NButton>
        <NButton v-else type="error" @click="handleStop">停止</NButton>
      </NSpace>
    </div>
  </NCard>
</template>

<style scoped>
.chat-messages { flex: 1; overflow-y: auto; padding: 16px 24px; }
.chat-empty { display: flex; justify-content: center; align-items: center; height: 100%; }
.chat-msg { display: flex; margin-bottom: 16px; }
.chat-msg.user { justify-content: flex-end; }
.chat-msg.assistant { justify-content: flex-start; }
.chat-bubble { max-width: 70%; padding: 10px 14px; border-radius: 12px; }
.chat-msg.user .chat-bubble { background: #6C9EFF; color: white; }
.chat-msg.assistant .chat-bubble { background: var(--bg-input, #f0f0f0); }
[data-theme='dark'] .chat-msg.assistant .chat-bubble { background: rgba(255,255,255,0.08); }
.chat-role { font-size: 11px; opacity: 0.7; margin-bottom: 4px; }
.chat-reasoning { font-size: 12px; opacity: 0.6; margin-bottom: 6px; padding: 4px 8px; border-left: 2px solid currentColor; white-space: pre-wrap; }
.chat-text { white-space: pre-wrap; word-break: break-word; line-height: 1.5; }
.chat-input-area { padding: 12px 24px; border-top: 1px solid var(--border-color-global); }
.attempts-panel { border-top: 1px solid var(--border-color-global); max-height: 200px; overflow-y: auto; }
.attempt-row { display: flex; align-items: center; gap: 8px; padding: 4px 0; font-size: 13px; }
</style>

<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'
import { NCard, NSpace, NSelect, NInput, NButton, NTag, useMessage } from 'naive-ui'
import * as chatApi from '@/api/chat'
import type { ChatModel } from '@/api/chat'

interface Message { role: 'user' | 'assistant'; content: string }

const message = useMessage()
const models = ref<ChatModel[]>([])
const selectedModel = ref<string | null>(null)
const input = ref('')
const sending = ref(false)
const messages = ref<Message[]>([])
const streamingContent = ref('')
const messagesContainer = ref<HTMLElement | null>(null)
let abortController: AbortController | null = null

const modelOptions = computed(() => models.value.map((m) => ({ label: `${m.displayName} (${m.availableSiteCount}站点)`, value: m.modelId })))

async function loadModels(): Promise<void> {
  models.value = await chatApi.getChatModels()
  if (models.value.length > 0) selectedModel.value = models.value[0].modelId
}

async function scrollToBottom(): Promise<void> {
  await nextTick()
  if (messagesContainer.value) messagesContainer.value.scrollTop = messagesContainer.value.scrollHeight
}

async function handleSend(): Promise<void> {
  const text = input.value.trim()
  if (!text || !selectedModel.value || sending.value) return
  input.value = ''
  messages.value.push({ role: 'user', content: text })
  messages.value.push({ role: 'assistant', content: '' })
  const assistantIdx = messages.value.length - 1
  streamingContent.value = ''
  sending.value = true
  abortController = new AbortController()

  await chatApi.sendChatStream(
    { model: selectedModel.value, messages: [{ role: 'user', content: text }], signal: abortController.signal },
    (delta) => {
      streamingContent.value += delta
      messages.value[assistantIdx].content = streamingContent.value
      scrollToBottom()
    },
    () => {
      sending.value = false
      if (!streamingContent.value) {
        messages.value[assistantIdx].content = '(空回复)'
      }
    },
    (err) => {
      sending.value = false
      messages.value[assistantIdx].content = `(错误：${err.message})`
      message.error(err.message)
    }
  )
}

function handleStop(): void {
  abortController?.abort()
  sending.value = false
}

function handleClear(): void {
  messages.value = []
  streamingContent.value = ''
}

onMounted(loadModels)
</script>

<template>
  <div class="page-container" style="height: calc(100vh - 88px); display: flex; flex-direction: column">
    <NCard style="flex: 1; display: flex; flex-direction: column" content-style="flex: 1; display: flex; flex-direction: column; padding: 0">
      <template #header>
        <NSpace justify="space-between" align="center">
          <NSpace align="center" :size="12">
            <span>对话测试</span>
            <NSelect v-model:value="selectedModel" :options="modelOptions" placeholder="选择模型" style="width: 280px" />
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
            <div class="chat-text">{{ msg.content || (sending.value && idx === messages.length - 1 ? '正在输入...' : '') }}</div>
          </div>
        </div>
      </div>

      <div class="chat-input-area">
        <NSpace :size="8" align="flex-end">
          <NInput
            v-model:value="input"
            type="textarea"
            :autosize="{ minRows: 1, maxRows: 4 }"
            placeholder="输入消息，Enter 发送，Shift+Enter 换行"
            style="width: 600px"
            @keydown.enter.exact.prevent="handleSend"
          />
          <NButton v-if="!sending" type="primary" :disabled="!input.trim()" @click="handleSend">发送</NButton>
          <NButton v-else type="error" @click="handleStop">停止</NButton>
        </NSpace>
      </div>
    </NCard>
  </div>
</template>

<style scoped>
.chat-messages { flex: 1; overflow-y: auto; padding: 16px 24px; }
.chat-empty { display: flex; justify-content: center; align-items: center; height: 100%; }
.chat-msg { display: flex; margin-bottom: 16px; }
.chat-msg.user { justify-content: flex-end; }
.chat-msg.assistant { justify-content: flex-start; }
.chat-bubble { max-width: 70%; padding: 10px 14px; border-radius: 12px; }
.chat-msg.user .chat-bubble { background: #6C9EFF; color: white; }
.chat-msg.assistant .chat-bubble { background: var(--n-color-target, #f0f0f0); }
[data-theme='dark'] .chat-msg.assistant .chat-bubble { background: rgba(255,255,255,0.08); }
.chat-role { font-size: 11px; opacity: 0.7; margin-bottom: 4px; }
.chat-text { white-space: pre-wrap; word-break: break-word; line-height: 1.5; }
.chat-input-area { padding: 12px 24px; border-top: 1px solid var(--n-border-color); }
</style>

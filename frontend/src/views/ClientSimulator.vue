<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { NCard, NSpace, NInput, NButton, NSelect, NTag, useMessage, type SelectOption } from 'naive-ui'
import * as devApi from '@/api/developer'

const message = useMessage()
const baseUrl = ref('')
const accessKey = ref('')
const models = ref<Array<{ modelName: string; canUseOpenAi: boolean; canUseAnthropic: boolean }>>([])
const selectedModel = ref<string | null>(null)
const inputText = ref('')
const responseText = ref('')
const sending = ref(false)
let abortController: AbortController | null = null

onMounted(async () => {
  try {
    const init = await devApi.getDeveloperInit()
    baseUrl.value = init.defaultBaseUrl || ''
    accessKey.value = init.defaultAccessKey || ''
    models.value = init.models || []
    if (models.value.length > 0) selectedModel.value = init.defaultOpenAiModel || models.value[0].modelName
  } catch {
    // 功能开关关闭或加载失败，保留空表单
  }
})

const modelOptions: SelectOption[] = []
const modelOptionsRef = ref<SelectOption[]>([])
async function refreshOptions() {
  modelOptionsRef.value = models.value.map((m) => ({ label: m.modelName, value: m.modelName }))
}
// 监听 models 变化更新选项
import { watch } from 'vue'
watch(models, refreshOptions, { immediate: true })

async function handleSend(): Promise<void> {
  const text = inputText.value.trim()
  if (!text || !selectedModel.value || !baseUrl.value || !accessKey.value || sending.value) return
  responseText.value = ''
  sending.value = true
  abortController = new AbortController()

  const url = `${baseUrl.value.replace(/\/$/, '')}/v1/chat/completions`
  const body = {
    model: selectedModel.value,
    messages: [{ role: 'user', content: text }],
    stream: false
  }

  try {
    const resp = await fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${accessKey.value}`
      },
      body: JSON.stringify(body),
      signal: abortController.signal
    })
    const data = await resp.json()
    if (!resp.ok) {
      responseText.value = `错误 ${resp.status}: ${JSON.stringify(data)}`
    } else {
      const content = data.choices?.[0]?.message?.content
      responseText.value = content || JSON.stringify(data, null, 2)
    }
  } catch (e) {
    if ((e as Error).name === 'AbortError') return
    responseText.value = `请求失败: ${(e as Error).message}`
    message.error((e as Error).message)
  } finally {
    sending.value = false
  }
}

function handleStop(): void {
  abortController?.abort()
  sending.value = false
}

function handleClear(): void {
  inputText.value = ''
  responseText.value = ''
}
</script>

<template>
  <NCard>
    <template #header>
      <NSpace align="center" :size="12" wrap>
        <span>客户端模拟器</span>
        <NTag size="tiny" :bordered="false">OpenAI 兼容</NTag>
      </NSpace>
    </template>

    <NSpace vertical :size="12">
      <NSpace :size="12" wrap>
        <NInput v-model:value="baseUrl" placeholder="Base URL（如 http://127.0.0.1:15029）" style="width: 280px" />
        <NInput v-model:value="accessKey" placeholder="Access Key（sk-...）" style="width: 240px" />
        <NSelect v-model:value="selectedModel" :options="modelOptionsRef" placeholder="选择模型" filterable style="width: 200px" />
      </NSpace>

      <NInput
        v-model:value="inputText"
        type="textarea"
        :autosize="{ minRows: 3, maxRows: 8 }"
        placeholder="输入消息，使用 OpenAI /v1/chat/completions 格式发送（非流式）"
        @keydown.ctrl.enter="handleSend"
      />

      <NSpace :size="8">
        <NButton v-if="!sending" type="primary" :disabled="!inputText.trim()" @click="handleSend">发送（Ctrl+Enter）</NButton>
        <NButton v-else type="error" @click="handleStop">停止</NButton>
        <NButton quaternary @click="handleClear">清空</NButton>
      </NSpace>

      <div v-if="responseText" class="response-box">
        <div class="response-label">响应：</div>
        <pre class="response-text">{{ responseText }}</pre>
      </div>
    </NSpace>
  </NCard>
</template>

<style scoped>
.response-box { background: var(--bg-input, #f6f6f6); border-radius: 8px; padding: 12px; }
[data-theme='dark'] .response-box { background: rgba(255,255,255,0.05); }
.response-label { font-size: 12px; color: var(--text-color-secondary); margin-bottom: 6px; }
.response-text { margin: 0; font-size: 13px; white-space: pre-wrap; word-break: break-word; max-height: 400px; overflow: auto; font-family: monospace; }
</style>

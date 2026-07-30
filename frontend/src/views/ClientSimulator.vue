<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { NButton, NCard, NInput, NSelect, NSpace, NSwitch, NTabPane, NTabs, NTag, useMessage, type SelectOption } from 'naive-ui'
import * as devApi from '@/api/developer'

interface SimulatorTab {
  key: string
  label: string
  endpoint: string
  method: 'GET' | 'POST'
  streamable?: boolean
  buildBody: () => unknown
}

const message = useMessage()
const baseUrl = ref('')
const accessKey = ref('')
const models = ref<Array<{ modelName: string; canUseOpenAi: boolean; canUseAnthropic: boolean }>>([])
const selectedModel = ref<string | null>(null)
const inputText = ref('你好，请简单介绍一下你自己。')
const activeTab = ref('models')
const streamEnabled = ref<Record<string, boolean>>({ openai: false, anthropic: false, responses: false, completions: false })
const responses = ref<Record<string, string>>({})
const sendingTab = ref<string | null>(null)
let abortController: AbortController | null = null

const normalizedBaseUrl = computed(() => baseUrl.value.replace(/\/$/, ''))
const modelOptions = computed<SelectOption[]>(() => models.value.map((m) => ({ label: m.modelName, value: m.modelName })))
const supportHint = computed(() => {
  const model = models.value.find((m) => m.modelName === selectedModel.value)
  if (!model) return '请选择支持当前协议的模型。'
  const parts = []
  if (model.canUseOpenAi) parts.push('OpenAI 兼容')
  if (model.canUseAnthropic) parts.push('Anthropic 兼容')
  return parts.length ? `当前模型支持：${parts.join(' / ')}` : '当前模型暂无可用协议。'
})

function modelName(): string {
  return selectedModel.value || models.value[0]?.modelName || ''
}

const tabs: SimulatorTab[] = [
  { key: 'models', label: '模型列表', endpoint: '/v1/models', method: 'GET', buildBody: () => null },
  { key: 'openai', label: 'OpenAI 聊天', endpoint: '/v1/chat/completions', method: 'POST', streamable: true, buildBody: () => ({ model: modelName(), messages: [{ role: 'user', content: inputText.value }], stream: streamEnabled.value.openai === true }) },
  { key: 'anthropic', label: 'Anthropic 聊天', endpoint: '/v1/messages', method: 'POST', streamable: true, buildBody: () => ({ model: modelName(), max_tokens: 1024, messages: [{ role: 'user', content: inputText.value }], stream: streamEnabled.value.anthropic === true }) },
  { key: 'responses', label: 'Responses', endpoint: '/v1/responses', method: 'POST', streamable: true, buildBody: () => ({ model: modelName(), input: inputText.value, stream: streamEnabled.value.responses === true }) },
  { key: 'completions', label: 'Completions', endpoint: '/v1/completions', method: 'POST', streamable: true, buildBody: () => ({ model: modelName(), prompt: inputText.value, max_tokens: 256, stream: streamEnabled.value.completions === true }) },
  { key: 'embeddings', label: 'Embeddings', endpoint: '/v1/embeddings', method: 'POST', buildBody: () => ({ model: modelName(), input: inputText.value }) },
  { key: 'countTokens', label: 'Count Tokens', endpoint: '/v1/messages/count_tokens', method: 'POST', buildBody: () => ({ model: modelName(), messages: [{ role: 'user', content: inputText.value }] }) },
  { key: 'responsesCompact', label: 'Responses Compact', endpoint: '/v1/responses/compact', method: 'POST', buildBody: () => ({ model: modelName(), input: inputText.value }) }
]

function getTab(key: string): SimulatorTab {
  return tabs.find((tab) => tab.key === key) ?? tabs[0]
}

function endpointUrl(tab: SimulatorTab): string {
  return `${normalizedBaseUrl.value}${tab.endpoint}`
}

function requestExample(tab: SimulatorTab): string {
  const headers: Record<string, string> = { Authorization: 'Bearer ***' }
  if (tab.method === 'POST') headers['Content-Type'] = 'application/json'
  return JSON.stringify({ method: tab.method, url: endpointUrl(tab), headers, body: tab.method === 'POST' ? tab.buildBody() : undefined }, null, 2)
}

async function copyText(text: string): Promise<void> {
  if (!text) return
  if (window.isSecureContext && navigator.clipboard) {
    try {
      await navigator.clipboard.writeText(text)
      message.success('已复制')
      return
    } catch {
      // HTTP 下剪贴板 API 可能不可用，继续使用 execCommand 兜底。
    }
  }
  const textarea = document.createElement('textarea')
  textarea.value = text
  textarea.style.position = 'fixed'
  textarea.style.left = '-9999px'
  document.body.appendChild(textarea)
  textarea.focus()
  textarea.select()
  try {
    document.execCommand('copy')
    message.success('已复制')
  } catch {
    message.error('复制失败，请手动复制')
  } finally {
    document.body.removeChild(textarea)
  }
}

async function sendRequest(tabKey = activeTab.value): Promise<void> {
  const tab = getTab(tabKey)
  if (!baseUrl.value || !accessKey.value || sendingTab.value) return
  if (tab.method === 'POST' && !modelName()) {
    message.warning('请选择模型')
    return
  }
  sendingTab.value = tab.key
  responses.value[tab.key] = '请求中...'
  abortController = new AbortController()
  try {
    const resp = await fetch(endpointUrl(tab), {
      method: tab.method,
      headers: {
        Authorization: `Bearer ${accessKey.value}`,
        ...(tab.method === 'POST' ? { 'Content-Type': 'application/json' } : {})
      },
      body: tab.method === 'POST' ? JSON.stringify(tab.buildBody()) : undefined,
      signal: abortController.signal
    })
    const text = await resp.text()
    try {
      responses.value[tab.key] = JSON.stringify(JSON.parse(text), null, 2)
    } catch {
      responses.value[tab.key] = text || `HTTP ${resp.status}`
    }
    if (!resp.ok) responses.value[tab.key] = `HTTP ${resp.status}\n${responses.value[tab.key]}`
  } catch (e) {
    if ((e as Error).name !== 'AbortError') {
      responses.value[tab.key] = `请求失败：${(e as Error).message}`
      message.error((e as Error).message)
    }
  } finally {
    sendingTab.value = null
  }
}

function stopRequest(): void {
  abortController?.abort()
  sendingTab.value = null
}

onMounted(async () => {
  try {
    const init = await devApi.getDeveloperInit()
    baseUrl.value = init.defaultBaseUrl || ''
    accessKey.value = init.defaultAccessKey || ''
    models.value = init.models || []
    if (models.value.length > 0) selectedModel.value = init.defaultOpenAiModel || models.value[0].modelName
  } catch {
    // 功能开关关闭或加载失败时保留空表单，允许手动输入。
  }
})

watch(activeTab, (key) => {
  const tab = getTab(key)
  responses.value[tab.key] ||= '尚未请求'
}, { immediate: true })
</script>

<template>
  <div class="client-simulator-page">
    <div class="simulator-page-header">
      <div>
        <h3 class="simulator-title">客户端模拟</h3>
        <p class="simulator-subtitle">按真实代理 URL 链路模拟 OpenAI 与 Anthropic 客户端调用，自动带入本机地址与访问密钥</p>
      </div>
    </div>

    <NCard class="simulator-config-card">
      <div class="simulator-config-grid">
        <label class="simulator-field">
          <span>代理根地址</span>
          <NInput v-model:value="baseUrl" placeholder="http://127.0.0.1:15029" />
        </label>
        <label class="simulator-field">
          <span>访问密钥</span>
          <div class="simulator-input-group">
            <NInput v-model:value="accessKey" placeholder="sk-..." />
            <NButton secondary type="primary" @click="copyText(accessKey)">复制</NButton>
          </div>
        </label>
        <label class="simulator-field">
          <span>模型名</span>
          <NSelect v-model:value="selectedModel" :options="modelOptions" filterable tag placeholder="输入对外模型名" />
          <small>{{ supportHint }}</small>
        </label>
        <label class="simulator-field">
          <span>测试消息</span>
          <NInput v-model:value="inputText" type="textarea" :autosize="{ minRows: 3, maxRows: 6 }" placeholder="输入测试消息..." />
        </label>
      </div>
    </NCard>

    <NCard class="simulator-tabs-card" :content-style="{ padding: '16px' }">
      <NTabs v-model:value="activeTab" type="line" animated>
        <NTabPane v-for="tab in tabs" :key="tab.key" :name="tab.key" :tab="tab.label">
          <div class="simulator-endpoint-row">
            <code class="simulator-endpoint-code">{{ endpointUrl(tab) }}</code>
            <div class="simulator-toolbar">
              <label v-if="tab.streamable" class="simulator-stream-toggle">
                <NSwitch v-model:value="streamEnabled[tab.key]" size="small" />
                <span>流式</span>
              </label>
              <NButton size="small" secondary type="primary" @click="copyText(endpointUrl(tab))">复制 URL</NButton>
              <NButton v-if="sendingTab !== tab.key" size="small" type="primary" @click="sendRequest(tab.key)">{{ tab.method === 'GET' ? '拉取模型' : '发送请求' }}</NButton>
              <NButton v-else size="small" type="error" @click="stopRequest">停止</NButton>
            </div>
          </div>
          <div class="simulator-panel-grid">
            <div>
              <div class="simulator-section-title">请求示例</div>
              <pre class="simulator-pre">{{ requestExample(tab) }}</pre>
            </div>
            <div>
              <div class="simulator-section-title">响应结果</div>
              <pre class="simulator-pre simulator-result-pre">{{ responses[tab.key] || '尚未请求' }}</pre>
            </div>
          </div>
        </NTabPane>
      </NTabs>
    </NCard>
  </div>
</template>

<style scoped>
.client-simulator-page { display: flex; flex-direction: column; gap: 16px; min-width: 0; }
.simulator-page-header { display: flex; justify-content: space-between; gap: 12px; }
.simulator-title { margin: 0 0 4px; font-size: 20px; font-weight: 700; }
.simulator-subtitle { margin: 0; color: var(--text-color-secondary); }
.simulator-config-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
.simulator-field { display: flex; flex-direction: column; gap: 8px; min-width: 0; color: var(--text-primary); font-size: 13px; font-weight: 600; }
.simulator-field small { color: var(--text-color-secondary); font-weight: 400; }
.simulator-input-group { display: flex; gap: 8px; min-width: 0; }
.simulator-input-group :deep(.n-input) { min-width: 0; flex: 1; }
.simulator-endpoint-row { display: flex; justify-content: space-between; align-items: center; gap: 12px; margin-bottom: 16px; }
.simulator-endpoint-code { min-width: 0; overflow: hidden; padding: 6px 8px; border-radius: 6px; background: var(--bg-input, #f6f6f6); text-overflow: ellipsis; white-space: nowrap; }
.simulator-toolbar, .simulator-stream-toggle { display: inline-flex; align-items: center; gap: 8px; white-space: nowrap; }
.simulator-panel-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
.simulator-section-title { margin-bottom: 8px; color: var(--text-primary); font-size: 13px; font-weight: 700; }
.simulator-pre { min-height: 260px; max-height: 460px; margin: 0; overflow: auto; padding: 12px; border: 1px solid var(--border-color-global); border-radius: 8px; background: #0f172a; color: #dbeafe; font-size: 12px; line-height: 1.6; white-space: pre-wrap; word-break: break-word; }
.simulator-result-pre { background: #111827; color: #d1fae5; }
@media (max-width: 900px) { .simulator-config-grid, .simulator-panel-grid { grid-template-columns: 1fr; } .simulator-endpoint-row { align-items: stretch; flex-direction: column; } .simulator-toolbar { flex-wrap: wrap; } }
</style>

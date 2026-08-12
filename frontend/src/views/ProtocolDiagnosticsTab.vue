<script setup lang="ts">
import { computed, ref } from 'vue'
import { NAlert, NButton, NCard, NInput, NSelect, NSpace, NTag, useMessage } from 'naive-ui'
import {
  runProtocolDiagnostics,
  type ProtocolDiagnosticsDirection,
  type ProtocolDiagnosticsResult,
  type ProtocolName
} from '@/api/protocolDiagnostics'

const message = useMessage()
const direction = ref<ProtocolDiagnosticsDirection>('request')
const sourceProtocol = ref<ProtocolName>('OpenAI')
const targetProtocol = ref<ProtocolName>('Responses')
const streaming = ref(false)
const modelName = ref('deepseek-v4-flash')
const eventName = ref('')
const payload = ref('{\n  "model": "deepseek-v4-flash",\n  "messages": [\n    { "role": "user", "content": "hello" }\n  ]\n}')
const inputTokens = ref('0')
const cachedTokens = ref('0')
const outputTokens = ref('0')
const loading = ref(false)
const error = ref('')
const result = ref<ProtocolDiagnosticsResult | null>(null)

const protocolOptions = [
  { label: 'OpenAI Chat', value: 'OpenAI' },
  { label: 'Anthropic Messages', value: 'Anthropic' },
  { label: 'OpenAI Responses', value: 'Responses' }
]
const directionOptions = [
  { label: '请求转换', value: 'request' },
  { label: '响应转换', value: 'response' }
]

const isAnthropicResponsesRequest = computed(() =>
  direction.value === 'request'
  && sourceProtocol.value === 'Anthropic'
  && targetProtocol.value === 'Responses'
)

async function diagnose(): Promise<void> {
  error.value = ''
  result.value = null
  loading.value = true
  try {
    result.value = await runProtocolDiagnostics({
      direction: direction.value,
      sourceProtocol: sourceProtocol.value,
      targetProtocol: targetProtocol.value,
      streaming: streaming.value,
      modelName: modelName.value,
      payload: payload.value,
      eventName: eventName.value || undefined,
      inputTokens: Number(inputTokens.value) || 0,
      cachedTokens: Number(cachedTokens.value) || 0,
      outputTokens: Number(outputTokens.value) || 0
    })
    message.success('协议转换完成')
  } catch (err) {
    error.value = err instanceof Error ? err.message : '协议诊断失败'
  } finally {
    loading.value = false
  }
}

function clearResult(): void {
  result.value = null
  error.value = ''
}
</script>

<template>
  <div class="protocol-diagnostics-tab">
    <div class="diagnostics-heading">
      <div>
        <h2 class="pane-title">离线协议诊断</h2>
        <p class="pane-subtitle">只在本地执行已有协议桥接，不调用上游、不使用密钥、不写入调用记录。</p>
      </div>
      <NTag type="info" round>仅允许 deepseek-v4-flash</NTag>
    </div>

    <NAlert type="warning" :show-icon="false" class="diagnostics-warning">
      这是 payload 转换检查工具，不是客户端模拟器；输入内容不会进入真实代理转发链路。
    </NAlert>

    <NCard class="diagnostics-form-card" :content-style="{ padding: '16px' }">
      <div class="diagnostics-form-grid">
        <NSelect v-model:value="direction" :options="directionOptions" />
        <NSelect v-model:value="sourceProtocol" :options="protocolOptions" />
        <NSelect v-model:value="targetProtocol" :options="protocolOptions" />
        <NInput v-model:value="modelName" placeholder="模型名" />
      </div>
      <div class="diagnostics-stream-row">
        <label class="diagnostics-checkbox">
          <input v-model="streaming" type="checkbox">
          <span>流式片段</span>
        </label>
        <NInput
          v-if="isAnthropicResponsesRequest"
          v-model:value="eventName"
          placeholder="Anthropic eventName，例如 content_block_delta"
        />
      </div>
      <div v-if="direction === 'response' || streaming" class="diagnostics-token-grid">
        <NInput v-model:value="inputTokens" type="text" placeholder="输入 token" />
        <NInput v-model:value="cachedTokens" type="text" placeholder="缓存 token" />
        <NInput v-model:value="outputTokens" type="text" placeholder="输出 token" />
      </div>
      <NInput
        v-model:value="payload"
        type="textarea"
        :autosize="{ minRows: 12, maxRows: 28 }"
        placeholder="输入 JSON 或符合当前方向要求的 SSE 片段"
      />
      <NSpace justify="end" class="diagnostics-actions">
        <NButton secondary @click="clearResult">清空结果</NButton>
        <NButton type="primary" :loading="loading" @click="diagnose">执行离线转换</NButton>
      </NSpace>
    </NCard>

    <NAlert v-if="error" type="error" :show-icon="false" class="diagnostics-error">
      {{ error }}
    </NAlert>

    <NCard v-if="result" class="diagnostics-result-card" :content-style="{ padding: '16px' }">
      <div class="diagnostics-result-meta">
        <NTag :type="result.conversionFailed ? 'error' : 'success'">
          {{ result.conversionFailed ? '转换失败' : '转换完成' }}
        </NTag>
        <span>事件数：{{ result.eventCount }}</span>
        <span>检测到完成：{{ result.completionDetected ? '是' : '否' }}</span>
      </div>
      <pre class="diagnostics-pre">{{ result.convertedPayload || '（转换器没有输出内容）' }}</pre>
    </NCard>
  </div>
</template>

<style scoped>
.protocol-diagnostics-tab { min-width: 0; }
.diagnostics-heading,
.diagnostics-result-meta,
.diagnostics-stream-row { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.diagnostics-warning,
.diagnostics-error { margin: 14px 0; }
.diagnostics-form-card,
.diagnostics-result-card { margin-top: 14px; }
.diagnostics-form-grid,
.diagnostics-token-grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 12px; margin-bottom: 12px; }
.diagnostics-token-grid { grid-template-columns: repeat(3, minmax(0, 1fr)); }
.diagnostics-stream-row { margin-bottom: 12px; }
.diagnostics-stream-row :deep(.n-input) { flex: 1; }
.diagnostics-checkbox { display: inline-flex; align-items: center; gap: 8px; white-space: nowrap; }
.diagnostics-actions { margin-top: 14px; }
.diagnostics-pre { margin: 14px 0 0; max-height: 520px; overflow: auto; padding: 14px; border-radius: 8px; background: var(--n-color-target); white-space: pre-wrap; word-break: break-word; }
@media (max-width: 800px) {
  .diagnostics-form-grid,
  .diagnostics-token-grid { grid-template-columns: 1fr; }
  .diagnostics-heading,
  .diagnostics-result-meta { align-items: flex-start; flex-direction: column; }
}
</style>

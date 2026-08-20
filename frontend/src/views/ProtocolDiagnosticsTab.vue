<script setup lang="ts">
import { computed, inject, onMounted, ref, watch, type Ref } from 'vue'
import {
  NAlert, NButton, NCard, NInput, NModal, NSelect, NSpace, NTag, useMessage
} from 'naive-ui'
import {
  runProtocolDiagnostics,
  type ProtocolDiagnosticsDirection,
  type ProtocolDiagnosticsResult,
  type ProtocolDiagnosticsTrialRule,
  type ProtocolName
} from '@/api/protocolDiagnostics'
import {
  createProfile,
  getProfile,
  listProfiles,
  updateProfile,
  type CompatibilityProfileListItem
} from '@/api/compatibility'
import {
  parseCompatibilityRules,
  serializeCompatibilityRules,
  type CompatibilityRuleForm
} from './compatibilityState'
import JsonDiffView from '@/components/JsonDiffView.vue'
import JsonTreeView from '@/components/JsonTreeView.vue'
import {
  takeProtocolDiagnosticsPrefill
} from './developerInvocationsState'

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

// ── 规则试运行：请求方向可选一个规则集，转换后按真实链路语义应用 ──
const profiles = ref<CompatibilityProfileListItem[]>([])
const selectedProfileId = ref<string | null>(null)
const selectedProfileName = ref('')
const trialRules = ref<ProtocolDiagnosticsTrialRule[]>([])

async function loadProfiles(): Promise<void> {
  try {
    profiles.value = (await listProfiles()).filter(p => p.isEnabled)
  } catch {
    // 规则集加载失败不阻塞诊断，静默降级。
  }
}

async function onProfileSelected(id: string | null): Promise<void> {
  trialRules.value = []
  selectedProfileName.value = ''
  if (!id) return
  try {
    const detail = await getProfile(id)
    trialRules.value = parseCompatibilityRules(detail.rulesJson)
    const profile = profiles.value.find(p => p.id === id)
    selectedProfileName.value = profile?.name ?? ''
  } catch {
    message.error('加载规则集失败')
  }
}

// ── 一键保存为兼容规则集 ──
const saveDialogVisible = ref(false)
const saveTargetMode = ref<'create' | 'existing'>('create')
const newProfileName = ref('')
const existingProfileId = ref<string | null>(null)
const saveLoading = ref(false)
const draftRules = ref<CompatibilityRuleForm[]>([])

function openSaveDialog(): void {
  // 从缺失字段提醒生成候选 default 规则（如“Anthropic 请求缺少必填字段 max_tokens”）
  const suggested = (result.value?.missingFields ?? [])
    .map((text) => {
      const match = text.match(/字段\s*([A-Za-z_][A-Za-z0-9_.]*)/)
      return match?.[1] ?? null
    })
    .filter((name): name is string => name !== null)
    .map((name): CompatibilityRuleForm => ({
      op: 'default',
      key: name,
      value: '',
      scope: 'bridge'
    }))
  draftRules.value = suggested
  saveTargetMode.value = 'create'
  newProfileName.value = '转换修复 - ' + (result.value?.sourceProtocol ?? '') + '→' + (result.value?.targetProtocol ?? '')
  existingProfileId.value = null
  saveDialogVisible.value = true
}

async function saveRules(): Promise<void> {
  saveLoading.value = true
  try {
    const rulesJson = serializeCompatibilityRules(draftRules.value)
    if (saveTargetMode.value === 'create') {
      await createProfile({
        name: newProfileName.value.trim() || '未命名规则集',
        description: '由协议诊断台生成',
        rulesJson,
        isEnabled: true
      })
      message.success('已新建规则集')
    } else if (existingProfileId.value) {
      const detail = await getProfile(existingProfileId.value)
      const existing = parseCompatibilityRules(detail.rulesJson)
      await updateProfile(existingProfileId.value, {
        name: detail.name,
        description: detail.description,
        rulesJson: serializeCompatibilityRules([...existing, ...draftRules.value]),
        isEnabled: detail.isEnabled
      })
      message.success('已追加到规则集')
    }
    saveDialogVisible.value = false
    void loadProfiles()
  } catch (err) {
    message.error(err instanceof Error ? err.message : '保存失败')
  } finally {
    saveLoading.value = false
  }
}

// 调用记录详情 → 本诊断台 的联动信号（由父页面 provide）
const prefillSignal = inject<Ref<number>>('protocol-diagnostics-prefill')

const protocolOptions = [
  { label: 'OpenAI Chat', value: 'OpenAI' },
  { label: 'Anthropic Messages', value: 'Anthropic' },
  { label: 'OpenAI Responses', value: 'Responses' }
]
const directionOptions = [
  { label: '请求转换（客户端 → 上游）', value: 'request' },
  { label: '响应转换（上游 → 客户端）', value: 'response' }
]

const isAnthropicResponsesRequest = computed(() =>
  direction.value === 'request'
  && sourceProtocol.value === 'Anthropic'
  && targetProtocol.value === 'Responses'
)

const summaryEntries = computed(() =>
  result.value?.inputSummary
    ? Object.entries(result.value.inputSummary)
    : []
)

const executedPayload = ref('')

async function diagnose(): Promise<void> {
  error.value = ''
  result.value = null
  loading.value = true
  executedPayload.value = payload.value
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
      outputTokens: Number(outputTokens.value) || 0,
      rules: trialRules.value.length > 0 ? trialRules.value : undefined
    })
    if (result.value.conversionFailed) {
      message.warning(result.value.failureReason || '协议转换失败')
    } else {
      message.success('协议转换完成')
    }
  } catch (err) {
    error.value = err instanceof Error ? err.message : '协议诊断失败'
  } finally {
    loading.value = false
  }
}

// 故障现场上下文展示
const faultContext = ref<{
  targetSiteName?: string
  attemptedModel?: string
  statusCode?: number
  errorMessage?: string
} | null>(null)

function applyPrefill(): void {
  const prefill = takeProtocolDiagnosticsPrefill()
  if (!prefill) return
  direction.value = prefill.direction
  sourceProtocol.value = prefill.sourceProtocol as ProtocolName
  targetProtocol.value = prefill.targetProtocol as ProtocolName
  streaming.value = prefill.streaming
  modelName.value = prefill.modelName || modelName.value
  payload.value = prefill.payload
  eventName.value = prefill.eventName ?? ''
  inputTokens.value = String(prefill.inputTokens ?? 0)
  cachedTokens.value = String(prefill.cachedTokens ?? 0)
  outputTokens.value = String(prefill.outputTokens ?? 0)
  
  if (prefill.errorMessage || prefill.statusCode || prefill.targetSiteName) {
    faultContext.value = {
      targetSiteName: prefill.targetSiteName,
      attemptedModel: prefill.attemptedModel,
      statusCode: prefill.statusCode,
      errorMessage: prefill.errorMessage
    }
  } else {
    faultContext.value = null
  }

  // 如果携带了试运行规则，直接预填
  if (prefill.trialRules && prefill.trialRules.length > 0) {
    trialRules.value = [...prefill.trialRules]
    selectedProfileName.value = '现场推荐规则'
    selectedProfileId.value = null
  } else {
    selectedProfileId.value = null
    selectedProfileName.value = ''
    trialRules.value = []
  }

  void diagnose()
}

onMounted(() => {
  applyPrefill()
  void loadProfiles()
  if (prefillSignal) {
    watch(prefillSignal, () => applyPrefill())
  }
})

function clearResult(): void {
  result.value = null
  error.value = ''
}

// 转换结果 JSON 解析结果缓存：模板里避免对大 payload 重复 JSON.parse。
const convertedJson = computed(() => {
  const text = result.value?.convertedPayload ?? ''
  const trimmed = text.trim()
  if (!trimmed.startsWith('{') && !trimmed.startsWith('[')) return null
  try {
    return JSON.parse(text) as unknown
  } catch {
    return null
  }
})
</script>

<template>
  <div class="protocol-diagnostics-tab">
    <div class="diagnostics-heading">
      <div>
        <h2 class="pane-title">离线协议诊断</h2>
        <p class="pane-subtitle">任意协议组合离线转换测试：只在本地执行已有协议桥接，不调用上游、不使用密钥、不写入调用记录。</p>
      </div>
      <NTag type="info" round>自由协议组合</NTag>
    </div>

    <NAlert type="warning" :show-icon="false" class="diagnostics-warning">
      这是 payload 转换检查工具，不是客户端模拟器；输入内容不会进入真实代理转发链路。转换失败/缺字段时对照下方的“字段对应关系”定位原因。
    </NAlert>

    <!-- 如果是从错误现场跳转过来，醒目展示故障现场 -->
    <div v-if="faultContext" class="mb-3 p-3 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-800/40 rounded-lg">
      <div class="flex items-center justify-between mb-1">
        <span class="text-xs font-bold text-red-600 dark:text-red-400">🚨 正在排查故障现场</span>
        <NTag v-if="faultContext.statusCode" size="tiny" type="error">HTTP {{ faultContext.statusCode }}</NTag>
      </div>
      <div class="text-xs text-slate-700 dark:text-slate-300 mb-1">
        站点/模型: <strong>{{ faultContext.targetSiteName || '-' }} / {{ faultContext.attemptedModel || '-' }}</strong>
      </div>
      <div v-if="faultContext.errorMessage" class="text-xs text-red-500 font-mono bg-red-100/50 dark:bg-red-900/40 p-1.5 rounded">
        {{ faultContext.errorMessage }}
      </div>
    </div>

    <NCard class="diagnostics-form-card" :content-style="{ padding: '16px' }">
      <div class="diagnostics-form-grid">
        <NSelect v-model:value="direction" :options="directionOptions" />
        <NSelect v-model:value="sourceProtocol" :options="protocolOptions" />
        <NSelect v-model:value="targetProtocol" :options="protocolOptions" />
        <NInput v-model:value="modelName" placeholder="模型名（仅用于写入转换结果）" />
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
        <NSelect
          v-if="direction === 'request'"
          v-model:value="selectedProfileId"
          :options="profiles.map(p => ({ label: p.name, value: p.id }))"
          placeholder="应用规则集（试运行，请求方向）"
          clearable
          @update:value="onProfileSelected"
        />
      </div>
      <div v-if="direction === 'response'" class="diagnostics-token-grid">
        <NInput v-model:value="inputTokens" type="text" placeholder="输入 token（上游 input/prompt_tokens）" />
        <NInput v-model:value="cachedTokens" type="text" placeholder="缓存 token（cache_read/cached_tokens）" />
        <NInput v-model:value="outputTokens" type="text" placeholder="输出 token（output/completion_tokens）" />
      </div>
      <p v-if="direction === 'response'" class="diagnostics-token-hint">
        这三个值用于响应转换时还原 usage 字段：从调用记录点“诊断此响应”会自动带入；留 0 也可以（转换结果的 token 显示为 0）。
      </p>
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
        <NTag v-if="result.conversionPath" type="info">{{ result.conversionPath }}</NTag>
        <span class="diagnostics-meta-text">事件数：{{ result.eventCount }}</span>
        <span class="diagnostics-meta-text">检测到完成：{{ result.completionDetected ? '是' : '否' }}</span>
      </div>

      <NAlert v-if="result.failureReason" type="error" :show-icon="false" class="diagnostics-failure">
        {{ result.failureReason }}
      </NAlert>

      <NAlert v-if="result.rulesApplied" type="success" :show-icon="false" class="diagnostics-rules-applied">
        已应用规则集「{{ selectedProfileName || '自定义规则' }}」（{{ trialRules.length }} 条规则，转换后按真实链路顺序执行）
      </NAlert>

      <div v-if="result.chain" class="diagnostics-section">
        <div class="diagnostics-section-title chain-title">
          转换链路
          <NTag size="small" :type="result.chain.mode === 'direct' ? 'success' : 'warning'" round>
            {{ result.chain.mode === 'direct' ? '透传方案' : '兼容方案' }}
          </NTag>
        </div>
        <div class="chain-flow">
          <template v-for="(stage, index) in result.chain.stages" :key="index">
            <div v-if="index > 0" class="chain-arrow">→</div>
            <div class="chain-node" :class="['chain-node-' + stage.kind, { 'chain-node-bridge': stage.isBridge }]">
              <div class="chain-node-label">{{ stage.label }}</div>
              <div class="chain-node-protocol">{{ stage.protocol }}</div>
              <div v-if="stage.function" class="chain-node-function">{{ stage.function }}</div>
              <div v-if="stage.note" class="chain-node-note">{{ stage.note }}</div>
            </div>
          </template>
        </div>
        <div v-if="result.chain.eventMappings?.length" class="chain-events">
          <div class="diagnostics-section-title">流式事件对应（上游事件 → 客户端事件）</div>
          <table class="diagnostics-mapping-table">
            <thead>
              <tr><th>上游事件</th><th></th><th>客户端事件</th><th>说明</th></tr>
            </thead>
            <tbody>
              <tr v-for="(mapping, index) in result.chain.eventMappings" :key="index">
                <td><code>{{ mapping.sourceEvent }}</code></td>
                <td class="chain-event-arrow">→</td>
                <td><code>{{ mapping.targetEvent }}</code></td>
                <td>{{ mapping.note || '-' }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <div v-if="result.missingFields?.length" class="diagnostics-section">
        <div class="diagnostics-section-title">缺失字段提醒</div>
        <div class="diagnostics-missing-list">
          <NTag v-for="missing in result.missingFields" :key="missing" type="warning">
            {{ missing }}
          </NTag>
        </div>
      </div>

      <div v-if="summaryEntries.length" class="diagnostics-section">
        <div class="diagnostics-section-title">输入识别摘要（网关从这个 payload 中识别的字段）</div>
        <div class="diagnostics-summary-grid">
          <div v-for="[key, value] in summaryEntries" :key="key" class="diagnostics-summary-chip">
            <span>{{ key }}</span>
            <strong>{{ String(value) }}</strong>
          </div>
        </div>
      </div>

      <div v-if="result.fieldMappings?.length" class="diagnostics-section">
        <div class="diagnostics-section-title">
          字段对应关系（{{ result.sourceProtocol }} → {{ result.targetProtocol }}）
        </div>
        <table class="diagnostics-mapping-table">
          <thead>
            <tr><th>源字段</th><th>目标字段</th><th>说明</th></tr>
          </thead>
          <tbody>
            <tr v-for="(mapping, index) in result.fieldMappings" :key="index">
              <td><code>{{ mapping.source }}</code></td>
              <td><code>{{ mapping.target }}</code></td>
              <td>{{ mapping.note || '-' }}</td>
            </tr>
          </tbody>
        </table>
      </div>

      <div class="diagnostics-section">
        <div class="diagnostics-section-title">转换前后字段对比（输入 payload → 转换结果）</div>
        <JsonDiffView :before="executedPayload" :after="result.convertedPayload" />
      </div>

      <div class="diagnostics-section">
        <div class="diagnostics-section-title">转换后内容</div>
        <div v-if="convertedJson !== null" class="diagnostics-json-tree">
          <JsonTreeView :value="convertedJson" />
        </div>
        <pre v-else class="diagnostics-pre">{{ result.convertedPayload || '（转换器没有输出内容）' }}</pre>
      </div>

      <NSpace class="diagnostics-save-actions">
        <NButton size="small" type="primary" ghost @click="openSaveDialog">保存为兼容规则</NButton>
      </NSpace>
    </NCard>

    <NModal
      v-model:show="saveDialogVisible"
      preset="card"
      title="保存为兼容规则集"
      style="max-width: 640px"
    >
      <div class="save-dialog-body">
        <div class="save-dialog-row">
          <label class="save-dialog-label">目标</label>
          <NSelect
            v-model:value="saveTargetMode"
            :options="[
              { label: '新建规则集', value: 'create' },
              { label: '追加到已有规则集', value: 'existing' }
            ]"
          />
        </div>
        <div v-if="saveTargetMode === 'create'" class="save-dialog-row">
          <label class="save-dialog-label">名称</label>
          <NInput v-model:value="newProfileName" placeholder="规则集名称" />
        </div>
        <div v-else class="save-dialog-row">
          <label class="save-dialog-label">规则集</label>
          <NSelect
            v-model:value="existingProfileId"
            :options="profiles.map(p => ({ label: p.name, value: p.id }))"
            placeholder="选择要追加的规则集"
          />
        </div>
        <div class="save-dialog-row save-dialog-rules">
          <label class="save-dialog-label">规则</label>
          <div class="save-dialog-rules-list">
            <div v-if="draftRules.length === 0" class="save-dialog-empty">
              未从缺失字段生成候选规则，可手动添加
            </div>
            <div v-for="(rule, index) in draftRules" :key="index" class="save-dialog-rule">
              <NSelect
                v-model:value="rule.op"
                class="save-dialog-op"
                :options="[
                  { label: 'default 补默认值', value: 'default' },
                  { label: 'strip 剔除', value: 'strip' },
                  { label: 'rename 重命名', value: 'rename' }
                ]"
              />
              <NInput v-if="rule.op === 'default'" v-model:value="rule.key" class="save-dialog-target" placeholder="字段名（如 max_tokens）" />
              <NInput v-if="rule.op === 'default'" v-model:value="rule.value" class="save-dialog-target" placeholder="默认值（如 8192）" />
              <NInput v-if="rule.op === 'strip'" v-model:value="rule.target" class="save-dialog-target" placeholder="目标字段路径（如 metadata）" />
              <template v-if="rule.op === 'rename'">
                <NInput v-model:value="rule.from" class="save-dialog-target" placeholder="原字段名" />
                <NInput v-model:value="rule.to" class="save-dialog-target" placeholder="新字段名" />
              </template>
              <NSelect
                v-model:value="rule.scope"
                class="save-dialog-scope"
                :options="[
                  { label: '兼容路径', value: 'bridge' },
                  { label: '全部', value: 'all' },
                  { label: '透传路径', value: 'passthrough' }
                ]"
              />
              <NButton size="tiny" quaternary type="error" @click="draftRules.splice(index, 1)">删除</NButton>
            </div>
            <NButton size="tiny" secondary @click="draftRules.push({ op: 'default', key: '', value: '', scope: 'bridge' })">
              + 添加规则
            </NButton>
          </div>
        </div>
      </div>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="saveDialogVisible = false">取消</NButton>
          <NButton type="primary" :loading="saveLoading" @click="saveRules">保存</NButton>
        </NSpace>
      </template>
    </NModal>
  </div>
</template>

<style scoped>
.protocol-diagnostics-tab { min-width: 0; }
.diagnostics-heading,
.diagnostics-result-meta,
.diagnostics-stream-row { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.diagnostics-warning,
.diagnostics-error,
.diagnostics-failure { margin: 14px 0; }
.diagnostics-form-card,
.diagnostics-result-card { margin-top: 14px; }
.diagnostics-form-grid,
.diagnostics-token-grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 12px; margin-bottom: 12px; }
.diagnostics-token-grid { grid-template-columns: repeat(3, minmax(0, 1fr)); }
.diagnostics-stream-row { margin-bottom: 12px; }
.diagnostics-stream-row :deep(.n-input) { flex: 1; }
.diagnostics-checkbox { display: inline-flex; align-items: center; gap: 8px; white-space: nowrap; }
.diagnostics-token-hint {
  margin: 0 0 12px; font-size: 12px;
  color: var(--text-color-secondary);
}
.diagnostics-rules-applied { margin: 14px 0; }
.diagnostics-save-actions { margin-top: 14px; }
.diagnostics-json-tree {
  margin-top: 10px;
  padding: 10px 12px;
  border: 1px solid var(--border-color-global);
  border-radius: 8px;
  background: var(--bg-input);
  overflow: auto;
  max-height: 560px;
}
.save-dialog-body { display: flex; flex-direction: column; gap: 12px; }
.save-dialog-row { display: flex; align-items: flex-start; gap: 10px; }
.save-dialog-label { width: 56px; flex: none; padding-top: 6px; font-size: 13px; color: var(--text-color-secondary); }
.save-dialog-rules { align-items: flex-start; }
.save-dialog-rules-list { display: flex; flex-direction: column; gap: 8px; flex: 1; min-width: 0; }
.save-dialog-empty { font-size: 12px; color: var(--text-color-secondary); padding: 6px 0; }
.save-dialog-rule { display: flex; gap: 8px; align-items: center; }
.save-dialog-op { width: 150px; flex: none; }
.save-dialog-target { flex: 1; }
.save-dialog-scope { width: 120px; flex: none; }
/* NInput 显式主题变量：任何主题下保证 背景/文字/占位符 对比度 */
.protocol-diagnostics-tab :deep(.n-input) {
  --n-color: var(--bg-input);
  --n-color-focus: var(--bg-input);
  --n-color-hover: var(--bg-input);
  --n-text-color: var(--text-primary);
  --n-text-color-focus: var(--text-primary);
  --n-text-color-hover: var(--text-primary);
  --n-placeholder-color: var(--text-color-secondary);
  --n-border: 1px solid var(--border-color-global);
  --n-border-focus: 1px solid var(--status-info-text);
  --n-border-hover: 1px solid var(--border-color-global);
  --n-box-shadow-focus: 0 0 0 2px var(--status-info-bg);
}
.diagnostics-actions { margin-top: 14px; }
.diagnostics-meta-text { color: var(--n-text-color-3, inherit); font-size: 12px; }
.diagnostics-section { margin-top: 16px; }
.diagnostics-section-title { font-size: 13px; font-weight: 600; margin-bottom: 8px; }
.diagnostics-missing-list { display: flex; flex-wrap: wrap; gap: 8px; }
.diagnostics-summary-grid { display: flex; flex-wrap: wrap; gap: 8px; }
.diagnostics-summary-chip {
  display: inline-flex; align-items: center; gap: 6px;
  padding: 4px 10px; border-radius: 6px;
  background: var(--bg-input);
  color: var(--text-primary);
  border: 1px solid var(--border-color-global);
  font-size: 12px;
}
.diagnostics-summary-chip span { color: var(--n-text-color-3, inherit); }
.diagnostics-mapping-table {
  width: 100%; border-collapse: collapse; font-size: 12px;
}
.diagnostics-mapping-table th,
.diagnostics-mapping-table td {
  text-align: left; padding: 6px 10px;
  border-bottom: 1px solid var(--n-border-color, rgba(128, 128, 128, 0.2));
}
.diagnostics-mapping-table th {
  font-weight: 600; white-space: nowrap;
  color: var(--n-text-color-3, inherit);
}
.diagnostics-mapping-table code { font-size: 12px; }
.chain-title { display: flex; align-items: center; gap: 8px; }
.chain-flow { display: flex; align-items: stretch; gap: 6px; flex-wrap: wrap; }
.chain-arrow { display: flex; align-items: center; color: var(--n-text-color-3, inherit); font-size: 14px; flex: none; }
.chain-node {
  flex: 1 1 140px; min-width: 140px;
  border: 1px solid var(--n-border-color, rgba(128, 128, 128, 0.3));
  border-radius: 8px; padding: 8px 10px;
  background: var(--bg-input);
  color: var(--text-primary);
}
.chain-node-transform { border-color: var(--n-primary-color, #2080f0); }
.chain-node-transform.chain-node-bridge { border-color: var(--n-warning-color, #f0a020); }
.chain-node-label { font-size: 12px; font-weight: 600; }
.chain-node-protocol { font-size: 12px; color: var(--n-text-color-3, inherit); margin-top: 2px; }
.chain-node-function {
  font-size: 11px; font-family: var(--n-font-family-mono, monospace);
  margin-top: 5px; color: var(--n-primary-color, #2080f0);
  word-break: break-all; line-height: 1.4;
}
.chain-node-bridge .chain-node-function { color: var(--n-warning-color, #f0a020); }
.chain-node-note { font-size: 11px; color: var(--n-text-color-3, inherit); margin-top: 4px; line-height: 1.4; }
.chain-events { margin-top: 14px; }
.chain-event-arrow { text-align: center; color: var(--n-text-color-3, inherit); }
@media (max-width: 800px) {
  .chain-flow { flex-direction: column; }
  .chain-arrow { transform: rotate(90deg); justify-content: center; }
}
.diagnostics-pre {
  margin: 0; max-height: 520px; overflow: auto;
  padding: 14px; border-radius: 8px;
  background: var(--bg-input);
  color: var(--text-primary);
  white-space: pre-wrap; word-break: break-word;
}
@media (max-width: 800px) {
  .diagnostics-form-grid,
  .diagnostics-token-grid { grid-template-columns: 1fr; }
  .diagnostics-heading,
  .diagnostics-result-meta { align-items: flex-start; flex-direction: column; }
}
</style>

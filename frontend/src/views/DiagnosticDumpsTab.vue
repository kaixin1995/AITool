<script setup lang="ts">
import { computed, h, onMounted, onUnmounted, ref } from 'vue'
import {
  NAlert,
  NButton,
  NCard,
  NDataTable,
  NDrawer,
  NDrawerContent,
  NEmpty,
  NForm,
  NFormItem,
  NInputNumber,
  NModal,
  NPopconfirm,
  NRadioGroup,
  NRadioButton,
  NSpace,
  NSpin,
  NTag,
  NTooltip,
  useMessage,
  type DataTableColumns
} from 'naive-ui'
import {
  getDiagnosticSamplingStatus,
  enableDiagnosticSampling,
  disableDiagnosticSampling,
  getDiagnosticDumps,
  getDiagnosticDumpContent,
  clearDiagnosticDumps,
  getDiagnosticConfig,
  updateDiagnosticConfig,
  type DiagnosticSamplingStatus,
  type DiagnosticDumpItem,
  type DiagnosticConfig
} from '@/api/developer'
import { useRouter } from 'vue-router'
import { setProtocolDiagnosticsPrefill } from './developerInvocationsState'

const message = useMessage()

const loading = ref(false)
const dumps = ref<DiagnosticDumpItem[]>([])
const categoryFilter = ref<'all' | 'failure' | 'sample'>('all')

const samplingStatus = ref<DiagnosticSamplingStatus>({
  enabled: false,
  remainingSeconds: 0,
  expiresAtUtc: null,
  maxDurationMinutes: 10
})
const samplingLoading = ref(false)

const showDetailDrawer = ref(false)
const currentDumpItem = ref<DiagnosticDumpItem | null>(null)
const currentDumpContent = ref<any | null>(null)
const detailLoading = ref(false)

// 动态限制参数设置弹窗
const showConfigModal = ref(false)
const configLoading = ref(false)
const configSaving = ref(false)
const configForm = ref<DiagnosticConfig>({
  maxBodyLengthMb: 4,
  maxRoundResponseMb: 2,
  retentionDays: 3,
  maxFailuresPerDay: 50
})

let countdownTimer: ReturnType<typeof setInterval> | null = null

const filteredDumps = computed(() => {
  if (categoryFilter.value === 'all') return dumps.value
  return dumps.value.filter((d) => d.category === categoryFilter.value)
})

function formatTime(val: string | null | undefined): string {
  if (!val) return '-'
  return new Date(val).toLocaleString('zh-CN')
}

function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(2)} MB`
}

function formatSeconds(sec: number): string {
  const m = Math.floor(sec / 60)
  const s = sec % 60
  return `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`
}

async function loadSamplingStatus(): Promise<void> {
  try {
    const res = await getDiagnosticSamplingStatus()
    samplingStatus.value = res
  } catch (err: any) {
    // 静默忽略
  }
}

async function loadConfig(): Promise<void> {
  configLoading.value = true
  try {
    const cfg = await getDiagnosticConfig()
    configForm.value = { ...cfg }
  } catch (err: any) {
    // 静默忽略
  } finally {
    configLoading.value = false
  }
}

async function handleSaveConfig(): Promise<void> {
  configSaving.value = true
  try {
    const updated = await updateDiagnosticConfig(configForm.value)
    configForm.value = { ...updated }
    message.success('诊断限制参数已更新并即时生效！')
    showConfigModal.value = false
  } catch (err: any) {
    message.error(err?.message || '保存诊断参数失败')
  } finally {
    configSaving.value = false
  }
}

async function handleToggleSampling(): Promise<void> {
  samplingLoading.value = true
  try {
    if (samplingStatus.value.enabled) {
      const res = await disableDiagnosticSampling()
      samplingStatus.value = res
      message.info('已关闭成功请求采样')
    } else {
      const res = await enableDiagnosticSampling(10)
      samplingStatus.value = res
      message.success('已开启成功请求抓包采样（限时 10 分钟自动关闭）')
      await loadDumps()
    }
  } catch (err: any) {
    message.error(err?.message || '切换采样状态失败')
  } finally {
    samplingLoading.value = false
  }
}

async function loadDumps(): Promise<void> {
  loading.value = true
  try {
    const data = await getDiagnosticDumps(100)
    dumps.value = Array.isArray(data) ? data : []
  } catch (err: any) {
    message.error(err?.message || '加载诊断抓包清单失败')
  } finally {
    loading.value = false
  }
}

async function handleClearDumps(): Promise<void> {
  try {
    const res = await clearDiagnosticDumps()
    message.success(`清理完成，已删除 ${res.deletedCount} 个历史转储文件`)
    await loadDumps()
  } catch (err: any) {
    message.error(err?.message || '清理失败')
  }
}

async function viewDumpDetail(item: DiagnosticDumpItem): Promise<void> {
  currentDumpItem.value = item
  currentDumpContent.value = null
  showDetailDrawer.value = true
  detailLoading.value = true
  try {
    const data = await getDiagnosticDumpContent(item.fileName)
    currentDumpContent.value = data
  } catch (err: any) {
    message.error(err?.message || '读取抓包内容失败')
  } finally {
    detailLoading.value = false
  }
}

function copyText(text: string, tip = '已复制到剪贴板'): void {
  navigator.clipboard.writeText(text).then(
    () => message.success(tip),
    () => message.error('复制失败，请手动选择复制')
  )
}

const router = useRouter()

function stringifyDumpField(value: unknown): string {
  if (!value) return ''
  return typeof value === 'string' ? value : JSON.stringify(value, null, 2)
}

function loadIntoProtocolDiagnostics(item: DiagnosticDumpItem, content: any): void {
  const reqBody = stringifyDumpField(content?.clientRequestBody)
  const preparedBody = stringifyDumpField(content?.preparedRequestBody)
  const errBody = stringifyDumpField(content?.upstreamResponseBody)
    || content?.diagnostic?.errorMessage
    || item.errorSummary
    || ''

  setProtocolDiagnosticsPrefill({
    direction: 'request',
    sourceProtocol: item.clientProtocol || content?.diagnostic?.clientProtocol || 'OpenAI',
    targetProtocol: item.upstreamProtocol || content?.diagnostic?.upstreamProtocol || 'Gemini',
    streaming: content?.diagnostic?.isStreaming || false,
    modelName: item.requestModel || content?.diagnostic?.requestModel || '',
    payload: reqBody,
    preparedPayload: preparedBody,
    targetSiteName: item.siteName || content?.diagnostic?.siteName || '',
    attemptedModel: item.attemptedModel || content?.diagnostic?.attemptedModel || '',
    statusCode: item.statusCode || content?.diagnostic?.httpStatusCode || 400,
    errorMessage: errBody
  })

  showDetailDrawer.value = false
  void router.replace({ hash: '#developerProtocolDiagnosticsPane' })
  message.success(`已载入抓包现场至 AI 协议自愈调试工作台`)
}

function generateCurlCommand(item: DiagnosticDumpItem, dumpData: any): string {
  if (!dumpData) return ''
  const baseUrl = dumpData.diagnostic?.baseUrl || ''
  const clientProtocol = dumpData.diagnostic?.clientProtocol || 'OpenAI'
  const isAnthropic = clientProtocol.toLowerCase().includes('anthropic')
  const endpoint = isAnthropic ? '/v1/messages' : '/v1/chat/completions'
  const targetUrl = `${baseUrl.replace(/\/$/, '')}${endpoint}`
  const body = JSON.stringify(dumpData.preparedRequestBody || dumpData.clientRequestBody || {}, null, 2)

  return `curl -X POST "${targetUrl}" \\\n  -H "Content-Type: application/json" \\\n  -H "Authorization: Bearer YOUR_API_KEY" \\\n  -d '${body.replace(/'/g, "'\\''")}'`
}

const columns: DataTableColumns<DiagnosticDumpItem> = [
  {
    title: '类别',
    key: 'category',
    width: 95,
    render(row) {
      return row.category === 'failure'
        ? h(NTag, { type: 'error', size: 'small', round: true }, { default: () => '失败抓包' })
        : h(NTag, { type: 'success', size: 'small', round: true }, { default: () => '成功样本' })
    }
  },
  {
    title: '路由 / 模型',
    key: 'routeName',
    width: 220,
    render(row) {
      return h('div', { class: 'flex flex-col text-xs' }, [
        h('span', { class: 'font-bold text-slate-800 dark:text-slate-100' }, row.routeName || row.requestModel || '-'),
        h('span', { class: 'text-slate-400 font-mono text-[11px]' }, `➔ ${row.attemptedModel || '-'}`)
      ])
    }
  },
  {
    title: '目标站点',
    key: 'siteName',
    width: 130,
    render(row) {
      return h('span', { class: 'text-xs text-slate-600 dark:text-slate-300' }, row.siteName || '-')
    }
  },
  {
    title: '协议转换模式',
    key: 'forwardingMode',
    width: 140,
    render(row) {
      const isDirect = row.forwardingMode === 'direct'
      return h('div', { class: 'flex flex-col gap-0.5' }, [
        h(
          NTag,
          { size: 'tiny', type: isDirect ? 'default' : 'info', round: true },
          { default: () => (isDirect ? '直接透传' : '兼容转换') }
        ),
        h('span', { class: 'text-[10px] text-slate-400 font-mono' }, `${row.clientProtocol} ➔ ${row.upstreamProtocol}`)
      ])
    }
  },
  {
    title: '状态 / 耗时',
    key: 'statusCode',
    width: 120,
    render(row) {
      return h('div', { class: 'flex flex-col text-xs' }, [
        h(
          'span',
          { class: row.success ? 'text-emerald-600 font-semibold' : 'text-red-500 font-bold font-mono' },
          row.statusCode ? `HTTP ${row.statusCode}` : (row.success ? '成功' : '失败')
        ),
        h('span', { class: 'text-slate-400 text-[11px]' }, `${row.totalDurationMs}ms`)
      ])
    }
  },
  {
    title: '文件大小',
    key: 'fileSizeBytes',
    width: 95,
    render(row) {
      return h('span', { class: 'text-xs text-slate-500 font-mono' }, formatFileSize(row.fileSizeBytes))
    }
  },
  {
    title: '抓包时间',
    key: 'timestamp',
    width: 160,
    render(row) {
      return h('span', { class: 'text-xs text-slate-500' }, formatTime(row.timestamp as any))
    }
  },
  {
    title: '操作',
    key: 'actions',
    width: 110,
    fixed: 'right',
    render(row) {
      return h(
        NButton,
        {
          size: 'tiny',
          type: 'primary',
          ghost: true,
          onClick: () => viewDumpDetail(row)
        },
        { default: () => '查看抓包' }
      )
    }
  }
]

onMounted(() => {
  loadSamplingStatus()
  loadConfig()
  loadDumps()

  countdownTimer = setInterval(() => {
    if (samplingStatus.value.enabled && samplingStatus.value.remainingSeconds > 0) {
      samplingStatus.value.remainingSeconds--
      if (samplingStatus.value.remainingSeconds <= 0) {
        samplingStatus.value.enabled = false
        message.info('成功请求采样 10 分钟已到期，已自动关闭以保护磁盘')
      }
    }
  }, 1000)
})

onUnmounted(() => {
  if (countdownTimer) {
    clearInterval(countdownTimer)
    countdownTimer = null
  }
})
</script>

<template>
  <div class="diagnostic-dumps-tab flex flex-col gap-4">
    <!-- 说明及采样控制栏 -->
    <div class="dumps-banner p-4 rounded-xl border border-slate-200/80 dark:border-slate-800 bg-gradient-to-r from-slate-50 to-blue-50/40 dark:from-slate-900 dark:to-slate-800/60 flex flex-col md:flex-row md:items-center justify-between gap-4">
      <div class="flex flex-col gap-1">
        <div class="flex items-center gap-2 flex-wrap">
          <span class="font-bold text-base text-slate-800 dark:text-slate-100">代理诊断全量抓包与对比样本</span>
          <NTag type="info" size="small" round>保留 {{ configForm.retentionDays }} 天</NTag>
          <NTag type="default" size="small" round>正文上限 {{ configForm.maxBodyLengthMb }}MB</NTag>
        </div>
        <div class="text-xs text-slate-500 leading-relaxed max-w-3xl">
          <span class="text-red-500 font-semibold">🔴 失败请求</span>：始终自动 100% 完整抓包落地到磁盘（含原始体、发往上游体、真实报错及复现参数），方便随时二次精准复现。<br />
          <span class="text-emerald-600 font-semibold">🟢 成功请求</span>：默认<strong class="text-slate-700 dark:text-slate-200">不记录</strong>以防止硬盘爆满；点击右侧按钮可开启临时对比采样，<strong class="text-amber-600 dark:text-amber-400">最多开启 10 分钟后自动关闭</strong>。
        </div>
      </div>

      <!-- 采样与配置控制 -->
      <div class="flex items-center gap-2.5 shrink-0 flex-wrap">
        <NButton
          v-if="!samplingStatus.enabled"
          type="primary"
          secondary
          :loading="samplingLoading"
          @click="handleToggleSampling"
        >
          ⚡ 开启成功采样 (限时10分钟)
        </NButton>

        <div v-else class="flex items-center gap-2 bg-amber-50 dark:bg-amber-950/40 border border-amber-300 dark:border-amber-700 px-3 py-1.5 rounded-lg">
          <span class="text-xs font-bold text-amber-700 dark:text-amber-300 animate-pulse">
            采样中 (剩余 {{ formatSeconds(samplingStatus.remainingSeconds) }})
          </span>
          <NButton size="tiny" type="warning" ghost :loading="samplingLoading" @click="handleToggleSampling">
            关闭采样
          </NButton>
        </div>

        <NButton size="small" secondary @click="showConfigModal = true">
          ⚙️ 限制设置
        </NButton>

        <NButton secondary size="small" :loading="loading" @click="loadDumps">
          刷新
        </NButton>

        <NPopconfirm @positive-click="handleClearDumps">
          <template #trigger>
            <NButton size="small" tertiary type="error">清空抓包</NButton>
          </template>
          确定要清空所有历史抓包和对比样本文件吗？
        </NPopconfirm>
      </div>
    </div>

    <!-- 筛选和表格 -->
    <NCard :content-style="{ padding: '16px' }">
      <div class="flex items-center justify-between gap-4 mb-3 flex-wrap">
        <NRadioGroup v-model:value="categoryFilter" size="small">
          <NRadioButton value="all">全部 ({{ dumps.length }})</NRadioButton>
          <NRadioButton value="failure">仅失败抓包 ({{ dumps.filter(d => d.category === 'failure').length }})</NRadioButton>
          <NRadioButton value="sample">仅成功样本 ({{ dumps.filter(d => d.category === 'sample').length }})</NRadioButton>
        </NRadioGroup>

        <span class="text-xs text-slate-400">
          已加载最近 {{ filteredDumps.length }} 条抓包转储记录
        </span>
      </div>

      <NDataTable
        :columns="columns"
        :data="filteredDumps"
        :loading="loading"
        :pagination="{ pageSize: 15 }"
        size="small"
      />
    </NCard>

    <!-- 抓包详情抽屉 -->
    <NDrawer v-model:show="showDetailDrawer" :width="780" placement="right">
      <NDrawerContent
        :title="currentDumpItem ? `抓包详情: ${currentDumpItem.fileName}` : '抓包详情'"
        closable
      >
        <div v-if="detailLoading" class="flex justify-center items-center py-20">
          <NSpin size="large" />
        </div>

        <div v-else-if="!currentDumpContent" class="py-12">
          <NEmpty description="未读取到抓包数据或文件已被清理" />
        </div>

        <div v-else class="flex flex-col gap-4 text-xs">
          <!-- 核心元数据概览 -->
          <div class="p-3 bg-slate-50 dark:bg-slate-900 rounded-lg border border-slate-200 dark:border-slate-800 flex flex-col gap-2">
            <div class="grid grid-cols-2 gap-2 text-xs">
              <div><strong>路由名称：</strong>{{ currentDumpContent.diagnostic?.routeName || '-' }}</div>
              <div><strong>目标站点：</strong>{{ currentDumpContent.diagnostic?.siteName || '-' }} ({{ currentDumpContent.diagnostic?.baseUrl || '-' }})</div>
              <div><strong>客户端模型：</strong>{{ currentDumpContent.diagnostic?.requestModel || '-' }}</div>
              <div><strong>上游真实模型：</strong>{{ currentDumpContent.diagnostic?.attemptedModel || '-' }}</div>
              <div><strong>状态码：</strong><span :class="currentDumpContent.diagnostic?.httpStatusCode >= 400 ? 'text-red-500 font-bold' : 'text-emerald-500 font-bold'">{{ currentDumpContent.diagnostic?.httpStatusCode }}</span></div>
              <div><strong>总耗时：</strong>{{ currentDumpContent.diagnostic?.totalDurationMs }}ms</div>
              <div><strong>协议：</strong>{{ currentDumpContent.diagnostic?.clientProtocol }} ➔ {{ currentDumpContent.diagnostic?.upstreamProtocol }}</div>
              <div><strong>客户端IP：</strong>{{ currentDumpContent.diagnostic?.clientIp || '-' }}</div>
            </div>
            <div v-if="currentDumpContent.diagnostic?.errorMessage" class="text-red-600 dark:text-red-400 font-mono bg-red-50 dark:bg-red-950/40 p-2 rounded border border-red-200 dark:border-red-900">
              <strong>错误信息：</strong>{{ currentDumpContent.diagnostic?.errorMessage }}
            </div>
          </div>

          <!-- 一键复制复现 curl 与载入协议自愈 -->
          <div class="flex items-center justify-between p-2.5 bg-blue-50/70 dark:bg-blue-950/30 border border-blue-200 dark:border-blue-900 rounded-lg">
            <span class="text-blue-700 dark:text-blue-300">💡 快捷排障操作：</span>
            <div class="flex items-center gap-2">
              <NButton size="tiny" type="warning" secondary @click="loadIntoProtocolDiagnostics(currentDumpItem!, currentDumpContent)">
                🤖 载入至 AI 协议自愈调试
              </NButton>
              <NButton size="tiny" type="primary" @click="copyText(generateCurlCommand(currentDumpItem!, currentDumpContent), '已生成并复制复现 curl 命令')">
                复制 cURL 复现命令
              </NButton>
            </div>
          </div>

          <!-- 发往上游的实际请求体 (preparedRequestBody) -->
          <div class="border border-slate-200 dark:border-slate-800 rounded-lg overflow-hidden">
            <div class="bg-slate-100 dark:bg-slate-800 px-3 py-2 flex items-center justify-between font-semibold">
              <span>发往上游的实际请求体 (preparedRequestBody - 转换后)</span>
              <NButton size="tiny" secondary @click="copyText(JSON.stringify(currentDumpContent.preparedRequestBody, null, 2))">
                复制 JSON
              </NButton>
            </div>
            <pre class="p-3 bg-slate-950 text-slate-200 font-mono text-[11px] overflow-auto max-h-72 leading-relaxed">{{ JSON.stringify(currentDumpContent.preparedRequestBody, null, 2) }}</pre>
          </div>

          <!-- 原始客户端请求体 (clientRequestBody) -->
          <div class="border border-slate-200 dark:border-slate-800 rounded-lg overflow-hidden">
            <div class="bg-slate-100 dark:bg-slate-800 px-3 py-2 flex items-center justify-between font-semibold">
              <span>客户端原始请求体 (clientRequestBody)</span>
              <NButton size="tiny" secondary @click="copyText(JSON.stringify(currentDumpContent.clientRequestBody, null, 2))">
                复制 JSON
              </NButton>
            </div>
            <pre class="p-3 bg-slate-950 text-slate-200 font-mono text-[11px] overflow-auto max-h-72 leading-relaxed">{{ JSON.stringify(currentDumpContent.clientRequestBody, null, 2) }}</pre>
          </div>

          <!-- 上游响应体 (upstreamResponseBody) -->
          <div class="border border-slate-200 dark:border-slate-800 rounded-lg overflow-hidden">
            <div class="bg-slate-100 dark:bg-slate-800 px-3 py-2 flex items-center justify-between font-semibold">
              <span>上游返回正文 (upstreamResponseBody)</span>
              <NButton size="tiny" secondary @click="copyText(typeof currentDumpContent.upstreamResponseBody === 'string' ? currentDumpContent.upstreamResponseBody : JSON.stringify(currentDumpContent.upstreamResponseBody, null, 2))">
                复制
              </NButton>
            </div>
            <pre class="p-3 bg-slate-950 text-slate-200 font-mono text-[11px] overflow-auto max-h-72 leading-relaxed">{{ typeof currentDumpContent.upstreamResponseBody === 'string' ? currentDumpContent.upstreamResponseBody : JSON.stringify(currentDumpContent.upstreamResponseBody, null, 2) }}</pre>
          </div>
        </div>
      </NDrawerContent>
    </NDrawer>

    <!-- 动态限制设置弹窗 -->
    <NModal
      v-model:show="showConfigModal"
      preset="card"
      title="⚙️ 诊断抓包与自愈限制参数设置"
      style="width: 540px; max-width: 95vw;"
    >
      <NAlert type="info" :bordered="false" class="mb-4 text-xs">
        在此可随时临时放宽正文捕获上限与保留天数，设置保存后<strong>立即生效</strong>，无需重启服务。
      </NAlert>

      <NForm label-placement="left" label-width="180" size="small">
        <NFormItem label="抓包正文捕获上限 (MB)">
          <div class="flex items-center gap-2 w-full">
            <NInputNumber v-model:value="configForm.maxBodyLengthMb" :min="1" :max="50" class="w-32" />
            <span class="text-xs text-slate-400">MB (支持 1 ~ 50MB)</span>
          </div>
        </NFormItem>

        <NFormItem label="AI自愈试探响应上限 (MB)">
          <div class="flex items-center gap-2 w-full">
            <NInputNumber v-model:value="configForm.maxRoundResponseMb" :min="1" :max="20" class="w-32" />
            <span class="text-xs text-slate-400">MB (支持 1 ~ 20MB)</span>
          </div>
        </NFormItem>

        <NFormItem label="历史抓包保留天数 (天)">
          <div class="flex items-center gap-2 w-full">
            <NInputNumber v-model:value="configForm.retentionDays" :min="1" :max="30" class="w-32" />
            <span class="text-xs text-slate-400">天 (支持 1 ~ 30天)</span>
          </div>
        </NFormItem>

        <NFormItem label="单日单目录失败抓包上限">
          <div class="flex items-center gap-2 w-full">
            <NInputNumber v-model:value="configForm.maxFailuresPerDay" :min="10" :max="500" class="w-32" />
            <span class="text-xs text-slate-400">个 (支持 10 ~ 500个)</span>
          </div>
        </NFormItem>
      </NForm>

      <template #footer>
        <div class="flex justify-end gap-2">
          <NButton secondary @click="showConfigModal = false">取消</NButton>
          <NButton type="primary" :loading="configSaving" @click="handleSaveConfig">保存并即时生效</NButton>
        </div>
      </template>
    </NModal>
  </div>
</template>

<style scoped>
.diagnostic-dumps-tab {
  min-width: 0;
}
</style>

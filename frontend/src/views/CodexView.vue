<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NCard, NButton, NSpace, NTag, NEmpty, NSpin, NModal, NInput, NPopconfirm, NProgress, NCheckbox, NTabs, NTabPane, useMessage } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import * as api from '@/api/codex'
import type {
  CodexAccount,
  CodexCredentialImportFailure,
  CodexInspectionLog,
  CodexInspectionRunResult,
  CodexInspectionStatus,
  CodexModelSelection,
  CodexRemoteModelItem,
  CodexResetCreditsInfo
} from '@/api/codex'

const message = useMessage()
const route = useRoute()
const router = useRouter()
const activeTab = ref(route.query.tab === 'inspection' ? 'inspection' : 'accounts')
const loading = ref(false)
const accounts = ref<CodexAccount[]>([])
const inspection = ref<CodexInspectionStatus | null>(null)
const inspectionLastRun = ref<CodexInspectionRunResult | null>(null)
const inspectionLogs = ref<CodexInspectionLog[]>([])
const inspectionRunning = ref(false)
// 功能未开启时的提示态
const featureDisabled = ref(false)

// OAuth 弹窗
const oauthModal = ref(false)
const oauthUrl = ref('')
const oauthCallbackInput = ref('')
const oauthDisplayName = ref('')
const oauthLoading = ref(false)
const oauthStartLoading = ref(false)

// 凭证导入弹窗
const importModal = ref(false)
const importJsonText = ref('')
const importFiles = ref<File[]>([])
const importLoading = ref(false)
const importFailures = ref<CodexCredentialImportFailure[]>([])

const exportMode = ref(false)
const selectedExportAccountIds = ref<string[]>([])
const exportLoading = ref(false)

// 编辑账号（重命名）弹窗
const editModal = ref(false)
const editAccount = ref<CodexAccount | null>(null)
const editDisplayName = ref('')
const editLoading = ref(false)

// 重置额度信用弹窗
const resetCreditModal = ref(false)
const resetCreditAccount = ref<CodexAccount | null>(null)
const resetCreditInfo = ref<CodexResetCreditsInfo | null>(null)
const resetCreditLoading = ref(false)
const resetCreditSubmitting = ref(false)

// 拉取/导入模型弹窗
const modelModal = ref(false)
const modelAccount = ref<CodexAccount | null>(null)
type EditableCodexModel = CodexRemoteModelItem & { alias: string }

const modelList = ref<EditableCodexModel[]>([])
const checkedModels = ref<string[]>([])
const modelSearch = ref('')
const modelLoading = ref(false)

const filteredModelList = computed(() => {
  const keyword = modelSearch.value.trim().toLowerCase()
  if (!keyword) return modelList.value
  return modelList.value.filter((model) => (
    `${model.remoteModelName} ${model.displayName} ${model.alias}`
      .toLowerCase()
      .includes(keyword)
  ))
})
const visibleCheckedModelCount = computed(() => (
  filteredModelList.value.filter(model => (
    checkedModels.value.includes(model.remoteModelName)
  )).length
))
const allVisibleModelsChecked = computed(() => (
  filteredModelList.value.length > 0
  && visibleCheckedModelCount.value === filteredModelList.value.length
))
const someVisibleModelsChecked = computed(() => (
  visibleCheckedModelCount.value > 0
  && !allVisibleModelsChecked.value
))

let pollTimer: ReturnType<typeof setInterval> | null = null

async function loadInspection(): Promise<void> {
  try {
    const [status, lastRun, logs] = await Promise.all([
      api.getCodexInspectionStatus(),
      api.getCodexInspectionLastRun(),
      api.getCodexInspectionLogs()
    ])
    inspection.value = status
    inspectionLastRun.value = lastRun
    inspectionLogs.value = logs
  } catch {
    inspection.value = null
    inspectionLastRun.value = null
    inspectionLogs.value = []
  }
}

async function load(): Promise<void> {
  loading.value = true
  featureDisabled.value = false
  try {
    accounts.value = await api.listCodexAccounts()
    await loadInspection()
  } catch (e) {
    // Codex 功能未开启时后端返回 404，显示提示而非空白
    if ((e as { status?: number }).status === 404) {
      featureDisabled.value = true
    } else {
      message.error((e as Error).message)
    }
  } finally { loading.value = false }
}

async function refreshSilently(): Promise<void> {
  try {
    accounts.value = await api.listCodexAccounts()
  } catch {
    return
  }
  await loadInspection()
}

async function handleStartOAuth(): Promise<void> {
  oauthStartLoading.value = true
  try {
    const result = await api.startCodexOAuth()
    oauthUrl.value = result.url
    oauthCallbackInput.value = ''
    oauthDisplayName.value = ''
    oauthModal.value = true
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    oauthStartLoading.value = false
  }
}

async function handleCompleteOAuth(): Promise<void> {
  if (!oauthCallbackInput.value.trim()) { message.warning('请粘贴回调 URL'); return }
  oauthLoading.value = true
  try {
    await api.completeCodexOAuth(oauthCallbackInput.value.trim(), oauthDisplayName.value.trim() || undefined)
    message.success('OAuth 登录成功')
    oauthModal.value = false
    await load()
  } catch (e) { message.error((e as Error).message) } finally { oauthLoading.value = false }
}

async function handleToggle(acc: CodexAccount): Promise<void> {
  try {
    await api.toggleCodexAccount(acc.id)
    acc.isEnabled = !acc.isEnabled
  } catch (e) { message.error((e as Error).message) }
}
async function handleRefreshQuota(acc: CodexAccount): Promise<void> {
  try {
    await api.refreshCodexQuota(acc.id)
    message.success('已刷新额度')
    await load()
  } catch (e) { message.error((e as Error).message) }
}
async function handleRefreshToken(acc: CodexAccount): Promise<void> {
  try {
    await api.refreshCodexToken(acc.id)
    message.success('已刷新 Token')
    await load()
  } catch (e) { message.error((e as Error).message) }
}
async function handleResetQuota(acc: CodexAccount): Promise<void> {
  try {
    await api.resetCodexQuota(acc.id)
    message.success('已清除额度冷却并恢复账号')
    await load()
  } catch (e) { message.error((e as Error).message) }
}
async function handleDelete(acc: CodexAccount): Promise<void> {
  await api.deleteCodexAccount(acc.id)
  message.success('已删除账号')
  await load()
}
async function handleRunInspection(force: boolean): Promise<void> {
  if (inspectionRunning.value) return
  inspectionRunning.value = true
  try {
    inspectionLastRun.value = await api.runCodexInspection(force)
    message.success(force ? '真实巡检已完成' : '手动巡检已完成')
    await refreshSilently()
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    inspectionRunning.value = false
  }
}

// 编辑（重命名）
function openEdit(acc: CodexAccount): void {
  editAccount.value = acc
  editDisplayName.value = acc.displayName
  editModal.value = true
}
async function handleSaveEdit(): Promise<void> {
  if (!editAccount.value || !editDisplayName.value.trim()) { message.warning('名称不能为空'); return }
  editLoading.value = true
  try {
    await api.updateCodexAccount(editAccount.value.id, editDisplayName.value.trim())
    message.success('已更新')
    editModal.value = false
    await load()
  } catch (e) { message.error((e as Error).message) } finally { editLoading.value = false }
}

// 重置额度信用
async function openResetCredit(acc: CodexAccount): Promise<void> {
  resetCreditAccount.value = acc
  resetCreditInfo.value = null
  resetCreditModal.value = true
  resetCreditLoading.value = true
  try {
    const info = await api.getResetCredits(acc.id)
    resetCreditInfo.value = info
    if (!info.success) {
      message.error(info.error || '重置信用加载失败')
    }
  } catch (e) { message.error((e as Error).message) } finally { resetCreditLoading.value = false }
}
async function handleConsumeResetCredit(): Promise<void> {
  if (!resetCreditAccount.value || resetCreditSubmitting.value) return
  resetCreditSubmitting.value = true
  try {
    await api.consumeResetCredit(resetCreditAccount.value.id)
    message.success('手动重置额度成功')
    resetCreditModal.value = false
    await load()
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    resetCreditSubmitting.value = false
  }
}

// 拉取/导入模型
async function openFetchModels(acc: CodexAccount): Promise<void> {
  modelAccount.value = acc
  modelList.value = []
  checkedModels.value = []
  modelModal.value = true
  modelLoading.value = true
  try {
    modelSearch.value = ''
    const models = await api.fetchCodexModels(acc.id)
    modelList.value = models.map(model => ({
      ...model,
      alias: model.existingDisplayName
        || model.displayName
        || model.remoteModelName
    }))
    checkedModels.value = models
      .filter(model => (
        model.existingMappingId
          ? model.isEnabled
          : true
      ))
      .map(model => model.remoteModelName)
  } catch (e) { message.error((e as Error).message) } finally { modelLoading.value = false }
}

function toggleVisibleModels(checked: boolean): void {
  const visibleNames = filteredModelList.value.map(
    model => model.remoteModelName
  )
  if (checked) {
    checkedModels.value = Array.from(new Set([
      ...checkedModels.value,
      ...visibleNames
    ]))
  } else {
    checkedModels.value = checkedModels.value.filter(
      name => !visibleNames.includes(name)
    )
  }
}
async function handleImportModels(): Promise<void> {
  if (!modelAccount.value || checkedModels.value.length === 0) { message.warning('请选择要导入的模型'); return }
  modelLoading.value = true
  try {
    const selections: CodexModelSelection[] = modelList.value.map(model => ({
      remoteModelName: model.remoteModelName,
      displayName: model.alias.trim() || model.remoteModelName,
      selected: checkedModels.value.includes(model.remoteModelName)
    }))
    await api.importSelectedCodexModels(modelAccount.value.id, selections)
    message.success(`已导入 ${checkedModels.value.length} 个模型`)
    modelModal.value = false
    await load()
  } catch (e) { message.error((e as Error).message) } finally { modelLoading.value = false }
}

function openImportCredential(): void {
  importJsonText.value = ''
  importFiles.value = []
  importFailures.value = []
  importModal.value = true
}

function closeImportCredential(): void {
  importJsonText.value = ''
  importFiles.value = []
  importFailures.value = []
  importModal.value = false
}

function handleCredentialFiles(event: Event): void {
  const input = event.target as HTMLInputElement
  importFiles.value = Array.from(input.files ?? [])
}

async function handleImportCredential(): Promise<void> {
  if (importFiles.value.length === 0 && !importJsonText.value.trim()) {
    message.warning('请选择凭证文件或粘贴凭证 JSON')
    return
  }
  importLoading.value = true
  importFailures.value = []
  try {
    const result = importFiles.value.length > 0
      ? await api.importCredentialFiles(importFiles.value)
      : await api.importCredential(importJsonText.value.trim())
    importJsonText.value = ''
    importFiles.value = []
    importFailures.value = result.failures

    if (result.failures.length > 0) {
      if (result.successes.length > 0) {
        message.warning(`成功导入 ${result.successes.length} 个，失败 ${result.failures.length} 个`)
        await load()
      } else {
        message.error('凭证导入失败')
      }
      return
    }

    message.success(`成功导入 ${result.successes.length} 个凭证`)
    importModal.value = false
    await load()
  } catch (e) { message.error((e as Error).message) } finally { importLoading.value = false }
}

function beginExportCredentials(): void {
  selectedExportAccountIds.value = []
  exportMode.value = true
}

function cancelExportCredentials(): void {
  selectedExportAccountIds.value = []
  exportMode.value = false
}

function toggleExportAccount(id: string, checked: boolean): void {
  selectedExportAccountIds.value = checked
    ? Array.from(new Set([...selectedExportAccountIds.value, id]))
    : selectedExportAccountIds.value.filter(accountId => accountId !== id)
}

function downloadCredential(
  credential: api.CodexExportCredential,
  index: number
): void {
  const identity = String(
    credential.email
    || credential.account_id
    || index + 1
  ).replace(/[^a-zA-Z0-9._-]+/g, '_')
  const blob = new Blob(
    [JSON.stringify(credential, null, 2)],
    { type: 'application/json' }
  )
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = `codex_credential_${identity}.json`
  link.click()
  URL.revokeObjectURL(url)
}

async function handleExportCredentials(): Promise<void> {
  if (selectedExportAccountIds.value.length === 0) {
    message.warning('请选择要导出的账号')
    return
  }
  exportLoading.value = true
  try {
    const result = await api.exportCredentials(
      selectedExportAccountIds.value
    )
    result.credentials.forEach(downloadCredential)
    message.success(`已导出 ${result.credentials.length} 个凭证文件`)
    cancelExportCredentials()
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    exportLoading.value = false
  }
}

async function copyText(text: string): Promise<void> {
  if (!text) return
  if (window.isSecureContext && navigator.clipboard) {
    try {
      await navigator.clipboard.writeText(text)
      message.success('已复制到剪贴板')
      return
    } catch {
      // HTTP 或权限受限时使用传统复制方式。
    }
  }
  const textarea = document.createElement('textarea')
  textarea.value = text
  textarea.style.position = 'fixed'
  textarea.style.left = '-9999px'
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

function formatDateTime(value: string | null | undefined): string {
  if (!value) return '从未'
  return new Date(value).toLocaleString('zh-CN')
}

function formatBeijingTime(value: string | null | undefined): string {
  if (!value) return '—'
  return new Date(value).toLocaleString('zh-CN', {
    timeZone: 'Asia/Shanghai',
    hour12: false
  })
}

function formatQuotaPercent(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(Number(value))) return '-'
  return `${Math.round(Number(value))}%`
}

function accountQuotaPercent(acc: CodexAccount): number | null {
  if (acc.windows && acc.windows.length > 0) {
    return Math.min(...acc.windows.map((w) => Math.max(0, 100 - Number(w.usedPercent || 0))))
  }
  const percents = [acc.fiveHourUsedPercent, acc.weeklyUsedPercent]
    .filter((value): value is number => value != null && Number.isFinite(Number(value)))
  return percents.length ? Math.min(...percents.map((value) => Math.max(0, 100 - value))) : null
}

function accountStatusLabel(acc: CodexAccount): string {
  if (acc.isQuotaCooling) return '冷却中'
  return acc.isEnabled ? '正常' : '已禁用'
}

function accountStatusType(acc: CodexAccount): 'success' | 'warning' | 'default' {
  if (acc.isQuotaCooling) return 'warning'
  return acc.isEnabled ? 'success' : 'default'
}

// 额度进度条颜色
function quotaColor(percent: number | null | undefined): 'success' | 'warning' | 'error' {
  if (percent == null) return 'success'
  if (percent < 20) return 'error'
  if (percent < 50) return 'warning'
  return 'success'
}

watch(activeTab, (tab) => {
  const query = { ...route.query }
  if (tab === 'inspection') query.tab = 'inspection'
  else delete query.tab
  void router.replace({ query })
})

watch(() => route.query.tab, (tab) => {
  activeTab.value = tab === 'inspection' ? 'inspection' : 'accounts'
})

onMounted(() => {
  load()
  pollTimer = setInterval(() => {
    if (document.visibilityState === 'visible') {
      void refreshSilently()
    }
  }, 5000)
})
onUnmounted(() => { if (pollTimer) clearInterval(pollTimer) })
</script>

<template>
  <div class="page-container">
    <PageHeader title="OAuth 管理" subtitle="管理 Codex OAuth 登录账号、凭证导入、额度、巡检与自动禁用">
      <template #actions>
        <template v-if="exportMode">
          <NTag round :bordered="false" size="small">已选 {{ selectedExportAccountIds.length }} 个</NTag>
          <NButton size="small" @click="cancelExportCredentials">取消选择</NButton>
          <NButton size="small" type="primary" :loading="exportLoading" @click="handleExportCredentials">导出选中</NButton>
        </template>
        <template v-else>
          <NTag v-if="accounts.length" round :bordered="false" size="small">{{ accounts.length }} 个</NTag>
          <NButton size="small" quaternary @click="openImportCredential">导入凭证</NButton>
          <NButton size="small" quaternary :disabled="accounts.length === 0" @click="beginExportCredentials">导出凭证</NButton>
          <NButton size="small" type="primary" :loading="oauthStartLoading" @click="handleStartOAuth">OAuth 登录</NButton>
        </template>
      </template>
    </PageHeader>
    <NSpin :show="loading">
      <NTabs v-model:value="activeTab" type="line" animated>
        <NTabPane name="accounts" tab="账号额度">
          <div class="codex-stack">
            <NEmpty v-if="featureDisabled" description="Codex 功能未开启，请在系统设置中开启" />
            <NEmpty v-else-if="accounts.length === 0" description="暂无 Codex 账号，可使用右上角 OAuth 登录或导入凭证" />

            <div v-else class="codex-grid">
              <article v-for="acc in accounts" :key="acc.id" class="codex-card" :class="{ disabled: !acc.isEnabled, selected: selectedExportAccountIds.includes(acc.id) }">
                <div class="codex-card-header">
                  <NCheckbox
                    v-if="exportMode"
                    :checked="selectedExportAccountIds.includes(acc.id)"
                    @update:checked="(checked: boolean) => toggleExportAccount(acc.id, checked)"
                  />
                  <div class="codex-card-header-main">
                    <div class="codex-account-name">
                      <span>{{ acc.displayName }}</span>
                      <NTag size="small" :type="accountStatusType(acc)" :bordered="false">{{ accountStatusLabel(acc) }}</NTag>
                    </div>
                    <div class="codex-account-email">{{ acc.email || acc.accountId || '未记录账号标识' }}</div>
                  </div>
                  <span v-if="acc.planType" class="codex-plan">{{ acc.planType }}</span>
                </div>

                <div class="account-kpi-row">
                  <div class="account-kpi">
                    <span class="account-kpi-label">剩余额度</span>
                    <strong :class="['account-kpi-value', quotaColor(accountQuotaPercent(acc))]">{{ formatQuotaPercent(accountQuotaPercent(acc)) }}</strong>
                  </div>
                  <div class="account-kpi">
                    <span class="account-kpi-label">重置信用</span>
                    <strong class="account-kpi-value">{{ acc.resetCreditsAvailableCount ?? '-' }}</strong>
                  </div>
                </div>

                <div class="account-meta-grid">
                  <div><span>上次额度检查</span><strong>{{ formatDateTime(acc.lastQuotaCheckedAt) }}</strong></div>
                  <div><span>自动禁用阈值</span><strong>{{ formatQuotaPercent(acc.autoDisableThreshold) }}</strong></div>
                  <div v-if="acc.quotaCoolingUntil"><span>冷却至</span><strong>{{ formatDateTime(acc.quotaCoolingUntil) }}</strong></div>
                </div>

                <div v-if="acc.windows && acc.windows.length > 0" class="codex-windows-container">
                  <div v-for="w in acc.windows" :key="w.id" class="codex-window">
                    <div class="codex-window-label">{{ w.label }}</div>
                    <NProgress
                      :percentage="Math.max(0, 100 - Math.round(w.usedPercent))"
                      :status="quotaColor(100 - w.usedPercent)"
                      :show-indicator="false"
                      :height="6"
                      :border-radius="3"
                    />
                    <span class="codex-window-percent">剩余 {{ Math.max(0, 100 - Math.round(w.usedPercent)) }}%</span>
                    <div v-if="w.resetLabel" class="codex-window-reset">重置于 {{ w.resetLabel }}</div>
                  </div>
                </div>
                <div v-else class="codex-window-placeholder">暂无额度窗口数据，刷新额度后显示。</div>

                <div v-if="!exportMode" class="account-actions codex-card-actions">
                  <NButton size="small" secondary @click="handleRefreshQuota(acc)">刷新额度</NButton>
                  <NButton size="small" secondary @click="handleRefreshToken(acc)">刷新 Token</NButton>
                  <NButton size="small" secondary @click="openEdit(acc)">编辑</NButton>
                  <NButton size="small" secondary @click="openFetchModels(acc)">拉取模型</NButton>
                  <NPopconfirm v-if="acc.isQuotaCooling" @positive-click="handleResetQuota(acc)">
                    <template #trigger><NButton size="small" secondary type="warning">清除冷却</NButton></template>
                    清除本地额度冷却、刷新 Token 并恢复该账号？
                  </NPopconfirm>
                  <NButton size="small" secondary @click="openResetCredit(acc)">重置信用</NButton>
                  <NButton size="small" secondary :type="acc.isEnabled ? 'warning' : 'success'" @click="handleToggle(acc)">{{ acc.isEnabled ? '禁用' : '启用' }}</NButton>
                  <NPopconfirm @positive-click="handleDelete(acc)">
                    <template #trigger><NButton size="small" secondary type="error">删除</NButton></template>
                    删除账号「{{ acc.displayName }}」？关联站点和路由会一并清理。
                  </NPopconfirm>
                </div>
              </article>
            </div>
          </div>
        </NTabPane>

        <NTabPane name="inspection" tab="巡检">
          <NEmpty v-if="!inspection" description="Codex 巡检功能未开启" />
          <div v-else class="inspection-workspace">
            <NCard class="inspection-card" size="small">
              <div class="inspection-content">
                <div class="inspection-main">
                  <NTag :type="inspection.isRunning || inspectionRunning ? 'warning' : 'success'" :bordered="false">
                    {{ inspection.isRunning || inspectionRunning ? '巡检中' : '空闲' }}
                  </NTag>
                  <div>
                    <div class="inspection-title">Codex 巡检状态</div>
                    <div class="inspection-meta">上次完成：{{ formatDateTime(inspection.lastFinishedAt) }}</div>
                  </div>
                </div>
                <div class="inspection-actions">
                  <span v-if="inspection.nextScheduledAt" class="inspection-meta">下次：{{ formatDateTime(inspection.nextScheduledAt) }}</span>
                  <NButton size="small" :loading="inspectionRunning" @click="handleRunInspection(false)">手动巡检</NButton>
                  <NButton size="small" type="primary" :loading="inspectionRunning" @click="handleRunInspection(true)">真实巡检</NButton>
                  <NButton size="small" secondary :disabled="inspectionRunning" @click="loadInspection">刷新状态</NButton>
                </div>
              </div>
            </NCard>

            <NCard v-if="inspectionLastRun" title="上次巡检结果" size="small">
              <div class="inspection-summary-grid">
                <div><span>保留</span><strong>{{ inspectionLastRun.keepCount }}</strong></div>
                <div><span>禁用</span><strong>{{ inspectionLastRun.disableCount }}</strong></div>
                <div><span>启用</span><strong>{{ inspectionLastRun.enableCount }}</strong></div>
                <div><span>缓存命中</span><strong>{{ inspectionLastRun.cacheCount }}</strong></div>
                <div><span>真实刷新</span><strong>{{ inspectionLastRun.realRefreshCount }}</strong></div>
              </div>
              <div class="inspection-run-meta">
                {{ inspectionLastRun.autoTriggered ? '自动巡检' : '手动巡检' }} ·
                {{ inspectionLastRun.forcedRefresh ? '强制真实刷新' : '允许使用缓存' }} ·
                完成于 {{ formatDateTime(inspectionLastRun.finishedAt) }}
              </div>
              <div class="inspection-table-scroll">
                <div class="inspection-table">
                  <div class="inspection-table-head">
                    <span>账号</span><span>5 小时</span><span>周额度</span><span>来源</span><span>动作</span><span>原因</span>
                  </div>
                  <div v-for="item in inspectionLastRun.accounts" :key="item.accountId" class="inspection-table-row">
                    <strong>{{ item.displayName }}</strong>
                    <span>{{ formatQuotaPercent(item.fiveHourUsedPercent) }}</span>
                    <span>{{ formatQuotaPercent(item.weeklyUsedPercent) }}</span>
                    <NTag size="tiny" :bordered="false">{{ item.fromCache ? '缓存' : '实时' }}</NTag>
                    <NTag size="tiny" :type="item.action === 'disable' ? 'error' : item.action === 'enable' ? 'success' : 'default'" :bordered="false">{{ item.action }}</NTag>
                    <span class="inspection-reason">{{ item.reason }}</span>
                  </div>
                </div>
              </div>
            </NCard>

            <NCard title="巡检日志" size="small">
              <NEmpty v-if="inspectionLogs.length === 0" description="暂无巡检日志" size="small" />
              <div v-else class="inspection-log-list">
                <div v-for="(log, idx) in inspectionLogs" :key="`${log.at}-${idx}`" class="inspection-log-row">
                  <span>{{ formatDateTime(log.at) }}</span>
                  <NTag size="tiny" :bordered="false">{{ log.category }}</NTag>
                  <strong>{{ log.message }}</strong>
                </div>
              </div>
            </NCard>
          </div>
        </NTabPane>
      </NTabs>
    </NSpin>

    <!-- OAuth 弹窗 -->
    <NModal v-model:show="oauthModal" title="Codex OAuth 登录" preset="card" style="width: 600px; max-width: 92vw" :mask-closable="false">
      <div style="margin-bottom: 12px">
        <p style="margin: 0 0 8px; font-weight: 600">第 1 步：打开授权链接</p>
        <NInput :value="oauthUrl" readonly type="textarea" :autosize="{ minRows: 2 }" />
        <NSpace style="margin-top: 8px">
          <NButton size="small" secondary @click="copyText(oauthUrl)">复制授权链接</NButton>
          <NButton size="small" tag="a" :href="oauthUrl" target="_blank">在新标签打开</NButton>
        </NSpace>
      </div>
      <div>
        <p style="margin: 0 0 8px; font-weight: 600">第 2 步：完成授权后，粘贴回调后的完整 URL</p>
        <p class="oauth-callback-hint">
          浏览器跳转到 localhost 后无法连接是正常现象，请复制地址栏中包含 code 和 state 的完整地址。
        </p>
        <NInput v-model:value="oauthCallbackInput" placeholder="http://localhost:1455/auth/callback?code=...&state=..." type="textarea" :autosize="{ minRows: 2 }" />
      </div>
      <div style="margin-top: 12px">
        <p style="margin: 0 0 8px; font-weight: 600">显示名称（可选）</p>
        <NInput v-model:value="oauthDisplayName" placeholder="给这个账号起个好认的名字" />
      </div>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="oauthModal = false">取消</NButton>
          <NButton type="primary" :loading="oauthLoading" @click="handleCompleteOAuth">完成登录</NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- 导入凭证弹窗 -->
    <NModal v-model:show="importModal" title="导入 Codex 凭证" preset="card" style="width: 600px; max-width: 92vw" :mask-closable="false" @after-leave="closeImportCredential">
      <p class="credential-import-label">选择一个或多个 CPA 凭证 JSON 文件：</p>
      <input class="credential-file-input" type="file" accept=".json,application/json" multiple @change="handleCredentialFiles">
      <div v-if="importFiles.length" class="credential-file-summary">已选择 {{ importFiles.length }} 个文件，将优先导入所选文件。</div>
      <div class="credential-import-divider"><span>或粘贴 JSON</span></div>
      <p class="credential-import-label">粘贴 CPA 格式的凭证 JSON（含 access_token / refresh_token / id_token）：</p>
      <NInput
        v-model:value="importJsonText"
        type="textarea"
        :autosize="{ minRows: 8, maxRows: 20 }"
        placeholder='{"access_token":"...","refresh_token":"...","id_token":"..."}'
        style="font-family: monospace"
      />
      <div v-if="importFailures.length" class="credential-import-failures">
        <strong>导入失败明细</strong>
        <div v-for="(failure, idx) in importFailures" :key="`${failure.fileName}-${idx}`">
          {{ failure.fileName || '凭证 JSON' }}：{{ failure.error }}
        </div>
      </div>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="closeImportCredential">取消</NButton>
          <NButton type="primary" :loading="importLoading" @click="handleImportCredential">导入</NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- 编辑账号（重命名）弹窗 -->
    <NModal v-model:show="editModal" title="编辑账号" preset="card" style="width: 420px; max-width: 92vw">
      <NInput v-model:value="editDisplayName" placeholder="显示名称" />
      <template #footer>
        <NSpace justify="end">
          <NButton @click="editModal = false">取消</NButton>
          <NButton type="primary" :loading="editLoading" @click="handleSaveEdit">保存</NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- 重置额度信用弹窗 -->
    <NModal v-model:show="resetCreditModal" title="重置额度信用" preset="card" style="width: 480px; max-width: 92vw">
      <NSpin :show="resetCreditLoading">
        <div v-if="resetCreditInfo">
          <p v-if="!resetCreditInfo.success" class="reset-credit-error">
            {{ resetCreditInfo.error || '重置信用加载失败' }}
          </p>
          <template v-else>
            <p class="reset-credit-count">
              可用重置次数：<strong>{{ resetCreditInfo.availableCount }}</strong>
            </p>
            <div v-if="resetCreditInfo.credits.length > 0" class="reset-credit-list">
              <p class="reset-credit-list-title">各次重置过期时间（北京时间 GMT+8）：</p>
              <div
                v-for="(credit, idx) in resetCreditInfo.credits"
                :key="credit.id || idx"
                class="reset-credit-item"
              >
                <div>
                  <strong>第 {{ idx + 1 }} 次重置</strong>
                  <span>发放时间：{{ formatBeijingTime(credit.grantedAt) }}</span>
                </div>
                <div class="reset-credit-expiry">
                  <span>过期时间</span>
                  <strong>{{ formatBeijingTime(credit.expiresAt) }}</strong>
                </div>
              </div>
            </div>
          </template>
        </div>
      </NSpin>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="resetCreditModal = false">关闭</NButton>
          <NPopconfirm
            v-if="resetCreditInfo?.success && resetCreditInfo.availableCount > 0"
            @positive-click="handleConsumeResetCredit"
          >
            <template #trigger>
              <NButton type="primary" :loading="resetCreditSubmitting">消耗一次重置</NButton>
            </template>
            确认消耗一张手动重置额度并执行真实额度重置？此操作不可撤销。
          </NPopconfirm>
        </NSpace>
      </template>
    </NModal>

    <!-- 拉取/导入模型弹窗 -->
    <NModal v-model:show="modelModal" :title="`拉取模型 - ${modelAccount?.displayName ?? ''}`" preset="card" style="width: 560px; max-width: 92vw">
      <NSpin :show="modelLoading">
        <NEmpty v-if="!modelLoading && modelList.length === 0" description="该账号无可用模型" size="small" />
        <template v-else>
          <div class="codex-model-toolbar">
            <NInput v-model:value="modelSearch" size="small" clearable placeholder="搜索模型" />
            <NCheckbox
              :checked="allVisibleModelsChecked"
              :indeterminate="someVisibleModelsChecked"
              @update:checked="toggleVisibleModels"
            >
              已选 {{ visibleCheckedModelCount }} / {{ filteredModelList.length }} 个
            </NCheckbox>
          </div>
          <div class="codex-model-list">
            <div
              v-for="m in filteredModelList"
              :key="m.remoteModelName"
              class="codex-model-row"
            >
              <NCheckbox
                :checked="checkedModels.includes(m.remoteModelName)"
                @update:checked="(checked: boolean) => checked
                  ? checkedModels.push(m.remoteModelName)
                  : (checkedModels = checkedModels.filter(name => name !== m.remoteModelName))"
              />
              <code :title="m.remoteModelName">{{ m.remoteModelName }}</code>
              <NInput v-model:value="m.alias" size="small" placeholder="显示别名" />
              <NTag
                size="tiny"
                :type="m.existingMappingId ? (m.isEnabled ? 'success' : 'default') : 'info'"
                :bordered="false"
              >
                {{ m.existingMappingId ? (m.isEnabled ? '已启用' : '已禁用') : '新' }}
              </NTag>
            </div>
          </div>
        </template>
      </NSpin>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="modelModal = false">取消</NButton>
          <NButton type="primary" :disabled="checkedModels.length === 0" :loading="modelLoading" @click="handleImportModels">
            导入选中（{{ checkedModels.length }}）
          </NButton>
        </NSpace>
      </template>
    </NModal>
  </div>
</template>

<style scoped>
.codex-model-toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 12px;
}

.codex-model-toolbar :deep(.n-input) {
  flex: 1;
}

.codex-model-list {
  max-height: 420px;
  overflow: auto;
}

.codex-model-row {
  display: grid;
  grid-template-columns: 30px minmax(140px, 1fr) 200px auto;
  align-items: center;
  gap: 8px;
  padding: 8px 0;
  border-bottom: 1px solid var(--border-color-global);
}

.codex-model-row code {
  overflow: hidden;
  font-size: 13px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.oauth-callback-hint {
  margin: 0 0 8px;
  color: var(--text-color-secondary);
  font-size: 13px;
  line-height: 1.6;
}

.credential-import-label {
  margin: 0 0 8px;
  color: var(--text-color-secondary);
  font-size: 13px;
}

.credential-file-input {
  width: 100%;
  padding: 9px 10px;
  border: 1px solid var(--border-color-global);
  border-radius: 8px;
  background: var(--bg-input);
  color: var(--text-primary);
  font-size: 13px;
}

.credential-file-summary {
  margin-top: 7px;
  color: #18a058;
  font-size: 13px;
}

.credential-import-divider {
  display: flex;
  align-items: center;
  gap: 10px;
  margin: 14px 0;
  color: var(--text-color-secondary);
  font-size: 12px;
}

.credential-import-divider::before,
.credential-import-divider::after {
  height: 1px;
  flex: 1;
  background: var(--border-color-global);
  content: '';
}

.credential-import-failures {
  display: grid;
  gap: 5px;
  margin-top: 12px;
  padding: 10px 12px;
  border: 1px solid rgba(208, 48, 80, 0.24);
  border-radius: 8px;
  background: rgba(208, 48, 80, 0.07);
  color: #d03050;
  font-size: 13px;
}

.reset-credit-count {
  margin: 0 0 12px;
}

.reset-credit-error {
  margin: 0;
  color: #d03050;
}

.reset-credit-list-title {
  margin: 0 0 8px;
  color: var(--text-color-secondary);
  font-size: 13px;
}

.reset-credit-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 8px;
  padding: 12px 16px;
  border: 1px solid var(--border-color-global);
  border-radius: 6px;
  background: var(--bg-page);
  font-size: 13px;
}

.reset-credit-item > div {
  display: grid;
  gap: 4px;
}

.reset-credit-item span {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.reset-credit-expiry {
  text-align: right;
}

.reset-credit-expiry strong {
  color: #c4612f;
}

.codex-stack {
  display: flex;
  flex-direction: column;
  gap: 16px;
  min-width: 0;
}

.inspection-card {
  min-width: 0;
}

.inspection-content {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  flex-wrap: wrap;
}

.inspection-main {
  display: flex;
  align-items: center;
  gap: 12px;
  min-width: 0;
}

.inspection-title {
  color: var(--text-primary);
  font-weight: 700;
}

.inspection-actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  flex-wrap: wrap;
}

.inspection-workspace {
  display: grid;
  gap: 16px;
  min-width: 0;
  padding-top: 8px;
}

.inspection-summary-grid {
  display: grid;
  grid-template-columns: repeat(5, minmax(0, 1fr));
  gap: 10px;
}

.inspection-summary-grid > div {
  display: grid;
  gap: 4px;
  padding: 12px;
  border-radius: 8px;
  background: var(--bg-page);
}

.inspection-summary-grid span,
.inspection-run-meta,
.inspection-log-row > span,
.inspection-reason {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.inspection-summary-grid strong {
  color: var(--text-primary);
  font-size: 18px;
}

.inspection-run-meta {
  margin: 12px 0;
}

.inspection-table-scroll {
  width: 100%;
  max-width: 100%;
  overflow-x: auto;
}

.inspection-table {
  min-width: 820px;
}

.inspection-table-head,
.inspection-table-row {
  display: grid;
  grid-template-columns: minmax(150px, 1fr) 80px 80px 72px 72px minmax(220px, 1.4fr);
  gap: 12px;
  align-items: center;
  padding: 11px 12px;
  border-bottom: 1px solid var(--border-color-global);
  font-size: 13px;
}

.inspection-table-head {
  color: var(--text-color-secondary);
  background: var(--bg-page);
  font-weight: 600;
}

.inspection-log-list {
  display: grid;
  max-height: 360px;
  overflow: auto;
}

.inspection-log-row {
  display: grid;
  grid-template-columns: 170px 90px minmax(0, 1fr);
  gap: 12px;
  align-items: center;
  padding: 10px 0;
  border-bottom: 1px solid var(--border-color-global);
  font-size: 13px;
}

.inspection-meta,
.account-subtitle,
.account-kpi-label,
.account-meta-grid span,
.quota-label,
.quota-empty {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.codex-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(340px, 1fr));
  gap: 18px;
  margin-top: 16px;
}

.codex-card {
  display: flex;
  position: relative;
  flex-direction: column;
  min-width: 0;
  padding: 20px;
  border: 1px solid #e7e1d7;
  border-radius: 12px;
  background: #fbf9f5;
  box-shadow: 0 2px 8px rgba(196, 97, 47, 0.06);
  transition: all 0.2s ease;
}

.codex-card:hover {
  border-color: #d4c5b4;
  box-shadow: 0 4px 16px rgba(196, 97, 47, 0.12);
}

.codex-card.disabled {
  opacity: 0.72;
}

.codex-card.selected {
  border-color: #6c9eff;
  box-shadow: 0 0 0 2px rgba(108, 158, 255, 0.16);
}

.codex-card-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 16px;
  padding-bottom: 12px;
  border-bottom: 1px solid #e7e1d7;
}

.codex-card-header-main {
  min-width: 0;
  flex: 1;
}

.codex-account-name {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  color: #1f2421;
  font-size: 15px;
  font-weight: 700;
  line-height: 1.45;
  word-break: break-all;
}

.codex-account-email {
  margin-top: 4px;
  color: #6c757d;
  font-size: 12px;
  word-break: break-all;
}

.codex-plan {
  flex-shrink: 0;
  padding: 3px 10px;
  border-radius: 12px;
  background: #f2e3d6;
  color: #c4612f;
  font-size: 11px;
  font-weight: 500;
}

.account-kpi-row {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 10px;
}

.account-kpi {
  min-width: 0;
  padding: 12px;
  border-radius: 14px;
  background: #f8fafc;
  border: 1px solid rgba(226, 232, 240, 0.95);
}

.account-kpi-value {
  display: block;
  margin-top: 4px;
  color: var(--text-primary);
  font-size: 20px;
  font-weight: 800;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.account-kpi-value.small {
  font-size: 12px;
}

.account-kpi-value.success { color: #18a058; }
.account-kpi-value.warning { color: #f0a020; }
.account-kpi-value.error { color: #d03050; }

.account-meta-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 8px;
}

.account-meta-grid div {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.account-meta-grid strong {
  overflow: hidden;
  color: var(--text-primary);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.codex-windows-container {
  min-height: 60px;
  margin: 16px 0;
}

.codex-window {
  display: grid;
  grid-template-columns: 92px minmax(0, 1fr) 56px;
  align-items: center;
  column-gap: 12px;
  row-gap: 6px;
  padding: 10px 0 12px;
  border-bottom: 1px solid #f3f4f6;
}

.codex-window:last-child {
  border-bottom: none;
}

.codex-window-label {
  min-width: 0;
  overflow: hidden;
  color: var(--text-color-secondary);
  font-size: 13px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.codex-window-percent {
  min-width: 56px;
  color: var(--text-primary);
  font-size: 13px;
  font-weight: 600;
  text-align: right;
}

.codex-window-reset {
  grid-column: 2 / 4;
  min-width: 0;
  overflow: hidden;
  color: var(--text-color-secondary);
  font-size: 11px;
  line-height: 1.5;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.codex-window-placeholder {
  padding: 20px;
  border: 1px dashed #e5e7eb;
  border-radius: 8px;
  background: #f9fafb;
  color: #9ca3af;
  font-size: 13px;
  text-align: center;
}

.account-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: auto;
}

[data-theme='dark'] .account-kpi {
  background: rgba(255, 255, 255, 0.05);
}

@media (max-width: 720px) {
  .codex-grid,
  .account-kpi-row,
  .account-meta-grid,
  .codex-window,
  .inspection-summary-grid,
  .inspection-log-row {
    grid-template-columns: 1fr;
  }

  .codex-window-reset {
    grid-column: 1;
  }

  .codex-card-header,
  .inspection-content,
  .reset-credit-item {
    align-items: stretch;
    flex-direction: column;
  }

  .codex-model-row {
    grid-template-columns: 30px minmax(0, 1fr) auto;
  }

  .codex-model-row :deep(.n-input) {
    grid-column: 2 / -1;
  }

  .reset-credit-expiry {
    text-align: left;
  }
}
</style>

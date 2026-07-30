<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { NCard, NButton, NSpace, NTag, NEmpty, NSpin, NModal, NInput, NPopconfirm, NProgress, NCheckbox, useMessage } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import * as api from '@/api/codex'
import type { CodexAccount, CodexInspectionStatus } from '@/api/codex'

const message = useMessage()
const loading = ref(false)
const accounts = ref<CodexAccount[]>([])
const inspection = ref<CodexInspectionStatus | null>(null)
// 功能未开启时的提示态
const featureDisabled = ref(false)

// OAuth 弹窗
const oauthModal = ref(false)
const oauthUrl = ref('')
const oauthCallbackInput = ref('')
const oauthDisplayName = ref('')
const oauthLoading = ref(false)

// 凭证导入弹窗
const importModal = ref(false)
const importJsonText = ref('')
const importLoading = ref(false)

// 编辑账号（重命名）弹窗
const editModal = ref(false)
const editAccount = ref<CodexAccount | null>(null)
const editDisplayName = ref('')
const editLoading = ref(false)

// 重置额度信用弹窗
const resetCreditModal = ref(false)
const resetCreditAccount = ref<CodexAccount | null>(null)
const resetCreditInfo = ref<{ availableCount: number; items: Array<{ count: number; expiresAt: string }> } | null>(null)
const resetCreditLoading = ref(false)

// 拉取/导入模型弹窗
const modelModal = ref(false)
const modelAccount = ref<CodexAccount | null>(null)
const modelList = ref<Array<{ id: string; name: string }>>([])
const checkedModels = ref<string[]>([])
const modelLoading = ref(false)

let pollTimer: ReturnType<typeof setInterval> | null = null

async function load(): Promise<void> {
  loading.value = true
  featureDisabled.value = false
  try {
    const [accs, insp] = await Promise.all([
      api.listCodexAccounts(),
      api.getCodexInspectionStatus().catch(() => null)
    ])
    accounts.value = accs
    inspection.value = insp
  } catch (e) {
    // Codex 功能未开启时后端返回 404，显示提示而非空白
    if ((e as { status?: number }).status === 404) {
      featureDisabled.value = true
    } else {
      message.error((e as Error).message)
    }
  } finally { loading.value = false }
}

async function handleStartOAuth(): Promise<void> {
  try {
    const result = await api.startCodexOAuth()
    oauthUrl.value = result.url
    oauthCallbackInput.value = ''
    oauthDisplayName.value = ''
    oauthModal.value = true
  } catch (e) { message.error((e as Error).message) }
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
async function handleDelete(acc: CodexAccount): Promise<void> {
  await api.deleteCodexAccount(acc.id)
  message.success('已删除账号')
  await load()
}
async function handleRunInspection(): Promise<void> {
  await api.runCodexInspection()
  message.success('已触发巡检')
  setTimeout(load, 2000)
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
    resetCreditInfo.value = await api.getResetCredits(acc.id)
  } catch (e) { message.error((e as Error).message) } finally { resetCreditLoading.value = false }
}
async function handleConsumeResetCredit(): Promise<void> {
  if (!resetCreditAccount.value) return
  try {
    await api.consumeResetCredit(resetCreditAccount.value.id)
    message.success('已消耗一次重置信用')
    resetCreditInfo.value = await api.getResetCredits(resetCreditAccount.value.id)
    await load()
  } catch (e) { message.error((e as Error).message) }
}

// 拉取/导入模型
async function openFetchModels(acc: CodexAccount): Promise<void> {
  modelAccount.value = acc
  modelList.value = []
  checkedModels.value = []
  modelModal.value = true
  modelLoading.value = true
  try {
    modelList.value = await api.fetchCodexModels(acc.id)
  } catch (e) { message.error((e as Error).message) } finally { modelLoading.value = false }
}
async function handleImportModels(): Promise<void> {
  if (!modelAccount.value || checkedModels.value.length === 0) { message.warning('请选择要导入的模型'); return }
  modelLoading.value = true
  try {
    await api.importSelectedCodexModels(modelAccount.value.id, checkedModels.value)
    message.success(`已导入 ${checkedModels.value.length} 个模型`)
    modelModal.value = false
  } catch (e) { message.error((e as Error).message) } finally { modelLoading.value = false }
}

async function handleImportCredential(): Promise<void> {
  if (!importJsonText.value.trim()) { message.warning('请粘贴凭证 JSON'); return }
  importLoading.value = true
  try {
    await api.importCredential(importJsonText.value.trim())
    message.success('凭证导入成功')
    importModal.value = false
    importJsonText.value = ''
    await load()
  } catch (e) { message.error((e as Error).message) } finally { importLoading.value = false }
}

async function handleExportCredentials(): Promise<void> {
  const ids = accounts.value.map((a) => a.id)
  if (ids.length === 0) { message.warning('没有可导出的账号'); return }
  try {
    await api.exportCredentials(ids)
    message.success('凭证已导出')
  } catch (e) { message.error((e as Error).message) }
}

function formatDateTime(value: string | null | undefined): string {
  if (!value) return '从未'
  return new Date(value).toLocaleString('zh-CN')
}

function formatQuotaPercent(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(Number(value))) return '-'
  return `${Math.round(Number(value))}%`
}

function accountQuotaPercent(acc: CodexAccount): number | null {
  if (acc.windows && acc.windows.length > 0) {
    return Math.max(...acc.windows.map((w) => Number(w.usedPercent || 0)))
  }
  const percents = [acc.fiveHourUsedPercent, acc.weeklyUsedPercent]
    .filter((value): value is number => value != null && Number.isFinite(Number(value)))
  return percents.length ? Math.max(...percents) : null
}

function accountStatusLabel(acc: CodexAccount): string {
  if (acc.isQuotaCooling) return '冷却中'
  return acc.isEnabled ? '启用' : '禁用'
}

function accountStatusType(acc: CodexAccount): 'success' | 'warning' | 'default' {
  if (acc.isQuotaCooling) return 'warning'
  return acc.isEnabled ? 'success' : 'default'
}

// 额度进度条颜色
function quotaColor(percent: number | null | undefined): 'success' | 'warning' | 'error' {
  if (percent == null) return 'success'
  if (percent >= 95) return 'error'
  if (percent >= 80) return 'warning'
  return 'success'
}

onMounted(() => {
  load()
  pollTimer = setInterval(() => {
    if (document.visibilityState === 'visible') load()
  }, 10000)
})
onUnmounted(() => { if (pollTimer) clearInterval(pollTimer) })
</script>

<template>
  <div class="page-container">
    <PageHeader title="OAuth 管理" subtitle="管理 Codex OAuth 登录账号、凭证导入、额度、巡检与自动禁用">
      <template #actions>
        <NTag v-if="accounts.length" round :bordered="false" size="small">{{ accounts.length }} 个</NTag>
        <NButton v-if="inspection" size="small" @click="handleRunInspection">触发巡检</NButton>
        <NButton size="small" quaternary @click="importModal = true">导入凭证</NButton>
        <NButton size="small" quaternary :disabled="accounts.length === 0" @click="handleExportCredentials">导出凭证</NButton>
        <NButton size="small" type="primary" @click="handleStartOAuth">OAuth 登录</NButton>
      </template>
    </PageHeader>
    <NSpin :show="loading">
      <div class="codex-stack">
        <NCard v-if="inspection" class="inspection-card" size="small">
          <div class="inspection-content">
            <div class="inspection-main">
              <NTag :type="inspection.isRunning ? 'warning' : 'success'" :bordered="false">
                {{ inspection.isRunning ? '巡检中' : '空闲' }}
              </NTag>
              <div>
                <div class="inspection-title">Codex 巡检状态</div>
                <div v-if="inspection.lastRun" class="inspection-meta">
                  上次：{{ inspection.lastRun.totalAccounts }} 账号，禁用 {{ inspection.lastRun.disabledAccounts }} 个 · 完成于 {{ formatDateTime(inspection.lastRun.finishedAt) }}
                </div>
                <div v-else class="inspection-meta">尚未执行过自动巡检</div>
              </div>
            </div>
            <div v-if="inspection.nextScheduledAt" class="inspection-meta">下次：{{ formatDateTime(inspection.nextScheduledAt) }}</div>
          </div>
        </NCard>

        <NEmpty v-if="featureDisabled" description="Codex 功能未开启，请在系统设置中开启" />
        <NEmpty v-else-if="accounts.length === 0" description="暂无 Codex 账号，点击右上角 OAuth 登录" />

        <div v-else class="codex-account-grid">
          <article v-for="acc in accounts" :key="acc.id" class="codex-account-card" :class="{ disabled: !acc.isEnabled }">
            <div class="account-card-header">
              <div class="account-title-block">
                <div class="account-avatar">{{ acc.displayName.slice(0, 1).toUpperCase() }}</div>
                <div class="account-title-text">
                  <div class="account-name-row">
                    <h3 class="account-name">{{ acc.displayName }}</h3>
                    <NTag size="small" :type="accountStatusType(acc)" :bordered="false">{{ accountStatusLabel(acc) }}</NTag>
                  </div>
                  <div class="account-subtitle">{{ acc.email || acc.accountId || '未记录账号标识' }}</div>
                </div>
              </div>
              <NTag v-if="acc.planType" size="small" type="info" :bordered="false">{{ acc.planType }}</NTag>
            </div>

            <div class="account-kpi-row">
              <div class="account-kpi">
                <span class="account-kpi-label">最高额度</span>
                <strong :class="['account-kpi-value', quotaColor(accountQuotaPercent(acc))]">{{ formatQuotaPercent(accountQuotaPercent(acc)) }}</strong>
              </div>
              <div class="account-kpi">
                <span class="account-kpi-label">重置信用</span>
                <strong class="account-kpi-value">{{ acc.resetCreditsAvailableCount ?? 0 }}</strong>
              </div>
              <div class="account-kpi">
                <span class="account-kpi-label">Token 过期</span>
                <strong class="account-kpi-value small">{{ formatDateTime(acc.tokenExpiresAt) }}</strong>
              </div>
            </div>

            <div class="account-meta-grid">
              <div><span>上次额度检查</span><strong>{{ formatDateTime(acc.lastQuotaCheckedAt) }}</strong></div>
              <div><span>自动禁用阈值</span><strong>{{ formatQuotaPercent(acc.autoDisableThreshold) }}</strong></div>
              <div v-if="acc.quotaCoolingUntil"><span>冷却至</span><strong>{{ formatDateTime(acc.quotaCoolingUntil) }}</strong></div>
            </div>

            <div v-if="acc.windows && acc.windows.length > 0" class="quota-windows">
              <div v-for="w in acc.windows" :key="w.id" class="quota-window">
                <div class="quota-label">
                  <span>{{ w.label }}</span>
                  <span v-if="w.resetLabel">重置于 {{ w.resetLabel }}</span>
                </div>
                <NProgress
                  :percentage="Math.round(w.usedPercent)"
                  :status="quotaColor(w.usedPercent)"
                  :show-indicator="false"
                  :height="8"
                  :border-radius="4"
                />
                <span class="quota-percent">{{ Math.round(w.usedPercent) }}%</span>
              </div>
            </div>
            <div v-else class="quota-empty">暂无额度窗口数据，刷新额度后显示。</div>

            <div class="account-actions">
              <NButton size="small" secondary @click="handleRefreshQuota(acc)">刷新额度</NButton>
              <NButton size="small" secondary @click="handleRefreshToken(acc)">刷新 Token</NButton>
              <NButton size="small" secondary @click="openEdit(acc)">编辑</NButton>
              <NButton size="small" secondary @click="openFetchModels(acc)">拉取模型</NButton>
              <NButton v-if="acc.resetCreditsAvailableCount != null && acc.resetCreditsAvailableCount > 0" size="small" secondary @click="openResetCredit(acc)">重置额度</NButton>
              <NButton size="small" secondary :type="acc.isEnabled ? 'warning' : 'success'" @click="handleToggle(acc)">{{ acc.isEnabled ? '禁用' : '启用' }}</NButton>
              <NPopconfirm @positive-click="handleDelete(acc)">
                <template #trigger><NButton size="small" secondary type="error">删除</NButton></template>
                删除账号「{{ acc.displayName }}」？关联站点和路由会一并清理。
              </NPopconfirm>
            </div>
          </article>
        </div>
      </div>
    </NSpin>

    <!-- OAuth 弹窗 -->
    <NModal v-model:show="oauthModal" title="Codex OAuth 登录" preset="card" style="width: 600px; max-width: 92vw" :mask-closable="false">
      <div style="margin-bottom: 12px">
        <p style="margin: 0 0 8px; font-weight: 600">第 1 步：打开授权链接</p>
        <NInput :value="oauthUrl" readonly type="textarea" :autosize="{ minRows: 2 }" />
        <NButton size="small" style="margin-top: 8px" tag="a" :href="oauthUrl" target="_blank">在新标签打开</NButton>
      </div>
      <div>
        <p style="margin: 0 0 8px; font-weight: 600">第 2 步：完成授权后，粘贴回调后的完整 URL</p>
        <NInput v-model:value="oauthCallbackInput" placeholder="https://chatgpt.com/auth/callback?code=..." type="textarea" :autosize="{ minRows: 2 }" />
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
    <NModal v-model:show="importModal" title="导入 Codex 凭证" preset="card" style="width: 600px; max-width: 92vw" :mask-closable="false">
      <p style="margin: 0 0 8px; color: var(--text-color-secondary)">粘贴 CPA 格式的凭证 JSON（含 access_token / refresh_token / id_token）：</p>
      <NInput
        v-model:value="importJsonText"
        type="textarea"
        :autosize="{ minRows: 8, maxRows: 20 }"
        placeholder='{"access_token":"...","refresh_token":"...","id_token":"..."}'
        style="font-family: monospace"
      />
      <template #footer>
        <NSpace justify="end">
          <NButton @click="importModal = false">取消</NButton>
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
          <p style="margin: 0 0 12px">可用重置次数：<strong>{{ resetCreditInfo.availableCount }}</strong></p>
          <div v-if="resetCreditInfo.items.length > 0">
            <p style="margin: 0 0 8px; color: var(--text-color-secondary); font-size: 13px">信用明细：</p>
            <div v-for="(item, idx) in resetCreditInfo.items" :key="idx" style="font-size: 13px; margin-bottom: 4px">
              {{ item.count }} 次 · 过期 {{ new Date(item.expiresAt).toLocaleString('zh-CN') }}
            </div>
          </div>
        </div>
      </NSpin>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="resetCreditModal = false">关闭</NButton>
          <NButton
            v-if="resetCreditInfo && resetCreditInfo.availableCount > 0"
            type="primary"
            @click="handleConsumeResetCredit"
          >消耗一次重置</NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- 拉取/导入模型弹窗 -->
    <NModal v-model:show="modelModal" :title="`拉取模型 - ${modelAccount?.displayName ?? ''}`" preset="card" style="width: 560px; max-width: 92vw">
      <NSpin :show="modelLoading">
        <NEmpty v-if="!modelLoading && modelList.length === 0" description="该账号无可用模型" size="small" />
        <NSpace v-else vertical :size="6">
          <NCheckbox
            v-for="m in modelList"
            :key="m.id"
            :checked="checkedModels.includes(m.id)"
            @update:checked="(v: boolean) => v ? checkedModels.push(m.id) : (checkedModels = checkedModels.filter(x => x !== m.id))"
          >
            {{ m.name }}
          </NCheckbox>
        </NSpace>
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
.codex-stack {
  display: flex;
  flex-direction: column;
  gap: 16px;
  min-width: 0;
}

.inspection-card,
.codex-account-card {
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

.inspection-meta,
.account-subtitle,
.account-kpi-label,
.account-meta-grid span,
.quota-label,
.quota-empty {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.codex-account-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 16px;
}

.codex-account-card {
  display: flex;
  flex-direction: column;
  gap: 16px;
  padding: 18px;
  border: 1px solid rgba(226, 232, 240, 0.92);
  border-radius: 20px;
  background: var(--bg-card);
  box-shadow: 0 12px 32px rgba(15, 23, 42, 0.06);
}

.codex-account-card.disabled {
  opacity: 0.72;
}

.account-card-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  min-width: 0;
}

.account-title-block {
  display: flex;
  align-items: center;
  gap: 12px;
  min-width: 0;
}

.account-avatar {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 44px;
  height: 44px;
  flex: 0 0 auto;
  border-radius: 14px;
  background: linear-gradient(135deg, #6C9EFF 0%, #A5B4FC 100%);
  color: #fff;
  font-size: 18px;
  font-weight: 800;
  box-shadow: 0 8px 20px rgba(108, 158, 255, 0.28);
}

.account-title-text {
  min-width: 0;
}

.account-name-row {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.account-name {
  margin: 0;
  color: var(--text-primary);
  font-size: 17px;
  font-weight: 800;
  word-break: break-word;
}

.account-subtitle {
  margin-top: 3px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.account-kpi-row {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
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

.quota-windows {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.quota-window {
  display: grid;
  grid-template-columns: 1fr auto;
  align-items: center;
  gap: 4px 8px;
}

.quota-window .n-progress {
  grid-column: 1 / 2;
}

.quota-label {
  grid-column: 1 / 3;
  display: flex;
  justify-content: space-between;
  gap: 8px;
}

.quota-percent {
  grid-column: 2 / 3;
  min-width: 40px;
  font-size: 12px;
  font-weight: 700;
  text-align: right;
}

.quota-empty {
  padding: 12px;
  border: 1px dashed var(--border-color-global);
  border-radius: 12px;
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

@media (max-width: 1280px) {
  .codex-account-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 720px) {
  .codex-account-grid,
  .account-kpi-row,
  .account-meta-grid {
    grid-template-columns: 1fr;
  }

  .account-card-header,
  .inspection-content {
    align-items: stretch;
    flex-direction: column;
  }
}
</style>

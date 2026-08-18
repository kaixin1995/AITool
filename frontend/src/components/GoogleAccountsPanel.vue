<template>
  <div class="oauth-stack">
    <div class="google-toolbar">
      <NButton type="primary" @click="openLogin('GeminiCli')">＋ GeminiCLI 登录</NButton>
      <NButton secondary type="primary" @click="openLogin('Antigravity')">＋ Antigravity 登录</NButton>
      <NButton secondary @click="importVisible = true">导入凭证</NButton>
      <NButton quaternary @click="loadAccounts">刷新列表</NButton>
    </div>

    <NEmpty v-if="!loading && accounts.length === 0" description="暂无 Google 账号，可使用上方按钮登录或导入 gcli2api 凭证" />
    <div v-else class="oauth-grid">
      <article
        v-for="acc in accounts"
        :key="acc.id"
        class="oauth-card"
        :class="{ disabled: !acc.isEnabled }"
      >
        <div class="oauth-card-header">
          <div class="oauth-card-header-main">
            <div class="oauth-account-name">{{ acc.displayName }}</div>
            <div class="oauth-account-email">{{ acc.email || '' }}</div>
            <div class="oauth-badges">
              <NTag size="small" :type="acc.isEnabled ? 'success' : 'default'" :bordered="false">{{ acc.isEnabled ? '已启用' : '已禁用' }}</NTag>
              <NTag size="small" :type="kindTagType(acc.accountKind)" :bordered="false">{{ kindLabel(acc.accountKind) }}</NTag>
              <span v-if="acc.subscriptionTier" class="oauth-plan">{{ tierLabel(acc.subscriptionTier) }}</span>
              <span v-if="acc.creditAmount != null" class="oauth-plan">积分 {{ acc.creditAmount }}</span>
              <span v-if="acc.tokenExpiresAt" class="oauth-token-expiry" :class="{ 'oauth-token-expired': isTokenExpired(acc.tokenExpiresAt), 'oauth-token-warning': isTokenExpiringSoon(acc.tokenExpiresAt) }">
                Token：{{ formatDateTime(acc.tokenExpiresAt) }}
              </span>
            </div>
            <div v-if="acc.projectId" class="google-project">项目：{{ acc.projectId }}</div>
          </div>
        </div>

        <div v-if="acc.windows && acc.windows.length > 0" class="oauth-windows-container google-windows">
          <div v-for="w in acc.windows" :key="w.id" class="oauth-window">
            <div class="oauth-window-label" :title="w.label">{{ w.label }}</div>
            <NProgress
              :percentage="Math.max(0, 100 - Math.round(Number(w.usedPercent ?? 0)))"
              :status="quotaColor(100 - Number(w.usedPercent ?? 0))"
              :show-indicator="false"
              :height="6"
              :border-radius="3"
            />
            <span class="oauth-window-percent">{{ Math.max(0, 100 - Math.round(Number(w.usedPercent ?? 0))) }}%</span>
            <div v-if="w.resetLabel && w.resetLabel !== 'N/A'" class="oauth-window-reset">重置于 {{ w.resetLabel }}</div>
          </div>
        </div>
        <div v-else class="oauth-window-placeholder">
          {{ acc.lastQuotaCheckedAt ? '暂无额度窗口数据' : '未刷新额度，点击下方「刷新额度」获取' }}
        </div>

        <div class="oauth-card-meta">
          <div class="oauth-source-meta">
            <div v-if="acc.lastQuotaCheckedAt">刷新时间：{{ formatDateTime(acc.lastQuotaCheckedAt) }}</div>
          </div>
          <div class="oauth-card-actions">
            <NButton class="oauth-icon-button" circle secondary title="刷新额度" aria-label="刷新额度" @click="handleRefreshQuota(acc)">
              <svg viewBox="0 0 24 24" aria-hidden="true"><polyline points="23 4 23 10 17 10" /><path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10" /></svg>
            </NButton>
            <NButton class="oauth-icon-button" circle secondary :title="acc.isEnabled ? '禁用账号' : '启用账号'" :aria-label="acc.isEnabled ? '禁用账号' : '启用账号'" @click="handleToggle(acc)">
              <svg v-if="acc.isEnabled" viewBox="0 0 24 24" aria-hidden="true"><line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" /></svg>
              <svg v-else viewBox="0 0 24 24" aria-hidden="true"><polyline points="20 6 9 17 4 12" /></svg>
            </NButton>
            <NButton class="oauth-icon-button primary" circle secondary title="编辑账号" aria-label="编辑账号" @click="openEdit(acc)">
              <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" /><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" /></svg>
            </NButton>
            <NButton class="oauth-icon-button info" circle secondary title="拉取模型" aria-label="拉取模型" @click="openFetchModels(acc)">
              <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" /><polyline points="7 10 12 15 17 10" /><line x1="12" y1="15" x2="12" y2="3" /></svg>
            </NButton>
            <NPopconfirm @positive-click="handleDelete(acc)">
              <template #trigger>
                <NButton class="oauth-icon-button danger" circle secondary title="删除账号" aria-label="删除账号">
                  <svg viewBox="0 0 24 24" aria-hidden="true"><polyline points="3 6 5 6 21 6" /><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" /></svg>
                </NButton>
              </template>
              删除账号「{{ acc.displayName }}」？关联站点和路由会一并清理。
            </NPopconfirm>
          </div>
        </div>
      </article>
    </div>

    <!-- OAuth 登录：打开授权页 + 粘贴回调 URL -->
    <NModal
      v-model:show="loginVisible"
      preset="card"
      :title="`登录 ${kindLabel(loginKind)}`"
      style="max-width: 640px"
    >
      <NSpace vertical>
        <NAlert v-if="loginUrl" type="info" :show-icon="false">
          <div class="google-login-steps">
            <div>1. 点击 <a :href="loginUrl" target="_blank" rel="noopener">打开 Google 授权页</a> 并完成登录授权。</div>
            <div>2. 授权后浏览器会跳转到 <code>http://localhost:17891/?code=...</code>（页面显示无法访问是正常现象）。</div>
            <div>3. 复制浏览器地址栏的完整 URL，粘贴到下方输入框完成登录。</div>
          </div>
        </NAlert>
        <NInput v-model:value="loginCallbackUrl" placeholder="粘贴回调 URL（http://localhost:17891/?code=...&state=...）" />
        <NInput v-model:value="loginDisplayName" placeholder="账号显示名（留空自动使用邮箱）" />
      </NSpace>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="loginVisible = false">取消</NButton>
          <NButton type="primary" :loading="loginSubmitting" :disabled="!loginCallbackUrl.trim()" @click="handleCompleteLogin">完成登录</NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- 导入凭证 -->
    <NModal v-model:show="importVisible" preset="card" title="导入 Google 凭证" style="max-width: 640px">
      <NSpace vertical>
        <NRadioGroup v-model:value="importKind">
          <NSpace>
            <NRadio value="GeminiCli">GeminiCLI</NRadio>
            <NRadio value="Antigravity">Antigravity</NRadio>
          </NSpace>
        </NRadioGroup>
        <NInput
          v-model:value="importJson"
          type="textarea"
          :rows="8"
          placeholder='粘贴 gcli2api 凭证 JSON（需包含 refresh_token 字段），例如：{"refresh_token": "...", "project_id": "..."}'
        />
      </NSpace>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="importVisible = false">取消</NButton>
          <NButton type="primary" :loading="importing" :disabled="!importJson.trim()" @click="handleImport">导入</NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- 编辑账号 -->
    <NModal v-model:show="editVisible" preset="card" title="编辑 Google 账号" style="max-width: 520px">
      <NSpace vertical>
        <NInput v-model:value="editDisplayName" placeholder="账号显示名" />
        <NInput v-model:value="editRefreshToken" type="textarea" :rows="3" placeholder="替换 refresh_token（可选，留空不修改）" />
      </NSpace>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="editVisible = false">取消</NButton>
          <NButton type="primary" :loading="editSubmitting" @click="handleEditSave">保存</NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- 拉取模型 -->
    <NModal v-model:show="modelsVisible" preset="card" title="拉取模型" style="max-width: 720px">
      <NSpace vertical>
        <NInput v-model:value="modelSearch" placeholder="搜索模型名" size="small" clearable />
        <div class="google-model-list">
          <div v-for="m in filteredModels" :key="m.remoteModelName" class="google-model-row">
            <NCheckbox
              :checked="selectionMap.get(m.remoteModelName)?.selected ?? false"
              @update:checked="(v: boolean) => updateModelSelected(m.remoteModelName, v)"
            >
              <code>{{ m.remoteModelName }}</code>
              <NTag v-if="m.existingMappingId" size="small" type="success" :bordered="false" style="margin-left: 8px">已导入</NTag>
            </NCheckbox>
          </div>
          <NEmpty v-if="filteredModels.length === 0 && !modelsLoading" description="无可用模型" />
        </div>
      </NSpace>
      <template #footer>
        <NSpace justify="end">
          <span class="google-model-count">已选 {{ selectedModelCount }} / {{ models.length }}</span>
          <NButton size="small" secondary @click="toggleAllModels(false)">全不选</NButton>
          <NButton size="small" secondary @click="toggleAllModels(true)">全选</NButton>
          <NButton type="primary" :loading="modelsImporting" :disabled="selectedModelCount === 0" @click="handleImportModels">导入选中</NButton>
        </NSpace>
      </template>
    </NModal>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import {
  NAlert, NButton, NCheckbox, NEmpty, NInput, NModal, NPopconfirm, NProgress,
  NRadio, NRadioGroup, NSpace, NTag, useMessage
} from 'naive-ui'
import {
  completeGoogleOAuth, deleteGoogleAccount, fetchGoogleModels, importGoogleCredential,
  importSelectedGoogleModels, listGoogleAccounts, refreshGoogleQuota, startGoogleOAuth,
  toggleGoogleAccount, updateGoogleAccount
} from '@/api/oauth'
import type { GoogleAccountKind, GoogleAccountSummary, OAuthRemoteModelItem } from '@/api/oauth'

const message = useMessage()
const loading = ref(false)
const accounts = ref<GoogleAccountSummary[]>([])

const loginVisible = ref(false)
const loginKind = ref<GoogleAccountKind>('GeminiCli')
const loginUrl = ref('')
const loginCallbackUrl = ref('')
const loginDisplayName = ref('')
const loginSubmitting = ref(false)

const importVisible = ref(false)
const importKind = ref<GoogleAccountKind>('GeminiCli')
const importJson = ref('')
const importing = ref(false)

const editVisible = ref(false)
const editId = ref('')
const editDisplayName = ref('')
const editRefreshToken = ref('')
const editSubmitting = ref(false)

const modelsVisible = ref(false)
const modelsLoading = ref(false)
const modelsImporting = ref(false)
const models = ref<OAuthRemoteModelItem[]>([])
const modelSearch = ref('')
const selectionMap = reactive(new Map<string, { selected: boolean }>())

const filteredModels = computed(() => {
  const keyword = modelSearch.value.trim().toLowerCase()
  if (!keyword) return models.value
  return models.value.filter(m => m.remoteModelName.toLowerCase().includes(keyword))
})
const selectedModelCount = computed(
  () => Array.from(selectionMap.values()).filter(s => s.selected).length
)

function kindLabel(kind: string): string {
  return kind === 'Antigravity' ? 'Antigravity' : 'GeminiCLI'
}
function kindTagType(kind: string): 'success' | 'warning' {
  return kind === 'Antigravity' ? 'warning' : 'success'
}
function tierLabel(tier: string): string {
  return tier.charAt(0).toUpperCase() + tier.slice(1)
}
function quotaColor(remaining: number): 'success' | 'warning' | 'error' {
  if (remaining > 30) return 'success'
  if (remaining > 10) return 'warning'
  return 'error'
}
function isTokenExpired(expiresAt: string): boolean {
  return new Date(expiresAt).getTime() <= Date.now()
}
function isTokenExpiringSoon(expiresAt: string): boolean {
  const diff = new Date(expiresAt).getTime() - Date.now()
  return diff > 0 && diff < 10 * 60 * 1000
}
function formatDateTime(value: string): string {
  return new Date(value).toLocaleString()
}

async function loadAccounts(): Promise<void> {
  loading.value = true
  try {
    accounts.value = await listGoogleAccounts()
  } catch (e) {
    message.error(`加载 Google 账号失败：${(e as Error).message}`)
  } finally {
    loading.value = false
  }
}

async function openLogin(kind: GoogleAccountKind): Promise<void> {
  loginKind.value = kind
  loginCallbackUrl.value = ''
  loginDisplayName.value = ''
  try {
    const result = await startGoogleOAuth(kind)
    loginUrl.value = result.url
    loginVisible.value = true
  } catch (e) {
    message.error(`创建授权链接失败：${(e as Error).message}`)
  }
}

async function handleCompleteLogin(): Promise<void> {
  loginSubmitting.value = true
  try {
    await completeGoogleOAuth(loginKind.value, loginCallbackUrl.value.trim(), loginDisplayName.value.trim() || undefined)
    message.success('登录成功，账号已创建')
    loginVisible.value = false
    await loadAccounts()
  } catch (e) {
    message.error(`登录失败：${(e as Error).message}`)
  } finally {
    loginSubmitting.value = false
  }
}

async function handleImport(): Promise<void> {
  importing.value = true
  try {
    const result = await importGoogleCredential(importKind.value, importJson.value)
    if (result.failures.length > 0) {
      message.warning(`导入完成：成功 ${result.successes.length} 个，失败 ${result.failures.length} 个（${result.failures[0].error}）`)
    } else {
      message.success(`导入成功：${result.successes.length} 个账号`)
    }
    importVisible.value = false
    importJson.value = ''
    await loadAccounts()
  } catch (e) {
    message.error(`导入失败：${(e as Error).message}`)
  } finally {
    importing.value = false
  }
}

async function handleRefreshQuota(acc: GoogleAccountSummary): Promise<void> {
  try {
    await refreshGoogleQuota(acc.id)
    message.success('额度已刷新')
    await loadAccounts()
  } catch (e) {
    message.error(`刷新额度失败：${(e as Error).message}`)
  }
}

async function handleToggle(acc: GoogleAccountSummary): Promise<void> {
  try {
    await toggleGoogleAccount(acc.id, !acc.isEnabled)
    await loadAccounts()
  } catch (e) {
    message.error(`操作失败：${(e as Error).message}`)
  }
}

async function handleDelete(acc: GoogleAccountSummary): Promise<void> {
  try {
    await deleteGoogleAccount(acc.id)
    message.success('账号已删除')
    await loadAccounts()
  } catch (e) {
    message.error(`删除失败：${(e as Error).message}`)
  }
}

function openEdit(acc: GoogleAccountSummary): void {
  editId.value = acc.id
  editDisplayName.value = acc.displayName
  editRefreshToken.value = ''
  editVisible.value = true
}

async function handleEditSave(): Promise<void> {
  editSubmitting.value = true
  try {
    await updateGoogleAccount(editId.value, editDisplayName.value.trim() || 'Google 账号', editRefreshToken.value.trim() || undefined)
    message.success('已保存')
    editVisible.value = false
    await loadAccounts()
  } catch (e) {
    message.error(`保存失败：${(e as Error).message}`)
  } finally {
    editSubmitting.value = false
  }
}

async function openFetchModels(acc: GoogleAccountSummary): Promise<void> {
  editIdForModels.value = acc.id
  modelsVisible.value = true
  modelsLoading.value = true
  models.value = []
  modelSearch.value = ''
  selectionMap.clear()
  try {
    const result = await fetchGoogleModels(acc.id)
    models.value = result
    for (const m of result) {
      selectionMap.set(m.remoteModelName, { selected: false })
    }
  } catch (e) {
    message.error(`拉取模型失败：${(e as Error).message}`)
    modelsVisible.value = false
  } finally {
    modelsLoading.value = false
  }
}

function updateModelSelected(name: string, selected: boolean): void {
  const item = selectionMap.get(name)
  if (item) item.selected = selected
}

function toggleAllModels(selected: boolean): void {
  for (const m of filteredModels.value) {
    const item = selectionMap.get(m.remoteModelName)
    if (item) item.selected = selected
  }
}

async function handleImportModels(): Promise<void> {
  modelsImporting.value = true
  try {
    const selected = models.value
      .filter(m => selectionMap.get(m.remoteModelName)?.selected)
      .map(m => ({ remoteModelName: m.remoteModelName, displayName: m.displayName }))
    await importSelectedGoogleModels(editIdForModels.value, selected)
    message.success(`已导入 ${selected.length} 个模型`)
    modelsVisible.value = false
  } catch (e) {
    message.error(`导入模型失败：${(e as Error).message}`)
  } finally {
    modelsImporting.value = false
  }
}

const editIdForModels = ref('')

onMounted(loadAccounts)
</script>

<style scoped>
.google-toolbar {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 12px;
}
.google-login-steps {
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: 13px;
}
.google-project {
  margin-top: 4px;
  font-size: 12px;
  color: var(--n-text-color-3, #888);
  font-family: monospace;
}
.google-windows {
  max-height: 180px;
  overflow-y: auto;
}
.google-model-list {
  max-height: 360px;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.google-model-row code {
  font-size: 12px;
}
.google-model-count {
  align-self: center;
  font-size: 12px;
  color: var(--n-text-color-3, #888);
}
</style>

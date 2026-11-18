<script setup lang="ts">
import { computed, h, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { NCard, NButton, NSpace, NDataTable, NTag, NModal, NForm, NFormItem, NInput, NInputNumber, NSelect, NPopconfirm, NAlert, NCheckbox, NCheckboxGroup, NEmpty, useMessage, type DataTableColumns } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import * as api from '@/api/accessKeys'
import * as routesApi from '@/api/routes'
import type { AccessKeyItem } from '@/api/accessKeys'
import { httpGet, httpPost } from '@/api/http'

type BudgetPeriod = 'day' | 'week' | 'month'
interface KeyBudget { keyId: string; period: BudgetPeriod; tokenLimit: number; costLimitUsd: number }
interface BudgetUsage { keyId: string; period: BudgetPeriod; usedTokens: number; usedCostUsd: number; hasUnpricedUsage: boolean }
interface BudgetConfiguration { revision: string; budgets: KeyBudget[]; [key: string]: unknown }

const message = useMessage()
const loading = ref(false)
const items = ref<AccessKeyItem[]>([])
const showModal = ref(false)
const newKeyName = ref('')
const newKeyPlain = ref('')
const newKeyRoutes = ref<string[]>([])
const saving = ref(false)
const copyingKeyId = ref<string | null>(null)
const deletingKeyId = ref<string | null>(null)
const budgetVisible = ref(false)
const budgetKey = ref<AccessKeyItem | null>(null)
const budgets = ref<KeyBudget[]>([])
const budgetUsage = ref<BudgetUsage[]>([])
const budgetLoading = ref(false)
const budgetSaving = ref(false)
const budgetReady = ref(false)
const budgetError = ref('')
let originalBudgets = ''
let budgetRequestSequence = 0
const periodOptions = [{ label: '每天', value: 'day' }, { label: '每周（周一重置）', value: 'week' }, { label: '每月', value: 'month' }]

function budgetSignature(rows: KeyBudget[]): string {
  return JSON.stringify(rows.map(b => ({ period: b.period, tokenLimit: b.tokenLimit, costLimitUsd: b.costLimitUsd })).sort((a, b) => a.period.localeCompare(b.period)))
}

async function loadBudgets(): Promise<void> {
  if (!budgetKey.value || !budgetVisible.value || budgetSaving.value) return
  const keyId = budgetKey.value.id
  const sequence = ++budgetRequestSequence
  const isCurrent = () => sequence === budgetRequestSequence && budgetVisible.value && budgetKey.value?.id === keyId
  budgetLoading.value = true
  budgetReady.value = false
  budgetError.value = ''
  try {
    const [configuration, usage, status] = await Promise.all([
      httpGet<BudgetConfiguration>('/api/admin/gateway-enhancements'),
      httpGet<BudgetUsage[]>('/api/admin/gateway-enhancements/budgets'),
      httpGet<{ configurationError: string | null }>('/api/admin/gateway-enhancements/status')
    ])
    if (!isCurrent()) return
    if (status.configurationError) throw new Error(status.configurationError)
    budgets.value = configuration.budgets.filter(b => b.keyId === keyId).map(b => ({ ...b }))
    originalBudgets = budgetSignature(budgets.value)
    budgetUsage.value = usage.filter(b => b.keyId === keyId)
    budgetReady.value = true
  } catch (e) {
    if (isCurrent()) budgetError.value = e instanceof Error ? e.message : '预算加载失败，请重试'
  } finally { if (isCurrent()) budgetLoading.value = false }
}

async function openBudgets(row: AccessKeyItem): Promise<void> {
  if (budgetSaving.value || deletingKeyId.value === row.id) return
  budgetKey.value = row
  budgets.value = []
  budgetUsage.value = []
  budgetVisible.value = true
  await loadBudgets()
}

function addBudget(): void {
  if (!budgetKey.value || !budgetReady.value || budgetSaving.value) return
  const period = (['day', 'week', 'month'] as const).find(p => !budgets.value.some(b => b.period === p))
  if (period) budgets.value.push({ keyId: budgetKey.value.id, period, tokenLimit: 0, costLimitUsd: 0 })
}

async function saveBudgets(): Promise<void> {
  if (!budgetKey.value || !budgetVisible.value || !budgetReady.value || budgetLoading.value || budgetSaving.value || deletingKeyId.value === budgetKey.value.id) return
  if (new Set(budgets.value.map(b => b.period)).size !== budgets.value.length) {
    message.warning('同一密钥的每天、每周、每月预算各只能配置一项')
    return
  }
  if (budgets.value.some(b => !Number.isSafeInteger(b.tokenLimit) || b.tokenLimit < 0 || !Number.isFinite(b.costLimitUsd) || b.costLimitUsd < 0)) {
    message.warning('请填写有效的非负预算，Token 上限须为整数')
    return
  }
  const keyId = budgetKey.value.id
  const sequence = budgetRequestSequence
  const pendingBudgets = budgets.value.map(b => ({ ...b, keyId }))
  const expectedBudgets = originalBudgets
  const isCurrent = () => sequence === budgetRequestSequence && budgetVisible.value && budgetKey.value?.id === keyId
  budgetSaving.value = true
  try {
    // Read the latest snapshot before changing this key, preserving settings edited on other pages.
    const latest = await httpGet<BudgetConfiguration>('/api/admin/gateway-enhancements')
    if (!isCurrent()) return
    if (budgetSignature(latest.budgets.filter(b => b.keyId === keyId)) !== expectedBudgets) {
      message.warning('此密钥的预算已在其他页面修改，请重新加载后再保存')
      return
    }
    latest.budgets = [...latest.budgets.filter(b => b.keyId !== keyId), ...pendingBudgets]
    await httpPost('/api/admin/gateway-enhancements', latest)
    if (!isCurrent()) return
    message.success('密钥预算已保存')
    budgetVisible.value = false
  } catch (e) {
    if (isCurrent()) message.error(e instanceof Error ? e.message : '预算保存失败')
  } finally { budgetSaving.value = false }
}

watch(budgetVisible, visible => {
  if (!visible) {
    budgetRequestSequence++
    budgetReady.value = false
    budgetLoading.value = false
  }
}, { flush: 'sync' })
onBeforeUnmount(() => { budgetRequestSequence++ })

// 可用路由入口列表（供权限选择）
const routeEntries = ref<string[]>([])
const routeEntriesLoading = ref(false)
const routeEntriesReady = ref(false)
const routeEntriesError = ref('')

// 编辑路由权限弹窗
const editRoutesVisible = ref(false)
const editKey = ref<AccessKeyItem | null>(null)
const editRoutes = ref<string[]>([])
const editSaving = ref(false)

async function loadRouteEntries(): Promise<void> {
  routeEntriesLoading.value = true
  routeEntriesReady.value = false
  routeEntriesError.value = ''
  try {
    const entries = await routesApi.getRouteEntries()
    routeEntries.value = entries.map((entry) => entry.entryName)
    routeEntriesReady.value = true
  } catch {
    routeEntries.value = []
    routeEntriesError.value = '路由入口加载失败。为避免误将密钥设置为允许全部路由，创建和编辑权限已暂时禁用。'
  } finally {
    routeEntriesLoading.value = false
  }
}

async function load(): Promise<void> {
  loading.value = true
  try {
    const [keys] = await Promise.all([
      api.listAccessKeys(),
      loadRouteEntries()
    ])
    items.value = keys
  } finally { loading.value = false }
}

function openCreate(): void {
  if (!routeEntriesReady.value) {
    message.error(routeEntriesError.value || '路由入口尚未加载完成')
    return
  }
  newKeyName.value = ''
  newKeyPlain.value = ''
  newKeyRoutes.value = []
  showModal.value = true
}

async function handleCreate(): Promise<void> {
  if (!routeEntriesReady.value) { message.error('路由入口未成功加载，暂不能创建密钥'); return }
  if (!newKeyName.value.trim()) { message.warning('请输入密钥名称'); return }
  saving.value = true
  try {
    const result = await api.createAccessKey(newKeyName.value.trim(), newKeyRoutes.value)
    newKeyPlain.value = result.plainKey
    message.success('密钥已创建，请立即复制（仅展示一次）')
    await load()
  } finally { saving.value = false }
}

async function copyText(text: string, successMessage = '已复制到剪贴板'): Promise<void> {
  if (!text) return
  if (window.isSecureContext && navigator.clipboard) {
    try {
      await navigator.clipboard.writeText(text)
      message.success(successMessage)
      return
    } catch {
      // HTTP 或浏览器权限受限时走传统复制路径。
    }
  }

  const textarea = document.createElement('textarea')
  textarea.value = text
  textarea.style.position = 'fixed'
  textarea.style.left = '-9999px'
  textarea.style.opacity = '0'
  document.body.appendChild(textarea)
  textarea.focus()
  textarea.select()
  try {
    document.execCommand('copy')
    message.success(successMessage)
  } catch {
    message.error('复制失败，请手动复制')
  } finally {
    document.body.removeChild(textarea)
  }
}

async function handleCopyPlain(row: AccessKeyItem): Promise<void> {
  if (copyingKeyId.value) return
  copyingKeyId.value = row.id
  try {
    const { plainKey } = await api.getAccessKeyPlain(row.id)
    await copyText(plainKey, '已复制完整密钥')
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    copyingKeyId.value = null
  }
}

async function handleToggle(row: AccessKeyItem): Promise<void> {
  await api.toggleAccessKey(row.id)
  row.isEnabled = !row.isEnabled
}
async function handleDelete(row: AccessKeyItem): Promise<void> {
  if (budgetSaving.value || deletingKeyId.value) return
  deletingKeyId.value = row.id
  try {
    await api.deleteAccessKey(row.id)
    if (budgetKey.value?.id === row.id) budgetVisible.value = false
    message.success('已删除')
    await load()
  } finally { deletingKeyId.value = null }
}

// 编辑路由权限
function openEditRoutes(row: AccessKeyItem): void {
  if (!routeEntriesReady.value) {
    message.error(routeEntriesError.value || '路由入口尚未加载完成')
    return
  }
  editKey.value = row
  const val = row.allowedRouteNames
  editRoutes.value = Array.isArray(val) ? val : (typeof val === 'string' && val.trim() ? (() => { try { return JSON.parse(val) as string[] } catch { return [] } })() : [])
  editRoutesVisible.value = true
}

function selectAllRoutes(): void {
  editRoutes.value = [...routeEntries.value]
}

function clearSelectedRoutes(): void {
  editRoutes.value = []
}

const editRoutesLabel = computed(() => {
  if (editRoutes.value.length === 0) return '当前: 全部'
  if (editRoutes.value.length <= 3) return `当前: ${editRoutes.value.join(', ')}`
  return `当前: 已选择 ${editRoutes.value.length} 个路由`
})

async function handleSaveRoutes(): Promise<void> {
  if (!editKey.value) return
  if (!routeEntriesReady.value) { message.error('路由入口未成功加载，暂不能保存权限'); return }
  editSaving.value = true
  try {
    await api.updateAccessKeyRoutes(editKey.value.id, editRoutes.value)
    message.success('路由权限已更新')
    editRoutesVisible.value = false
    await load()
  } catch (e) { message.error((e as Error).message) } finally { editSaving.value = false }
}

// 解析 allowedRouteNames（后端返回 string[] 或 JSON 字符串）
function parseRoutes(val: AccessKeyItem['allowedRouteNames']): string[] {
  if (Array.isArray(val)) return val
  if (typeof val === 'string' && val.trim()) { try { return JSON.parse(val) as string[] } catch { return [] } }
  return []
}

const columns = computed<DataTableColumns<AccessKeyItem>>(() => [
  { title: '名称', key: 'keyName', width: 120, ellipsis: { tooltip: true } },
  { title: '密钥', key: 'maskedValue', minWidth: 170, ellipsis: { tooltip: true } },
  { title: '允许路由', key: 'allowedRouteNames', minWidth: 120, render: (r) => {
    const arr = parseRoutes(r.allowedRouteNames)
    return arr.length > 0 ? h(NTag, { size: 'small', bordered: false }, () => arr.join(', ')) : '全部'
  } },
  { title: '状态', key: 'isEnabled', width: 80, render: (r) => h(NTag, { size: 'small', type: r.isEnabled ? 'success' : 'default', bordered: false }, () => r.isEnabled ? '启用' : '禁用') },
  { title: '操作', key: 'actions', width: 342, fixed: 'right', render: (row) => h(NSpace, { size: 6, wrap: false }, () => [
    h(NButton, { size: 'small', quaternary: true, loading: copyingKeyId.value === row.id, onClick: () => handleCopyPlain(row) }, () => '复制密钥'),
    h(NButton, { size: 'small', quaternary: true, disabled: !routeEntriesReady.value, onClick: () => openEditRoutes(row) }, () => '编辑路由'),
    h(NButton, { size: 'small', quaternary: true, disabled: budgetSaving.value || deletingKeyId.value === row.id, onClick: () => openBudgets(row) }, () => '预算'),
    h(NButton, { size: 'small', quaternary: true, onClick: () => handleToggle(row) }, () => row.isEnabled ? '禁用' : '启用'),
    h(NPopconfirm, { onPositiveClick: () => handleDelete(row) }, { trigger: () => h(NButton, { size: 'small', quaternary: true, type: 'error', disabled: budgetSaving.value || deletingKeyId.value !== null, loading: deletingKeyId.value === row.id }, () => '删除'), default: () => `确认删除「${row.keyName}」？` })
  ]) }
])

onMounted(load)
</script>

<template>
  <div class="page-container">
    <PageHeader title="访问密钥管理" subtitle="管理代理访问密钥、路由权限与每日 / 每周 / 每月用量预算">
      <template #actions>
        <NButton type="primary" :disabled="!routeEntriesReady" @click="openCreate">新建密钥</NButton>
      </template>
    </PageHeader>
    <NAlert
      v-if="routeEntriesError"
      type="error"
      :show-icon="true"
      style="margin-bottom: 16px"
    >
      <div class="route-load-error">
        <span>{{ routeEntriesError }}</span>
        <NButton size="small" :loading="routeEntriesLoading" @click="loadRouteEntries">重试</NButton>
      </div>
    </NAlert>
    <NCard>
      <NDataTable :columns="columns" :data="items" :loading="loading" :row-key="(r: AccessKeyItem) => r.id" :pagination="{ pageSize: 20 }" striped />
    </NCard>

    <NModal v-model:show="showModal" title="新建访问密钥" preset="card" style="width: 480px; max-width: 92vw" :mask-closable="false">
      <NAlert v-if="newKeyPlain" type="success" :show-icon="true" style="margin-bottom: 16px">
        <div class="created-key-alert">
          <div class="created-key-title">密钥已创建（仅展示一次，请立即复制）：</div>
          <NInput :value="newKeyPlain" readonly type="textarea" :autosize="{ minRows: 2 }" />
          <NSpace justify="end">
            <NButton size="small" type="primary" secondary @click="copyText(newKeyPlain)">复制</NButton>
          </NSpace>
        </div>
      </NAlert>
      <NForm v-else label-placement="top">
        <NFormItem label="密钥名称">
          <NInput v-model:value="newKeyName" placeholder="如：生产环境密钥" @keyup.enter="handleCreate" />
        </NFormItem>
        <NFormItem v-if="routeEntries.length > 0" label="允许路由（不选=全部）">
          <NCheckboxGroup v-model:value="newKeyRoutes">
            <NSpace>
              <NCheckbox v-for="r in routeEntries" :key="r" :value="r" :label="r" />
            </NSpace>
          </NCheckboxGroup>
        </NFormItem>
      </NForm>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="showModal = false">关闭</NButton>
          <NButton v-if="!newKeyPlain" type="primary" :loading="saving" @click="handleCreate">创建</NButton>
        </NSpace>
      </template>
    </NModal>

    <NModal v-model:show="budgetVisible" :title="`密钥预算 - ${budgetKey?.keyName ?? ''}`" preset="card" style="width: 640px; max-width: 94vw" :mask-closable="!budgetSaving" :closable="!budgetSaving" :close-on-esc="!budgetSaving">
      <NSpace vertical :size="16">
        <NAlert type="info" :show-icon="true">0 表示不限；同一密钥的所有会话、模型和站点合并统计，任一上限达到后拒绝新请求，已开始的请求可能超出剩余额度。周期按 UTC 重置（北京时间每天 / 周一 / 每月 1 日 08:00）。</NAlert>
        <NAlert type="warning" :show-icon="true">启用预算后，同一密钥的并发请求会排队结算。金额预算遇到未定价用量会暂停当前周期；补充价格后从新周期生效，也可改用 Token 预算。WebSocket 按轮次结算。</NAlert>
        <NAlert v-if="budgetError" type="error">{{ budgetError }}</NAlert>
        <NEmpty v-if="budgetReady && budgets.length === 0" description="此密钥未配置预算，用量不受预算限制" size="small" />
        <NCard v-for="(budget, index) in budgets" :key="index" size="small">
          <NSpace vertical :size="12">
            <NSpace align="center" justify="space-between">
              <NSelect v-model:value="budget.period" :options="periodOptions" style="width: 200px" :disabled="budgetSaving" />
              <NButton size="small" type="error" quaternary :disabled="budgetSaving" @click="budgets.splice(index, 1)">删除</NButton>
            </NSpace>
            <div class="budget-limits">
              <NFormItem label="Token 上限" :show-feedback="false"><NInputNumber v-model:value="budget.tokenLimit" :min="0" :max="Number.MAX_SAFE_INTEGER" :precision="0" :disabled="budgetSaving" style="width: 100%" /></NFormItem>
              <NFormItem label="美元上限" :show-feedback="false"><NInputNumber v-model:value="budget.costLimitUsd" :min="0" :precision="4" :disabled="budgetSaving" style="width: 100%" /></NFormItem>
            </div>
            <div v-for="u in budgetUsage.filter(u => u.period === budget.period)" :key="u.period" class="budget-usage">当前已用 {{ u.usedTokens.toLocaleString() }} Token / ${{ u.usedCostUsd.toFixed(4) }}<span v-if="u.hasUnpricedUsage">（含未定价记录）</span></div>
          </NSpace>
        </NCard>
        <NButton :disabled="!budgetReady || budgetSaving || budgets.length >= 3" @click="addBudget">添加预算</NButton>
      </NSpace>
      <template #footer>
        <NSpace justify="end">
          <NButton :disabled="budgetSaving" :loading="budgetLoading" @click="loadBudgets">重新加载</NButton>
          <NButton :disabled="budgetSaving" @click="budgetVisible = false">取消</NButton>
          <NButton type="primary" :disabled="!budgetReady || budgetLoading" :loading="budgetSaving" @click="saveBudgets">保存预算</NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- 编辑路由权限弹窗 -->
    <NModal v-model:show="editRoutesVisible" :title="`编辑路由权限 - ${editKey?.keyName ?? ''}`" preset="card" style="width: 480px; max-width: 92vw">
      <NTag size="small" :bordered="false" style="margin-bottom: 12px">{{ editRoutesLabel }}</NTag>
      <p style="margin: 0 0 12px; color: var(--text-color-secondary); font-size: 13px">
        勾选该密钥允许访问的路由入口。不勾选任何项 = 允许全部路由（包括后续新增的）。
      </p>
      <NSpace size="small" style="margin-bottom: 12px">
        <NButton size="small" secondary type="primary" @click="selectAllRoutes">全选</NButton>
        <NButton size="small" secondary @click="clearSelectedRoutes">清空（=全部路由）</NButton>
      </NSpace>
      <NCheckboxGroup v-model:value="editRoutes">
        <div class="route-checkbox-grid">
          <NCheckbox v-for="r in routeEntries" :key="r" :value="r" :label="r" />
        </div>
      </NCheckboxGroup>
      <NEmpty v-if="routeEntries.length === 0" description="暂无路由入口" size="small" />
      <template #footer>
        <NSpace justify="end">
          <NButton @click="editRoutesVisible = false">取消</NButton>
          <NButton type="primary" :loading="editSaving" @click="handleSaveRoutes">保存</NButton>
        </NSpace>
      </template>
    </NModal>
  </div>
</template>

<style scoped>
.route-load-error {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.created-key-alert {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.created-key-title {
  font-weight: 600;
}

.budget-limits {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px;
}

.budget-usage {
  color: var(--text-color-secondary);
  font-size: 13px;
}

.route-checkbox-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 8px 12px;
  max-height: 360px;
  overflow-y: auto;
  padding: 10px;
  border: 1px solid var(--border-color-global);
  border-radius: 8px;
}

@media (max-width: 640px) {
  .budget-limits {
    grid-template-columns: 1fr;
  }
  .route-checkbox-grid {
    grid-template-columns: 1fr;
  }
}
</style>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { SelectOption } from 'naive-ui'
import {
  NButton, NCard, NCheckbox, NEmpty, NInput, NModal, NPopconfirm,
  NRadio, NRadioGroup, NSelect, NSpace, NSpin, NSwitch, NTag, NTooltip, useMessage
} from 'naive-ui'
import {
  createAccountPool, deleteAccountPool, fetchAccountPools, fetchPoolCandidates, fetchPoolSyncStatus, previewPoolModels,
  replacePoolMembers, syncAccountPool, updatePoolModels,
  type AccountPoolCandidate, type AccountPoolSummary, type AccountPoolSyncSummary
} from '@/api/accountPools'
import { fetchDeviceOAuthVendors } from '@/api/deviceOauth'

const message = useMessage()

const pools = ref<AccountPoolSummary[]>([])
const loading = ref(false)
const syncWarning = ref('')

// —— 建池弹窗 ——
const createModal = ref(false)
const creating = ref(false)
const newName = ref('')
const newVendor = ref<string | null>(null)
const newPlan = ref<string | null>(null)
const newAutoInclude = ref(true)
const newMemberIds = ref<string[]>([])
const candidates = ref<AccountPoolCandidate[]>([])
const candidatesLoading = ref(false)
// 托管模型白名单：all = 成员模型并集全部生成；custom = 只给勾选模型生成入口
const newModelMode = ref<'all' | 'custom'>('all')
const newModels = ref<string[]>([])
const createModelOptions = ref<string[]>([])
const createModelsLoading = ref(false)
// 用户主动取消的模型即使暂时不在预览中，也不能在重新出现时自动勾选。
const createExcludedModels = new Set<string>()
let createModelsRequestId = 0
let candidatesRequestId = 0

const deviceVendorOptions = ref<Array<{ vendorKey: string; label: string; modelFetchSupported: boolean }>>([])

const vendorOptions = computed<SelectOption[]>(() => [
  { label: 'Codex', value: 'codex' },
  { label: 'Antigravity', value: 'antigravity' },
  { label: 'Kimi', value: 'kimi' },
  { label: 'xAI', value: 'xai' },
  ...deviceVendorOptions.value
    .filter(v => v.modelFetchSupported)
    .map(v => ({ label: v.label, value: v.vendorKey })),
])

const planOptions: SelectOption[] = [
  { label: '全部套餐（可混合）', value: '' },
  { label: 'team', value: 'team' },
  { label: 'plus', value: 'plus' },
  { label: 'pro', value: 'pro' },
  { label: 'free', value: 'free' },
]

const candidateOptions = computed<SelectOption[]>(() =>
  candidates.value.map(c => ({ label: c.displayName, value: c.accountId })))

// —— 成员编辑弹窗 ——
const memberModal = ref(false)
const memberPool = ref<AccountPoolSummary | null>(null)
const memberCandidates = ref<AccountPoolCandidate[]>([])
let memberRequestId = 0
const memberChecked = ref<Record<string, boolean>>({})
const memberExcluded = ref<Record<string, boolean>>({})
const savingMembers = ref(false)
const syncBusyId = ref<string | null>(null)

async function load(): Promise<void> {
  loading.value = true
  try {
    pools.value = await fetchAccountPools()
    try {
      const status = await fetchPoolSyncStatus()
      const errors = Object.entries(status.poolFailures).map(([id, error]) =>
        `${pools.value.find(p => p.id.toLowerCase() === id.toLowerCase())?.name ?? id}: ${error}`)
      syncWarning.value = [status.scanFailure, ...errors].filter(Boolean).join('；')
    } catch {
      syncWarning.value = '无法获取后台号池同步状态'
    }
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    loading.value = false
  }
}

async function openCreate(): Promise<void> {
  ++createModelsRequestId
  createModelsLoading.value = false
  ++candidatesRequestId
  candidatesLoading.value = false
  createExcludedModels.clear()
  if (deviceVendorOptions.value.length === 0) {
    try {
      deviceVendorOptions.value = await fetchDeviceOAuthVendors()
    } catch { /* 设备厂商拉取失败不阻断四垂直厂商建池 */ }
  }
  newName.value = ''
  newVendor.value = null
  newPlan.value = ''
  newAutoInclude.value = true
  newMemberIds.value = []
  candidates.value = []
  newModelMode.value = 'all'
  newModels.value = []
  createModelOptions.value = []
  createModal.value = true
}

async function refreshCandidates(): Promise<void> {
  const requestId = ++candidatesRequestId
  const vendor = newVendor.value
  const plan = newPlan.value || null
  if (!vendor) {
    candidates.value = []
    candidatesLoading.value = false
    void refreshCreateModelOptions()
    return
  }
  candidatesLoading.value = true
  try {
    const list = await fetchPoolCandidates(vendor, plan)
    if (requestId !== candidatesRequestId) return
    candidates.value = list
  } catch (e) {
    if (requestId === candidatesRequestId) message.error((e as Error).message)
  } finally {
    if (requestId === candidatesRequestId) candidatesLoading.value = false
  }
  if (requestId === candidatesRequestId) void refreshCreateModelOptions()
}

function updateCreateModels(selected: string[]): void {
  const selectedSet = new Set(selected)
  for (const model of createModelOptions.value) {
    if (selectedSet.has(model)) createExcludedModels.delete(model)
    else createExcludedModels.add(model)
  }
  newModels.value = selected
}

/** 建池弹窗的模型预览：按当前厂商（+套餐）与已勾选成员拉模型并集。 */
async function refreshCreateModelOptions(): Promise<void> {
  const requestId = ++createModelsRequestId
  if (!newVendor.value) {
    createModelOptions.value = []
    newModels.value = []
    createModelsLoading.value = false
    return
  }
  createModelsLoading.value = true
  try {
    const options = await previewPoolModels({
      vendorKey: newVendor.value,
      planType: newPlan.value || null,
      memberAccountIds: newAutoInclude.value ? undefined : newMemberIds.value,
    })
    if (requestId !== createModelsRequestId) return
    createModelOptions.value = options
    // 新模型默认勾选，但保留用户取消的模型（包括暂时消失后重新出现的模型）。
    newModels.value = newModelMode.value === 'all'
      ? [...options]
      : options.filter(m => !createExcludedModels.has(m))
  } catch (e) {
    if (requestId === createModelsRequestId) message.error((e as Error).message)
  } finally {
    if (requestId === createModelsRequestId) createModelsLoading.value = false
  }
}

async function handleCreate(): Promise<void> {
  const name = newName.value.trim()
  if (!name || !newVendor.value) { message.warning('请填写池名并选择厂商'); return }
  if (newModelMode.value === 'custom' && newModels.value.length === 0) {
    message.warning('自选模型时请至少选择一个模型；如需全部模型，请切换到「全部模型」')
    return
  }
  creating.value = true
  try {
    const sync = await createAccountPool({
      name,
      vendorKey: newVendor.value,
      planType: newPlan.value || null,
      autoIncludeMembers: newAutoInclude.value,
      memberAccountIds: newAutoInclude.value ? undefined : newMemberIds.value,
      // 自选模式只给勾选的模型生成入口；全部模式不传（成员模型并集全量生成）
      exposedModels: newModelMode.value === 'custom' ? newModels.value : undefined,
    })
    showSyncResult('号池已创建', sync)
    createModal.value = false
    await load()
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    creating.value = false
  }
}

/** 同步结果统一提示：被手工路由或其他号池占用而跳过的模型单独警告。 */
function showSyncResult(prefix: string, sync: AccountPoolSyncSummary): void {
  const base = `${prefix}：成员 ${sync.memberCount} · 模型 ${sync.modelCount} · 规则 ${sync.ruleCount}`
  if (sync.skippedModels?.length) {
    message.warning(`${base}；跳过 ${sync.skippedModels.length} 个被手工路由或其他号池占用的模型（${sync.skippedModels.join('、')}）`)
  } else {
    message.success(base)
  }
}

function openMembers(pool: AccountPoolSummary): void {
  const requestId = ++memberRequestId
  memberPool.value = pool
  memberCandidates.value = []
  memberChecked.value = {}
  memberExcluded.value = {}
  for (const member of pool.members) {
    memberChecked.value[member.accountRefId] = true
    memberExcluded.value[member.accountRefId] = member.excluded
  }
  memberModal.value = true
  void (async () => {
    try {
      const list = await fetchPoolCandidates(pool.vendorKey, pool.planType)
      if (requestId !== memberRequestId) return
      memberCandidates.value = list
      for (const item of list) {
        if (!(item.accountId in memberChecked.value)) memberChecked.value[item.accountId] = false
        if (!(item.accountId in memberExcluded.value)) memberExcluded.value[item.accountId] = false
      }
    } catch (e) {
      if (requestId === memberRequestId) message.error((e as Error).message)
    }
  })()
}

async function handleSaveMembers(): Promise<void> {
  if (!memberPool.value) return
  savingMembers.value = true
  try {
    const sync = await replacePoolMembers(
      memberPool.value.id,
      Object.keys(memberChecked.value)
        .filter(id => memberChecked.value[id])
        .map(id => ({ accountRefId: id, excluded: memberExcluded.value[id] }))
    )
    showSyncResult('成员已更新', sync)
    memberModal.value = false
    await load()
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    savingMembers.value = false
  }
}

async function handleSync(pool: AccountPoolSummary): Promise<void> {
  syncBusyId.value = pool.id
  try {
    const sync = await syncAccountPool(pool.id)
    showSyncResult(`已同步「${pool.name}」`, sync)
    await load()
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    syncBusyId.value = null
  }
}

// —— 模型白名单编辑弹窗 ——
const modelsModal = ref(false)
const modelsPool = ref<AccountPoolSummary | null>(null)
const modelsMode = ref<'all' | 'custom'>('all')
const modelsSelected = ref<string[]>([])
const modelsOptions = ref<string[]>([])
const modelsLoading = ref(false)
const savingModels = ref(false)

async function openModels(pool: AccountPoolSummary): Promise<void> {
  modelsPool.value = pool
  modelsMode.value = pool.exposedModels?.length ? 'custom' : 'all'
  modelsSelected.value = [...(pool.exposedModels ?? [])]
  modelsOptions.value = [...(pool.exposedModels ?? [])]
  modelsModal.value = true
  await refreshPoolModelOptions()
}

/** 模型编辑弹窗按有效成员预览；自选模式保留原有选择。 */
async function refreshPoolModelOptions(): Promise<void> {
  const pool = modelsPool.value
  if (!pool) return
  modelsLoading.value = true
  try {
    modelsOptions.value = await previewPoolModels({
      vendorKey: pool.vendorKey,
      planType: pool.planType,
      // 与同步口径一致：排除位成员不参与模型并集
      memberAccountIds: pool.members.filter(m => !m.excluded).map(m => m.accountRefId),
    })
    // 自选白名单是完整选择，不能把之前未选的模型重新自动勾选。
    modelsSelected.value = modelsMode.value === 'all'
      ? [...modelsOptions.value]
      : modelsSelected.value.filter(m => modelsOptions.value.includes(m))
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    modelsLoading.value = false
  }
}

async function handleSaveModels(): Promise<void> {
  if (!modelsPool.value) return
  if (modelsMode.value === 'custom' && modelsSelected.value.length === 0) {
    message.warning('自选模型时请至少选择一个模型；如需全部模型，请切换到「全部模型」')
    return
  }
  savingModels.value = true
  try {
    const sync = await updatePoolModels(modelsPool.value.id, modelsMode.value === 'custom' ? modelsSelected.value : null)
    showSyncResult('托管模型已更新', sync)
    modelsModal.value = false
    await load()
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    savingModels.value = false
  }
}

async function handleDelete(pool: AccountPoolSummary): Promise<void> {
  try {
    await deleteAccountPool(pool.id)
    message.success(`号池「${pool.name}」已删除，池规则已清除；有剩余规则的入口已解除托管并保留`)
    await load()
  } catch (e) {
    message.error((e as Error).message)
  }
}

const candidateEntries = computed(() =>
  Object.keys(memberChecked.value).map(id => ({
    id,
    name: memberCandidates.value.find(c => c.accountId === id)?.displayName
      ?? memberPool.value?.members.find(m => m.accountRefId === id)?.displayName
      ?? id,
  })))

onMounted(load)
</script>

<template>
  <div class="account-pools-view">
    <NCard size="small">
      <template #header>
        号池（生成式路由总包）
        <NTag size="small" :bordered="false" style="margin-left: 8px">对外一个入口 · 内部按额度自动排序切换</NTag>
        <NTooltip trigger="hover" placement="bottom-start" :style="{ maxWidth: '560px' }">
          <template #trigger>
            <span class="pool-hint-badge" tabindex="0">?</span>
          </template>
          号池把多个同厂商账号打包：按勾选的托管模型生成路由入口，
          对外照常按模型名调用（如 gpt-5），池在背后扇出到全部持有该模型的成员；新规则默认按账号名排序，
          运行时叠加「配额感知排序 / 分级冷却 / 熔断」自动优先剩余额度多/最早重置的账号并跳过耗尽账号。
          被手工路由或其他号池占用的模型名自动跳过；删除池会清除该池规则，无剩余规则的托管入口一并删除；
          有其他规则的入口解除托管并保留。未标记归属的旧规则保守保留；管理员可核实来源后按 docs/admin-api.md 的「无归属旧路由的人工恢复」认领并同步。
        </NTooltip>
      </template>
      <template #header-extra>
        <NButton type="primary" size="small" @click="openCreate">新建号池</NButton>
      </template>
      <div v-if="syncWarning" role="alert" style="color: var(--warning-color); margin-bottom: 12px">
        号池后台同步失败，正在重试：{{ syncWarning }}。请刷新页面查看最新状态。
      </div>
      <NSpin :show="loading">
        <NEmpty v-if="!loading && pools.length === 0" description="还没有号池，点「新建号池」把同组账号打包成一个入口" size="small" />
        <div v-else class="account-pools-list">
          <div v-for="pool in pools" :key="pool.id" class="account-pool-row">
            <div class="pool-main">
              <div class="pool-title">
                <strong>{{ pool.name }}</strong>
                <NTag size="tiny" :bordered="false">{{ pool.vendorLabel }}{{ pool.planType ? ` · ${pool.planType}` : pool.vendorKey === 'codex' ? ' · 全部套餐' : '' }}</NTag>
                <NTag v-if="pool.autoIncludeMembers" size="tiny" type="info" :bordered="false">自动纳入</NTag>
                <NTag v-if="pool.exposedModels?.length" size="tiny" :bordered="false">自选 {{ pool.exposedModels.length }} 个模型</NTag>
                <NTag size="tiny" :type="pool.managedEntryCount > 0 ? 'success' : 'warning'" :bordered="false">
                  托管 {{ pool.managedEntryCount }} 个入口
                </NTag>
              </div>
              <div class="pool-meta">
                成员 {{ pool.members.filter(m => !m.excluded).length }}/{{ pool.members.length }}
                · 最近同步 {{ new Date(pool.updatedAt).toLocaleString() }}
              </div>
            </div>
            <div class="pool-actions">
              <NButton size="tiny" :loading="syncBusyId === pool.id" @click="handleSync(pool)">同步</NButton>
              <NButton size="tiny" @click="openMembers(pool)">成员</NButton>
              <NButton size="tiny" @click="openModels(pool)">模型</NButton>
              <NPopconfirm @positive-click="handleDelete(pool)">
                <template #trigger>
                  <NButton size="tiny" type="warning">删除</NButton>
                </template>
                删除号池「{{ pool.name }}」？该池的规则会被删除；其 {{ pool.managedEntryCount }} 个托管入口中，
                无剩余规则的入口会一并删除，有其他规则的入口解除托管并保留。引用已删除入口的路由会失效，需手动修复。
              </NPopconfirm>
            </div>
          </div>
        </div>
      </NSpin>
    </NCard>

    <NModal v-model:show="createModal" preset="card" title="新建号池" style="width: 560px; max-width: 94vw">
      <NSpace vertical>
        <div>
          <div class="field-label">池名（管理标签，创建后不可改；对外入口按托管模型名生成）</div>
          <NInput v-model:value="newName" placeholder="如 gpt-team-pool" />
        </div>
        <div>
          <div class="field-label">厂商</div>
          <NSelect v-model:value="newVendor" :options="vendorOptions" placeholder="选择厂商" @update:value="refreshCandidates" />
        </div>
        <div v-if="newVendor === 'codex'">
          <div class="field-label">套餐（可选筛选，留空=全部套餐；实际可用模型以各成员站点映射为准）</div>
          <NSelect v-model:value="newPlan" :options="planOptions" @update:value="refreshCandidates" />
        </div>
        <div>
          <div class="field-label">自动纳入新账号（同厂商{{ newVendor === 'codex' ? '且符合可选套餐筛选' : '' }}的账号登录后同步时自动入池）</div>
          <NSwitch v-model:value="newAutoInclude" @update:value="refreshCreateModelOptions" />
        </div>
        <div v-if="!newAutoInclude">
          <div class="field-label">勾选成员（新路由默认按账号名排序）</div>
          <NSpin :show="candidatesLoading" size="small">
            <NEmpty v-if="!candidatesLoading && candidates.length === 0" description="该厂商暂无账号" size="small" />
            <div v-else class="candidate-list">
              <NCheckbox
                v-for="c in candidates"
                :key="c.accountId"
                :checked="newMemberIds.includes(c.accountId)"
                @update:checked="(checked: boolean) => {
                  newMemberIds = checked
                    ? [...newMemberIds, c.accountId]
                    : newMemberIds.filter(id => id !== c.accountId)
                  refreshCreateModelOptions()
                }"
              >
                {{ c.displayName }}
              </NCheckbox>
            </div>
          </NSpin>
        </div>
        <div>
          <div class="field-label">托管模型入口（决定给哪些模型生成对外路由入口）</div>
          <NRadioGroup v-model:value="newModelMode" @update:value="(mode: 'all' | 'custom') => {
            newModels = mode === 'all' ? [...createModelOptions] : createModelOptions.filter(m => !createExcludedModels.has(m))
          }">
            <NRadio value="all">全部模型</NRadio>
            <NRadio value="custom">自选模型</NRadio>
          </NRadioGroup>
          <NSpin v-if="newModelMode === 'custom'" :show="createModelsLoading" size="small" style="margin-top: 8px">
            <NEmpty v-if="!createModelsLoading && createModelOptions.length === 0" description="暂无可用模型（先勾选成员或检查站点模型映射）" size="small" />
            <NSelect
              v-else
              :value="newModels"
              @update:value="updateCreateModels"
              multiple
              clearable
              :options="createModelOptions.map(m => ({ label: m, value: m }))"
              placeholder="选择要生成路由入口的模型"
              :max-tag-count="6"
            />
          </NSpin>
        </div>
      </NSpace>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="createModal = false">取消</NButton>
          <NButton type="primary" :loading="creating" @click="handleCreate">创建并同步</NButton>
        </NSpace>
      </template>
    </NModal>

    <NModal v-model:show="memberModal" preset="card" :title="`成员 - ${memberPool?.name ?? ''}`" style="width: 560px; max-width: 94vw">
      <div class="account-pools-hint">
        勾选 = 池成员；「排除」= 保留在池里但被跳过（自动池不会再补回，取消排除即恢复）。
        已有路由保留原顺序，新成员默认按账号名追加。
      </div>
      <div class="candidate-list member-list">
        <div v-for="entry in candidateEntries" :key="entry.id" class="member-row">
          <NCheckbox
            v-model:checked="memberChecked[entry.id]"
            :disabled="memberPool?.autoIncludeMembers"
            :title="memberPool?.autoIncludeMembers ? '自动纳入池：成员随同步自动补齐，请用「排除」控制参与' : undefined"
          >
            {{ entry.name }}
          </NCheckbox>
          <NCheckbox v-model:checked="memberExcluded[entry.id]" :disabled="!memberChecked[entry.id]">排除</NCheckbox>
        </div>
      </div>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="memberModal = false">取消</NButton>
          <NButton type="primary" :loading="savingMembers" @click="handleSaveMembers">保存并同步</NButton>
        </NSpace>
      </template>
    </NModal>

    <NModal v-model:show="modelsModal" preset="card" :title="`托管模型 - ${modelsPool?.name ?? ''}`" style="width: 560px; max-width: 94vw">
      <div class="account-pools-hint">
        只给勾选的模型生成对外路由入口（用不到的模型不再占用入口）；「全部模型」= 成员模型并集全量生成。
        取消勾选保存后，对应入口与规则会在本次同步中一并清除。
      </div>
      <NRadioGroup v-model:value="modelsMode">
        <NRadio value="all">全部模型</NRadio>
        <NRadio value="custom">自选模型</NRadio>
      </NRadioGroup>
      <NSpin v-if="modelsMode === 'custom'" :show="modelsLoading" size="small" style="margin-top: 8px">
        <NEmpty v-if="!modelsLoading && modelsOptions.length === 0" description="成员暂无可用模型（检查站点模型映射）" size="small" />
        <NSelect
          v-else
          v-model:value="modelsSelected"
          multiple
          clearable
          :options="modelsOptions.map(m => ({ label: m, value: m }))"
          placeholder="选择要生成路由入口的模型"
          :max-tag-count="6"
        />
      </NSpin>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="modelsModal = false">取消</NButton>
          <NButton type="primary" :loading="savingModels" @click="handleSaveModels">保存并同步</NButton>
        </NSpace>
      </template>
    </NModal>
  </div>
</template>

<style scoped>
.account-pools-hint {
  margin: 0 0 12px;
  color: var(--text-color-secondary);
  font-size: 12px;
  line-height: 1.7;
}

/* 头部问号徽标：悬浮显示号池说明全文，替代占整行的小字 */
.pool-hint-badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  margin-left: 8px;
  border: 1px solid var(--border-color);
  border-radius: 50%;
  color: var(--text-color-secondary);
  font-size: 11px;
  font-weight: 600;
  line-height: 1;
  cursor: help;
  vertical-align: middle;
  user-select: none;
}

.pool-hint-badge:hover {
  color: var(--primary-color, #3b82f6);
  border-color: var(--primary-color, #3b82f6);
}

.account-pools-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.account-pool-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 12px;
  border: 1px solid var(--border-color);
  border-radius: 6px;
}

.pool-title {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.pool-meta {
  margin-top: 4px;
  color: var(--text-color-secondary);
  font-size: 12px;
}

.pool-actions {
  display: flex;
  gap: 6px;
  flex-shrink: 0;
}

.field-label {
  margin-bottom: 4px;
  font-size: 12px;
  color: var(--text-color-secondary);
}

.candidate-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
  max-height: 260px;
  overflow-y: auto;
}

.member-list .member-row {
  display: flex;
  align-items: center;
  gap: 16px;
}
</style>

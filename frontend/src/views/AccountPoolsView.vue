<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { SelectOption } from 'naive-ui'
import {
  NButton, NCard, NCheckbox, NEmpty, NInput, NModal, NPopconfirm,
  NSelect, NSpace, NSpin, NSwitch, NTag, useMessage
} from 'naive-ui'
import {
  createAccountPool, deleteAccountPool, fetchAccountPools, fetchPoolCandidates,
  replacePoolMembers, syncAccountPool,
  type AccountPoolCandidate, type AccountPoolSummary, type AccountPoolSyncSummary
} from '@/api/accountPools'
import { fetchDeviceOAuthVendors } from '@/api/deviceOauth'

const message = useMessage()

const pools = ref<AccountPoolSummary[]>([])
const loading = ref(false)

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
  { label: '不限套餐', value: '' },
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
const memberChecked = ref<Record<string, boolean>>({})
const memberExcluded = ref<Record<string, boolean>>({})
const savingMembers = ref(false)
const syncBusyId = ref<string | null>(null)

async function load(): Promise<void> {
  loading.value = true
  try {
    pools.value = await fetchAccountPools()
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    loading.value = false
  }
}

async function openCreate(): Promise<void> {
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
  createModal.value = true
}

async function refreshCandidates(): Promise<void> {
  if (!newVendor.value) { candidates.value = []; return }
  candidatesLoading.value = true
  try {
    candidates.value = await fetchPoolCandidates(newVendor.value, newPlan.value || null)
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    candidatesLoading.value = false
  }
}

async function handleCreate(): Promise<void> {
  const name = newName.value.trim()
  if (!name || !newVendor.value) { message.warning('请填写池名并选择厂商'); return }
  creating.value = true
  try {
    const sync = await createAccountPool({
      name,
      vendorKey: newVendor.value,
      planType: newPlan.value || null,
      autoIncludeMembers: newAutoInclude.value,
      memberAccountIds: newAutoInclude.value ? undefined : newMemberIds.value,
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

/** 同步结果统一提示：跳过的模型（被手工路由占用）单独警告。 */
function showSyncResult(prefix: string, sync: AccountPoolSyncSummary): void {
  const base = `${prefix}：成员 ${sync.memberCount} · 模型 ${sync.modelCount} · 规则 ${sync.ruleCount}`
  if (sync.skippedModels?.length) {
    message.warning(`${base}；跳过 ${sync.skippedModels.length} 个被手工路由占用的模型（${sync.skippedModels.join('、')}）`)
  } else {
    message.success(base)
  }
}

function openMembers(pool: AccountPoolSummary): void {
  memberPool.value = pool
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
      for (const item of list) {
        if (!(item.accountId in memberChecked.value)) memberChecked.value[item.accountId] = false
        if (!(item.accountId in memberExcluded.value)) memberExcluded.value[item.accountId] = false
      }
    } catch (e) {
      message.error((e as Error).message)
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

async function handleDelete(pool: AccountPoolSummary): Promise<void> {
  try {
    await deleteAccountPool(pool.id)
    message.success(`号池「${pool.name}」已删除（生成的路由规则保留，可手工维护）`)
    await load()
  } catch (e) {
    message.error((e as Error).message)
  }
}

const candidateEntries = computed(() =>
  Object.keys(memberChecked.value).map(id => ({
    id,
    name: candidates.value.find(c => c.accountId === id)?.displayName
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
      </template>
      <template #header-extra>
        <NButton type="primary" size="small" @click="openCreate">新建号池</NButton>
      </template>
      <div class="account-pools-hint">
        号池把多个同厂商（Codex 需同套餐）账号打包：每个成员模型生成一个托管路由入口，
        对外照常按模型名调用（如 gpt-5），池在背后扇出到全部持有该模型的成员；池内顺序是静态基线，
        运行时叠加「配额感知排序 / 分级冷却 / 熔断」自动优先剩余额度多/最早重置的账号并跳过耗尽账号。
        被手工路由占用的模型名自动跳过；删除池仅解除托管，已生成规则保留。
      </div>
      <NSpin :show="loading">
        <NEmpty v-if="!loading && pools.length === 0" description="还没有号池，点「新建号池」把同组账号打包成一个入口" size="small" />
        <div v-else class="account-pools-list">
          <div v-for="pool in pools" :key="pool.id" class="account-pool-row">
            <div class="pool-main">
              <div class="pool-title">
                <strong>{{ pool.name }}</strong>
                <NTag size="tiny" :bordered="false">{{ pool.vendorLabel }}{{ pool.planType ? ` · ${pool.planType}` : '' }}</NTag>
                <NTag v-if="pool.autoIncludeMembers" size="tiny" type="info" :bordered="false">自动纳入</NTag>
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
              <NPopconfirm @positive-click="handleDelete(pool)">
                <template #trigger>
                  <NButton size="tiny" type="warning">删除</NButton>
                </template>
                删除号池「{{ pool.name }}」？生成的路由规则会保留（解除托管）。
              </NPopconfirm>
            </div>
          </div>
        </div>
      </NSpin>
    </NCard>

    <NModal v-model:show="createModal" preset="card" title="新建号池" style="width: 560px; max-width: 94vw">
      <NSpace vertical>
        <div>
          <div class="field-label">池名（= 对外路由入口名，创建后不可改）</div>
          <NInput v-model:value="newName" placeholder="如 gpt-team-pool" />
        </div>
        <div>
          <div class="field-label">厂商</div>
          <NSelect v-model:value="newVendor" :options="vendorOptions" placeholder="选择厂商" @update:value="refreshCandidates" />
        </div>
        <div v-if="newVendor === 'codex'">
          <div class="field-label">套餐（同套餐模型清单一致）</div>
          <NSelect v-model:value="newPlan" :options="planOptions" @update:value="refreshCandidates" />
        </div>
        <div>
          <div class="field-label">自动纳入新账号（同厂商{{ newVendor === 'codex' ? '同套餐' : '' }}登录后同步时自动入池）</div>
          <NSwitch v-model:value="newAutoInclude" />
        </div>
        <div v-if="!newAutoInclude">
          <div class="field-label">勾选成员（顺序即优先级）</div>
          <NSpin :show="candidatesLoading" size="small">
            <NEmpty v-if="!candidatesLoading && candidates.length === 0" description="该厂商暂无账号" size="small" />
            <div v-else class="candidate-list">
              <NCheckbox
                v-for="c in candidates"
                :key="c.accountId"
                :checked="newMemberIds.includes(c.accountId)"
                @update:checked="(checked: boolean) => newMemberIds = checked
                  ? [...newMemberIds, c.accountId]
                  : newMemberIds.filter(id => id !== c.accountId)"
              >
                {{ c.displayName }}
              </NCheckbox>
            </div>
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
        列表顺序即池内优先级基线。
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
  </div>
</template>

<style scoped>
.account-pools-hint {
  margin: 0 0 12px;
  color: var(--text-color-secondary);
  font-size: 12px;
  line-height: 1.7;
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

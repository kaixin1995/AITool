<script setup lang="ts">
import { onMounted, ref } from 'vue'
import {
  NAlert,
  NButton,
  NCard,
  NDivider,
  NEmpty,
  NForm,
  NFormItem,
  NGrid,
  NGridItem,
  NInput,
  NInputNumber,
  NModal,
  NPopconfirm,
  NSelect,
  NSpace,
  NSpin,
  NSwitch,
  NTag,
  NTooltip,
  useMessage
} from 'naive-ui'
import {
  getProxyProfiles,
  createProxyProfile,
  updateProxyProfile,
  deleteProxyProfile,
  testProxyProfile,
  type ProxyProfile,
  type ProxyProfilePayload
} from '@/api/proxyProfiles'

const message = useMessage()
const loading = ref(false)
const profiles = ref<ProxyProfile[]>([])

// 测试状态：记录每个 proxyUrl 的测试结果
const testStatus = ref<Record<string, { loading?: boolean; latencyMs?: number; success?: boolean; error?: string }>>({})

// 编辑/新建弹窗
const showModal = ref(false)
const isEditing = ref(false)
const currentId = ref<string | null>(null)
const modalSubmitting = ref(false)

const proxyScheme = ref('http')
const proxyHost = ref('127.0.0.1')
const proxyPort = ref<number | null>(7890)
const proxySchemeOptions = [
  { label: 'HTTP', value: 'http' },
  { label: 'HTTPS', value: 'https' },
  { label: 'SOCKS5', value: 'socks5' },
  { label: 'SOCKS4', value: 'socks4' },
  { label: 'SOCKS4A', value: 'socks4a' }
]

function splitProxyUrl(proxyUrl: string): { scheme: string; host: string; port: number | null } {
  let rest = proxyUrl.trim()
  let scheme = 'http'
  const schemeMatch = rest.match(/^([a-z][a-z\d+.-]*):\/\/(.+)$/i)
  if (schemeMatch) {
    scheme = schemeMatch[1].toLowerCase()
    rest = schemeMatch[2].trim()
  }
  // 尾部 :端口（IPv6 除外：多个冒号视为原始主机地址不作端口切分）
  const portMatch = rest.match(/^(.+):(\d{1,5})$/)
  if (portMatch && rest.split(':').length === 2) {
    const port = Number(portMatch[2])
    return { scheme, host: portMatch[1].trim(), port: port >= 1 && port <= 65535 ? port : null }
  }
  return { scheme, host: rest, port: null }
}

function buildProxyUrl(): string {
  const host = proxyHost.value.trim().replace(/^\/\/+/, '')
  if (!host) return ''
  // IPv6 字面量需要方括号包裹才能组成合法 URL
  const hostPart = host.includes(':') && !host.startsWith('[') ? `[${host}]` : host
  if (proxyPort.value == null || proxyPort.value <= 0 || proxyPort.value > 65535) {
    return `${proxyScheme.value}://${hostPart}`
  }
  return `${proxyScheme.value}://${hostPart}:${proxyPort.value}`
}

const formModel = ref<ProxyProfilePayload>({
  key: '',
  name: '',
  proxyUrl: '',
  description: '',
  isEnabled: true,
  sortOrder: 10
})

const modalTesting = ref(false)
const modalTestResult = ref<{ isSuccess: boolean; latencyMs: number; errorMessage?: string | null } | null>(null)

async function loadProfiles() {
  loading.value = true
  try {
    profiles.value = await getProxyProfiles()
  } catch (err: any) {
    message.error(err?.response?.data?.message || err?.message || '加载代理方案列表失败')
  } finally {
    loading.value = false
  }
}

function openCreateModal() {
  isEditing.value = false
  currentId.value = null
  modalTestResult.value = null
  proxyScheme.value = 'http'
  proxyHost.value = '127.0.0.1'
  proxyPort.value = 7890
  formModel.value = {
    key: '',
    name: '',
    proxyUrl: buildProxyUrl(),
    description: '',
    isEnabled: true,
    sortOrder: (profiles.value.length + 1) * 10
  }
  showModal.value = true
}

function openEditModal(profile: ProxyProfile) {
  isEditing.value = true
  currentId.value = profile.id
  modalTestResult.value = null
  const parsedProxy = splitProxyUrl(profile.proxyUrl)
  proxyScheme.value = parsedProxy.scheme
  proxyHost.value = parsedProxy.host
  proxyPort.value = parsedProxy.port
  formModel.value = {
    key: profile.key,
    name: profile.name,
    proxyUrl: buildProxyUrl(),
    description: profile.description || '',
    isEnabled: profile.isEnabled,
    sortOrder: profile.sortOrder
  }
  showModal.value = true
}

async function handleTestNode(proxyUrl: string) {
  testStatus.value[proxyUrl] = { loading: true }
  try {
    const res = await testProxyProfile({ proxyUrl })
    if (res.isSuccess) {
      testStatus.value[proxyUrl] = { loading: false, success: true, latencyMs: res.latencyMs }
      message.success(`连接成功！延迟 ${res.latencyMs} ms`)
    } else {
      testStatus.value[proxyUrl] = { loading: false, success: false, error: res.errorMessage || '连接超时或无法建立握手' }
      message.error(`连接失败: ${res.errorMessage || '无法连接'}`)
    }
  } catch (err: any) {
    const errMsg = err?.response?.data?.message || err?.message || '测速请求失败'
    testStatus.value[proxyUrl] = { loading: false, success: false, error: errMsg }
    message.error(errMsg)
  }
}

async function handleTestInModal() {
  const proxyUrl = buildProxyUrl()
  if (!proxyUrl) {
    message.warning('请先输入代理 IP/域名和端口')
    return
  }
  formModel.value.proxyUrl = proxyUrl
  modalTesting.value = true
  modalTestResult.value = null
  try {
    const res = await testProxyProfile({ proxyUrl })
    modalTestResult.value = {
      isSuccess: res.isSuccess,
      latencyMs: res.latencyMs,
      errorMessage: res.errorMessage
    }
    if (res.isSuccess) {
      message.success(`测试通过，延迟 ${res.latencyMs} ms`)
    } else {
      message.error(`测试失败: ${res.errorMessage || '连接超时'}`)
    }
  } catch (err: any) {
    modalTestResult.value = {
      isSuccess: false,
      latencyMs: 0,
      errorMessage: err?.response?.data?.message || err?.message || '测试失败'
    }
  } finally {
    modalTesting.value = false
  }
}

async function handleSave() {
  const key = formModel.value.key.trim()
  const name = formModel.value.name.trim()
  const proxyUrl = buildProxyUrl()

  if (!key || !name || !proxyHost.value.trim()) {
    message.warning('请填写必填项（方案Key、名称、代理 IP/域名）')
    return
  }
  if (proxyPort.value == null || proxyPort.value < 1 || proxyPort.value > 65535) {
    message.warning('请输入有效端口（1-65535）')
    return
  }
  formModel.value.proxyUrl = proxyUrl

  const payload: ProxyProfilePayload = {
    key,
    name,
    proxyUrl,
    description: formModel.value.description?.trim() || null,
    isEnabled: formModel.value.isEnabled,
    sortOrder: formModel.value.sortOrder
  }

  modalSubmitting.value = true
  try {
    if (isEditing.value && currentId.value) {
      await updateProxyProfile(currentId.value, payload)
      message.success('代理方案已更新')
    } else {
      await createProxyProfile(payload)
      message.success('代理方案已创建')
    }
    showModal.value = false
    await loadProfiles()
  } catch (err: any) {
    message.error(err?.response?.data?.message || err?.message || '保存代理方案失败')
  } finally {
    modalSubmitting.value = false
  }
}

async function handleDelete(profile: ProxyProfile) {
  try {
    await deleteProxyProfile(profile.id)
    message.success(`已删除方案「${profile.name}」`)
    await loadProfiles()
  } catch (err: any) {
    message.error(err?.response?.data?.message || err?.message || '删除失败')
  }
}

async function handleToggle(profile: ProxyProfile) {
  try {
    await updateProxyProfile(profile.id, {
      key: profile.key,
      name: profile.name,
      proxyUrl: profile.proxyUrl,
      description: profile.description,
      isEnabled: profile.isEnabled,
      sortOrder: profile.sortOrder
    })
    message.success(`已${profile.isEnabled ? '启用' : '禁用'}方案「${profile.name}」`)
  } catch (err: any) {
    profile.isEnabled = !profile.isEnabled
    message.error('切换状态失败')
  }
}

function getScheme(url: string): string {
  try {
    const idx = url.indexOf('://')
    return idx > 0 ? url.slice(0, idx).toUpperCase() : 'HTTP'
  } catch {
    return 'PROXY'
  }
}

function getSchemeTagType(scheme: string): 'info' | 'success' | 'warning' | 'error' | 'default' {
  switch (scheme) {
    case 'SOCKS5':
    case 'SOCKS4':
      return 'success'
    case 'HTTPS':
      return 'info'
    case 'HTTP':
      return 'warning'
    default:
      return 'default'
  }
}

onMounted(loadProfiles)
</script>

<template>
  <div class="proxy-profiles-tab">
    <!-- 顶部说明提示条 -->
    <NAlert type="info" class="profiles-intro-alert" :bordered="false">
      <template #header>
        <span class="intro-title">🌐 出口网络代理池 (Egress Proxy Pool)</span>
      </template>
      集中统一维护上游请求出口代理节点（支持 HTTP / HTTPS / SOCKS5 / SOCKS4 协议）。站点配置和模型站点映射中可一键下拉绑定代理节点。<strong>默认均为直连（不走任何代理）</strong>。
    </NAlert>

    <!-- 操作工具栏 -->
    <div class="toolbar-row">
      <div class="toolbar-info">
        <NTag round size="small" type="primary" :bordered="false">
          共 {{ profiles.length }} 个代理节点
        </NTag>
      </div>
      <NSpace>
        <NButton size="small" secondary @click="loadProfiles" :loading="loading">
          刷新
        </NButton>
        <NButton size="small" type="primary" @click="openCreateModal">
          + 新增代理节点
        </NButton>
      </NSpace>
    </div>

    <!-- 代理方案卡片列表 -->
    <NSpin :show="loading">
      <div v-if="profiles.length === 0" class="empty-state">
        <NEmpty description="暂无代理节点配置，点击右上角按钮添加" size="medium">
          <template #extra>
            <NButton size="small" type="primary" @click="openCreateModal">
              创建第一个代理节点
            </NButton>
          </template>
        </NEmpty>
      </div>

      <NGrid v-else :x-gap="16" :y-gap="16" cols="1 s:1 m:2 l:3" responsive="screen">
        <NGridItem v-for="p in profiles" :key="p.id">
          <NCard size="small" hoverable class="proxy-card" :class="{ 'proxy-disabled': !p.isEnabled }">
            <div class="card-header-bar">
              <div class="card-title-group">
                <NTag size="small" :type="getSchemeTagType(getScheme(p.proxyUrl))" round>
                  {{ getScheme(p.proxyUrl) }}
                </NTag>
                <span class="card-name" :title="p.name">{{ p.name }}</span>
              </div>
              <NSwitch v-model:value="p.isEnabled" size="small" @update:value="handleToggle(p)" />
            </div>

            <div class="card-key-tag">
              Key: <code>{{ p.key }}</code>
            </div>

            <div class="card-url-bar">
              <span class="url-text" :title="p.proxyUrl">{{ p.proxyUrl }}</span>
            </div>

            <div class="card-desc" :title="p.description || '无备注说明'">
              {{ p.description || '无备注说明' }}
            </div>

            <!-- 测速结果显示 -->
            <div class="card-test-bar">
              <template v-if="testStatus[p.proxyUrl]?.loading">
                <NTag size="tiny" type="info" :bordered="false">测速中...</NTag>
              </template>
              <template v-else-if="testStatus[p.proxyUrl]?.success">
                <NTag size="tiny" type="success" :bordered="false">
                  ⚡ 延迟: {{ testStatus[p.proxyUrl]?.latencyMs }} ms
                </NTag>
              </template>
              <template v-else-if="testStatus[p.proxyUrl]?.error">
                <NTooltip trigger="hover">
                  <template #trigger>
                    <NTag size="tiny" type="error" :bordered="false">❌ 连接失败</NTag>
                  </template>
                  {{ testStatus[p.proxyUrl]?.error }}
                </NTooltip>
              </template>
            </div>

            <NDivider style="margin: 8px 0;" />

            <div class="card-footer-actions">
              <NButton
                size="tiny"
                secondary
                type="info"
                :loading="testStatus[p.proxyUrl]?.loading"
                @click="handleTestNode(p.proxyUrl)"
              >
                连通性测试
              </NButton>
              <NSpace size="small">
                <NButton size="tiny" secondary type="primary" @click="openEditModal(p)">
                  编辑
                </NButton>
                <NPopconfirm @positive-click="handleDelete(p)">
                  <template #trigger>
                    <NButton size="tiny" quaternary type="error">
                      删除
                    </NButton>
                  </template>
                  确认删除代理方案「{{ p.name }}」吗？
                </NPopconfirm>
              </NSpace>
            </div>
          </NCard>
        </NGridItem>
      </NGrid>
    </NSpin>

    <!-- 新增 / 编辑弹窗 -->
    <NModal
      v-model:show="showModal"
      preset="card"
      :title="isEditing ? '编辑代理方案' : '新增代理方案'"
      style="max-width: 540px;"
      :mask-closable="false"
    >
      <NForm size="small" label-placement="left" label-width="90">
        <NFormItem label="显示名称" required>
          <NInput v-model:value="formModel.name" placeholder="如：本地 Clash 代理 / 香港 SOCKS5" />
        </NFormItem>

        <NFormItem label="标识 Key" required>
          <NInput v-model:value="formModel.key" placeholder="如：clash-local / hk-socks5" />
        </NFormItem>

        <NFormItem label="代理地址" required>
          <div class="proxy-address-input">
            <NSelect
              v-model:value="proxyScheme"
              :options="proxySchemeOptions"
              class="proxy-scheme-select"
              :consistent-menu-width="false"
            />
            <NInput
              v-model:value="proxyHost"
              placeholder="IP 或域名，如 127.0.0.1"
            />
            <NInputNumber
              v-model:value="proxyPort"
              :min="1"
              :max="65535"
              placeholder="端口"
              style="width: 110px;"
            />
            <NButton secondary size="small" :loading="modalTesting" @click="handleTestInModal">
              测试
            </NButton>
          </div>
          <div class="proxy-url-preview">完整地址：{{ buildProxyUrl() || `${proxyScheme}://IP:端口` }}</div>
        </NFormItem>

        <!-- 弹窗内测速结果提示 -->
        <div v-if="modalTestResult" style="margin: -8px 0 12px 90px;">
          <NAlert v-if="modalTestResult.isSuccess" type="success" :bordered="false" size="small">
            连通正常，延迟 {{ modalTestResult.latencyMs }} ms
          </NAlert>
          <NAlert v-else type="error" :bordered="false" size="small">
            连通失败: {{ modalTestResult.errorMessage || '连接超时' }}
          </NAlert>
        </div>

        <NFormItem label="方案说明">
          <NInput v-model:value="formModel.description" type="textarea" :rows="2" placeholder="备注说明（可选）" />
        </NFormItem>

        <NFormItem label="排序序号">
          <NInputNumber v-model:value="formModel.sortOrder" :min="0" style="width: 100%;" />
        </NFormItem>

        <NFormItem label="是否启用">
          <NSwitch v-model:value="formModel.isEnabled" />
        </NFormItem>
      </NForm>

      <template #footer>
        <div class="modal-footer-actions">
          <NButton @click="showModal = false">取消</NButton>
          <NButton type="primary" :loading="modalSubmitting" @click="handleSave">
            保存方案
          </NButton>
        </div>
      </template>
    </NModal>
  </div>
</template>

<style scoped>
.proxy-profiles-tab {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.profiles-intro-alert {
  border-radius: 8px;
  background: var(--n-color-embedded);
}

.intro-title {
  font-weight: 600;
  font-size: 14px;
}

.toolbar-row {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-top: 4px;
}

.empty-state {
  padding: 48px 0;
}

.proxy-card {
  border-radius: 8px;
  transition: all 0.2s ease;
  display: flex;
  flex-direction: column;
}

.proxy-card:hover {
  transform: translateY(-2px);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.08);
}

.proxy-disabled {
  opacity: 0.65;
  filter: grayscale(0.2);
}

.card-header-bar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 6px;
}

.card-title-group {
  display: flex;
  align-items: center;
  gap: 8px;
  overflow: hidden;
}

.card-name {
  font-weight: 600;
  font-size: 14px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.card-key-tag {
  font-size: 11px;
  color: var(--n-text-color-3);
  margin-bottom: 8px;
}

.card-key-tag code {
  font-family: monospace;
  background: rgba(128, 128, 128, 0.12);
  padding: 1px 4px;
  border-radius: 3px;
}

.card-url-bar {
  background: var(--n-color-embedded);
  padding: 6px 8px;
  border-radius: 4px;
  font-family: monospace;
  font-size: 12px;
  margin-bottom: 8px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  color: var(--n-text-color-2);
}

.card-desc {
  font-size: 12px;
  color: var(--n-text-color-3);
  line-height: 1.4;
  height: 34px;
  overflow: hidden;
  text-overflow: ellipsis;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
}

.card-test-bar {
  height: 22px;
  display: flex;
  align-items: center;
  margin-top: 4px;
}

.card-footer-actions {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.proxy-address-input {
  width: 100%;
  display: flex;
  gap: 8px;
}

.proxy-scheme-select {
  width: 112px;
  flex: 0 0 112px;
}

.proxy-url-preview {
  width: 100%;
  margin-top: 6px;
  color: var(--n-text-color-3);
  font-family: monospace;
  font-size: 11px;
}

.modal-footer-actions {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
}
</style>

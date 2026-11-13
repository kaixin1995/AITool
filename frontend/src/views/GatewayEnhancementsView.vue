<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { NCard, NSpace, NButton, NInput, NAlert, NSwitch, NInputNumber, NSelect, NTabs, NTabPane, NEmpty, NPopconfirm, useMessage } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import { httpGet, httpPost } from '@/api/http'

const message = useMessage()
const config = ref<any>(null)
const sessions = ref<any[]>([]), usage = ref<any[]>([])
const agent = ref('codex'), baseUrl = ref(location.origin), model = ref(''), apiKey = ref(''), status = ref<any>(null), preview = ref('')
const busy = ref(false)
const configurationError = ref('')
const keyOptions = ref<{label: string; value: string}[]>([])
const sessionDetails = ref<any[]>([])
async function inspectSession(digest: string) { sessionDetails.value = await httpGet(`/api/admin/gateway-enhancements/sessions/${digest}`) }
async function load() {
  const health: any = await httpGet('/api/admin/gateway-enhancements/status')
  configurationError.value = health.configurationError ?? ''
  config.value = await httpGet('/api/admin/gateway-enhancements')
  const keys: {id: string; keyName: string}[] = await httpGet('/api/admin/access-keys')
  keyOptions.value = keys.map(k => ({ label: k.keyName, value: k.id }))
  sessions.value = await httpGet('/api/admin/gateway-enhancements/sessions')
  usage.value = await httpGet('/api/admin/gateway-enhancements/budgets')
}
async function save() {
  busy.value = true
  try {
    await httpPost('/api/admin/gateway-enhancements', config.value)
    message.success('已保存'); await load()
  } catch (e) { message.error(e instanceof Error ? e.message : '保存失败') } finally { busy.value = false }
}
async function inspectAgent() { status.value = await httpGet(`/api/admin/agent-configuration/${agent.value}`); preview.value = '' }
function agentRequest() { return { agent: agent.value, baseUrl: baseUrl.value, model: model.value, apiKey: apiKey.value, expectedHash: status.value?.hash ?? '' } }
async function generate() { const result: any = await httpPost('/api/admin/agent-configuration/preview', agentRequest()); preview.value = result.content; message.info(result.note) }
async function changeAgent(action: string) {
  busy.value = true
  try { status.value = await httpPost(`/api/admin/agent-configuration/${action}`, agentRequest()); message.success('配置已更新') } finally { busy.value = false }
}
onMounted(load)
</script>

<template>
  <PageHeader title="网关增强" subtitle="稳定性、会话统计、预算及共享工具" />
  <NCard v-if="config">
    <NAlert v-if="configurationError" type="error" style="margin-bottom: 16px">{{ configurationError }}</NAlert>
    <NTabs type="line">
      <NTabPane name="stability" tab="稳定性">
        <NSpace vertical>
          <NSpace align="center"><NSwitch v-model:value="config.keepAliveEnabled" />流式保活 <NInputNumber v-model:value="config.keepAliveSeconds" :min="5" :max="60" />秒</NSpace>
          <NAlert type="info">仅在已开始输出的 SSE 事件边界发送保活注释。客户端自身的事件超时仍然有效。</NAlert>
          <NSpace align="center"><NSwitch v-model:value="config.loopDetectionEnabled" />检测并终止重复输出</NSpace>
          <NSpace align="center"><NSwitch v-model:value="config.extendedHeadBufferEnabled" />延长 Responses/Chat 热身事件缓冲，保留故障回退机会</NSpace>
          <NSpace align="center"><NSwitch v-model:value="config.sessionMetricsEnabled" />会话与流量统计</NSpace>
          <NAlert>通过 Session-Id / Thread-Id / X-Session-Id 标识会话；子代理可传 X-Parent-Session-Id。只存标识摘要，不保存对话正文。缓存亲和使用五分钟活跃窗口。</NAlert>
        </NSpace>
      </NTabPane>
      <NTabPane name="sessions" tab="会话用量">
        <NButton @click="load">刷新</NButton>
        <NEmpty v-if="sessions.length === 0" description="还没有带会话标识的使用记录，开启会话统计后开始对话即可" />
        <div class="table-wrap"><table><thead><tr><th>会话摘要</th><th>请求/尝试</th><th>输入/缓存/输出</th><th>USD</th><th>上传/下载字节</th><th>站点切换</th></tr></thead>
          <tbody><tr v-for="s in sessions" :key="s.session"><td :title="s.session"><NButton text type="primary" @click="inspectSession(s.session)">{{ s.session.slice(0, 12) }}</NButton></td><td>{{ s.requests }}/{{ s.attempts }}</td><td>{{ s.inputTokens }}/{{ s.cachedTokens }}/{{ s.outputTokens }}</td><td>{{ s.costUsd }} <span v-if="s.unpricedAttempts">（有未定价）</span></td><td>{{ s.requestBytes }}/{{ s.responseBytes }}</td><td>{{ s.switches }}</td></tr></tbody></table></div>
        <div v-if="sessionDetails.length" class="table-wrap"><table><thead><tr><th>时间</th><th>模型</th><th>状态</th><th>上下文 tokens</th><th>缓存占比</th><th>输出</th><th>耗时 ms</th></tr></thead><tbody>
          <tr v-for="(d, index) in sessionDetails" :key="index"><td>{{ new Date(d.requestedAt).toLocaleString() }}</td><td>{{ d.attemptedModel }}</td><td>{{ d.status }} {{ d.errorCategory ?? '' }}</td><td>{{ d.contextTokens }}</td><td>{{ (d.cacheShare * 100).toFixed(1) }}%</td><td>{{ d.outputTokens }}</td><td>{{ d.totalDurationMs }}</td></tr>
        </tbody></table></div>
      </NTabPane>
      <NTabPane name="budgets" tab="密钥预算">
        <NAlert type="info">0 表示不限。按 UTC 周期统计，达到预算后拒绝新请求；正在执行的一次请求可能超出剩余额度。金额限制遇到未定价用量会暂停当前周期，补充价格后从新周期生效，也可改用 token 预算。WebSocket 按轮次结算。</NAlert>
        <NCard v-for="(budget, index) in config.budgets" :key="index" size="small" style="margin-top:12px">
          <NSpace vertical>
            <NSelect v-model:value="budget.keyId" :options="keyOptions" placeholder="访问密钥" />
            <NSelect v-model:value="budget.period" :options="[{ label: '每天', value: 'day' }, { label: '每周（周一重置）', value: 'week' }, { label: '每月', value: 'month' }]" />
            <NSpace align="center">Token 上限<NInputNumber v-model:value="budget.tokenLimit" :min="0" :precision="0" />美元上限<NInputNumber v-model:value="budget.costLimitUsd" :min="0" :precision="4" /><NButton type="error" @click="config.budgets.splice(index, 1)">删除</NButton></NSpace>
            <span v-for="u in usage.filter(u => u.keyId === budget.keyId && u.period === budget.period)" :key="u.keyId">当前已用 {{ u.usedTokens }} tokens / ${{ u.usedCostUsd }} {{ u.hasUnpricedUsage ? '（含未定价记录）' : '' }}</span>
          </NSpace>
        </NCard>
        <NButton style="margin-top:12px" @click="config.budgets.push({keyId: keyOptions[0]?.value ?? '', period: 'day', tokenLimit: 0, costLimitUsd: 0})">添加预算</NButton>
      </NTabPane>
      <NTabPane name="tools" tab="搜索与识图">
        <NSpace vertical>
          <NAlert type="info">搜索使用支持 SearXNG JSON 格式的服务。调用 POST /v1/tools/search（query）；识图调用 POST /v1/tools/describe-image（imageUrl、query），携带访问密钥。图片会发送到你选择的识图路由。</NAlert>
          <NInput v-model:value="config.searchEndpoint" placeholder="搜索服务地址，例如 https://search.example/search" />
          <NInput v-model:value="config.searchApiKey" type="password" show-password-on="click" placeholder="搜索服务 Bearer 密钥（可选）" />
          <NInput v-model:value="config.visionRoute" placeholder="识图模型的路由入口名" />
          <NSelect v-model:value="config.toolAllowedKeyIds" multiple :options="keyOptions" placeholder="选择允许使用搜索与识图的访问密钥" />
        </NSpace>
      </NTabPane>
      <NTabPane name="mcp" tab="共享 MCP">
        <NAlert type="info">Agent 连接 /mcp/aitool，使用访问密钥认证。每个服务须显式填写 allowedKeyIds。当前支持 HTTP MCP 的工具列表与调用，工具名加服务名前缀。</NAlert>
        <NCard v-for="(server, index) in config.mcpServers" :key="index" size="small" style="margin-top:12px">
          <NSpace vertical>
            <NSpace align="center"><NSwitch v-model:value="server.enabled" />启用<NButton type="error" @click="config.mcpServers.splice(index, 1)">删除</NButton></NSpace>
            <NInput v-model:value="server.name" placeholder="服务名（字母、数字或单下划线）" />
            <NInput v-model:value="server.url" placeholder="HTTP MCP 地址" />
            <NInput v-model:value="server.apiKey" type="password" show-password-on="click" placeholder="服务 Bearer 密钥（可选）" />
            <NSelect v-model:value="server.allowedKeyIds" multiple :options="keyOptions" placeholder="允许访问的密钥（留空则全部禁止）" />
          </NSpace>
        </NCard>
        <NButton style="margin-top:12px" @click="config.mcpServers.push({name:'',url:'',apiKey:'',enabled:true,allowedKeyIds:[]})">添加 MCP 服务</NButton>
      </NTabPane>
      <NTabPane name="agents" tab="客户端配置">
        <NSpace vertical>
          <NAlert>配置写入发生在 AI-Tool 服务所在主机，仅本机连接可写入。先预览再应用；发现外部改动时停止自动覆盖。Codex 使用环境变量 AITOOL_API_KEY。WSL 使用导出的预览手动配置。</NAlert>
          <NSpace><NButton @click="agent = 'codex'; inspectAgent()">Codex</NButton><NButton @click="agent = 'claude'; inspectAgent()">Claude Code</NButton></NSpace>
          <NInput v-model:value="baseUrl" placeholder="网关根地址" /><NInput v-model:value="model" placeholder="模型路由名" /><NInput v-model:value="apiKey" type="password" placeholder="Claude 配置使用的访问密钥" />
          <pre v-if="status">{{ JSON.stringify(status, null, 2) }}</pre>
          <NButton :disabled="!status" @click="generate">生成预览</NButton>
          <NInput v-if="preview" :value="preview" type="textarea" readonly :autosize="{ minRows: 10 }" />
          <NSpace><NButton :disabled="!preview || status?.drifted" :loading="busy" @click="changeAgent('apply')">应用到本机配置</NButton><NPopconfirm @positive-click="changeAgent('restore')"><template #trigger><NButton :disabled="!status?.canRestore || status?.drifted">恢复接入前配置</NButton></template>将恢复为首次接入前的备份，确认继续？</NPopconfirm></NSpace>
        </NSpace>
      </NTabPane>
    </NTabs>
    <NSpace style="margin-top: 20px"><NButton type="primary" :loading="busy" @click="save">保存网关增强配置</NButton></NSpace>
  </NCard>
</template>

<style scoped>
.table-wrap { overflow-x: auto; margin-top: 16px; } table { width: 100%; border-collapse: collapse; } td, th { text-align: left; padding: 10px; border-bottom: 1px solid #8883; } pre { white-space: pre-wrap; overflow-wrap: anywhere; }
</style>

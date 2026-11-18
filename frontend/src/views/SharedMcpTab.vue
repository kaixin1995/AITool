<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { NAlert, NButton, NCard, NEmpty, NForm, NFormItem, NInput, NPopconfirm, NSelect, NSpace, NSpin, NSwitch, useMessage } from 'naive-ui'
import { gatewayError, loadDeveloperGatewaySettings, saveDeveloperGatewayFields, type DeveloperGatewaySettings, type SharedMcpServer } from './developerGatewaySettings'

const message = useMessage()
const loading = ref(false), saving = ref(false), error = ref('')
const original = ref<DeveloperGatewaySettings | null>(null)
const servers = ref<SharedMcpServer[]>([])
const keyOptions = ref<{ label: string; value: string }[]>([])
async function load() {
  loading.value = true
  error.value = ''
  try {
    const result = await loadDeveloperGatewaySettings()
    original.value = result.settings
    keyOptions.value = result.keyOptions
    servers.value = structuredClone(result.settings.mcpServers)
  } catch (e) { error.value = gatewayError(e) } finally { loading.value = false }
}
async function save() {
  if (!original.value) return
  saving.value = true
  error.value = ''
  try {
    const snapshot = servers.value.map(server => ({ ...server, name: server.name.trim(), url: server.url.trim(), allowedKeyIds: [...server.allowedKeyIds] }))
    original.value = await saveDeveloperGatewayFields(original.value, { mcpServers: snapshot })
    servers.value = original.value.mcpServers.map(server => ({ ...server, allowedKeyIds: [...server.allowedKeyIds] }))
    message.success('共享 MCP 配置已保存')
  } catch (e) { error.value = gatewayError(e) } finally { saving.value = false }
}
function add() { servers.value.push({ name: '', url: '', apiKey: '', enabled: true, allowedKeyIds: [] }) }
onMounted(load)
</script>

<template>
  <NSpace vertical :size="16">
    <NAlert type="info">客户端连接 /mcp/aitool，使用 AI-Tool 访问密钥认证。这里集中管理远端 HTTP MCP 的工具列表与调用；保存后仍需在客户端配置该 MCP 连接。</NAlert>
    <NAlert v-if="error" type="error">{{ error }}</NAlert>
    <NSpin :show="loading">
      <NSpace v-if="original" vertical :size="16">
        <NEmpty v-if="servers.length === 0" description="尚未添加共享 MCP 服务" />
        <NCard v-for="(server, index) in servers" :key="index" size="small" :title="server.name || `MCP 服务 ${index + 1}`">
          <template #header-extra>
            <NPopconfirm @positive-click="servers.splice(index, 1)">
              <template #trigger><NButton size="small" type="error" secondary :disabled="saving">删除</NButton></template>
              删除此服务后，保存配置才会生效。
            </NPopconfirm>
          </template>
          <NForm label-placement="top" :disabled="loading || saving">
            <NFormItem label="启用服务"><NSwitch v-model:value="server.enabled" /></NFormItem>
            <NFormItem label="服务名"><NInput v-model:value="server.name" placeholder="字母、数字或单下划线，例如 docs；工具名将加 docs__ 前缀" /></NFormItem>
            <NFormItem label="HTTP MCP 地址"><NInput v-model:value="server.url" placeholder="https://mcp.example/mcp" /></NFormItem>
            <NFormItem label="服务密钥（可选）"><NInput v-model:value="server.apiKey" type="password" show-password-on="click" placeholder="Bearer 密钥；******** 表示保留，修改服务名后需重新填写" /></NFormItem>
            <NFormItem label="允许访问的密钥"><NSelect v-model:value="server.allowedKeyIds" multiple filterable :options="keyOptions" placeholder="留空则全部禁止" /></NFormItem>
          </NForm>
        </NCard>
      </NSpace>
    </NSpin>
    <NSpace>
      <NButton :disabled="!original || loading || saving || servers.length >= 32" @click="add">添加 MCP 服务</NButton>
      <NButton type="primary" :disabled="!original || loading" :loading="saving" @click="save">保存共享 MCP 配置</NButton>
      <NButton :disabled="loading || saving" @click="load">重新加载</NButton>
    </NSpace>
  </NSpace>
</template>

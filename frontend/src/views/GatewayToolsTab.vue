<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { NAlert, NButton, NForm, NFormItem, NInput, NSelect, NSpace, NSpin, useMessage } from 'naive-ui'
import { gatewayError, loadDeveloperGatewaySettings, saveDeveloperGatewayFields, type DeveloperGatewaySettings } from './developerGatewaySettings'

const message = useMessage()
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const original = ref<DeveloperGatewaySettings | null>(null)
const keyOptions = ref<{ label: string; value: string }[]>([])
const searchEndpoint = ref(''), searchApiKey = ref(''), visionRoute = ref('')
const allowedKeys = ref<string[]>([])

async function load() {
  loading.value = true
  error.value = ''
  try {
    const result = await loadDeveloperGatewaySettings()
    original.value = result.settings
    keyOptions.value = result.keyOptions
    searchEndpoint.value = result.settings.searchEndpoint
    searchApiKey.value = result.settings.searchApiKey
    visionRoute.value = result.settings.visionRoute
    allowedKeys.value = [...result.settings.toolAllowedKeyIds]
  } catch (e) { error.value = gatewayError(e) } finally { loading.value = false }
}
async function save() {
  if (!original.value) return
  saving.value = true
  error.value = ''
  try {
    original.value = await saveDeveloperGatewayFields(original.value, {
      searchEndpoint: searchEndpoint.value.trim(), searchApiKey: searchApiKey.value,
      visionRoute: visionRoute.value.trim(), toolAllowedKeyIds: [...allowedKeys.value]
    })
    searchEndpoint.value = original.value.searchEndpoint
    searchApiKey.value = original.value.searchApiKey
    visionRoute.value = original.value.visionRoute
    allowedKeys.value = [...original.value.toolAllowedKeyIds]
    message.success('搜索与识图配置已保存')
  } catch (e) { error.value = gatewayError(e) } finally { saving.value = false }
}
onMounted(load)
</script>

<template>
  <NSpace vertical :size="16">
    <NAlert type="info">为客户端提供共享搜索和辅助识图工具。保存后，客户端还需连接 /mcp/aitool 并携带允许的访问密钥，或直接调用工具接口。</NAlert>
    <NAlert v-if="error" type="error">{{ error }}</NAlert>
    <NSpin :show="loading">
      <NForm v-if="original" label-placement="top" :disabled="loading || saving">
        <NFormItem label="搜索服务地址"><NInput v-model:value="searchEndpoint" placeholder="支持 SearXNG JSON 的地址，例如 https://search.example/search" /></NFormItem>
        <NFormItem label="搜索服务密钥（可选）"><NInput v-model:value="searchApiKey" type="password" show-password-on="click" placeholder="Bearer 密钥；******** 表示保留已保存密钥，清空可删除" /></NFormItem>
        <NFormItem label="识图模型路由"><NInput v-model:value="visionRoute" placeholder="已配置且支持图片的模型路由入口名" /></NFormItem>
        <NFormItem label="允许使用工具的访问密钥"><NSelect v-model:value="allowedKeys" multiple filterable :options="keyOptions" placeholder="留空则全部禁止" /></NFormItem>
      </NForm>
    </NSpin>
    <NAlert>搜索接口：POST /v1/tools/search（query）；识图接口：POST /v1/tools/describe-image（imageUrl、query）。识图会将图片发送到所选路由，并产生模型用量。</NAlert>
    <NSpace>
      <NButton type="primary" :disabled="!original || loading" :loading="saving" @click="save">保存搜索与识图配置</NButton>
      <NButton :disabled="loading || saving" @click="load">重新加载</NButton>
    </NSpace>
  </NSpace>
</template>

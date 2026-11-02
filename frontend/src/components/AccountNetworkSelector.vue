<script setup lang="ts">
import { computed } from 'vue'
import { NAlert, NSelect, NSpace } from 'naive-ui'
import { accountProxySelectOptions, type AccountNetworkSelection, type AccountProxyOption, type NetworkMode } from '@/api/accountNetwork'
const props = defineProps<{
  modelValue?: AccountNetworkSelection
  options: AccountProxyOption[]
  disabled?: boolean
  loading?: boolean
  blockReason?: string | null
}>()
const emit = defineEmits<{ 'update:modelValue': [value: AccountNetworkSelection] }>()
const modes = [{ label: '兼容旧行为 (Legacy)', value: 0 }, { label: '直连 (Direct)', value: 1 }, { label: '代理方案 (ProxyProfile)', value: 2 }]
const profileOptions = computed(() => accountProxySelectOptions(props.options, props.modelValue?.proxyProfileId))
function setMode(mode: NetworkMode) {
  emit('update:modelValue', { mode, proxyProfileId: mode === 2 ? props.modelValue?.proxyProfileId ?? null : null })
}
function setProfile(id: string) {
  if (props.options.some(p => p.id === id && p.isEnabled)) emit('update:modelValue', { mode: 2, proxyProfileId: id })
}
</script>

<template>
  <NSpace vertical>
    <strong>账户网络出口</strong>
    <NSelect :value="modelValue?.mode ?? null" :options="modes" :disabled="disabled || loading" :loading="loading" placeholder="未加载，不修改现有策略" @update:value="setMode" />
    <NSelect v-if="modelValue?.mode === 2" :value="modelValue.proxyProfileId ?? null" :options="profileOptions" :disabled="disabled || loading" placeholder="选择已启用的代理方案" @update:value="setProfile" />
    <NAlert v-if="blockReason" type="error" :show-icon="false">当前网络已阻断：{{ blockReason }}。不会回退为直连。</NAlert>
    <small>兼容旧行为保留原有网络路径；直连不使用代理；代理方案失效时阻断请求。</small>
  </NSpace>
</template>

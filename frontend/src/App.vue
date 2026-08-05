<script setup lang="ts">
import { defineComponent } from 'vue'
import {
  NConfigProvider,
  NMessageProvider,
  NDialogProvider,
  NLoadingBarProvider,
  zhCN,
  dateZhCN,
  useMessage
} from 'naive-ui'
import { useTheme } from '@/composables/useTheme'
import { setMessageHandler } from '@/api/http'

// 消息注入桥：把 naive-ui 的 useMessage 注入到 http.ts 的全局处理器。
// 必须作为 NMessageProvider 的子组件存在，才能调用 useMessage。
const MessageBridge = defineComponent({
  name: 'MessageBridge',
  setup() {
    const message = useMessage()
    setMessageHandler((type, content) => {
      if (type === 'success') message.success(content)
      else if (type === 'error') message.error(content)
      else message.warning(content)
    })
    return () => null
  }
})

const { naiveTheme, themeOverrides } = useTheme()
</script>

<template>
  <NConfigProvider
    :theme="naiveTheme"
    :theme-overrides="themeOverrides"
    :locale="zhCN"
    :date-locale="dateZhCN"
  >
    <NLoadingBarProvider>
      <NDialogProvider>
        <NMessageProvider>
          <MessageBridge />
          <RouterView />
        </NMessageProvider>
      </NDialogProvider>
    </NLoadingBarProvider>
  </NConfigProvider>
</template>

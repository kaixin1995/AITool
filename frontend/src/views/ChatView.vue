<script setup lang="ts">
import { computed, ref } from 'vue'
import { NTabs, NTabPane } from 'naive-ui'
import { useAuthStore } from '@/stores/auth'
import ChatTestPane from './ChatTestPane.vue'
import ConversationsView from './ConversationsView.vue'

const auth = useAuthStore()
// 对话记录页签仅在开启对话记录功能时显示（与原 Razor Pages 逻辑一致）。
const conversationLogEnabled = computed(() => auth.status?.features?.conversationLogEnabled === true)
</script>

<template>
  <div class="page-container" style="height: calc(100vh - 88px); display: flex; flex-direction: column">
    <NTabs type="line" animated size="large" style="flex: 1; display: flex; flex-direction: column">
      <NTabPane name="chat" tab="对话测试" style="flex: 1; display: flex; flex-direction: column">
        <ChatTestPane />
      </NTabPane>
      <NTabPane v-if="conversationLogEnabled" name="conversations" tab="对话记录" style="flex: 1; display: flex; flex-direction: column">
        <ConversationsView />
      </NTabPane>
    </NTabs>
  </div>
</template>

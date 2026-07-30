<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { NTabs, NTabPane } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import { useAuthStore } from '@/stores/auth'
import ChatTestPane from './ChatTestPane.vue'
import ConversationsView from './ConversationsView.vue'

const auth = useAuthStore()
const activeTab = ref('chat')
// 对话记录页签仅在开启对话记录功能时显示（与原 Razor Pages 逻辑一致）。
const conversationLogEnabled = computed(() => auth.status?.features?.conversationLogEnabled === true)

onMounted(() => {
  if (window.location.hash === '#conversationLogPane' && conversationLogEnabled.value) activeTab.value = 'conversations'
  else if (window.location.hash === '#chatTestPane') activeTab.value = 'chat'
})

watch(activeTab, (value) => {
  const hash = value === 'conversations' ? '#conversationLogPane' : '#chatTestPane'
  if (window.location.hash !== hash) history.replaceState(null, '', hash)
})
</script>

<template>
  <div class="page-container chat-page">
    <PageHeader title="对话" subtitle="对话测试与对话记录" />
    <NTabs v-model:value="activeTab" class="chat-tabs" type="line" animated size="large">
      <NTabPane name="chat" tab="对话测试" display-directive="show" class="chat-tab-pane">
        <ChatTestPane />
      </NTabPane>
      <NTabPane v-if="conversationLogEnabled" name="conversations" tab="对话记录" display-directive="show" class="chat-tab-pane">
        <ConversationsView />
      </NTabPane>
    </NTabs>
  </div>
</template>

<style scoped>
.chat-page {
  display: flex;
  flex-direction: column;
  height: calc(100vh - 112px);
  min-width: 0;
  min-height: 0;
  overflow: hidden;
}

.chat-tabs {
  display: flex;
  flex: 1;
  min-height: 0;
  flex-direction: column;
}

.chat-tabs :deep(.n-tabs-pane-wrapper),
.chat-tabs :deep(.n-tab-pane) {
  flex: 1;
  min-height: 0;
}

.chat-tab-pane {
  height: 100%;
  min-height: 0;
}

@media (max-width: 1200px) {
  .chat-page {
    height: auto;
    min-height: calc(100vh - 112px);
    overflow: visible;
  }
}
</style>

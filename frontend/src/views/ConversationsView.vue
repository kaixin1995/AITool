<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { NCard, NSpace, NSelect, NInput, NButton, NList, NListItem, NThing, NTag, NEmpty, NSpin, NPopconfirm, useMessage } from 'naive-ui'
import * as api from '@/api/conversations'
import type { ConversationSession, ConversationTurn } from '@/api/conversations'

const message = useMessage()
const loading = ref(false)
const sessions = ref<ConversationSession[]>([])
const selectedGroupKey = ref<string | null>(null)
const turns = ref<ConversationTurn[]>([])
const turnsLoading = ref(false)

const sourceTool = ref<string | null>(null)
const keyword = ref('')
const page = ref(1)

const sourceOptions = [
  { label: '全部', value: null }, { label: 'claude-code', value: 'claude-code' },
  { label: 'codex', value: 'codex' }, { label: 'proxy', value: 'proxy' }
]

async function loadSessions(): Promise<void> {
  loading.value = true
  try {
    const params: Record<string, unknown> = { page: page.value, pageSize: 30 }
    if (sourceTool.value) params.sourceTool = sourceTool.value
    if (keyword.value) params.keyword = keyword.value
    const resp = await api.listSessions(params)
    sessions.value = resp.items ?? []
  } finally { loading.value = false }
}

async function loadTurns(groupKey: string): Promise<void> {
  selectedGroupKey.value = groupKey
  turnsLoading.value = true
  try {
    turns.value = await api.getTurns(groupKey)
  } finally { turnsLoading.value = false }
}

async function handleDelete(groupKey: string): Promise<void> {
  await api.deleteSession(groupKey)
  message.success('已删除会话')
  if (selectedGroupKey.value === groupKey) { selectedGroupKey.value = null; turns.value = [] }
  await loadSessions()
}

onMounted(loadSessions)
</script>

<template>
  <div class="page-container">
    <div style="display: grid; grid-template-columns: 360px 1fr; gap: 16px; height: calc(100vh - 120px)">
      <!-- 会话列表 -->
      <NCard size="small">
        <template #header>
          <NSpace vertical :size="8">
            <span>对话记录</span>
            <NSpace :size="8">
              <NSelect v-model:value="sourceTool" :options="sourceOptions" placeholder="来源" size="small" style="width: 130px" />
              <NInput v-model:value="keyword" placeholder="关键词" size="small" style="width: 130px" @keyup.enter="loadSessions" />
              <NButton size="small" type="primary" @click="loadSessions">搜索</NButton>
            </NSpace>
          </NSpace>
        </template>
        <NSpin :show="loading">
          <NEmpty v-if="sessions.length === 0" description="暂无会话" />
          <NList hoverable clickable>
            <NListItem v-for="s in sessions" :key="s.groupKey" @click="loadTurns(s.groupKey)">
              <NThing>
                <template #header>
                  <NSpace :size="6" align="center">
                    <NTag v-if="s.sourceTool" size="tiny" :bordered="false">{{ s.sourceTool }}</NTag>
                    <span>{{ s.title || s.requestModel }}</span>
                  </NSpace>
                </template>
                <template #description>
                  <span style="font-size: 12px; color: #888">{{ new Date(s.lastCreatedAt).toLocaleString('zh-CN') }} · {{ s.turnCount }} 轮</span>
                </template>
              </NThing>
              <template #suffix>
                <NPopconfirm @positive-click="handleDelete(s.groupKey)">
                  <template #trigger><NButton size="tiny" quaternary type="error">删除</NButton></template>
                  删除整个会话？
                </NPopconfirm>
              </template>
            </NListItem>
          </NList>
        </NSpin>
      </NCard>

      <!-- 轮次详情 -->
      <NCard size="small">
        <template #header>对话详情</template>
        <NSpin :show="turnsLoading">
          <NEmpty v-if="!selectedGroupKey" description="选择左侧会话查看详情" />
          <NEmpty v-else-if="turns.length === 0" description="该会话无轮次记录" />
          <div v-else class="turns-list">
            <div v-for="turn in turns" :key="turn.id" class="turn-item">
              <div class="turn-user">
                <strong>用户：</strong>
                <div class="turn-content">{{ turn.userInputText || '(空)' }}</div>
              </div>
              <div class="turn-assistant">
                <strong>AI：</strong>
                <div class="turn-content markdown-body">{{ turn.assistantOutputMarkdown || '(空)' }}</div>
              </div>
              <div class="turn-meta">
                <NTag size="tiny" :bordered="false">{{ new Date(turn.createdAt).toLocaleString('zh-CN') }}</NTag>
                <NTag size="tiny" :bordered="false">输入 {{ turn.inputTokens }} / 输出 {{ turn.outputTokens }}</NTag>
              </div>
            </div>
          </div>
        </NSpin>
      </NCard>
    </div>
  </div>
</template>

<style scoped>
.turns-list { padding: 0 8px; max-height: calc(100vh - 200px); overflow-y: auto; }
.turn-item { padding: 12px 0; border-bottom: 1px solid var(--n-border-color); }
.turn-content { margin-top: 4px; white-space: pre-wrap; word-break: break-word; }
.turn-assistant { margin-top: 12px; }
.turn-meta { margin-top: 8px; display: flex; gap: 8px; flex-wrap: wrap; }
</style>

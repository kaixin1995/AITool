<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { NButton, NCard, NEmpty, NGrid, NGi, NSpin } from 'naive-ui'
import { getDashboardStats, type DashboardStats } from '@/api/dashboard'

const router = useRouter()
const stats = ref<DashboardStats | null>(null)
const loading = ref(true)
const error = ref('')

const statCards = computed(() => {
  const s = stats.value
  return [
    { label: '启用站点', value: s?.siteCount ?? 0, icon: '🌐', tone: 'primary', route: 'sites' },
    { label: '模型总数', value: s?.modelCount ?? 0, icon: '🧠', tone: 'success', route: 'models' },
    { label: '路由规则', value: s?.routeCount ?? 0, icon: '🔀', tone: 'warning', route: 'routes' },
    { label: '启用密钥', value: s?.accessKeyCount ?? 0, icon: '🔑', tone: 'danger', route: 'access-keys' },
    { label: '启用检测任务', value: s?.detectionTaskCount ?? 0, icon: '⏰', tone: 'purple', route: 'detection-tasks' }
  ]
})

const statusCards = computed(() => [
  {
    label: 'Core 连接状态',
    value: stats.value?.coreStatusText || '加载中',
    detail: stats.value?.coreBaseUrl || '-',
    icon: '⚙️',
    tone: 'info'
  },
  {
    label: '最近同步状态',
    value: stats.value?.coreSyncStatusText || '加载中',
    detail: stats.value?.coreSyncDetailText || '-',
    icon: '🔄',
    tone: 'warning'
  }
])

const quickActions = [
  { label: '新增站点', icon: '＋', tone: 'primary', route: 'sites' },
  { label: '新增模型', icon: '＋', tone: 'success', route: 'models' },
  { label: '配置路由', icon: '🔀', tone: 'warning', route: 'routes' },
  { label: '查看日志', icon: '📋', tone: 'info', route: 'usage-logs' }
]

async function loadStats(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    stats.value = await getDashboardStats()
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    loading.value = false
  }
}

function go(routeName: string): void {
  void router.push({ name: routeName })
}

onMounted(loadStats)
</script>

<template>
  <div class="page-container dashboard-page">
    <h2 class="dashboard-sr-title">仪表盘</h2>
    <div class="welcome-banner">
      <h1>欢迎使用 AI Tool 管理平台</h1>
      <p>统一管理站点、模型、代理路由与密钥</p>
      <NButton size="small" tertiary @click="loadStats">刷新数据</NButton>
    </div>

    <NSpin :show="loading">
      <template v-if="stats">
        <NGrid :cols="5" :x-gap="16" :y-gap="16" responsive="screen" item-responsive class="dashboard-stat-grid">
          <NGi v-for="card in statCards" :key="card.label" span="5 s:1 m:1 l:1">
            <button class="stat-card" type="button" @click="go(card.route)">
              <span class="stat-card-icon" :class="card.tone">{{ card.icon }}</span>
              <span class="stat-card-value">{{ card.value }}</span>
              <span class="stat-card-label">{{ card.label }}</span>
            </button>
          </NGi>
        </NGrid>

        <NGrid :cols="2" :x-gap="16" :y-gap="16" responsive="screen" item-responsive class="dashboard-status-grid">
          <NGi v-for="card in statusCards" :key="card.label" span="2 m:1">
            <NCard class="status-card">
              <div class="status-card-content">
                <span class="stat-card-icon" :class="card.tone">{{ card.icon }}</span>
                <div class="status-card-main">
                  <div class="status-card-value">{{ card.value }}</div>
                  <div class="status-card-label">{{ card.label }}</div>
                  <div class="status-card-detail">{{ card.detail }}</div>
                </div>
              </div>
            </NCard>
          </NGi>
        </NGrid>

        <NCard class="quick-actions" title="快捷操作">
          <div class="quick-action-grid">
            <button v-for="action in quickActions" :key="action.label" class="quick-action-link" type="button" @click="go(action.route)">
              <span class="quick-action-icon" :class="action.tone">{{ action.icon }}</span>
              <span>{{ action.label }}</span>
            </button>
          </div>
        </NCard>
      </template>
      <NEmpty v-else-if="!loading" :description="error || '暂无数据'" />
    </NSpin>
  </div>
</template>

<style scoped>
.dashboard-page {
  min-width: 0;
}

.dashboard-sr-title {
  position: absolute;
  width: 1px;
  height: 1px;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
}

.welcome-banner {
  position: relative;
  margin-bottom: 24px;
  padding: 30px 32px;
  border-radius: 16px;
  color: white;
  background: linear-gradient(135deg, #6C9EFF 0%, #A5B4FC 100%);
  overflow: hidden;
}

.welcome-banner h1 {
  margin: 0;
  font-size: 28px;
  font-weight: 700;
  line-height: 1.25;
}

.welcome-banner p {
  margin: 8px 0 18px;
  font-size: 14px;
  opacity: 0.9;
}

.dashboard-stat-grid,
.dashboard-status-grid {
  margin-bottom: 24px;
}

.stat-card {
  display: flex;
  width: 100%;
  min-height: 150px;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 10px;
  border: 1px solid var(--border-color-global);
  border-radius: 12px;
  background: var(--bg-card);
  color: inherit;
  cursor: pointer;
  text-decoration: none;
  transition: transform 0.15s ease, box-shadow 0.15s ease;
}

.stat-card:hover {
  transform: translateY(-2px);
  box-shadow: 0 8px 22px rgba(15, 23, 42, 0.08);
}

.stat-card-icon,
.quick-action-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 12px;
  font-weight: 700;
}

.stat-card-icon {
  width: 48px;
  height: 48px;
  font-size: 24px;
}

.stat-card-icon.primary,
.quick-action-icon.primary { background: #EEF4FF; color: #6C9EFF; }
.stat-card-icon.success,
.quick-action-icon.success { background: #E8F8EF; color: #18A058; }
.stat-card-icon.warning,
.quick-action-icon.warning { background: #FFF7E6; color: #F0A020; }
.stat-card-icon.danger,
.quick-action-icon.danger { background: #FFF0F0; color: #D03050; }
.stat-card-icon.purple,
.quick-action-icon.purple { background: #F3E8FF; color: #8B5CF6; }
.stat-card-icon.info,
.quick-action-icon.info { background: #E6F7FF; color: #2080F0; }

.stat-card-value {
  color: var(--text-primary);
  font-size: 28px;
  font-weight: 700;
  line-height: 1;
}

.stat-card-label,
.status-card-label {
  color: var(--text-color-secondary);
  font-size: 13px;
}

.status-card {
  height: 100%;
}

.status-card-content {
  display: flex;
  align-items: flex-start;
  gap: 14px;
}

.status-card-main {
  min-width: 0;
}

.status-card-value {
  margin-bottom: 4px;
  color: var(--text-primary);
  font-size: 16px;
  font-weight: 700;
}

.status-card-detail {
  margin-top: 8px;
  color: var(--text-color-secondary);
  font-size: 12px;
  line-height: 1.45;
  word-break: break-all;
}

.quick-actions :deep(.n-card-header) {
  font-size: 14px;
  font-weight: 600;
}

.quick-action-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 10px;
}

.quick-action-link {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
  padding: 12px;
  border: 1px solid var(--border-color-global);
  border-radius: 10px;
  background: var(--bg-card);
  color: var(--text-primary);
  cursor: pointer;
  font-size: 14px;
  text-align: left;
}

.quick-action-link:hover {
  background: rgba(108, 158, 255, 0.06);
}

.quick-action-icon {
  width: 28px;
  height: 28px;
  flex-shrink: 0;
}

@media (max-width: 768px) {
  .welcome-banner {
    padding: 24px;
  }

  .welcome-banner h1 {
    font-size: 24px;
  }

  .quick-action-grid {
    grid-template-columns: 1fr;
  }
}
</style>

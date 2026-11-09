<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { NCard, NEmpty, NSpin, NTag } from 'naive-ui'
import { getDashboardStats, type DashboardStats } from '@/api/dashboard'
import { useTheme } from '@/composables/useTheme'

const router = useRouter()
const { skin } = useTheme()
const stats = ref<DashboardStats | null>(null)
const loading = ref(true)
const error = ref('')

interface StatCard {
  label: string
  desc: string
  value: number
  icon: string
  tone: string
  route: string
  query?: Record<string, string>
  hash?: string
}

const statCards = computed<StatCard[]>(() => {
  const s = stats.value
  return [
    { label: '启用站点', desc: '已接入 AI 服务商', value: s?.siteCount ?? 0, icon: '🌐', tone: 'primary', route: 'sites' },
    { label: '模型总数', desc: '已归一化模型库', value: s?.modelCount ?? 0, icon: '🧠', tone: 'success', route: 'models' },
    { label: '路由规则', desc: '多级故障转移调度', value: s?.routeCount ?? 0, icon: '🔀', tone: 'warning', route: 'routes' },
    { label: '启用密钥', desc: '对外 API Key 凭证', value: s?.accessKeyCount ?? 0, icon: '🔑', tone: 'danger', route: 'access-keys' },
    { label: '检测任务', desc: '定时健康巡检监控', value: s?.detectionTaskCount ?? 0, icon: '⏰', tone: 'purple', route: 'detection', hash: '#tasks' }
  ]
})

interface QuickAction {
  label: string
  desc: string
  icon: string
  tone: string
  route: string
  query?: Record<string, string>
  hash?: string
}

const quickActions: QuickAction[] = [
  { label: '新增站点', desc: '接入 OpenAI / Anthropic 等 API', icon: '＋', tone: 'primary', route: 'sites', query: { action: 'create' } },
  { label: '新增模型', desc: '注册新模型并绑定规则', icon: '＋', tone: 'success', route: 'models', query: { action: 'create' } },
  { label: '配置路由', desc: '调整主备调度与优先级', icon: '🔀', tone: 'warning', route: 'routes' },
  { label: '查看日志', desc: 'Token 消耗与首字延迟', icon: '📋', tone: 'info', route: 'usage-logs' }
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

function go(routeName: string, query?: Record<string, string>, hash?: string): void {
  void router.push({ name: routeName, query, hash })
}

onMounted(loadStats)
</script>

<template>
  <div class="page-container dashboard-page">
    <h2 class="dashboard-sr-title">仪表盘</h2>

    <!-- 1. 经典皮肤布局 (Classic Layout) -->
    <template v-if="skin === 'classic'">
      <div class="welcome-banner">
        <h1>欢迎使用 AI Tool 管理平台</h1>
        <p>统一管理站点、模型、代理路由与密钥</p>
      </div>

      <NSpin :show="loading">
        <template v-if="stats">
          <div class="dashboard-stat-grid">
            <button v-for="card in statCards" :key="card.label" class="stat-card" type="button" @click="go(card.route, card.query, card.hash)">
              <span class="stat-card-icon" :class="card.tone">{{ card.icon }}</span>
              <span class="stat-card-value">{{ card.value }}</span>
              <span class="stat-card-label">{{ card.label }}</span>
            </button>
          </div>

          <NCard class="quick-actions" title="快捷操作">
            <div class="quick-action-grid">
              <button v-for="action in quickActions" :key="action.label" class="quick-action-link" type="button" @click="go(action.route, action.query, action.hash)">
                <span class="quick-action-icon" :class="action.tone">{{ action.icon }}</span>
                <span>{{ action.label }}</span>
              </button>
            </div>
          </NCard>
        </template>
        <NEmpty v-else-if="!loading" :description="error || '暂无数据'" />
      </NSpin>
    </template>

    <!-- CPA 风格工作台 -->
    <template v-else-if="skin === 'cpa'">
      <div class="cpa-dashboard-heading">
        <div><span class="cpa-eyebrow">AI TOOL / WORKSPACE</span><h1>仪表盘</h1><p>查看资源配置与核心服务状态</p></div>
        <button class="cpa-heading-action" type="button" @click="go('chat')">开始对话 <span>↗</span></button>
      </div>
      <NSpin :show="loading">
        <div v-if="stats" class="cpa-dashboard">
          <div class="cpa-section-heading"><div><h2>资源概况</h2><p>当前已配置的资源</p></div><span>OVERVIEW</span></div>
          <div class="cpa-stat-grid">
            <button v-for="(card, index) in statCards" :key="card.label" class="cpa-stat" type="button" @click="go(card.route, card.query, card.hash)">
              <span class="cpa-stat-top"><span class="cpa-stat-index">{{ String(index + 1).padStart(2, '0') }} / {{ card.label }}</span><span aria-hidden="true">↗</span></span>
              <strong>{{ card.value }}</strong><span class="cpa-stat-desc">{{ card.desc }}</span>
            </button>
          </div>
          <div class="cpa-panel-grid">
            <section class="cpa-panel">
              <div class="cpa-section-heading"><div><h2>核心服务</h2><p>网关连接与同步信息</p></div><span>CORE</span></div>
              <dl class="cpa-core-list">
                <div><dt>服务地址</dt><dd>{{ stats.coreBaseUrl || '未配置' }}</dd></div>
                <div><dt>服务状态</dt><dd>{{ stats.coreStatusText || '暂无信息' }}</dd></div>
                <div><dt>同步状态</dt><dd>{{ stats.coreSyncStatusText || '暂无信息' }}</dd></div>
                <div><dt>同步详情</dt><dd>{{ stats.coreSyncDetailText || '暂无信息' }}</dd></div>
              </dl>
            </section>
            <section class="cpa-panel">
              <div class="cpa-section-heading"><div><h2>快捷操作</h2><p>继续配置和管理你的服务</p></div><span>QUICK ACCESS</span></div>
              <div class="cpa-action-grid">
                <button v-for="action in quickActions" :key="action.label" class="cpa-action" type="button" @click="go(action.route, action.query, action.hash)">
                  <span class="cpa-action-symbol">{{ action.icon }}</span><span class="cpa-action-copy"><strong>{{ action.label }}</strong><small>{{ action.desc }}</small></span><span aria-hidden="true">→</span>
                </button>
              </div>
            </section>
          </div>
        </div>
        <NEmpty v-else-if="!loading" :description="error || '暂无数据'" />
      </NSpin>
    </template>

    <!-- 2. 现代科技皮肤专属布局 (Modern Bento Grid & Hero) -->
    <template v-else>
      <NSpin :show="loading">
        <div v-if="stats" class="modern-dashboard-layout">
          <!-- 现代 Hero 区域 -->
          <div class="modern-hero-card">
            <div class="hero-left">
              <div class="hero-tag-row">
                <span class="pulse-dot"></span>
                <span class="hero-badge">AI GATEWAY ENGINE ACTIVE</span>
              </div>
              <h1 class="hero-title">统一智能代理与故障转移网关</h1>
              <p class="hero-subtitle">跨协议双向转换 · 多 Key 负载调度 · 毫秒级熔断与 Token 级精细分析</p>
              <div class="hero-quick-buttons">
                <button class="hero-btn primary" @click="go('chat')">💬 对话测试</button>
                <button class="hero-btn secondary" @click="go('developer-invocations')">🛠️ 调试追踪</button>
                <button class="hero-btn secondary" @click="go('analytics')">🛰️ 用量分析</button>
              </div>
            </div>
            <div class="hero-right-deco">
              <div class="gateway-status-pill">
                <span class="status-num">{{ stats.siteCount }}</span>
                <span class="status-sub">活跃站点</span>
              </div>
              <div class="gateway-status-pill">
                <span class="status-num">{{ stats.modelCount }}</span>
                <span class="status-sub">托管模型</span>
              </div>
            </div>
          </div>

          <!-- 便当盒 Bento 风格统计网格 -->
          <div class="modern-bento-grid">
            <div
              v-for="card in statCards"
              :key="card.label"
              class="bento-stat-card"
              :class="`bento-${card.tone}`"
              @click="go(card.route, card.query, card.hash)"
            >
              <div class="bento-card-top">
                <span class="bento-icon-wrapper">{{ card.icon }}</span>
                <span class="bento-arrow">↗</span>
              </div>
              <div class="bento-card-main">
                <div class="bento-value">{{ card.value }}</div>
                <div class="bento-label">{{ card.label }}</div>
                <div class="bento-desc">{{ card.desc }}</div>
              </div>
            </div>
          </div>

          <!-- 现代快捷操作栏 -->
          <div class="modern-action-section">
            <div class="section-heading">
              <div class="heading-title">常用配置快捷入口</div>
              <div class="heading-sub">一键直达核心资源配置</div>
            </div>
            <div class="modern-action-grid">
              <div
                v-for="action in quickActions"
                :key="action.label"
                class="modern-action-card"
                @click="go(action.route, action.query, action.hash)"
              >
                <div class="action-card-icon" :class="action.tone">{{ action.icon }}</div>
                <div class="action-card-text">
                  <div class="action-card-title">{{ action.label }}</div>
                  <div class="action-card-desc">{{ action.desc }}</div>
                </div>
                <div class="action-card-hover-arrow">➔</div>
              </div>
            </div>
          </div>
        </div>
        <NEmpty v-else-if="!loading" :description="error || '暂无数据'" />
      </NSpin>
    </template>
  </div>
</template>

<style scoped>
.cpa-dashboard-heading { display: flex; justify-content: space-between; align-items: flex-end; gap: 20px; margin: 8px 0 34px; }
.cpa-eyebrow, .cpa-section-heading > span { color: var(--text-color-secondary); font-size: 10px; font-weight: 700; letter-spacing: .14em; }
.cpa-dashboard-heading h1 { margin: 8px 0 3px; font-size: 30px; line-height: 1.25; letter-spacing: -.04em; }
.cpa-dashboard-heading p, .cpa-section-heading p { margin: 0; color: var(--text-color-secondary); font-size: 13px; }
.cpa-heading-action { padding: 10px 14px; border: 1px solid var(--border-color-global); border-radius: 8px; background: var(--bg-card); color: var(--text-primary); cursor: pointer; font-weight: 600; white-space: nowrap; }
.cpa-heading-action span { margin-left: 15px; }
.cpa-section-heading { display: flex; justify-content: space-between; align-items: flex-end; margin-bottom: 14px; }
.cpa-section-heading h2 { margin: 0 0 2px; font-size: 16px; }
.cpa-stat-grid { display: grid; grid-template-columns: repeat(5, minmax(0, 1fr)); gap: 10px; }
.cpa-stat { display: flex; flex-direction: column; align-items: flex-start; gap: 6px; min-width: 0; min-height: 142px; padding: 16px; border: 1px solid var(--border-color-global); border-radius: 10px; background: var(--bg-card); color: var(--text-primary); text-align: left; cursor: pointer; transition: border-color .2s, transform .2s; }
.cpa-stat:hover, .cpa-action:hover, .cpa-heading-action:hover { border-color: var(--cpa-border-hover); }
.cpa-stat:hover { transform: translateY(-2px); }
.cpa-stat-top { display: flex; justify-content: space-between; gap: 8px; width: 100%; color: var(--text-color-secondary); }
.cpa-stat-index { font-size: 12px; font-weight: 600; }
.cpa-stat strong { margin-top: auto; font-size: 30px; line-height: 1; font-weight: 700; font-variant-numeric: tabular-nums; }
.cpa-stat-desc { color: var(--text-color-secondary); font-size: 12px; }
.cpa-actions-heading { margin-top: 32px; }
.cpa-action-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px; }
.cpa-action { display: flex; align-items: center; gap: 14px; min-width: 0; padding: 15px; border: 1px solid var(--border-color-global); border-radius: 10px; background: var(--bg-card); color: var(--text-color-secondary); text-align: left; cursor: pointer; }
.cpa-action-symbol { display: grid; place-items: center; width: 38px; height: 38px; flex-shrink: 0; border-radius: 8px; background: var(--bg-surface-soft); color: var(--text-primary); font-size: 18px; }
.cpa-action-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 2px; }
.cpa-action-copy strong { color: var(--text-primary); font-size: 13px; }
.cpa-action-copy small { overflow: hidden; font-size: 12px; text-overflow: ellipsis; white-space: nowrap; }
.cpa-dashboard-heading { margin-bottom: 26px; }
.cpa-eyebrow, .cpa-section-heading > span { color: #4383d4; }
.cpa-heading-action { color: #fff; background: #3b82f6; border-color: #3b82f6; box-shadow: 0 6px 16px rgba(59, 130, 246, .2); }
.cpa-heading-action:hover { background: #2563eb; border-color: #2563eb; }
.cpa-stat-grid { gap: 14px; }
.cpa-stat { min-height: 132px; border-radius: 14px; box-shadow: var(--card-shadow); }
.cpa-stat-top > span:last-child { color: #3b82f6; }
.cpa-stat strong { color: #3b82f6; }
.cpa-panel-grid { display: grid; grid-template-columns: 1fr 1.4fr; gap: 16px; margin-top: 20px; }
.cpa-panel { min-width: 0; padding: 20px; border: 1px solid var(--border-color-global); border-radius: 14px; background: var(--bg-card); box-shadow: var(--card-shadow); }
.cpa-panel .cpa-section-heading { margin-bottom: 18px; }
.cpa-core-list { margin: 0; }
.cpa-core-list div { display: flex; align-items: baseline; justify-content: space-between; gap: 16px; padding: 10px 0; border-bottom: 1px solid var(--border-color-soft); }
.cpa-core-list div:last-child { border-bottom: 0; }
.cpa-core-list dt { flex-shrink: 0; color: var(--text-color-secondary); }
.cpa-core-list dd { min-width: 0; margin: 0; overflow-wrap: anywhere; text-align: right; font-weight: 600; }
.cpa-action { background: var(--bg-surface-soft); }
.cpa-action-symbol { background: var(--status-info-bg); color: var(--status-info-text); }
@media (max-width: 1100px) { .cpa-panel-grid { grid-template-columns: 1fr; } }
@media (max-width: 1199.98px) { .cpa-stat-grid { grid-template-columns: repeat(3, minmax(0, 1fr)); } }
@media (max-width: 767.98px) { .cpa-stat-grid, .cpa-action-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (max-width: 480px) { .cpa-dashboard-heading { align-items: flex-start; flex-direction: column; } .cpa-stat-grid, .cpa-action-grid { grid-template-columns: 1fr; } .cpa-stat { min-height: 115px; } }
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
  font-size: 24px;
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

.dashboard-stat-grid {
  display: grid;
  grid-template-columns: repeat(5, minmax(0, 1fr));
  gap: 16px;
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

/* 暗色补齐：浅色粉底图标块在暗色下是刺眼亮块，改为半透明色调底+提亮图标色 */
[data-theme='dark'] .stat-card-icon.primary,
[data-theme='dark'] .quick-action-icon.primary { background: rgba(108, 158, 255, 0.16); color: #85B0FF; }
[data-theme='dark'] .stat-card-icon.success,
[data-theme='dark'] .quick-action-icon.success { background: rgba(24, 160, 88, 0.18); color: #4ADE80; }
[data-theme='dark'] .stat-card-icon.warning,
[data-theme='dark'] .quick-action-icon.warning { background: rgba(240, 160, 32, 0.16); color: #FBBF24; }
[data-theme='dark'] .stat-card-icon.danger,
[data-theme='dark'] .quick-action-icon.danger { background: rgba(208, 48, 80, 0.16); color: #F87171; }
[data-theme='dark'] .stat-card-icon.purple,
[data-theme='dark'] .quick-action-icon.purple { background: rgba(139, 92, 246, 0.18); color: #C4B5FD; }
[data-theme='dark'] .stat-card-icon.info,
[data-theme='dark'] .quick-action-icon.info { background: rgba(32, 128, 240, 0.16); color: #7EB8FF; }

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

@media (max-width: 1199.98px) {
  .dashboard-stat-grid {
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }
}

@media (max-width: 991.98px) {
  .dashboard-stat-grid,
  .quick-action-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 767.98px) {
  .welcome-banner {
    padding: 24px;
  }
}

@media (max-width: 575.98px) {
  .dashboard-stat-grid,
  .quick-action-grid {
    grid-template-columns: 1fr;
  }
}

/* ===== 现代化专属布局与动效 (Modern Dashboard Layout) ===== */
.modern-dashboard-layout {
  display: flex;
  flex-direction: column;
  gap: 24px;
}

.modern-hero-card {
  position: relative;
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 32px 36px;
  background: radial-gradient(circle at 10% 20%, rgba(99, 102, 241, 0.15) 0%, rgba(168, 85, 247, 0.05) 90%), var(--bg-card);
  border: 1px solid var(--border-color-soft);
  border-radius: 20px;
  box-shadow: var(--card-shadow);
  overflow: hidden;
}

.hero-left {
  max-width: 680px;
  z-index: 1;
}

.hero-tag-row {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 12px;
}

.pulse-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: #10B981;
  box-shadow: 0 0 0 0 rgba(16, 185, 129, 0.7);
  animation: pulse 2s infinite;
}

@keyframes pulse {
  0% { transform: scale(0.95); box-shadow: 0 0 0 0 rgba(16, 185, 129, 0.7); }
  70% { transform: scale(1); box-shadow: 0 0 0 8px rgba(16, 185, 129, 0); }
  100% { transform: scale(0.95); box-shadow: 0 0 0 0 rgba(16, 185, 129, 0); }
}

.hero-badge {
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 1px;
  color: #6366F1;
}

[data-theme='dark'] .hero-badge {
  color: #A5B4FC;
}

.hero-title {
  margin: 0 0 8px;
  font-size: 26px;
  font-weight: 800;
  letter-spacing: -0.5px;
  color: var(--text-primary);
}

.hero-subtitle {
  margin: 0 0 20px;
  font-size: 14px;
  color: var(--text-color-secondary);
  line-height: 1.6;
}

.hero-quick-buttons {
  display: flex;
  gap: 12px;
}

.hero-btn {
  padding: 8px 16px;
  border-radius: 10px;
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
  transition: all 0.2s ease;
}

.hero-btn.primary {
  background: #6366F1;
  color: #ffffff;
  border: none;
  box-shadow: 0 4px 14px rgba(99, 102, 241, 0.35);
}

.hero-btn.primary:hover {
  background: #4F46E5;
  transform: translateY(-1px);
}

.hero-btn.secondary {
  background: var(--bg-surface-soft);
  color: var(--text-primary);
  border: 1px solid var(--border-color-soft);
}

.hero-btn.secondary:hover {
  background: var(--border-color-soft);
}

.hero-right-deco {
  display: flex;
  gap: 14px;
  z-index: 1;
}

.gateway-status-pill {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 16px 20px;
  background: var(--bg-surface-soft);
  border: 1px solid var(--border-color-soft);
  border-radius: 14px;
  min-width: 100px;
}

.status-num {
  font-size: 24px;
  font-weight: 800;
  color: #6366F1;
  font-family: ui-monospace, monospace;
}

[data-theme='dark'] .status-num {
  color: #818CF8;
}

.status-sub {
  font-size: 12px;
  color: var(--text-color-secondary);
}

/* 便当盒 Bento Grid */
.modern-bento-grid {
  display: grid;
  grid-template-columns: repeat(5, minmax(0, 1fr));
  gap: 16px;
}

.bento-stat-card {
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  padding: 20px;
  min-height: 160px;
  background: var(--bg-card);
  border: 1px solid var(--border-color-soft);
  border-radius: 16px;
  cursor: pointer;
  transition: all 0.25s cubic-bezier(0.4, 0, 0.2, 1);
  position: relative;
  overflow: hidden;
}

.bento-stat-card:hover {
  transform: translateY(-4px);
  border-color: #6366F1;
  box-shadow: 0 12px 30px -5px rgba(99, 102, 241, 0.15);
}

.bento-card-top {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.bento-icon-wrapper {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 42px;
  height: 42px;
  border-radius: 12px;
  font-size: 20px;
  background: var(--bg-surface-soft);
}

.bento-arrow {
  font-size: 16px;
  color: var(--text-color-disabled);
  transition: transform 0.2s ease, color 0.2s ease;
}

.bento-stat-card:hover .bento-arrow {
  transform: translate(2px, -2px);
  color: #6366F1;
}

.bento-value {
  font-size: 28px;
  font-weight: 800;
  color: var(--text-primary);
  font-family: ui-monospace, monospace;
  line-height: 1.2;
  margin-bottom: 4px;
}

.bento-label {
  font-size: 14px;
  font-weight: 700;
  color: var(--text-primary);
}

.bento-desc {
  font-size: 12px;
  color: var(--text-color-secondary);
  margin-top: 2px;
}

/* 现代快捷卡片 */
.modern-action-section {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.section-heading {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.heading-title {
  font-size: 16px;
  font-weight: 700;
  color: var(--text-primary);
}

.heading-sub {
  font-size: 12px;
  color: var(--text-color-secondary);
}

.modern-action-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 14px;
}

.modern-action-card {
  display: flex;
  align-items: center;
  gap: 14px;
  padding: 16px 18px;
  background: var(--bg-card);
  border: 1px solid var(--border-color-soft);
  border-radius: 14px;
  cursor: pointer;
  transition: all 0.2s ease;
}

.modern-action-card:hover {
  transform: translateY(-2px);
  border-color: #6366F1;
  box-shadow: 0 8px 20px -4px rgba(99, 102, 241, 0.12);
}

.action-card-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 40px;
  height: 40px;
  border-radius: 10px;
  font-size: 18px;
  font-weight: 700;
  flex-shrink: 0;
}

.action-card-text {
  flex: 1;
  min-width: 0;
}

.action-card-title {
  font-size: 14px;
  font-weight: 700;
  color: var(--text-primary);
}

.action-card-desc {
  font-size: 12px;
  color: var(--text-color-secondary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  margin-top: 2px;
}

.action-card-hover-arrow {
  font-size: 14px;
  color: var(--text-color-disabled);
  transition: all 0.2s ease;
}

.modern-action-card:hover .action-card-hover-arrow {
  transform: translateX(4px);
  color: #6366F1;
}

@media (max-width: 1199.98px) {
  .modern-bento-grid {
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }
}

@media (max-width: 991.98px) {
  .modern-hero-card {
    flex-direction: column;
    align-items: flex-start;
    gap: 20px;
  }
  .modern-action-grid,
  .modern-bento-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 575.98px) {
  .modern-action-grid,
  .modern-bento-grid {
    grid-template-columns: 1fr;
  }
}

</style>

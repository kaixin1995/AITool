<script setup lang="ts">
import { computed, h, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NLayout, NLayoutContent, NMenu, NSwitch, NButton, NSpace, NText, NTooltip, type MenuOption } from 'naive-ui'
import { useTheme } from '@/composables/useTheme'
import { useAuthStore } from '@/stores/auth'
import { version } from '@/composables/useVersion'

const router = useRouter()
const route = useRoute()
const auth = useAuthStore()
const { isDark, toggleTheme } = useTheme()

// 侧边栏收起状态：与原设计一致，收起到 64px 纯图标模式（隐藏文字和分组标题）。
const collapsed = ref(localStorage.getItem('aitool.sidebarCollapsed') === 'true')
function toggleCollapsed(): void {
  collapsed.value = !collapsed.value
  localStorage.setItem('aitool.sidebarCollapsed', collapsed.value ? 'true' : 'false')
}

// 图标用 emoji（与原 Razor Pages 设计一致）。
function renderEmoji(emoji: string) {
  return () => h('span', { style: 'font-size: 16px; line-height: 1; display: inline-block; width: 20px; text-align: center;' }, emoji)
}

// 菜单结构严格对齐原 _Layout.cshtml：
// 概览 = 仪表盘 + 可视化分析 + 对话
// 资源管理 = 站点 + Codex(开关) + 模型库
// 代理配置 = 路由规则 + 兼容规则集 + 访问密钥
// 监控运维 = 模型检测 + 检测任务 + 模型健康 + 调试工具(开关) + 使用日志 + 系统设置
const menuOptions = computed<MenuOption[]>(() => {
  const features = auth.status?.features

  const overview: MenuOption[] = [
    { label: '仪表盘', key: 'dashboard', icon: renderEmoji('📊') },
    { label: '可视化分析', key: 'analytics', icon: renderEmoji('🛰️') },
    { label: '对话', key: 'chat', icon: renderEmoji('💬') }
  ]

  const resource: MenuOption[] = [
    { label: '站点管理', key: 'sites', icon: renderEmoji('🌐') }
  ]
  if (features?.codexEnabled) {
    resource.push({ label: 'OAuth 管理', key: 'codex', icon: renderEmoji('🔐') })
  }
  resource.push({ label: '模型库', key: 'models', icon: renderEmoji('🧠') })

  const proxy: MenuOption[] = [
    { label: '路由规则', key: 'routes', icon: renderEmoji('🔀') },
    { label: '兼容规则集', key: 'compatibility', icon: renderEmoji('🧩') },
    { label: '访问密钥', key: 'access-keys', icon: renderEmoji('🔑') }
  ]

  const monitor: MenuOption[] = [
    { label: '模型检测', key: 'detection', icon: renderEmoji('🔍') },
    { label: '检测任务', key: 'detection-tasks', icon: renderEmoji('⏰') },
    { label: '模型健康', key: 'model-health', icon: renderEmoji('💊') }
  ]
  if (features?.developerEnabled) {
    monitor.push({ label: '调试工具', key: 'developer-invocations', icon: renderEmoji('🛠️') })
  }
  monitor.push({ label: '使用日志', key: 'usage-logs', icon: renderEmoji('📋') })
  monitor.push({ label: '系统设置', key: 'system-settings', icon: renderEmoji('⚙️') })

  return [
    { type: 'group', label: '概览', key: 'g-overview', children: overview },
    { type: 'group', label: '资源管理', key: 'g-resource', children: resource },
    { type: 'group', label: '代理配置', key: 'g-proxy', children: proxy },
    { type: 'group', label: '监控运维', key: 'g-monitor', children: monitor }
  ]
})

// 当前激活的菜单项（取路由 name）。
const activeKey = computed(() => (route.name as string) ?? '')

function handleMenuSelect(key: string): void {
  router.push({ name: key })
}

async function handleLogout(): Promise<void> {
  await auth.logout()
  router.push({ name: 'login' })
}

onMounted(async () => {
  if (!auth.status) {
    try {
      await auth.fetchStatus()
    } catch {
      // 忽略，路由守卫会处理。
    }
  }
})
</script>

<template>
  <div class="app-wrapper" :class="{ 'sidebar-collapsed': collapsed }">
    <aside class="app-sidebar">
      <!-- 品牌区 -->
      <div class="sidebar-brand">
        <div class="sidebar-brand-main">
          <div class="brand-icon">AI</div>
          <span v-if="!collapsed" class="brand-text">AI Tool</span>
        </div>
        <div v-if="!collapsed" class="sidebar-brand-actions">
          <button class="sidebar-collapse-toggle" type="button" title="折叠侧边栏" @click="toggleCollapsed">‹</button>
        </div>
      </div>

      <!-- 导航 -->
      <nav class="sidebar-nav">
        <NMenu
          :value="activeKey"
          :collapsed="collapsed"
          :collapsed-width="64"
          :collapsed-icon-size="20"
          :indent="18"
          :root-indent="18"
          :options="menuOptions"
          @update:value="handleMenuSelect"
        />
      </nav>

      <!-- 收起状态下的展开按钮（底部） -->
      <button v-if="collapsed" class="sidebar-expand-toggle" type="button" title="展开侧边栏" @click="toggleCollapsed">›</button>
    </aside>

    <!-- 主内容区 -->
    <div class="app-main">
      <header class="app-topbar">
        <h1 class="app-topbar-title">{{ (route.meta.title as string) ?? 'AI Tool' }}</h1>
        <div class="app-topbar-right">
          <NTooltip trigger="hover">
            <template #trigger>
              <button class="theme-icon-toggle" type="button" @click="toggleTheme">{{ isDark ? '☀️' : '🌙' }}</button>
            </template>
            {{ isDark ? '切换到经典模式' : '切换到暗夜模式' }}
          </NTooltip>
          <span class="topbar-version">AI Tool v{{ version }}</span>
          <NButton size="small" quaternary @click="handleLogout">退出</NButton>
        </div>
      </header>
      <main class="app-content">
        <NLayoutContent class="app-content-scroll" content-style="height: 100%;" :native-scrollbar="false">
          <RouterView />
        </NLayoutContent>
      </main>
    </div>
  </div>
</template>

<style scoped>
/* 布局：严格复刻原 theme.css 的 flex 布局，侧边栏固定、主内容区自适应。 */
.app-wrapper {
  display: flex;
  min-height: 100vh;
}
.app-sidebar {
  width: 260px;
  background: var(--bg-card);
  border-right: 1px solid var(--border-color-global);
  display: flex;
  flex-direction: column;
  position: fixed;
  top: 0;
  left: 0;
  bottom: 0;
  z-index: 1000;
  transition: width 0.2s ease;
  overflow: hidden;
}
.app-wrapper.sidebar-collapsed .app-sidebar {
  width: 64px;
}

/* 品牌区 */
.sidebar-brand {
  height: 60px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 20px;
  border-bottom: 1px solid var(--border-color-global);
  flex-shrink: 0;
}
.app-wrapper.sidebar-collapsed .sidebar-brand {
  padding: 0;
  justify-content: center;
}
.sidebar-brand-main {
  display: flex;
  align-items: center;
  gap: 10px;
}
.brand-icon {
  width: 36px;
  height: 36px;
  border-radius: 10px;
  background: linear-gradient(135deg, #6C9EFF 0%, #A5B4FC 100%);
  color: white;
  font-weight: 700;
  font-size: 15px;
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  box-shadow: 0 2px 8px rgba(108, 158, 255, 0.3);
}
.brand-text {
  font-weight: 700;
  font-size: 16px;
  white-space: nowrap;
}
.sidebar-collapse-toggle {
  background: none;
  border: 1px solid var(--border-color-global);
  border-radius: 6px;
  width: 24px;
  height: 24px;
  cursor: pointer;
  color: var(--text-color-secondary);
  font-size: 16px;
  line-height: 1;
}
.sidebar-collapse-toggle:hover {
  color: #6C9EFF;
  border-color: #6C9EFF;
}

/* 导航区滚动 */
.sidebar-nav {
  flex: 1;
  overflow-y: auto;
  overflow-x: hidden;
  padding: 8px 12px;
}

/* 收起状态下的展开按钮 */
.sidebar-expand-toggle {
  margin: 8px auto 12px;
  background: none;
  border: 1px solid var(--border-color-global);
  border-radius: 6px;
  width: 24px;
  height: 24px;
  cursor: pointer;
  color: var(--text-color-secondary);
  font-size: 16px;
  line-height: 1;
  flex-shrink: 0;
}
.sidebar-expand-toggle:hover {
  color: #6C9EFF;
  border-color: #6C9EFF;
}

/* 主内容区 */
.app-main {
  flex: 1;
  margin-left: 260px;
  display: flex;
  flex-direction: column;
  min-height: 100vh;
  transition: margin-left 0.2s ease;
}
.app-wrapper.sidebar-collapsed .app-main {
  margin-left: 64px;
}

/* 顶部栏 */
.app-topbar {
  height: 56px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 24px;
  border-bottom: 1px solid var(--border-color-global);
  background: var(--bg-card);
  flex-shrink: 0;
}
.app-topbar-title {
  margin: 0;
  font-size: 16px;
  font-weight: 600;
}
.app-topbar-right {
  display: flex;
  align-items: center;
  gap: 16px;
}
.theme-icon-toggle {
  background: none;
  border: none;
  cursor: pointer;
  font-size: 18px;
  line-height: 1;
  padding: 4px;
  border-radius: 6px;
}
.theme-icon-toggle:hover {
  background: var(--n-color-hover, rgba(0,0,0,0.03));
}
.topbar-version {
  font-size: 13px;
  color: var(--text-color-secondary);
}

/* 内容区 */
.app-content {
  flex: 1;
  background: var(--bg-page);
  overflow: hidden;
}
.app-content-scroll {
  height: 100%;
}

/* 暗色模式适配 */
.app-wrapper :deep(.n-menu .n-menu-item-content--selected::before) {
  background: #EEF4FF;
}
[data-theme='dark'] .app-wrapper :deep(.n-menu .n-menu-item-content--selected::before) {
  background: rgba(108, 158, 255, 0.15);
}
</style>

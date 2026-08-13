<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NLayoutContent, NButton, NTooltip } from 'naive-ui'
import { useTheme } from '@/composables/useTheme'
import { useAuthStore } from '@/stores/auth'
import { version, buildTimeDisplay } from '@/composables/useVersion'

const router = useRouter()
const route = useRoute()
const auth = useAuthStore()
const { isDark, toggleTheme } = useTheme()

const collapsed = ref(localStorage.getItem('aitool.sidebarCollapsed') === 'true')
const mobileSidebarOpen = ref(false)

function toggleCollapsed(): void {
  collapsed.value = !collapsed.value
  localStorage.setItem('aitool.sidebarCollapsed', collapsed.value ? 'true' : 'false')
}

interface NavLink { label: string; key: string; icon: string }
interface NavGroup { title: string; items: NavLink[] }

const navGroups = computed<NavGroup[]>(() => {
  const features = auth.status?.features
  const groups: NavGroup[] = [
    {
      title: '概览',
      items: [
        { label: '仪表盘', key: 'dashboard', icon: '📊' },
        { label: '可视化分析', key: 'analytics', icon: '🛰️' },
        { label: '对话', key: 'chat', icon: '💬' }
      ]
    },
    {
      title: '资源管理',
      items: [
        { label: '站点管理', key: 'sites', icon: '🌐' },
        ...(features?.codexEnabled ? [{ label: 'OAuth 管理', key: 'codex', icon: '🔐' }] : []),
        { label: '模型库', key: 'models', icon: '🧠' }
      ]
    },
    {
      title: '代理配置',
      items: [
        { label: '路由管理', key: 'routes', icon: '🔀' },
        { label: '访问密钥', key: 'access-keys', icon: '🔑' }
      ]
    },
    {
      title: '监控运维',
      items: [
        { label: '模型检测', key: 'detection', icon: '🔍' },
        { label: '检测任务', key: 'detection-tasks', icon: '⏰' },
        { label: '模型健康', key: 'model-health', icon: '💊' },
        ...(features?.developerEnabled ? [{ label: '调试工具', key: 'developer-invocations', icon: '🛠️' }] : []),
        { label: '使用日志', key: 'usage-logs', icon: '📋' },
        { label: '系统设置', key: 'system-settings', icon: '⚙️' }
      ]
    }
  ]
  return groups
})

const activeKey = computed(() => (route.name as string) ?? '')

function handleNavigate(key: string): void {
  mobileSidebarOpen.value = false
  router.push({ name: key })
}

watch(() => route.fullPath, () => {
  mobileSidebarOpen.value = false
})

async function handleLogout(): Promise<void> {
  await auth.logout()
  router.push({ name: 'login' })
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') mobileSidebarOpen.value = false
}

onMounted(async () => {
  window.addEventListener('keydown', handleKeydown)
  if (!auth.status) {
    try { await auth.fetchStatus() } catch { /* 路由守卫处理 */ }
  }
})

onBeforeUnmount(() => {
  window.removeEventListener('keydown', handleKeydown)
})
</script>

<template>
  <div class="app-wrapper" :class="{ 'sidebar-collapsed': collapsed, 'mobile-sidebar-open': mobileSidebarOpen }">
    <aside class="app-sidebar">
      <!-- 品牌区 -->
      <div class="sidebar-brand">
        <div class="sidebar-brand-main">
          <div class="brand-icon">AI</div>
          <span class="brand-text">AI Tool</span>
        </div>
        <div class="sidebar-brand-actions">
          <button class="sidebar-collapse-toggle" type="button" :title="collapsed ? '展开侧边栏' : '折叠侧边栏'" @click="toggleCollapsed">{{ collapsed ? '›' : '‹' }}</button>
          <button class="mobile-sidebar-close" type="button" title="关闭导航" @click="mobileSidebarOpen = false">×</button>
        </div>
      </div>

      <!-- 导航：纯 HTML，完全复刻原 _Layout.cshtml 的 sidebar-link 结构 -->
      <nav class="sidebar-nav">
        <div v-for="group in navGroups" :key="group.title" class="sidebar-section">
          <div class="sidebar-section-title">{{ group.title }}</div>
          <a
            v-for="item in group.items"
            :key="item.key"
            class="sidebar-link"
            :class="{ active: activeKey === item.key }"
            :title="collapsed ? item.label : undefined"
            href="javascript:void(0)"
            @click="handleNavigate(item.key)"
          >
            <span class="sidebar-link-icon">{{ item.icon }}</span>{{ item.label }}
          </a>
        </div>
      </nav>
    </aside>
    <button v-if="mobileSidebarOpen" class="sidebar-overlay" type="button" aria-label="关闭导航" @click="mobileSidebarOpen = false" />

    <div class="app-main">
      <header class="app-topbar">
        <div class="app-topbar-heading">
          <button class="mobile-menu-toggle" type="button" aria-label="打开导航" @click="mobileSidebarOpen = true">☰</button>
          <h1 class="app-topbar-title">{{ (route.meta.title as string) ?? 'AI Tool' }}</h1>
        </div>
        <div class="app-topbar-right">
          <NTooltip trigger="hover">
            <template #trigger>
              <button class="theme-icon-toggle" type="button" @click="toggleTheme">{{ isDark ? '☀️' : '🌙' }}</button>
            </template>
            {{ isDark ? '切换到经典模式' : '切换到暗夜模式' }}
          </NTooltip>
          <NTooltip :show-arrow="false">
            <template #trigger>
              <span class="topbar-version">AI Tool v{{ version }}{{ buildTimeDisplay ? ' · ' + buildTimeDisplay : '' }}</span>
            </template>
            编译时间：{{ buildTimeDisplay || '未知' }}
          </NTooltip>
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
.app-wrapper { display: flex; min-height: 100vh; }

/* ===== 侧边栏 ===== */
.app-sidebar {
  width: 260px;
  background: var(--bg-card);
  border-right: 1px solid var(--border-color-global);
  position: fixed; top: 0; left: 0; bottom: 0; z-index: 1000;
  transition: width 0.2s ease;
  overflow-x: hidden; overflow-y: auto;
  display: flex; flex-direction: column;
}
.app-wrapper.sidebar-collapsed .app-sidebar { width: 72px; }

/* 品牌区 */
.sidebar-brand {
  padding: 20px 16px;
  display: flex; align-items: center; justify-content: space-between;
  gap: 12px; border-bottom: 1px solid var(--border-color-global); flex-shrink: 0;
}
.app-wrapper.sidebar-collapsed .sidebar-brand {
  padding: 18px 0 12px; flex-direction: column; justify-content: center; gap: 10px;
}
.sidebar-brand-main { display: flex; align-items: center; gap: 10px; }
.app-wrapper.sidebar-collapsed .sidebar-brand-main { justify-content: center; }
.brand-icon {
  width: 36px; height: 36px; border-radius: 10px;
  background: linear-gradient(135deg, #6C9EFF 0%, #A5B4FC 100%);
  color: white; font-weight: 700; font-size: 15px;
  display: flex; align-items: center; justify-content: center; flex-shrink: 0;
  box-shadow: 0 2px 8px rgba(108, 158, 255, 0.3);
}
.brand-text { font-weight: 700; font-size: 16px; white-space: nowrap; color: var(--text-primary); }
.app-wrapper.sidebar-collapsed .brand-text { display: none; }
.sidebar-brand-actions { display: flex; align-items: center; gap: 6px; flex-shrink: 0; }
.sidebar-collapse-toggle {
  display: flex; align-items: center; justify-content: center;
  width: 30px; height: 30px;
  border: 1px solid var(--border-color-global); border-radius: 8px;
  background: var(--bg-card); color: var(--text-color-secondary);
  cursor: pointer; font-size: 18px; line-height: 1; transition: all 0.15s ease;
}
.sidebar-collapse-toggle:hover { background: #EEF4FF; color: #6C9EFF; border-color: #EEF4FF; }
[data-theme='dark'] .sidebar-collapse-toggle:hover { background: rgba(108, 158, 255, 0.15); }
.mobile-sidebar-close,
.mobile-menu-toggle,
.sidebar-overlay { display: none; }

/* 导航 */
.sidebar-nav { flex: 1; padding: 12px; overflow-y: auto; }
.app-wrapper.sidebar-collapsed .sidebar-nav { padding: 12px 8px; }
.sidebar-section { margin-bottom: 4px; }
.sidebar-section-title {
  font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.5px;
  color: var(--text-color-secondary); padding: 12px 12px 6px;
}
.app-wrapper.sidebar-collapsed .sidebar-section-title { display: none; }
.sidebar-link {
  display: flex; align-items: center; gap: 10px;
  padding: 9px 12px; border-radius: 8px;
  color: var(--text-color-secondary); text-decoration: none;
  font-size: 13.5px; font-weight: 500; transition: all 0.15s ease; margin-bottom: 2px;
  cursor: pointer;
}
.sidebar-link:hover { background: rgba(0, 0, 0, 0.03); color: var(--text-primary); }
[data-theme='dark'] .sidebar-link:hover { background: rgba(255, 255, 255, 0.05); }
.sidebar-link.active { background: #EEF4FF; color: #6C9EFF; }
[data-theme='dark'] .sidebar-link.active { background: rgba(108, 158, 255, 0.15); color: #6C9EFF; }
.sidebar-link-icon { width: 20px; text-align: center; font-size: 15px; flex-shrink: 0; }

/* ===== 折叠态：完全复刻原 theme.css ===== */
.app-wrapper.sidebar-collapsed .sidebar-link {
  justify-content: center; gap: 0; padding: 10px 0; font-size: 0;
}
.app-wrapper.sidebar-collapsed .sidebar-link-icon { width: auto; font-size: 17px; }

/* ===== 主内容区 ===== */
.app-main {
  flex: 1 1 auto; margin-left: 260px;
  width: calc(100% - 260px); min-width: 0;
  display: flex; flex-direction: column; min-height: 100vh;
  transition: margin-left 0.2s ease, width 0.2s ease;
}
.app-wrapper.sidebar-collapsed .app-main { margin-left: 72px; width: calc(100% - 72px); }

/* 顶部栏 */
.app-topbar {
  width: 100%; min-width: 0; box-sizing: border-box;
  height: 60px; display: flex; align-items: center; justify-content: space-between;
  padding: 0 var(--layout-gutter-x); border-bottom: 1px solid var(--border-color-global);
  background: var(--bg-card); flex-shrink: 0;
}
.app-topbar-heading { display: flex; align-items: center; gap: 12px; min-width: 0; flex: 1; }
.app-topbar-title { margin: 0; font-size: 16px; font-weight: 600; color: var(--text-primary); }
.app-topbar-right { display: flex; align-items: center; gap: 16px; flex-shrink: 0; }
.theme-icon-toggle {
  background: none; border: none; cursor: pointer; font-size: 18px; line-height: 1;
  padding: 4px; border-radius: 6px;
}
.theme-icon-toggle:hover { background: rgba(0, 0, 0, 0.03); }
[data-theme='dark'] .theme-icon-toggle:hover { background: rgba(255, 255, 255, 0.05); }
.topbar-version { font-size: 13px; color: var(--text-color-secondary); }

/* 内容区 */
.app-content { flex: 1; background: var(--bg-page); overflow: hidden; }
.app-content-scroll { height: 100%; }

@media (max-width: 991.98px) {
  .app-wrapper,
  .app-wrapper.sidebar-collapsed {
    display: block;
    min-height: 100vh;
  }

  .app-sidebar,
  .app-wrapper.sidebar-collapsed .app-sidebar {
    position: fixed;
    top: 0;
    bottom: 0;
    left: 0;
    z-index: 1100;
    width: 260px;
    max-width: calc(100vw - 48px);
    border-right: 1px solid var(--border-color-global);
    transform: translateX(-100%);
    transition: transform 0.2s ease;
  }

  .app-wrapper.mobile-sidebar-open .app-sidebar {
    transform: translateX(0);
  }

  .sidebar-brand,
  .app-wrapper.sidebar-collapsed .sidebar-brand {
    padding: 16px;
    flex-direction: row;
    justify-content: space-between;
  }

  .app-wrapper.sidebar-collapsed .sidebar-brand-main {
    justify-content: flex-start;
  }

  .brand-text,
  .app-wrapper.sidebar-collapsed .brand-text {
    display: inline;
  }

  .sidebar-collapse-toggle {
    display: none;
  }

  .mobile-sidebar-close {
    display: flex;
    width: 30px;
    height: 30px;
    align-items: center;
    justify-content: center;
    border: 1px solid var(--border-color-global);
    border-radius: 8px;
    background: var(--bg-card);
    color: var(--text-color-secondary);
    cursor: pointer;
    font-size: 20px;
    line-height: 1;
  }

  .sidebar-nav,
  .app-wrapper.sidebar-collapsed .sidebar-nav {
    display: block;
    padding: 12px;
    overflow-x: hidden;
    overflow-y: auto;
  }

  .sidebar-section {
    display: block;
    margin-bottom: 4px;
  }

  .sidebar-section-title,
  .app-wrapper.sidebar-collapsed .sidebar-section-title {
    display: block;
  }

  .sidebar-link,
  .app-wrapper.sidebar-collapsed .sidebar-link {
    justify-content: flex-start;
    gap: 10px;
    margin-bottom: 2px;
    padding: 9px 12px;
    font-size: 13.5px;
    white-space: normal;
  }

  .app-wrapper.sidebar-collapsed .sidebar-link-icon {
    width: 20px;
    font-size: 15px;
  }

  .sidebar-overlay {
    position: fixed;
    inset: 0;
    z-index: 1050;
    display: block;
    padding: 0;
    border: 0;
    background: rgba(15, 23, 42, 0.42);
  }

  .app-main,
  .app-wrapper.sidebar-collapsed .app-main {
    margin-left: 0;
    width: 100%;
    min-height: 100vh;
  }

  .app-topbar {
    min-height: 56px;
    height: auto;
    padding: 10px var(--layout-gutter-x);
    gap: 12px;
  }

  .mobile-menu-toggle {
    display: flex;
    width: 34px;
    height: 34px;
    flex-shrink: 0;
    align-items: center;
    justify-content: center;
    border: 1px solid var(--border-color-global);
    border-radius: 8px;
    background: var(--bg-card);
    color: var(--text-primary);
    cursor: pointer;
    font-size: 17px;
  }

  .app-topbar-right {
    gap: 10px;
  }
}

@media (max-width: 575.98px) {
  .app-topbar-title {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .topbar-version {
    display: none;
  }
}

/* 滚动条 */
.sidebar-nav::-webkit-scrollbar { width: 4px; height: 4px; }
.sidebar-nav::-webkit-scrollbar-thumb { background: rgba(0,0,0,0.1); border-radius: 2px; }
[data-theme='dark'] .sidebar-nav::-webkit-scrollbar-thumb { background: rgba(255,255,255,0.1); }
</style>

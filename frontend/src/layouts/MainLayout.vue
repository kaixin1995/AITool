<script setup lang="ts">
import { computed, h, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NIcon, NLayout, NLayoutContent, NLayoutSider, NMenu, NSwitch, NButton, NSpace, NText, NBadge, type MenuOption } from 'naive-ui'
import { useTheme } from '@/composables/useTheme'
import { useAuthStore } from '@/stores/auth'
import { AppVersion, version } from '@/composables/useVersion'

const router = useRouter()
const route = useRoute()
const auth = useAuthStore()
const { isDark, toggleTheme } = useTheme()

const collapsed = ref(false)

// 图标用内联 SVG（避免引入图标库增加体积）。
function renderIcon(svg: string) {
  return () => h(NIcon, null, { default: () => h('span', { innerHTML: svg, style: 'display:inline-flex' }) })
}

// 通用图标
const icons = {
  dashboard: '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/></svg>',
  chat: '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>',
  site: '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="10"/><line x1="2" y1="12" x2="22" y2="12"/><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/></svg>',
  model: '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 2L2 7l10 5 10-5-10-5z"/><path d="M2 17l10 5 10-5"/><path d="M2 12l10 5 10-5"/></svg>',
  route: '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="6" cy="6" r="3"/><circle cx="18" cy="18" r="3"/><path d="M9 6h6a3 3 0 0 1 3 3v6"/></svg>',
  key: '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 2l-2 2m-7.61 7.61a5.5 5.5 0 1 1-7.778 7.778 5.5 5.5 0 0 1 7.777-7.777zm0 0L15.5 7.5m0 0l3 3L22 7l-3-3"/></svg>',
  health: '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M22 12h-4l-3 9L9 3l-3 9H2"/></svg>',
  log: '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/><line x1="8" y1="13" x2="16" y2="13"/><line x1="8" y1="17" x2="16" y2="17"/></svg>',
  chart: '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><line x1="18" y1="20" x2="18" y2="10"/><line x1="12" y1="20" x2="12" y2="4"/><line x1="6" y1="20" x2="6" y2="14"/></svg>',
  settings: '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>',
  developer: '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="16 18 22 12 16 6"/><polyline points="8 6 2 12 8 18"/></svg>',
  codex: '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="11" width="18" height="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>',
  puzzle: '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M19.439 7.85c-.049.322.059.648.289.878l1.568 1.568c.47.47.706 1.087.706 1.704s-.235 1.233-.706 1.704l-1.611 1.611a.98.98 0 0 1-.837.276c-.47-.07-.802-.48-.968-.925a2.501 2.501 0 1 0-3.214 3.214c.446.166.855.497.925.968a.979.979 0 0 1-.276.837l-1.61 1.61a2.404 2.404 0 0 1-1.705.707 2.402 2.402 0 0 1-1.704-.706l-1.568-1.568a1.026 1.026 0 0 0-.877-.29c-.493.074-.84.504-1.02.968a2.5 2.5 0 1 1-3.237-3.237c.464-.18.894-.527.967-1.02a1.026 1.026 0 0 0-.289-.877l-1.568-1.568A2.402 2.402 0 0 1 1.998 12c0-.617.236-1.234.706-1.704L4.23 8.77c.24-.24.581-.353.917-.303.515.077.877.528 1.073 1.01a2.5 2.5 0 1 0 3.259-3.259c-.482-.196-.933-.558-1.01-1.073-.05-.336.062-.676.303-.917l1.525-1.525A2.402 2.402 0 0 1 12 1.998c.617 0 1.234.236 1.704.706l1.568 1.568c.23.23.556.338.877.29.493-.074.84-.504 1.02-.968a2.5 2.5 0 1 1 3.237 3.237c-.464.18-.894.527-.967 1.02Z"/></svg>'
}

// 菜单按导航分组构建，功能开关关闭时隐藏对应分组/项。
const menuOptions = computed<MenuOption[]>(() => {
  const features = auth.status?.features
  const groups: MenuOption[] = [
    {
      type: 'group',
      label: '概览',
      key: 'g-overview',
      children: [
        { label: '仪表盘', key: 'dashboard', icon: renderIcon(icons.dashboard) },
        { label: '对话测试', key: 'chat', icon: renderIcon(icons.chat) }
      ]
    },
    {
      type: 'group',
      label: '资源管理',
      key: 'g-resource',
      children: [
        { label: '站点管理', key: 'sites', icon: renderIcon(icons.site) },
        { label: '模型库', key: 'models', icon: renderIcon(icons.model) }
      ]
    },
    {
      type: 'group',
      label: '代理配置',
      key: 'g-proxy',
      children: [
        { label: '路由规则', key: 'routes', icon: renderIcon(icons.route) },
        { label: '访问密钥', key: 'access-keys', icon: renderIcon(icons.key) },
        { label: '兼容规则', key: 'compatibility', icon: renderIcon(icons.puzzle) }
      ]
    },
    {
      type: 'group',
      label: '监控运维',
      key: 'g-monitor',
      children: [
        { label: '模型检测', key: 'detection', icon: renderIcon(icons.health) },
        { label: '检测任务', key: 'detection-tasks', icon: renderIcon(icons.health) },
        { label: '模型健康', key: 'model-health', icon: renderIcon(icons.health) },
        { label: '对话记录', key: 'conversations', icon: renderIcon(icons.chat) },
        { label: '使用日志', key: 'usage-logs', icon: renderIcon(icons.log) },
        { label: '统计分析', key: 'analytics', icon: renderIcon(icons.chart) }
      ]
    }
  ]

  // Codex 和开发者入口按功能开关显隐。
  const devItems: MenuOption[] = []
  if (features?.codexEnabled) {
    devItems.push({ label: 'Codex 账号', key: 'codex', icon: renderIcon(icons.codex) })
  }
  devItems.push({ label: '系统设置', key: 'system-settings', icon: renderIcon(icons.settings) })
  if (features?.developerEnabled) {
    devItems.push({ label: '调试追踪', key: 'developer-invocations', icon: renderIcon(icons.developer) })
  }
  groups.push({ type: 'group', label: '系统', key: 'g-system', children: devItems })

  return groups
})

// 当前激活的菜单项（取路由 name 第一段）。
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
  <NLayout has-sider style="height: 100vh">
    <NLayoutSider
      bordered
      collapse-mode="width"
      :collapsed-width="64"
      :width="240"
      :collapsed="collapsed"
      show-trigger
      @collapse="collapsed = true"
      @expand="collapsed = false"
    >
      <div class="sidebar-brand">
        <span v-if="!collapsed" class="brand-text">AI Tool 管理后台</span>
        <span v-else class="brand-icon">AT</span>
      </div>
      <NMenu
        :value="activeKey"
        :collapsed="collapsed"
        :collapsed-width="64"
        :collapsed-icon-size="22"
        :options="menuOptions"
        @update:value="handleMenuSelect"
      />
    </NLayoutSider>

    <NLayout>
      <header class="app-topbar">
        <NSpace align="center" :size="12">
          <NText strong style="font-size: 16px">{{ (route.meta.title as string) ?? 'AI Tool' }}</NText>
        </NSpace>
        <NSpace align="center" :size="16">
          <NSpace align="center" :size="8">
            <NText depth="3" style="font-size: 12px">深色</NText>
            <NSwitch :value="isDark" size="small" @update:value="toggleTheme" />
          </NSpace>
          <NText depth="3" style="font-size: 12px">v{{ version }}</NText>
          <NButton size="small" quaternary @click="handleLogout">退出</NButton>
        </NSpace>
      </header>
      <NLayoutContent class="app-content" content-style="padding: 0;" :native-scrollbar="false">
        <RouterView />
      </NLayoutContent>
    </NLayout>
  </NLayout>
</template>

<style scoped>
.sidebar-brand {
  height: 56px;
  display: flex;
  align-items: center;
  justify-content: center;
  border-bottom: 1px solid var(--n-border-color);
}
.brand-text {
  font-weight: 600;
  font-size: 15px;
  white-space: nowrap;
}
.brand-icon {
  font-weight: 700;
  font-size: 18px;
  color: #6c9eff;
}
.app-topbar {
  height: 56px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 24px;
  border-bottom: 1px solid var(--n-border-color);
}
.app-content {
  height: calc(100vh - 56px);
}
</style>

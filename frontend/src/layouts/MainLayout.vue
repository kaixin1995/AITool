<script setup lang="ts">
import { computed, h, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NLayout, NLayoutContent, NLayoutSider, NMenu, NSwitch, NButton, NSpace, NText, type MenuOption } from 'naive-ui'
import { useTheme } from '@/composables/useTheme'
import { useAuthStore } from '@/stores/auth'
import { AppVersion, version } from '@/composables/useVersion'

const router = useRouter()
const route = useRoute()
const auth = useAuthStore()
const { isDark, toggleTheme } = useTheme()

const collapsed = ref(false)

// 图标用 emoji（与原 Razor Pages 设计一致：📊💬🌐🧠等）。
function renderEmoji(emoji: string) {
  return () => h('span', { style: 'font-size: 16px; line-height: 1; display: inline-block; width: 20px; text-align: center;' }, emoji)
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
        { label: '仪表盘', key: 'dashboard', icon: renderEmoji('📊') },
        { label: '对话测试', key: 'chat', icon: renderEmoji('💬') }
      ]
    },
    {
      type: 'group',
      label: '资源管理',
      key: 'g-resource',
      children: [
        { label: '站点管理', key: 'sites', icon: renderEmoji('🌐') },
        { label: '模型库', key: 'models', icon: renderEmoji('🧠') }
      ]
    },
    {
      type: 'group',
      label: '代理配置',
      key: 'g-proxy',
      children: [
        { label: '路由规则', key: 'routes', icon: renderEmoji('🔀') },
        { label: '访问密钥', key: 'access-keys', icon: renderEmoji('🔑') },
        { label: '兼容规则', key: 'compatibility', icon: renderEmoji('🧩') }
      ]
    },
    {
      type: 'group',
      label: '监控运维',
      key: 'g-monitor',
      children: [
        { label: '模型检测', key: 'detection', icon: renderEmoji('🔍') },
        { label: '检测任务', key: 'detection-tasks', icon: renderEmoji('⏰') },
        { label: '模型健康', key: 'model-health', icon: renderEmoji('❤️') },
        { label: '对话记录', key: 'conversations', icon: renderEmoji('📝') },
        { label: '使用日志', key: 'usage-logs', icon: renderEmoji('📋') },
        { label: '统计分析', key: 'analytics', icon: renderEmoji('📈') }
      ]
    }
  ]

  // Codex 和开发者入口按功能开关显隐。
  const devItems: MenuOption[] = []
  if (features?.codexEnabled) {
    devItems.push({ label: 'Codex 账号', key: 'codex', icon: renderEmoji('🔐') })
  }
  devItems.push({ label: '系统设置', key: 'system-settings', icon: renderEmoji('⚙️') })
  if (features?.developerEnabled) {
    devItems.push({ label: '调试追踪', key: 'developer-invocations', icon: renderEmoji('🐞') })
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
      :width="260"
      :collapsed="collapsed"
      show-trigger
      @collapse="collapsed = true"
      @expand="collapsed = false"
    >
      <div class="sidebar-brand">
        <div class="brand-icon">AI</div>
        <span v-if="!collapsed" class="brand-text">AI Tool</span>
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
  height: 60px;
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 0 20px;
  border-bottom: 1px solid var(--n-border-color);
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
  color: #1E293B;
}
.app-topbar {
  height: 56px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 24px;
  border-bottom: 1px solid #E2E8F0;
  background: #FFFFFF;
}
.app-content {
  height: calc(100vh - 56px);
  background: #F8FAFC;
}
</style>

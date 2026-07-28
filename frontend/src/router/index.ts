import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import { useAuthStore } from '@/stores/auth'

// 路由定义：与原 Razor Pages 导航一一对应。
// 第一批（API 已就绪）的页面先占位，阶段 4 逐步实现。
const routes: RouteRecordRaw[] = [
  { path: '/login', name: 'login', component: () => import('@/views/LoginView.vue'), meta: { public: true, title: '登录' } },
  {
    path: '/',
    component: () => import('@/layouts/MainLayout.vue'),
    children: [
      { path: '', name: 'dashboard', component: () => import('@/views/DashboardView.vue'), meta: { title: '仪表盘' } },
      { path: 'analytics', name: 'analytics', component: () => import('@/views/AnalyticsView.vue'), meta: { title: '可视化分析' } },
      { path: 'chat', name: 'chat', component: () => import('@/views/ChatView.vue'), meta: { title: '对话' } },
      { path: 'sites', name: 'sites', component: () => import('@/views/SitesView.vue'), meta: { title: '站点管理' } },
      { path: 'codex', name: 'codex', component: () => import('@/views/CodexView.vue'), meta: { title: 'OAuth 管理', requiresCodex: true } },
      { path: 'models', name: 'models', component: () => import('@/views/ModelsView.vue'), meta: { title: '模型库' } },
      { path: 'routes', name: 'routes', component: () => import('@/views/RoutesView.vue'), meta: { title: '路由规则' } },
      { path: 'compatibility', name: 'compatibility', component: () => import('@/views/CompatibilityView.vue'), meta: { title: '兼容规则集' } },
      { path: 'access-keys', name: 'access-keys', component: () => import('@/views/AccessKeysView.vue'), meta: { title: '访问密钥' } },
      { path: 'detection', name: 'detection', component: () => import('@/views/DetectionView.vue'), meta: { title: '模型检测' } },
      { path: 'detection-tasks', name: 'detection-tasks', component: () => import('@/views/DetectionTasksView.vue'), meta: { title: '检测任务' } },
      { path: 'model-health', name: 'model-health', component: () => import('@/views/ModelHealthView.vue'), meta: { title: '模型健康' } },
      { path: 'developer/invocations', name: 'developer-invocations', component: () => import('@/views/DeveloperInvocationsView.vue'), meta: { title: '调试工具', requiresDeveloper: true } },
      { path: 'usage-logs', name: 'usage-logs', component: () => import('@/views/UsageLogsView.vue'), meta: { title: '使用日志' } },
      { path: 'system/settings', name: 'system-settings', component: () => import('@/views/SystemSettingsView.vue'), meta: { title: '系统设置' } }
    ]
  },
  { path: '/:pathMatch(.*)*', redirect: '/' }
]

const router = createRouter({
  history: createWebHistory('./'),
  routes
})

// 全局前置守卫：未登录跳登录页（公开页除外）；功能开关控制菜单可访问性。
router.beforeEach(async (to) => {
  const auth = useAuthStore()

  // 公开页面（登录页）直接放行。
  if (to.meta.public) {
    return true
  }

  // 拉取认证状态（如果还没有），用于判断是否已设密码 + 功能开关。
  if (!auth.status) {
    try {
      await auth.fetchStatus()
    } catch {
      // 拉取失败时不阻断，让后续 isAuthenticated 判断处理。
    }
  }

  // 未设密码 → 强制走首次设置（登录页会自动切到 setup 模式）。
  if (auth.status && !auth.status.hasPassword) {
    return { name: 'login', query: { returnUrl: to.fullPath } }
  }

  // 已设密码但未登录 → 跳登录。
  if (!auth.isAuthenticated()) {
    return { name: 'login', query: { returnUrl: to.fullPath } }
  }

  // 功能开关：未开启对应功能的页面重定向到仪表盘，避免空白页。
  const features = auth.status?.features
  if (to.meta.requiresCodex && !features?.codexEnabled) {
    return { name: 'dashboard' }
  }
  if (to.meta.requiresDeveloper && !features?.developerEnabled) {
    return { name: 'dashboard' }
  }

  return true
})

export default router

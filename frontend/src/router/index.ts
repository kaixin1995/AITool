import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import { useAuthStore } from '@/stores/auth'

// 路由定义：与原 Razor Pages 导航一一对应。
// 第一批（API 已就绪）的页面先占位，阶段 4 逐步实现。
const routes: RouteRecordRaw[] = [
  { path: '/login', name: 'login', component: () => import('@/views/LoginView.vue'), meta: { public: true, title: '登录' } },
  {
    path: '/Admin/ClientSimulator',
    redirect: to => ({
      path: '/developer/invocations',
      query: to.query,
      hash: '#developerSimulatorPane'
    })
  },
  {
    path: '/Admin/Developer/Invocations',
    redirect: to => ({
      path: '/developer/invocations',
      query: to.query,
      hash: to.hash
    })
  },
  {
    path: '/Admin/ModelHealth',
    redirect: to => ({
      path: '/model-health',
      query: to.query
    })
  },
  {
    // 兼容旧书签：OAuth 管理页的规范地址为 /oauth。
    path: '/codex',
    redirect: to => ({
      path: '/oauth',
      query: to.query,
      hash: to.hash
    })
  },
  {
    path: '/Admin/Analytics',
    redirect: to => ({
      path: '/analytics',
      query: to.query,
      hash: to.hash
    })
  },
  {
    path: '/',
    component: () => import('@/layouts/MainLayout.vue'),
    children: [
      { path: '', name: 'dashboard', component: () => import('@/views/DashboardView.vue'), meta: { title: '仪表盘' } },
      { path: 'analytics', name: 'analytics', component: () => import('@/views/AnalyticsView.vue'), meta: { title: '可视化分析' } },
      { path: 'chat', name: 'chat', component: () => import('@/views/ChatView.vue'), meta: { title: '对话' } },
      { path: 'sites', name: 'sites', component: () => import('@/views/SitesView.vue'), meta: { title: '站点管理' } },
      { path: 'oauth', name: 'oauth', component: () => import('@/views/OAuthView.vue'), meta: { title: 'OAuth 管理', requiresOAuth: true } },
      { path: 'models', name: 'models', component: () => import('@/views/ModelsView.vue'), meta: { title: '模型库' } },
      { path: 'routes', name: 'routes', component: () => import('@/views/RouteManagementView.vue'), meta: { title: '路由管理' } },
      { path: 'access-keys', name: 'access-keys', component: () => import('@/views/AccessKeysView.vue'), meta: { title: '访问密钥' } },
      {
        path: 'route-fallback',
        name: 'route-fallback',
        redirect: to => ({ name: 'model-health', query: to.query })
      },
      {
        path: 'compatibility',
        name: 'compatibility',
        redirect: to => ({ name: 'routes', query: to.query, hash: '#compatibility' })
      },
      { path: 'detection', name: 'detection', component: () => import('@/views/DetectionManagementView.vue'), meta: { title: '模型检测' } },
      {
        path: 'detection-tasks',
        name: 'detection-tasks',
        redirect: to => ({ name: 'detection', query: to.query, hash: '#tasks' })
      },
      { path: 'model-health', name: 'model-health', component: () => import('@/views/ModelHealthManagementView.vue'), meta: { title: '模型健康' } },
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
  const oauthEnabled = features?.oauthEnabled ?? features?.codexEnabled
  if (to.meta.requiresOAuth && !oauthEnabled) {
    return { name: 'dashboard' }
  }
  const developerEnabled = features?.developerEnabled ?? false
  if (to.meta.requiresDeveloper && !developerEnabled) {
    return { name: 'dashboard' }
  }
  // 旧 Hash 兼容重定向：并发检测与熔断监控已迁移至「模型健康」页面
  if (to.path === '/developer/invocations' || to.path === '/Admin/Developer/Invocations') {
    if (to.hash === '#developerConcurrencyPane' || to.hash === '#concurrency') {
      return { path: '/model-health', query: to.query, hash: '#concurrency' }
    }
    if (to.hash === '#developerCircuitBreakerPane' || to.hash === '#circuit-breaker') {
      return { path: '/model-health', query: to.query, hash: '#circuit-breaker' }
    }
  }

  return true
})

export default router

import { computed } from 'vue'
import { useAuthStore } from '@/stores/auth'

// 版本号与编译时间从后端 auth/status 获取（对应 Program.cs 的 applicationVersion）。
// store 在应用启动时已拉取 status，这里直接读它的 version/buildTime。
// 若 status 尚未就绪（极端情况），回退到 vite 注入的构建版本号，避免右上角空白。
const fallbackVersion = __APP_VERSION__

// 版本号：与后端 AppVersionInfo.Value 对齐
export const version = computed(() => useAuthStore().status?.version ?? fallbackVersion)

// 编译时间：与后端 AppVersionInfo.BuildTime 对齐，用于确认运行的程序是否是最新版本
export const buildTime = computed(() => useAuthStore().status?.buildTime ?? '')

// 格式化编译时间用于展示（后端返回 ISO 字符串，转成本地可读格式）
export const buildTimeDisplay = computed(() => {
  const raw = buildTime.value
  if (!raw) return ''
  const d = new Date(raw)
  if (Number.isNaN(d.getTime())) return raw
  // 年-月-日 时:分
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`
})

// 占位导出，保留与后端 AppVersionInfo 类对应的命名。
export const AppVersion = { value: version }

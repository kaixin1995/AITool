import { computed, ref, watch } from 'vue'
import { darkTheme, type GlobalThemeOverrides } from 'naive-ui'

// 复刻后端 theme.css 的主色 #6C9EFF（柔和蓝）与圆角。
// Naive UI 通过 GlobalThemeOverrides 一处配置全局生效。
const themeOverrides: GlobalThemeOverrides = {
  common: {
    primaryColor: '#6C9EFF',
    primaryColorHover: '#85B0FF',
    primaryColorPressed: '#5A8EE8',
    primaryColorSuppl: '#6C9EFF',
    borderRadius: '8px',
    borderRadiusSmall: '6px'
  }
}

const THEME_KEY = 'aitool.theme'

// 全局主题状态（响应式），持久化到 localStorage。
const isDark = ref<boolean>(loadInitialTheme())

function loadInitialTheme(): boolean {
  const stored = localStorage.getItem(THEME_KEY)
  if (stored === 'dark') return true
  if (stored === 'light') return false
  // 默认跟随系统偏好。
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false
}

watch(isDark, (dark) => {
  localStorage.setItem(THEME_KEY, dark ? 'dark' : 'light')
  // 同步设置 <html> 上的 data-theme 属性，便于自定义 CSS 适配（与原 theme.css 的暗色选择器一致）。
  document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light')
})

export function useTheme() {
  const naiveTheme = computed(() => (isDark.value ? darkTheme : null))
  function toggleTheme(): void {
    isDark.value = !isDark.value
  }
  return { isDark, naiveTheme, themeOverrides, toggleTheme }
}

import { computed, ref, watch } from 'vue'
import { darkTheme, type GlobalThemeOverrides } from 'naive-ui'

// 复刻原 theme.css 的设计语言：主色 #6C9EFF + 丰富的语义色 + 圆角 + 细腻阴影。
// 对齐原设计 token，确保 Vue 版视觉与原 Razor Pages 一致。
const themeOverrides: GlobalThemeOverrides = {
  common: {
    primaryColor: '#6C9EFF',
    primaryColorHover: '#5A8EF5',
    primaryColorPressed: '#4A7DE0',
    primaryColorSuppl: '#6C9EFF',
    infoColor: '#60A5FA',
    infoColorHover: '#3B82F6',
    successColor: '#34D399',
    successColorHover: '#16A34A',
    warningColor: '#FBBF24',
    warningColorHover: '#D97706',
    errorColor: '#F87171',
    errorColorHover: '#DC2626',
    borderRadius: '12px',
    borderRadiusSmall: '8px',
    // 页面背景色：原设计用 #F8FAFC（浅灰蓝），比纯白更柔和
    bodyColor: '#F8FAFC',
    cardColor: '#FFFFFF',
    modalColor: '#FFFFFF',
    popoverColor: '#FFFFFF',
    // 文字色对齐原设计
    textColorBase: '#1E293B',
    textColor1: '#1E293B',
    textColor2: '#64748B',
    textColor3: '#94A3B8',
    // 边框色
    borderColor: '#E2E8F0',
    dividerColor: '#F1F5F9'
  },
  Card: {
    // 细腻的多层阴影，对齐原 --shadow
    boxShadow: '0 1px 3px rgba(0, 0, 0, 0.05), 0 1px 2px rgba(0, 0, 0, 0.03)',
    borderRadius: '12px',
    paddingMedium: '20px'
  },
  Menu: {
    // 侧边栏菜单：活跃项浅蓝背景 + 圆角，对齐原 sidebar-active-bg
    itemColorActive: '#EEF4FF',
    itemColorActiveHover: '#EEF4FF',
    itemColorActiveCollapsed: '#EEF4FF',
    itemTextColorActive: '#6C9EFF',
    itemTextColorActiveHover: '#6C9EFF',
    itemTextColorHorizontalActive: '#6C9EFF',
    itemIconColorActive: '#6C9EFF',
    itemIconColorActiveHover: '#6C9EFF',
    borderRadius: '8px'
  },
  Button: {
    borderRadiusMedium: '8px',
    borderRadiusSmall: '6px'
  }
}

// 暗色主题覆盖
const darkOverrides: GlobalThemeOverrides = {
  common: {
    primaryColor: '#6C9EFF',
    primaryColorHover: '#85B0FF',
    primaryColorPressed: '#5A8EE8',
    primaryColorSuppl: '#6C9EFF',
    // 暗色模式完整色板：页面背景、卡片、弹窗、输入框全部跟随，避免白色穿透。
    bodyColor: '#101014',
    cardColor: '#18181C',
    modalColor: '#1F1F24',
    popoverColor: '#1F1F24',
    tableColor: '#18181C',
    tableHeaderColor: '#202026',
    inputColor: '#1F1F24',
    inputColorDisabled: '#1A1A1F',
    actionColor: '#202026',
    hoverColor: '#25252B',
    borderColor: '#2D2D33',
    dividerColor: '#25252B',
    textColorBase: 'rgba(255, 255, 255, 0.82)',
    textColor1: 'rgba(255, 255, 255, 0.82)',
    textColor2: 'rgba(255, 255, 255, 0.65)',
    textColor3: 'rgba(255, 255, 255, 0.45)'
  },
  Card: {
    color: '#18181C',
    colorModal: '#1F1F24',
    colorPopover: '#1F1F24'
  },
  Menu: {
    itemColorActive: 'rgba(108, 158, 255, 0.15)',
    itemColorActiveHover: 'rgba(108, 158, 255, 0.18)',
    itemColorActiveCollapsed: 'rgba(108, 158, 255, 0.15)'
  }
}

const THEME_KEY = 'aitool.theme'

const isDark = ref<boolean>(loadInitialTheme())

function loadInitialTheme(): boolean {
  const stored = localStorage.getItem(THEME_KEY)
  if (stored === 'dark') return true
  if (stored === 'light') return false
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false
}

function applyThemeAttribute(dark: boolean): void {
  document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light')
}

// 初始化即应用 data-theme，避免首屏闪白 / 首屏暗色模式下 body 仍是白色。
applyThemeAttribute(isDark.value)

watch(isDark, (dark) => {
  localStorage.setItem(THEME_KEY, dark ? 'dark' : 'light')
  applyThemeAttribute(dark)
})

export function useTheme() {
  const naiveTheme = computed(() => (isDark.value ? darkTheme : null))
  const effectiveOverrides = computed(() => (isDark.value ? { ...themeOverrides, ...darkOverrides } : themeOverrides))
  function toggleTheme(): void {
    isDark.value = !isDark.value
  }
  return { isDark, naiveTheme, themeOverrides: effectiveOverrides, toggleTheme }
}

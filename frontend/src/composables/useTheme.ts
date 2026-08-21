import { computed, ref, watch } from 'vue'
import { darkTheme, type GlobalThemeOverrides } from 'naive-ui'

export type SkinMode = 'classic' | 'modern' | 'cyberpunk' | 'nordic'

const THEME_KEY = 'aitool.theme'
const SKIN_KEY = 'aitool.skin'

const isDark = ref<boolean>(loadInitialTheme())
const skin = ref<SkinMode>(loadInitialSkin())

function loadInitialTheme(): boolean {
  const stored = localStorage.getItem(THEME_KEY)
  if (stored === 'dark') return true
  if (stored === 'light') return false
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false
}

function loadInitialSkin(): SkinMode {
  const stored = localStorage.getItem(SKIN_KEY)
  if (stored === 'modern' || stored === 'classic' || stored === 'cyberpunk' || stored === 'nordic') {
    return stored
  }
  return 'modern'
}

// 1. 经典原版主题覆盖 (Classic)
const classicThemeOverrides: GlobalThemeOverrides = {
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
    bodyColor: '#F8FAFC',
    cardColor: '#FFFFFF',
    modalColor: '#FFFFFF',
    popoverColor: '#FFFFFF',
    textColorBase: '#1E293B',
    textColor1: '#1E293B',
    textColor2: '#64748B',
    textColor3: '#94A3B8',
    borderColor: '#E2E8F0',
    dividerColor: '#F1F5F9'
  },
  Card: {
    boxShadow: '0 1px 3px rgba(0, 0, 0, 0.05), 0 1px 2px rgba(0, 0, 0, 0.03)',
    borderRadius: '12px',
    paddingMedium: '20px'
  },
  Menu: {
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
    borderRadiusSmall: '6px',
    heightMedium: '36px',
    heightSmall: '30px',
    heightTiny: '24px',
    fontSizeMedium: '13.5px'
  }
}

// 2. 现代化 AI 科技质感皮肤覆盖 (Modern)
const modernThemeOverrides: GlobalThemeOverrides = {
  common: {
    primaryColor: '#6366F1', // Indigo
    primaryColorHover: '#4F46E5',
    primaryColorPressed: '#4338CA',
    primaryColorSuppl: '#6366F1',
    infoColor: '#0EA5E9',
    infoColorHover: '#0284C7',
    successColor: '#10B981',
    successColorHover: '#059669',
    warningColor: '#F59E0B',
    warningColorHover: '#D97706',
    errorColor: '#EF4444',
    errorColorHover: '#DC2626',
    borderRadius: '14px',
    borderRadiusSmall: '10px',
    bodyColor: '#F4F6F9',
    cardColor: '#FFFFFF',
    modalColor: '#FFFFFF',
    popoverColor: '#FFFFFF',
    textColorBase: '#0F172A',
    textColor1: '#0F172A',
    textColor2: '#475569',
    textColor3: '#94A3B8',
    borderColor: '#E2E8F0',
    dividerColor: '#F1F5F9'
  },
  Card: {
    boxShadow: '0 4px 20px -2px rgba(15, 23, 42, 0.06), 0 2px 6px -1px rgba(15, 23, 42, 0.03)',
    borderRadius: '16px',
    paddingMedium: '22px'
  },
  Menu: {
    itemColorActive: 'rgba(99, 102, 241, 0.1)',
    itemColorActiveHover: 'rgba(99, 102, 241, 0.15)',
    itemColorActiveCollapsed: 'rgba(99, 102, 241, 0.1)',
    itemTextColorActive: '#6366F1',
    itemTextColorActiveHover: '#4F46E5',
    itemTextColorHorizontalActive: '#6366F1',
    itemIconColorActive: '#6366F1',
    itemIconColorActiveHover: '#4F46E5',
    borderRadius: '10px'
  },
  Button: {
    borderRadiusMedium: '10px',
    borderRadiusSmall: '8px',
    heightMedium: '36px',
    heightSmall: '30px',
    heightTiny: '24px',
    fontSizeMedium: '13.5px'
  }
}

// 3. 赛博朋克 / 极客霓虹 (Cyberpunk / Terminal)
const cyberpunkThemeOverrides: GlobalThemeOverrides = {
  common: {
    primaryColor: '#00F0FF', // 霓虹青
    primaryColorHover: '#38F9D7',
    primaryColorPressed: '#00B4D8',
    primaryColorSuppl: '#00F0FF',
    infoColor: '#00F0FF',
    successColor: '#00FF66', // 荧光绿
    warningColor: '#FFE600', // 警示黄
    errorColor: '#FF0055',   // 霓虹品红
    borderRadius: '4px',     // 锋利硬朗直角
    borderRadiusSmall: '2px',
    bodyColor: '#0D0E15',
    cardColor: '#141622',
    modalColor: '#1A1D2D',
    popoverColor: '#1A1D2D',
    textColorBase: '#00F0FF',
    textColor1: '#E2E8F0',
    textColor2: '#94A3B8',
    textColor3: '#64748B',
    borderColor: '#00F0FF40',
    dividerColor: '#1F2438'
  },
  Card: {
    borderRadius: '4px',
    boxShadow: '0 0 15px rgba(0, 240, 255, 0.15), inset 0 0 1px rgba(0, 240, 255, 0.3)'
  },
  Button: {
    borderRadiusMedium: '4px',
    borderRadiusSmall: '2px'
  }
}

// 4. 北欧极简极净 (Nordic Minimalist)
const nordicThemeOverrides: GlobalThemeOverrides = {
  common: {
    primaryColor: '#2B4C6F', // 冰岛深海蓝灰
    primaryColorHover: '#3A638F',
    primaryColorPressed: '#1F3854',
    primaryColorSuppl: '#2B4C6F',
    infoColor: '#5C7C9E',
    successColor: '#4A7C59', // 苔藓绿
    warningColor: '#C48B47', // 暖木色
    errorColor: '#B85450',   // 砖红
    borderRadius: '20px',    // 极致圆润超大圆角
    borderRadiusSmall: '12px',
    bodyColor: '#F9FBFB',
    cardColor: '#FFFFFF',
    modalColor: '#FFFFFF',
    popoverColor: '#FFFFFF',
    textColorBase: '#1D2A3A',
    textColor1: '#1D2A3A',
    textColor2: '#687A8F',
    textColor3: '#9EAEC0',
    borderColor: '#E5ECF2',
    dividerColor: '#F0F5F8'
  },
  Card: {
    borderRadius: '20px',
    boxShadow: '0 20px 40px -15px rgba(43, 76, 111, 0.07)'
  },
  Button: {
    borderRadiusMedium: '14px',
    borderRadiusSmall: '10px'
  }
}

// 经典暗色
const classicDarkOverrides: GlobalThemeOverrides = {
  common: {
    primaryColor: '#6C9EFF',
    primaryColorHover: '#85B0FF',
    primaryColorPressed: '#5A8EE8',
    primaryColorSuppl: '#6C9EFF',
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
  }
}

// 现代质感暗色
const modernDarkOverrides: GlobalThemeOverrides = {
  common: {
    primaryColor: '#818CF8',
    primaryColorHover: '#9FA8FF',
    primaryColorPressed: '#6366F1',
    primaryColorSuppl: '#818CF8',
    bodyColor: '#0B0F19',
    cardColor: '#111827',
    modalColor: '#1E293B',
    popoverColor: '#1E293B',
    tableColor: '#111827',
    tableHeaderColor: '#1F2937',
    inputColor: '#1F2937',
    inputColorDisabled: '#111827',
    actionColor: '#1F2937',
    hoverColor: '#374151',
    borderColor: '#1F2937',
    dividerColor: '#1F2937',
    textColorBase: '#F8FAFC',
    textColor1: '#F8FAFC',
    textColor2: '#94A3B8',
    textColor3: '#64748B'
  },
  Card: {
    color: '#111827',
    colorModal: '#1E293B',
    colorPopover: '#1E293B',
    boxShadow: '0 4px 25px rgba(0, 0, 0, 0.4)'
  }
}

// 赛博朋克暗色（原生即暗黑）
const cyberpunkDarkOverrides: GlobalThemeOverrides = {
  common: {
    bodyColor: '#05070D',
    cardColor: '#0C0F1D',
    modalColor: '#14182E',
    popoverColor: '#14182E',
    borderColor: '#00F0FF50',
    textColorBase: '#E2E8F0',
    textColor1: '#FFFFFF'
  },
  Card: {
    boxShadow: '0 0 20px rgba(0, 240, 255, 0.2), inset 0 0 2px rgba(255, 0, 85, 0.4)'
  }
}

// 北欧极简暗色（极夜灰调）
const nordicDarkOverrides: GlobalThemeOverrides = {
  common: {
    primaryColor: '#7A9CBF',
    bodyColor: '#13181F',
    cardColor: '#1C232D',
    modalColor: '#232D3A',
    popoverColor: '#232D3A',
    borderColor: '#283442',
    dividerColor: '#283442',
    textColorBase: '#E5ECF2',
    textColor1: '#FFFFFF'
  },
  Card: {
    boxShadow: '0 20px 40px -15px rgba(0, 0, 0, 0.5)'
  }
}

function applyAttributes(dark: boolean, sk: SkinMode): void {
  document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light')
  document.documentElement.setAttribute('data-skin', sk)
}

// 初始化
applyAttributes(isDark.value, skin.value)

watch([isDark, skin], ([dark, sk]) => {
  localStorage.setItem(THEME_KEY, dark ? 'dark' : 'light')
  localStorage.setItem(SKIN_KEY, sk)
  applyAttributes(dark, sk)
})

export function useTheme() {
  const naiveTheme = computed(() => (isDark.value ? darkTheme : null))
  const effectiveOverrides = computed(() => {
    switch (skin.value) {
      case 'modern':
        return isDark.value ? { ...modernThemeOverrides, ...modernDarkOverrides } : modernThemeOverrides
      case 'cyberpunk':
        return isDark.value ? { ...cyberpunkThemeOverrides, ...cyberpunkDarkOverrides } : cyberpunkThemeOverrides
      case 'nordic':
        return isDark.value ? { ...nordicThemeOverrides, ...nordicDarkOverrides } : nordicThemeOverrides
      case 'classic':
      default:
        return isDark.value ? { ...classicThemeOverrides, ...classicDarkOverrides } : classicThemeOverrides
    }
  })

  function toggleTheme(): void {
    isDark.value = !isDark.value
  }

  function setSkin(newSkin: SkinMode): void {
    skin.value = newSkin
  }

  function toggleSkin(): void {
    const skins: SkinMode[] = ['modern', 'cyberpunk', 'nordic', 'classic']
    const nextIdx = (skins.indexOf(skin.value) + 1) % skins.length
    skin.value = skins[nextIdx]
  }

  return {
    isDark,
    skin,
    naiveTheme,
    themeOverrides: effectiveOverrides,
    toggleTheme,
    setSkin,
    toggleSkin
  }
}

import { describe, expect, it } from 'vitest'
import {
  CUSTOM_PRESET_ID,
  SITE_VENDOR_PRESETS,
  applyVendorPreset,
  buildVendorPresetOptions,
  findVendorPreset,
  type VendorPresetFormShape
} from './siteVendorPresets'

function emptyForm(): VendorPresetFormShape {
  return {
    name: '',
    baseUrl: '',
    endpointPathMode: 'standard-root',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false
  }
}

describe('站点厂商预设 - 目录完整性', () => {
  it('id 与 baseUrl 目录内唯一', () => {
    const ids = SITE_VENDOR_PRESETS.map(p => p.id)
    expect(new Set(ids).size).toBe(ids.length)
    const urls = SITE_VENDOR_PRESETS.map(p => p.baseUrl)
    expect(new Set(urls).size).toBe(urls.length)
  })

  it('每个预设字段合法：http(s) 地址、路径模式枚举、至少一个协议开关', () => {
    for (const p of SITE_VENDOR_PRESETS) {
      expect(p.baseUrl).toMatch(/^https?:\/\//)
      expect(['standard-root', 'versioned-base']).toContain(p.endpointPathMode)
      expect(p.supportsOpenAi || p.supportsAnthropic || p.supportsResponses).toBe(true)
      expect(['cn', 'global', 'local']).toContain(p.group)
    }
  })

  it('用户在用的六个厂商端点全部收录', () => {
    const urls = SITE_VENDOR_PRESETS.map(p => p.baseUrl)
    expect(urls).toContain('https://api.deepseek.com')
    expect(urls).toContain('https://api.z.ai/api/coding/paas/v4')
    expect(urls).toContain('https://api.groq.com/openai')
    expect(urls).toContain('https://integrate.api.nvidia.com')
    expect(urls).toContain('https://token.sensenova.cn')
    expect(urls).toContain('http://localhost:11434')
  })

  it('智谱编程套餐预设标记为支持额度查询', () => {
    expect(findVendorPreset('zhipu-cn')?.quotaAvailable).toBe(true)
    expect(findVendorPreset('zhipu-intl')?.quotaAvailable).toBe(true)
    expect(findVendorPreset('openai')?.quotaAvailable).toBeUndefined()
  })

  it('智谱编程套餐端点为 OpenAI 协议（cc-switch pi/hermes/opencode 三处实证），Anthropic 走专用端点', () => {
    const coding = emptyForm()
    applyVendorPreset(coding, 'zhipu-intl', null)
    expect(coding.baseUrl).toBe('https://api.z.ai/api/coding/paas/v4')
    expect(coding.endpointPathMode).toBe('versioned-base')
    expect(coding.supportsOpenAi).toBe(true)
    expect(coding.supportsAnthropic).toBe(false)

    const anthropic = emptyForm()
    applyVendorPreset(anthropic, 'zhipu-anthropic-intl', null)
    expect(anthropic.baseUrl).toBe('https://api.z.ai/api/anthropic')
    expect(anthropic.endpointPathMode).toBe('standard-root')
    expect(anthropic.supportsAnthropic).toBe(true)
    expect(anthropic.supportsOpenAi).toBe(false)

    const anthropicCn = emptyForm()
    applyVendorPreset(anthropicCn, 'zhipu-anthropic-cn', null)
    expect(anthropicCn.baseUrl).toBe('https://open.bigmodel.cn/api/anthropic')
    expect(anthropicCn.supportsAnthropic).toBe(true)
  })

  it('下拉选项：自定义在首位，分组覆盖全部预设', () => {
    const options = buildVendorPresetOptions()
    expect(options[0]).toEqual({ label: '自定义', value: CUSTOM_PRESET_ID })
    const grouped = options.filter(o => 'type' in o && o.type === 'group') as Array<{ children: { value: string }[] }>
    const optionValues = grouped.flatMap(g => g.children.map(c => c.value))
    expect(optionValues).toHaveLength(SITE_VENDOR_PRESETS.length)
  })
})

describe('站点厂商预设 - 表单应用', () => {
  it('选中预设填充地址/路径模式/协议，并默认带出名称', () => {
    const form = emptyForm()
    const applied = applyVendorPreset(form, 'groq', null)

    expect(applied).toBe('groq')
    expect(form.name).toBe('Groq')
    expect(form.baseUrl).toBe('https://api.groq.com/openai')
    expect(form.endpointPathMode).toBe('standard-root')
    expect(form.supportsOpenAi).toBe(true)
    expect(form.supportsAnthropic).toBe(false)
  })

  it('OpenAI 预设同时开启 Chat 与 Responses', () => {
    const form = emptyForm()
    applyVendorPreset(form, 'openai', null)
    expect(form.supportsOpenAi).toBe(true)
    expect(form.supportsResponses).toBe(true)
    expect(form.endpointPathMode).toBe('versioned-base')
  })

  it('切回自定义不改任何字段', () => {
    const form = emptyForm()
    applyVendorPreset(form, 'groq', null)
    const before = { ...form }

    const applied = applyVendorPreset(form, CUSTOM_PRESET_ID, 'groq')

    expect(applied).toBe(CUSTOM_PRESET_ID)
    expect(form).toEqual(before)
  })

  it('用户手改过的名称不被预设覆盖', () => {
    const form = emptyForm()
    form.name = '我的专属站点'
    applyVendorPreset(form, 'groq', null)
    expect(form.name).toBe('我的专属站点')
  })

  it('上一个预设自动填入的名称会被新预设更新', () => {
    const form = emptyForm()
    applyVendorPreset(form, 'groq', null)
    expect(form.name).toBe('Groq')

    applyVendorPreset(form, 'deepseek', 'groq')
    expect(form.name).toBe('DeepSeek')
  })

  it('未知预设 id 按自定义处理（防御旧会话/脏数据）', () => {
    const form = emptyForm()
    form.name = 'x'
    form.baseUrl = 'https://example.com'
    const applied = applyVendorPreset(form, 'no-such-preset', null)
    expect(applied).toBe(CUSTOM_PRESET_ID)
    expect(form.baseUrl).toBe('https://example.com')
  })
})

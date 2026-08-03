import { describe, expect, it } from 'vitest'
import type { ModelVendorCatalog } from '@/api/models'
import {
  buildVendorIconMarkup,
  extractSvgBody,
  removeVendorAt,
  renameVendor,
} from './vendorCatalogState'

describe('vendorCatalogState', () => {
  it('将 SVG body 包装成可显示的完整图标', () => {
    const markup = buildVendorIconMarkup('<path d="M0 0h24v24z" />')

    expect(markup).toContain('<svg viewBox="0 0 24 24"')
    expect(markup).toContain('<path d="M0 0h24v24z" />')
  })

  it('保留完整 SVG 的原始 viewBox', () => {
    expect(extractSvgBody('<svg viewBox="0 0 1024 1024"><path d="M1 1" /></svg>'))
      .toBe('<svg viewBox="0 0 1024 1024"><path d="M1 1" /></svg>')
  })

  it('厂商改名时同步更新其匹配规则', () => {
    const catalog: ModelVendorCatalog = {
      vendors: [{ vendorName: '旧厂商', iconSvgBody: '', headerBackground: '#fff', sortOrder: 1 }],
      rules: [{ vendorName: '旧厂商', matchType: 'wildcard', pattern: 'old-*', priority: 10 }],
    }

    renameVendor(catalog, 0, '新厂商')

    expect(catalog.vendors[0].vendorName).toBe('新厂商')
    expect(catalog.rules[0].vendorName).toBe('新厂商')
  })

  it('删除厂商时一并删除其匹配规则', () => {
    const catalog: ModelVendorCatalog = {
      vendors: [{ vendorName: '待删除', iconSvgBody: '', headerBackground: '#fff', sortOrder: 1 }],
      rules: [
        { vendorName: '待删除', matchType: 'exact', pattern: 'a', priority: 1 },
        { vendorName: '保留', matchType: 'exact', pattern: 'b', priority: 2 },
      ],
    }

    removeVendorAt(catalog, 0)

    expect(catalog.vendors).toHaveLength(0)
    expect(catalog.rules).toEqual([
      { vendorName: '保留', matchType: 'exact', pattern: 'b', priority: 2 },
    ])
  })
})

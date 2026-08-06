import { describe, expect, it } from 'vitest'
import {
  buildSelectedSitesExportJson,
  parseSitesImportText,
  updateSitesSelection
} from './sitesState'

describe('Sites 导入解析', () => {
  it('解析旧页面 TSV 粘贴格式并跳过表头', () => {
    const result = parseSitesImportText([
      'site_name\tsite_url\tKey',
      'OpenAI\thttps://api.openai.com\tsk-live',
      'Claude\thttps://api.anthropic.com\tsk-ant-live'
    ].join('\n'))

    expect(result.error).toBeUndefined()
    expect(result.items).toEqual([
      expect.objectContaining({
        name: 'OpenAI',
        baseUrl: 'https://api.openai.com',
        endpointPathMode: 'standard-root',
        apiKey: 'sk-live',
        protocolType: 'OpenAI',
        supportsOpenAi: true,
        supportsAnthropic: false,
        selected: true
      }),
      expect.objectContaining({
        name: 'Claude',
        protocolType: 'Anthropic',
        supportsOpenAi: false,
        supportsAnthropic: true,
        selected: true
      })
    ])
  })

  it('解析 JSON 文本并保留接口路径和协议能力', () => {
    const result = parseSitesImportText(JSON.stringify([
      {
        name: 'Responses',
        baseUrl: 'https://example.com/v1',
        endpointPathMode: 'versioned-base',
        apiKey: 'sk-resp',
        supportsOpenAi: false,
        supportsAnthropic: false
      }
    ]))

    expect(result.error).toBeUndefined()
    expect(result.items[0]).toEqual(expect.objectContaining({
      endpointPathMode: 'versioned-base',
      protocolType: 'Responses',
      supportsOpenAi: false,
      supportsAnthropic: false
    }))
  })

  it('无有效数据时返回错误', () => {
    expect(parseSitesImportText('name\turl').error).toBe('未能解析到有效数据，请检查格式')
    expect(parseSitesImportText('{bad json').error).toContain('JSON 解析失败')
  })
})

describe('Sites 导入导出选择', () => {
  const sites = [
    { id: 'a', name: 'A', baseUrl: 'https://a.test', endpointPathMode: 'standard-root', apiKey: 'sk-a', supportsOpenAi: true, supportsAnthropic: false },
    { id: 'b', name: 'B', baseUrl: 'https://b.test', endpointPathMode: 'versioned-base', apiKey: 'sk-b', supportsOpenAi: false, supportsAnthropic: true }
  ]

  it('导出 JSON 只包含选中的站点', () => {
    expect(buildSelectedSitesExportJson(sites, ['b'])).toBe(JSON.stringify([sites[1]], null, 2))
  })

  it('按索引更新导入预览选择状态', () => {
    const updated = updateSitesSelection([
      { name: 'A', baseUrl: 'https://a.test', apiKey: 'sk-a', selected: true },
      { name: 'B', baseUrl: 'https://b.test', apiKey: 'sk-b', selected: true }
    ], 1, false)

    expect(updated.map(item => item.selected)).toEqual([true, false])
  })
})

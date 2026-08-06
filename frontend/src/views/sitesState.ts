import type { SitePayload, SiteKeyPayload } from '@/api/sites'

export interface SiteImportPreviewItem extends SitePayload {
  selected: boolean
  protocolType: 'OpenAI' | 'Anthropic' | 'Responses'
}

export interface SiteExportItem extends SitePayload {
  id: string
  // 导出数据携带完整密钥列表（含原始 KeyValue），用于跨实例迁移多 Key。
  keys?: SiteKeyPayload[]
}

export interface ParseSitesImportResult {
  items: SiteImportPreviewItem[]
  error?: string
}

function resolveProtocolFromFlags(
  supportsOpenAi = true,
  supportsAnthropic = false
): SiteImportPreviewItem['protocolType'] {
  if (!supportsOpenAi && !supportsAnthropic) return 'Responses'
  return supportsAnthropic && !supportsOpenAi ? 'Anthropic' : 'OpenAI'
}

function resolveProtocolFromTsv(baseUrl: string, apiKey: string): SiteImportPreviewItem['protocolType'] {
  const lowerUrl = baseUrl.toLowerCase()
  if (lowerUrl.includes('anthropic') || apiKey.startsWith('sk-ant-')) return 'Anthropic'
  return 'OpenAI'
}

function toCapabilities(protocolType: SiteImportPreviewItem['protocolType']): {
  supportsOpenAi: boolean
  supportsAnthropic: boolean
} {
  if (protocolType === 'Anthropic') return { supportsOpenAi: false, supportsAnthropic: true }
  if (protocolType === 'Responses') return { supportsOpenAi: false, supportsAnthropic: false }
  return { supportsOpenAi: true, supportsAnthropic: false }
}

function normalizeJsonItem(item: Partial<SitePayload> & { protocolType?: string }): SiteImportPreviewItem | null {
  if (!item.name?.trim() || !item.baseUrl?.trim() || !item.apiKey?.trim()) return null
  const protocolType = item.protocolType === 'Anthropic' || item.protocolType === 'Responses'
    ? item.protocolType
    : resolveProtocolFromFlags(item.supportsOpenAi, item.supportsAnthropic)
  const capabilities = toCapabilities(protocolType)

  return {
    name: item.name.trim(),
    baseUrl: item.baseUrl.trim(),
    endpointPathMode: item.endpointPathMode || 'standard-root',
    apiKey: item.apiKey.trim(),
    supportsOpenAi: item.supportsOpenAi ?? capabilities.supportsOpenAi,
    supportsAnthropic: item.supportsAnthropic ?? capabilities.supportsAnthropic,
    isEnabled: item.isEnabled ?? true,
    protocolType,
    selected: true
  }
}

export function parseSitesImportText(rawText: string): ParseSitesImportResult {
  const raw = rawText.trim()
  if (!raw) return { items: [], error: '请先粘贴数据' }

  if (raw.startsWith('[') || raw.startsWith('{')) {
    try {
      const data = JSON.parse(raw) as Array<Partial<SitePayload> & { protocolType?: string }>
      if (!Array.isArray(data)) return { items: [], error: 'JSON 格式错误：需要一个数组' }
      const items = data.map(normalizeJsonItem).filter((item): item is SiteImportPreviewItem => item !== null)
      return items.length > 0
        ? { items }
        : { items: [], error: '未解析到有效站点数据，请检查 JSON 格式' }
    } catch (e) {
      return { items: [], error: `JSON 解析失败：${(e as Error).message}` }
    }
  }

  const items: SiteImportPreviewItem[] = []
  raw.split(/\r?\n/).forEach((line, index) => {
    const columns = line.trim().split(/\t/)
    if (columns.length < 3) return

    const [name, baseUrl, apiKey] = columns.map((column) => column.trim())
    const lowerName = name.toLowerCase()
    if (index === 0 && (lowerName === 'site_name' || lowerName === 'name' || name === '站点名称')) return
    if (!name || !baseUrl || !apiKey) return

    const protocolType = resolveProtocolFromTsv(baseUrl, apiKey)
    const capabilities = toCapabilities(protocolType)
    items.push({
      name,
      baseUrl,
      endpointPathMode: 'standard-root',
      apiKey,
      ...capabilities,
      isEnabled: true,
      protocolType,
      selected: true
    })
  })

  return items.length > 0
    ? { items }
    : { items: [], error: '未能解析到有效数据，请检查格式' }
}

export function updateSitesSelection(
  items: Array<SitePayload & { selected: boolean }>,
  index: number,
  selected: boolean
): Array<SitePayload & { selected: boolean }> {
  return items.map((item, itemIndex) => itemIndex === index ? { ...item, selected } : item)
}

export function buildSelectedSitesExportJson(sites: SiteExportItem[], selectedIds: string[]): string {
  const selected = new Set(selectedIds)
  return JSON.stringify(sites.filter((site) => selected.has(site.id)), null, 2)
}

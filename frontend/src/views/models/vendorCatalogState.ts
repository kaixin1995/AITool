import type { ModelVendorCatalog } from '@/api/models'

export function extractSvgBody(iconSvgBody: string): string {
  const normalized = String(iconSvgBody || '').trim()
  if (!normalized) return ''

  const svgMatch = normalized.match(/^<svg\b([^>]*)>([\s\S]*)<\/svg>$/i)
  if (!svgMatch) return normalized

  const attrs = svgMatch[1] || ''
  const body = (svgMatch[2] || '').trim()
  const viewBoxMatch = attrs.match(/\bviewBox\s*=\s*(['"])(.*?)\1/i)
  if (!viewBoxMatch?.[2]) return body

  return `<svg viewBox="${viewBoxMatch[2].trim()}">${body}</svg>`
}

export function buildVendorIconMarkup(iconSvgBody: string): string {
  const svgBody = extractSvgBody(iconSvgBody)
  if (!svgBody) return ''

  const normalizedBody = svgBody
    .split('url(#lobe-icons-qwen-fill)').join('url(#vendor-gradient-alibaba)')
    .split('id="lobe-icons-qwen-fill"').join('id="vendor-gradient-alibaba"')

  return `<svg viewBox="0 0 24 24" aria-hidden="true"><defs><linearGradient id="vendor-gradient-alibaba" x1="0%" y1="0%" x2="100%" y2="100%"><stop offset="0%" stop-color="#FF8A00" /><stop offset="100%" stop-color="#FF5F00" /></linearGradient></defs>${normalizedBody}</svg>`
}

export function renameVendor(catalog: ModelVendorCatalog, vendorIndex: number, nextName: string): void {
  const vendor = catalog.vendors[vendorIndex]
  if (!vendor) return

  const previousName = vendor.vendorName
  vendor.vendorName = nextName
  catalog.rules.forEach((rule) => {
    if (rule.vendorName === previousName) rule.vendorName = nextName
  })
}

export function removeVendorAt(catalog: ModelVendorCatalog, vendorIndex: number): void {
  const vendor = catalog.vendors[vendorIndex]
  if (!vendor) return

  catalog.vendors.splice(vendorIndex, 1)
  catalog.rules = catalog.rules.filter((rule) => rule.vendorName !== vendor.vendorName)
}

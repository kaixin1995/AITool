export function isNewerVersion(latest?: string | null, current?: string | null): boolean {
  if (!latest || !current || !/^\d+\.\d+\.\d+$/.test(latest) || !/^\d+\.\d+\.\d+$/.test(current)) return false
  const a = latest.split('.').map(Number), b = current.split('.').map(Number)
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return a[i] > b[i]
  return false
}

export function proposeClaudeDesktopHeaders(json: string, cli: string | null, desktop: string | null): string {
  try {
    const headers = JSON.parse(json) as Record<string, string>
    const uaKey = Object.keys(headers).find(k => k.toLowerCase() === 'user-agent')
    const desktopKey = Object.keys(headers).find(k => k.toLowerCase() === 'anthropic-client-version')
    if (cli && uaKey)
      headers[uaKey] = headers[uaKey].replace(/(claude-cli\/)\d+\.\d+\.\d+/i, (_match, prefix: string) => `${prefix}${cli}`)
    if (desktop && desktopKey) headers[desktopKey] = desktop
    return JSON.stringify(headers, null, 2)
  } catch { return '' }
}

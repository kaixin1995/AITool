import { describe, expect, it } from 'vitest'
import { isNewerVersion, proposeClaudeDesktopHeaders } from './claudeDesktopVersionState'

describe('Claude Desktop version selection', () => {
  it('compares numeric versions and rejects invalid inputs or downgrades', () => {
    expect(isNewerVersion('2.1.327', '2.1.326')).toBe(true)
    expect(isNewerVersion('6.6395.0', '6.6395.0')).toBe(false)
    expect(isNewerVersion('2.1.20', '2.1.326')).toBe(false)
    expect(isNewerVersion('unknown', '2.1.326')).toBe(false)
  })
  it('updates only selected product fields and preserves the SDK and other headers', () => {
    const original = JSON.stringify({ 'User-Agent': 'claude-cli/2.1.326 (external, local-agent, agent-sdk/0.3.326)', 'Anthropic-Client-Version': '6.6395.0', 'X-Custom': 'keep' })
    const cli = JSON.parse(proposeClaudeDesktopHeaders(original, '2.1.327', null))
    expect(cli['User-Agent']).toBe('claude-cli/2.1.327 (external, local-agent, agent-sdk/0.3.326)')
    expect(cli['Anthropic-Client-Version']).toBe('6.6395.0')
    expect(cli['X-Custom']).toBe('keep')
    const desktop = JSON.parse(proposeClaudeDesktopHeaders(original, null, '6.7000.0'))
    expect(desktop['User-Agent']).toContain('2.1.326')
    expect(desktop['Anthropic-Client-Version']).toBe('6.7000.0')
  })
})

import { describe, expect, it } from 'vitest'
import { analyzeProtocolError } from './protocolErrorAnalyzer'

describe('protocolErrorAnalyzer', () => {
  it('should recognize extra inputs reasoning_effort', () => {
    const err = "Extra inputs are not permitted: 'reasoning_effort'"
    const diag = analyzeProtocolError(err, 400)
    expect(diag).not.toBeNull()
    expect(diag?.category).toBe('unsupported_field')
    expect(diag?.recommendedRule).toEqual({
      op: 'strip',
      target: 'reasoning_effort',
      scope: 'bridge'
    })
  })

  it('should recognize missing required max_tokens', () => {
    const err = 'missing required field: max_tokens'
    const diag = analyzeProtocolError(err, 400)
    expect(diag).not.toBeNull()
    expect(diag?.category).toBe('missing_field')
    expect(diag?.recommendedRule).toEqual({
      op: 'default',
      key: 'max_tokens',
      value: '4096',
      scope: 'bridge'
    })
  })

  it('should recognize missing reasoning_content for tool calls', () => {
    const err = "'reasoning_content' is required when tool calls are present"
    const diag = analyzeProtocolError(err, 400)
    expect(diag).not.toBeNull()
    expect(diag?.category).toBe('reasoning')
    expect(diag?.recommendedRule).toEqual({
      op: 'keep_reasoning',
      scope: 'bridge'
    })
  })

  it('should recognize 401 unauthorized', () => {
    const diag = analyzeProtocolError('Incorrect API key provided', 401)
    expect(diag).not.toBeNull()
    expect(diag?.category).toBe('auth')
  })

  it('should return null for empty error', () => {
    expect(analyzeProtocolError('', 200)).toBeNull()
  })
})

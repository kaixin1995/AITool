import { httpPost } from './http'

export type ProtocolDiagnosticsDirection = 'request' | 'response'
export type ProtocolName = 'OpenAI' | 'Anthropic' | 'Responses'

export interface ProtocolDiagnosticsRequest {
  direction: ProtocolDiagnosticsDirection
  sourceProtocol: ProtocolName
  targetProtocol: ProtocolName
  streaming: boolean
  modelName: string
  payload: string
  eventName?: string
  overrideReasoningEffort?: string
  inputTokens?: number
  cachedTokens?: number
  outputTokens?: number
}

export interface ProtocolDiagnosticsResult {
  direction: ProtocolDiagnosticsDirection
  sourceProtocol: ProtocolName
  targetProtocol: ProtocolName
  streaming: boolean
  convertedPayload: string
  eventCount: number
  completionDetected: boolean
  conversionFailed: boolean
}

export function runProtocolDiagnostics(
  request: ProtocolDiagnosticsRequest
): Promise<ProtocolDiagnosticsResult> {
  return httpPost<ProtocolDiagnosticsResult>(
    '/api/admin/developer/invocations/protocol-diagnostics',
    request
  )
}

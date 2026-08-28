import { httpPost } from './http'

export type ProtocolDiagnosticsDirection = 'request' | 'response'
export type ProtocolName = 'OpenAI' | 'Anthropic' | 'Responses' | 'Gemini'

export interface ProtocolDiagnosticsTrialRule {
  op: 'strip' | 'rename' | 'default' | 'keep_reasoning'
  target?: string
  from?: string
  to?: string
  key?: string
  value?: string
  scope: 'all' | 'passthrough' | 'bridge'
}

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
  rules?: ProtocolDiagnosticsTrialRule[]
}

export interface ProtocolFieldMappingInfo {
  source: string
  target: string
  note?: string
}

export interface ProtocolChainStage {
  kind: string
  label: string
  protocol: string
  function?: string
  note?: string
  isBridge: boolean
}

export interface ProtocolEventMappingInfo {
  sourceEvent: string
  targetEvent: string
  note?: string
}

export interface ProtocolChainInfo {
  mode: 'direct' | 'bridge'
  stages: ProtocolChainStage[]
  eventMappings?: ProtocolEventMappingInfo[]
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
  conversionPath?: string
  failureReason?: string
  inputSummary?: Record<string, string | number | boolean | null>
  fieldMappings?: ProtocolFieldMappingInfo[]
  missingFields?: string[]
  chain?: ProtocolChainInfo
  rulesApplied?: boolean
}

export function runProtocolDiagnostics(
  request: ProtocolDiagnosticsRequest
): Promise<ProtocolDiagnosticsResult> {
  return httpPost<ProtocolDiagnosticsResult>(
    '/api/admin/developer/invocations/protocol-diagnostics',
    request
  )
}

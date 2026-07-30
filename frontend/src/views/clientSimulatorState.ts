import { supportsResponsesNatively } from './developerInvocationsState'

export interface SimulatorModelCapability {
  canUseOpenAi: boolean
  canUseAnthropic: boolean
  supportsOpenAi?: boolean
  supportsAnthropic?: boolean
  supportsResponses?: boolean
  routeCount?: number
}

export function buildModelSupportLabels(
  model: SimulatorModelCapability
): string[] {
  const labels: string[] = []
  if (model.supportsOpenAi) labels.push('OpenAI 原生')
  else if (model.canUseOpenAi) labels.push('OpenAI 兼容')

  if (model.supportsAnthropic) labels.push('Anthropic 原生')
  else if (model.canUseAnthropic) labels.push('Anthropic 兼容')

  if (supportsResponsesNatively(model)) {
    labels.push('Responses 原生')
  }
  if (model.routeCount !== undefined) {
    labels.push(`路由 ${model.routeCount}`)
  }
  return labels
}

export function formatSimulatorResponse(
  status: number,
  ok: boolean,
  body: string
): string {
  if (ok && body) return body
  return body ? `HTTP ${status}\n${body}` : `HTTP ${status}`
}

export function shouldReadStreamingResponse(
  requestedStreaming: boolean,
  responseOk: boolean,
  hasBody: boolean
): boolean {
  return requestedStreaming && responseOk && hasBody
}

export class SimulatorRequestRegistry {
  private readonly requests = new Map<string, AbortController>()

  start(tabKey: string, controller: AbortController): void {
    this.requests.set(tabKey, controller)
  }

  isRunning(tabKey: string): boolean {
    return this.requests.has(tabKey)
  }

  finish(tabKey: string, controller: AbortController): boolean {
    if (this.requests.get(tabKey) !== controller) return false
    this.requests.delete(tabKey)
    return true
  }

  stop(tabKey: string): boolean {
    const controller = this.requests.get(tabKey)
    if (!controller) return false
    this.requests.delete(tabKey)
    controller.abort()
    return true
  }

  abortAll(): void {
    this.requests.forEach(controller => controller.abort())
    this.requests.clear()
  }
}

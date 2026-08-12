export type DeveloperToolTab = 'invocations' | 'simulator' | 'concurrency' | 'circuit-breaker' | 'protocol-diagnostics'

const hashToTab: Record<string, DeveloperToolTab> = {
  '#developerInvocationsPane': 'invocations',
  '#developerSimulatorPane': 'simulator',
  '#developerConcurrencyPane': 'concurrency',
  '#developerCircuitBreakerPane': 'circuit-breaker',
  '#developerProtocolDiagnosticsPane': 'protocol-diagnostics',
  '#protocol-diagnostics': 'protocol-diagnostics',
  '#invocations': 'invocations',
  '#simulator': 'simulator',
  '#concurrency': 'concurrency',
  '#circuit-breaker': 'circuit-breaker'
}

const tabToHash: Record<DeveloperToolTab, string> = {
  invocations: '#developerInvocationsPane',
  simulator: '#developerSimulatorPane',
  concurrency: '#developerConcurrencyPane',
  'circuit-breaker': '#developerCircuitBreakerPane',
  'protocol-diagnostics': '#developerProtocolDiagnosticsPane'
}

export function developerTabFromHash(hash: string): DeveloperToolTab {
  return hashToTab[hash] ?? 'invocations'
}

export function developerHashForTab(tab: DeveloperToolTab): string {
  return tabToHash[tab]
}

export function supportsResponsesNatively(model: {
  supportsResponses?: boolean
  supportsOpenAi?: boolean
  supportsAnthropic?: boolean
}): boolean {
  return model.supportsResponses === true
    || (
      model.supportsOpenAi === false
      && model.supportsAnthropic === false
    )
}

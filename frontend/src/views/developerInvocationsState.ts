export type DeveloperToolTab = 'invocations' | 'simulator' | 'concurrency'

const hashToTab: Record<string, DeveloperToolTab> = {
  '#developerInvocationsPane': 'invocations',
  '#developerSimulatorPane': 'simulator',
  '#developerConcurrencyPane': 'concurrency',
  '#invocations': 'invocations',
  '#simulator': 'simulator',
  '#concurrency': 'concurrency'
}

const tabToHash: Record<DeveloperToolTab, string> = {
  invocations: '#developerInvocationsPane',
  simulator: '#developerSimulatorPane',
  concurrency: '#developerConcurrencyPane'
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

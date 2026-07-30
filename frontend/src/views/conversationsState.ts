export interface ConversationRenderWindow {
  start: number
  end: number
}

export interface ConversationMetaSource {
  createdAtText: string
  userCreatedAtText: string
  requestModel: string
  inputTokens: number
  cachedTokens: number
  outputTokens: number
}

const CONVERSATION_TURN_BATCH_SIZE = 30
const TOKEN_UNITS = [
  { value: 1e18, suffix: 'E' },
  { value: 1e15, suffix: 'P' },
  { value: 1e12, suffix: 'T' },
  { value: 1e9, suffix: 'B' },
  { value: 1e6, suffix: 'M' },
  { value: 1e3, suffix: 'K' }
]

export function buildInitialConversationWindow(
  total: number
): ConversationRenderWindow {
  const end = Math.max(0, total)
  return {
    start: Math.max(0, end - CONVERSATION_TURN_BATCH_SIZE),
    end
  }
}

export function buildPreviousConversationWindow(
  currentStart: number,
  total: number
): ConversationRenderWindow {
  return {
    start: Math.max(0, currentStart - CONVERSATION_TURN_BATCH_SIZE),
    end: Math.max(0, total)
  }
}

export function formatConversationTokenCount(value: number): string {
  const normalized = Math.max(0, value || 0)
  const unit = TOKEN_UNITS.find(item => normalized >= item.value)
  if (!unit) return String(Math.round(normalized))

  const scaled = normalized / unit.value
  const digits = scaled >= 100 ? 0 : scaled >= 10 ? 1 : 2
  return `${Number(scaled.toFixed(digits))}${unit.suffix}`
}

export function buildConversationUserMeta(
  turn: ConversationMetaSource
): string {
  const time = turn.userCreatedAtText || turn.createdAtText
  const tokenParts = [
    `输入 ${formatConversationTokenCount(turn.inputTokens)}`
  ]
  if (turn.cachedTokens > 0) {
    tokenParts.push(
      `缓存 ${formatConversationTokenCount(turn.cachedTokens)}`
    )
  }
  return [time, tokenParts.join('，')].filter(Boolean).join(' · ')
}

export function buildConversationAssistantMeta(
  turn: ConversationMetaSource
): string {
  return [
    turn.createdAtText,
    turn.requestModel,
    `输出 ${formatConversationTokenCount(turn.outputTokens)}`
  ].filter(Boolean).join(' · ')
}

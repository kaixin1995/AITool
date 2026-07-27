import { httpGet, httpDelete } from './http'

// 与后端 ConversationsApiController 的 sessions 端点返回对齐。
export interface ConversationSession {
  groupKey: string
  sourceTool: string
  sourceToolText: string
  sessionIdShort: string
  lastActivityAt: string
  lastActivityAtText: string
  turnCount: number
  totalTokens: number
  preview: string
  title: string
  isCustomTitle: boolean
}
export interface ConversationSessionListResponse {
  items: ConversationSession[]
  totalCount: number
}

// turns 端点返回 { items, truncated }。
export interface ConversationTurn {
  id: string
  createdAt: string
  userCreatedAt: string | null
  createdAtText: string
  userCreatedAtText: string
  requestModel: string
  userInputText: string
  assistantOutputMarkdown: string
  inputTokens: number
  cachedTokens: number
  outputTokens: number
}
export interface ConversationTurnListResponse {
  items: ConversationTurn[]
  truncated: boolean
}

export async function listSessions(params: Record<string, unknown>): Promise<ConversationSessionListResponse> {
  const query = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== '') query.append(k, String(v))
  }
  return httpGet<ConversationSessionListResponse>(`/api/admin/conversations/sessions?${query.toString()}`)
}

export async function deleteSession(groupKey: string): Promise<void> {
  await httpDelete(`/api/admin/conversations/sessions?groupKey=${encodeURIComponent(groupKey)}`)
}

export async function getTurns(groupKey: string): Promise<ConversationTurnListResponse> {
  return httpGet<ConversationTurnListResponse>(`/api/admin/conversations/turns?groupKey=${encodeURIComponent(groupKey)}`)
}

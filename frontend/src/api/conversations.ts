import { httpGet, httpDelete } from './http'

export interface ConversationSession {
  groupKey: string
  sessionId: string
  sourceTool: string
  requestModel: string
  lastCreatedAt: string
  turnCount: number
  title?: string
}
export interface ConversationTurn {
  id: string
  createdAt: string
  userInputText: string
  assistantOutputMarkdown: string
  inputTokens: number
  outputTokens: number
  isStreaming: boolean
  status: string
}

export async function listSessions(params: Record<string, unknown>): Promise<{ items: ConversationSession[]; totalCount: number }> {
  const query = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== '') query.append(k, String(v))
  }
  return httpGet(`/api/admin/conversations/sessions?${query.toString()}`)
}

export async function deleteSession(groupKey: string): Promise<void> {
  await httpDelete(`/api/admin/conversations/sessions?groupKey=${encodeURIComponent(groupKey)}`)
}

export async function getTurns(groupKey: string): Promise<ConversationTurn[]> {
  return httpGet<ConversationTurn[]>(`/api/admin/conversations/turns?groupKey=${encodeURIComponent(groupKey)}`)
}

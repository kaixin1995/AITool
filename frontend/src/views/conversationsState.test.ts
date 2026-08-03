import { describe, expect, it } from 'vitest'
import {
  buildConversationAssistantMeta,
  buildConversationUserMeta,
  buildInitialConversationWindow,
  buildPreviousConversationWindow,
  formatConversationTokenCount
} from './conversationsState'

describe('对话轮次渐进渲染', () => {
  it('初始只展示最后 30 轮', () => {
    expect(buildInitialConversationWindow(31)).toEqual({ start: 1, end: 31 })
    expect(buildInitialConversationWindow(60)).toEqual({ start: 30, end: 60 })
    expect(buildInitialConversationWindow(500)).toEqual({ start: 470, end: 500 })
  })

  it('每次向前追加最多 30 轮', () => {
    expect(buildPreviousConversationWindow(470, 500)).toEqual({ start: 440, end: 500 })
    expect(buildPreviousConversationWindow(30, 60)).toEqual({ start: 0, end: 60 })
    expect(buildPreviousConversationWindow(1, 31)).toEqual({ start: 0, end: 31 })
  })
})

describe('对话消息元数据', () => {
  it('将输入和缓存归属到用户消息，将模型和输出归属到 AI 消息', () => {
    const turn = {
      createdAtText: '10:05',
      userCreatedAtText: '10:00',
      requestModel: 'route-a',
      inputTokens: 1200,
      cachedTokens: 800,
      outputTokens: 45
    }

    expect(buildConversationUserMeta(turn)).toBe('10:00 · 输入 1.2K，缓存 800')
    expect(buildConversationAssistantMeta(turn)).toBe('10:05 · route-a · 输出 45')
  })

  it('省略空模型、零缓存和多余分隔符', () => {
    const turn = {
      createdAtText: '10:05',
      userCreatedAtText: '',
      requestModel: '',
      inputTokens: 0,
      cachedTokens: 0,
      outputTokens: 2_000_000
    }

    expect(buildConversationUserMeta(turn)).toBe('10:05 · 输入 0')
    expect(buildConversationAssistantMeta(turn)).toBe('10:05 · 输出 2M')
    expect(formatConversationTokenCount(1_250_000_000)).toBe('1.25B')
  })
})

import { describe, expect, it } from 'vitest'
import { ALL_MODELS_VALUE, normalizeDetectionTaskModelId } from './detectionTasksState'

describe('Detection Tasks 页面状态', () => {
  it('把全部模型选项转换为后端可接受的 null', () => {
    expect(normalizeDetectionTaskModelId(ALL_MODELS_VALUE)).toBeNull()
    expect(normalizeDetectionTaskModelId('')).toBeNull()
    expect(normalizeDetectionTaskModelId('model-1')).toBe('model-1')
  })
})

import { describe, expect, it } from 'vitest'
import type { DetectionModelGroup } from '@/api/detection'
import { applyDetectionProbeResult, formatDetectionDateTime, shouldRetryDetectionProgress } from './detectionState'

function createGroups(): DetectionModelGroup[] {
  return [{
    modelLibraryItemId: 'model-1',
    modelName: 'gpt-test',
    displayName: 'GPT Test',
    sites: [{
      mappingId: 'mapping-1',
      siteName: '站点一',
      remoteModelName: 'remote-model',
      lastStatus: 'fail',
      lastCheckedAt: null,
      lastDurationMs: 20
    }]
  }]
}

describe('Detection 页面状态', () => {
  it('使用检测结果即时更新对应站点状态和耗时', () => {
    const groups = createGroups()

    const updated = applyDetectionProbeResult(groups, {
      mappingId: 'mapping-1',
      siteName: '站点一',
      remoteModelName: 'remote-model',
      status: 'success',
      durationMs: 0,
      error: null
    }, '2026-07-30T10:20:30.000Z')

    expect(updated).toBe(true)
    expect(groups[0].sites[0]).toMatchObject({
      lastStatus: 'success',
      lastDurationMs: 0,
      lastCheckedAt: '2026-07-30T10:20:30.000Z'
    })
  })

  it('把检测时间格式化为固定的年月日时分秒', () => {
    const value = new Date(2026, 6, 30, 8, 9, 5).toISOString()
    expect(formatDetectionDateTime(value)).toBe('2026-07-30 08:09:05')
    expect(formatDetectionDateTime(null)).toBe('-')
  })

  it('任务已过期时停止轮询，临时错误继续重试', () => {
    expect(shouldRetryDetectionProgress({ status: 404 })).toBe(false)
    expect(shouldRetryDetectionProgress({ status: 503 })).toBe(true)
    expect(shouldRetryDetectionProgress(new Error('网络错误'))).toBe(true)
  })
})

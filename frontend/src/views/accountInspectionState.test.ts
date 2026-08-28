import { describe, expect, it } from 'vitest'
import {
  inspectionActionLabel,
  isInspectionDisabledError
} from './accountInspectionState'

describe('OAuth 巡检状态', () => {
  it('只有 404 表示巡检功能未开启', () => {
    expect(isInspectionDisabledError({ status: 404 })).toBe(true)
    expect(isInspectionDisabledError({ status: 500 })).toBe(false)
    expect(isInspectionDisabledError(new Error('网络错误'))).toBe(false)
  })

  it('巡检动作使用中文标签', () => {
    expect(inspectionActionLabel('keep')).toBe('保留')
    expect(inspectionActionLabel('disable')).toBe('禁用')
    expect(inspectionActionLabel('enable')).toBe('启用')
    expect(inspectionActionLabel('unknown')).toBe('unknown')
  })
})

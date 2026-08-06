import { describe, expect, it } from 'vitest'
import { modelHealthSuccessColor } from './modelHealthState'

describe('模型健康成功率颜色', () => {
  it('按旧页面阈值显示红黄绿', () => {
    expect(modelHealthSuccessColor(0.49)).toContain('danger')
    expect(modelHealthSuccessColor(0.5)).toContain('warning')
    expect(modelHealthSuccessColor(0.79)).toContain('warning')
    expect(modelHealthSuccessColor(0.8)).toContain('success')
  })
})

import { describe, expect, it } from 'vitest'
import {
  buildUsageLogsDefaultCustomRange,
  buildVisibleUsageLogPages,
  canAutoLoadUsageLogs,
  formatUsageLogModel
} from './usageLogsState'

describe('Usage Logs 模型显示', () => {
  it('chat 页等非路由调用只显示对外模型', () => {
    expect(formatUsageLogModel('deepseek-v4-flash', 'deepseek-ai/deepseek-v4-flash-0731', 'chat')).toBe('deepseek-v4-flash')
    expect(formatUsageLogModel('', 'gpt-5.2', 'chat')).toBe('gpt-5.2')
  })

  it('路由调用显示 路由名 -> 对外模型', () => {
    expect(formatUsageLogModel('chat-prod', 'gpt-5.5', 'proxy')).toBe('chat-prod -> gpt-5.5')
    expect(formatUsageLogModel('gpt-5.2', 'gpt-5.2', 'proxy')).toBe('gpt-5.2')
    expect(formatUsageLogModel('', 'gpt-5.2', 'proxy')).toBe('gpt-5.2')
    expect(formatUsageLogModel(null, null, 'proxy')).toBe('-')
  })

  it('未知来源按路由调用处理，名称相同时不重复拼接', () => {
    expect(formatUsageLogModel('deepseek-v4-flash', 'deepseek-v4-flash')).toBe('deepseek-v4-flash')
  })
})

describe('Usage Logs 分页', () => {
  it('在末页仍显示完整五个连续页码', () => {
    expect(buildVisibleUsageLogPages(10, 10)).toEqual([6, 7, 8, 9, 10])
    expect(buildVisibleUsageLogPages(1, 10)).toEqual([1, 2, 3, 4, 5])
    expect(buildVisibleUsageLogPages(4, 10)).toEqual([2, 3, 4, 5, 6])
  })

  it('无结果时不生成虚假的第一页', () => {
    expect(buildVisibleUsageLogPages(1, 0)).toEqual([])
  })
})

describe('Usage Logs 自定义时间', () => {
  it('默认填充今天零点到当前时间', () => {
    const { startTime, endTime } = buildUsageLogsDefaultCustomRange(new Date('2026-07-30T12:34:56'))

    const start = new Date(startTime)
    const end = new Date(endTime)
    expect([start.getFullYear(), start.getMonth(), start.getDate(), start.getHours(), start.getMinutes()])
      .toEqual([2026, 6, 30, 0, 0])
    expect([end.getFullYear(), end.getMonth(), end.getDate(), end.getHours(), end.getMinutes(), end.getSeconds()])
      .toEqual([2026, 6, 30, 12, 34, 56])
  })

  it('custom 模式起止时间完整后才自动查询', () => {
    expect(canAutoLoadUsageLogs('day', null, null)).toBe(true)
    expect(canAutoLoadUsageLogs('custom', null, Date.now())).toBe(false)
    expect(canAutoLoadUsageLogs('custom', Date.now(), null)).toBe(false)
    expect(canAutoLoadUsageLogs('custom', 1, 2)).toBe(true)
  })
})

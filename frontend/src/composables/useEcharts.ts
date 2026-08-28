// ECharts 按需引入：只注册项目实际用到的图表类型和组件，大幅减小打包体积。
// 全量 import * as echarts 会引入 ~1MB，按需后可降至 ~200KB。
import * as echarts from 'echarts/core'
import { LineChart, PieChart, BarChart, SankeyChart } from 'echarts/charts'
import {
  TitleComponent,
  TooltipComponent,
  GridComponent,
  LegendComponent
} from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'

echarts.use([
  LineChart,
  PieChart,
  BarChart,
  // AnalyticsView 的"回退链路"维度使用 sankey 系列，未注册会渲染空白并报 series not exists。
  SankeyChart,
  TitleComponent,
  TooltipComponent,
  GridComponent,
  LegendComponent,
  CanvasRenderer
])

// 根据当前主题（data-theme 属性）初始化图表实例。
// 不使用 registerTheme（echarts/core 按需引入时深度合并可能栈溢出），
// 改为在 setOption 时统一注入暗色配色（见 AnalyticsView 的 darkChartOption）。
export function initChart(el: HTMLElement): echarts.ECharts {
  return echarts.init(el)
}

// 暗色模式公共配色：调用方在 setOption 时合并此对象。
export function darkChartOverrides() {
  return {
    backgroundColor: 'transparent',
    textStyle: { color: 'rgba(255,255,255,0.82)' },
    legend: { textStyle: { color: 'rgba(255,255,255,0.65)' } },
    xAxis: { axisLine: { lineStyle: { color: 'rgba(255,255,255,0.2)' } }, axisLabel: { color: 'rgba(255,255,255,0.55)' }, splitLine: { lineStyle: { color: 'rgba(255,255,255,0.08)' } } },
    yAxis: { axisLine: { lineStyle: { color: 'rgba(255,255,255,0.2)' } }, axisLabel: { color: 'rgba(255,255,255,0.55)' }, splitLine: { lineStyle: { color: 'rgba(255,255,255,0.08)' } } }
  }
}

export { echarts }
export type ECharts = echarts.ECharts

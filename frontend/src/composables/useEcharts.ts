// ECharts 按需引入：只注册项目实际用到的图表类型和组件，大幅减小打包体积。
// 全量 import * as echarts 会引入 ~1MB，按需后可降至 ~200KB。
import * as echarts from 'echarts/core'
import { LineChart, PieChart, BarChart } from 'echarts/charts'
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
  TitleComponent,
  TooltipComponent,
  GridComponent,
  LegendComponent,
  CanvasRenderer
])

// 暗色主题：深色画布 + 浅色文字/网格线，避免暗色模式下图表白色背景刺眼。
echarts.registerTheme('aitool-dark', {
  backgroundColor: 'transparent',
  textStyle: { color: 'rgba(255,255,255,0.82)' },
  title: { textStyle: { color: 'rgba(255,255,255,0.82)' } },
  legend: { textStyle: { color: 'rgba(255,255,255,0.65)' } },
  categoryAxis: {
    axisLine: { lineStyle: { color: 'rgba(255,255,255,0.2)' } },
    axisLabel: { color: 'rgba(255,255,255,0.55)' },
    splitLine: { lineStyle: { color: 'rgba(255,255,255,0.08)' } }
  },
  valueAxis: {
    axisLine: { lineStyle: { color: 'rgba(255,255,255,0.2)' } },
    axisLabel: { color: 'rgba(255,255,255,0.55)' },
    splitLine: { lineStyle: { color: 'rgba(255,255,255,0.08)' } }
  },
  tooltip: {
    backgroundColor: 'rgba(40,40,48,0.95)',
    borderColor: 'rgba(255,255,255,0.12)',
    textStyle: { color: 'rgba(255,255,255,0.9)' }
  }
})

// 根据当前主题（data-theme 属性）初始化图表实例，自动适配明暗。
export function initChart(el: HTMLElement): echarts.ECharts {
  const isDark = document.documentElement.getAttribute('data-theme') === 'dark'
  return echarts.init(el, isDark ? 'aitool-dark' : undefined)
}

export { echarts }
export type ECharts = echarts.ECharts

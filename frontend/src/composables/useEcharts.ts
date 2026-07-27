// ECharts 按需引入：只注册项目实际用到的图表类型和组件，大幅减小打包体积。
// 全量 import * as echarts 会引入 ~1MB，按需后可降至 ~200KB。
import * as echarts from 'echarts/core'
import { LineChart, PieChart } from 'echarts/charts'
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
  TitleComponent,
  TooltipComponent,
  GridComponent,
  LegendComponent,
  CanvasRenderer
])

export { echarts }
export type ECharts = echarts.ECharts

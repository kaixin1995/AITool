import { httpGet } from './http'

// 仪表盘统计：站点数、模型数、映射数、路由规则数、密钥数、检测任务数。
// 复用现有的聚合查询。原 Razor Pages 的 Index.cshtml.cs OnGetAsync 做了 5 个 Count。
// 这里通过组合现有 API 的元数据缓存接口获取（避免新增后端端点）。
// 暂用 route-entries + chat-models + site-instances 三个聚合接口拼装。
export interface DashboardStats {
  siteCount: number
  modelCount: number
  mappingCount: number
  routeCount: number
  accessKeyCount: number
  detectionTaskCount: number
}

export async function getDashboardStats(): Promise<DashboardStats> {
  // 并发拉取多个轻量接口。
  const [entries, chatModels, siteInstances, accessKeys, detectionTasks] = await Promise.all([
    httpGet<{ entryName: string; candidateCount: number }[]>('/api/admin/route-rules/entries'),
    httpGet<{ modelId: string; displayName: string; availableSiteCount: number }[]>('/api/admin/chat/models'),
    httpGet<{ siteId: string }[]>('/api/admin/route-rules/site-instances'),
    httpGet<{ id: string }[]>('/api/admin/access-keys'),
    httpGet<{ tasks: unknown[] }>('/api/admin/detection-tasks')
  ])

  return {
    siteCount: new Set(siteInstances.map((s) => s.siteId)).size,
    modelCount: chatModels.length,
    mappingCount: siteInstances.length,
    routeCount: entries.reduce((sum, e) => sum + e.candidateCount, 0),
    accessKeyCount: accessKeys.length,
    detectionTaskCount: detectionTasks.tasks.length
  }
}

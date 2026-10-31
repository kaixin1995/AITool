import { httpGet, httpPost, httpPut, httpDelete } from './http'

/** 号池成员行摘要。 */
export interface AccountPoolMemberSummary {
  accountRefId: string
  displayName: string | null
  linkedSiteId: string
  priority: number
  excluded: boolean
}

/** 号池摘要。 */
export interface AccountPoolSummary {
  id: string
  name: string
  vendorKey: string
  vendorLabel: string
  planType: string | null
  autoIncludeMembers: boolean
  managed: boolean
  createdAt: string
  updatedAt: string
  members: AccountPoolMemberSummary[]
}

/** 建池请求。 */
export interface CreateAccountPoolRequest {
  name: string
  vendorKey: string
  planType?: string | null
  autoIncludeMembers: boolean
  memberAccountIds?: string[]
}

/** 成员候选（厂商账号）。 */
export interface AccountPoolCandidate {
  accountId: string
  displayName: string
  linkedSiteId: string
}

/** 一次池同步的结果摘要。 */
export interface AccountPoolSyncSummary {
  poolId: string
  name: string
  memberCount: number
  modelCount: number
  ruleCount: number
}

export function fetchAccountPools(): Promise<AccountPoolSummary[]> {
  return httpGet<AccountPoolSummary[]>('/api/admin/account-pools')
}

export function fetchPoolCandidates(vendorKey: string, planType?: string | null): Promise<AccountPoolCandidate[]> {
  const query = planType
    ? `?vendorKey=${encodeURIComponent(vendorKey)}&planType=${encodeURIComponent(planType)}`
    : `?vendorKey=${encodeURIComponent(vendorKey)}`
  return httpGet<AccountPoolCandidate[]>(`/api/admin/account-pools/candidates${query}`)
}

export async function createAccountPool(req: CreateAccountPoolRequest): Promise<AccountPoolSyncSummary> {
  const res = await httpPost<{ poolId: string; sync: AccountPoolSyncSummary }>('/api/admin/account-pools', req)
  return res.sync
}

export async function replacePoolMembers(
  id: string,
  members: Array<{ accountRefId: string; excluded: boolean }>
): Promise<AccountPoolSyncSummary> {
  const res = await httpPut<{ sync: AccountPoolSyncSummary }>(`/api/admin/account-pools/${id}/members`, { members })
  return res.sync
}

export async function syncAccountPool(id: string): Promise<AccountPoolSyncSummary> {
  const res = await httpPost<{ sync: AccountPoolSyncSummary }>(`/api/admin/account-pools/${id}/sync`)
  return res.sync
}

export async function syncAllAccountPools(): Promise<void> {
  await httpPost('/api/admin/account-pools/sync-all')
}

export async function deleteAccountPool(id: string): Promise<void> {
  await httpDelete(`/api/admin/account-pools/${id}`)
}

import apiClient from './client'

export interface DashboardCounts {
  leads: number
  clients: number
  activeProjects: number
  openActions: number
  dueToday: number
  overdue: number
  openQuotes: number
  openQuotesAmount: number
  awaitingSignature: number
}

export interface DashboardClientRow {
  id: string
  name: string
  score: number
  engagementCategory?: string | null
  nextActionAt?: string | null
  lastContactAt?: string | null
}

export interface DashboardDocumentRow {
  id: string
  clientId: string
  clientName: string
  kind: string
  status: string
  title: string
  amount?: number | null
  documentDate: string
  validUntil?: string | null
}

export interface DashboardActivityRow {
  id: string
  parentType: 'leads' | 'clients'
  parentId: string
  parentName: string
  kind: string
  summary: string
  at: string
}

export interface Dashboard {
  counts: DashboardCounts
  topClients: DashboardClientRow[]
  noNextAction: DashboardClientRow[]
  silent: DashboardClientRow[]
  awaitingSignature: DashboardDocumentRow[]
  expiring: DashboardDocumentRow[]
  projectsByStatus: Record<string, number>
  leadsByStage: Record<string, number>
  recentActivity: DashboardActivityRow[]
}

export async function getDashboard(organizationId?: string): Promise<Dashboard> {
  const res = await apiClient.get<Dashboard>('/api/dashboard', { params: organizationId ? { organizationId } : undefined })
  return res.data
}

import apiClient from './client'

export type ServiceKind = 'WebsiteManagement' | 'Hosting' | 'Maintenance' | 'Development' | 'Consulting' | 'Other'
export type ServiceStatus = 'Proposed' | 'Active' | 'Paused' | 'Ended'

export const SERVICE_LABELS: Record<ServiceKind, string> = {
  WebsiteManagement: 'Website management',
  Hosting: 'Hosting',
  Maintenance: 'Maintenance',
  Development: 'Development',
  Consulting: 'Consulting',
  Other: 'Other',
}

export const SERVICE_STATUS_LABELS: Record<ServiceStatus, string> = {
  Proposed: 'Proposed',
  Active: 'Active',
  Paused: 'Paused',
  Ended: 'Ended',
}

export interface ClientService {
  id: string
  clientId: string
  kind: ServiceKind
  status: ServiceStatus
  name: string
  projectId?: string | null
  startDate?: string | null
  endDate?: string | null
  monthlyAmount?: number | null
  notes?: string | null
  sourceRef?: string | null
}

export interface UpsertService {
  kind: ServiceKind
  name?: string | null
  status?: ServiceStatus | null
  startDate?: string | null
  endDate?: string | null
  monthlyAmount?: number | null
  notes?: string | null
  projectId?: string | null
}

export interface AgreementDoc {
  id: string
  kind: string
  status: string
  title: string
  reference?: string | null
  version: number
  serviceId?: string | null
  documentDate: string
  signedAt?: string | null
  amount?: number | null
  fileCount: number
  url?: string | null
}

export interface ClientAgreements {
  clientId: string
  clientName: string
  engagementCategory?: string | null
  services: ClientService[]
  contracts: AgreementDoc[]
  concepts: AgreementDoc[]
}

export async function getAgreements(service?: ServiceKind | '', onlyWithServices = false): Promise<ClientAgreements[]> {
  const res = await apiClient.get<ClientAgreements[]>('/api/agreements', {
    params: { service: service || undefined, onlyWithServices: onlyWithServices || undefined },
  })
  return res.data
}

export async function getServices(clientId: string): Promise<ClientService[]> {
  const res = await apiClient.get<ClientService[]>(`/api/clients/${clientId}/services`)
  return res.data
}

export async function createService(clientId: string, body: UpsertService): Promise<ClientService> {
  const res = await apiClient.post<ClientService>(`/api/clients/${clientId}/services`, body)
  return res.data
}

export async function updateService(id: string, body: UpsertService): Promise<ClientService> {
  const res = await apiClient.put<ClientService>(`/api/services/${id}`, body)
  return res.data
}

export async function deleteService(id: string): Promise<void> {
  await apiClient.delete(`/api/services/${id}`)
}

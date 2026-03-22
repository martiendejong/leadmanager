import apiClient from './client'

export interface Project {
  id: string
  name: string
  description?: string | null
  status: string
  createdAt: string
}

export interface Client {
  id: string
  name: string
  plan?: string | null
  primaryContactName?: string | null
  primaryContactEmail?: string | null
  primaryContactPhone?: string | null
  city?: string | null
  sector?: string | null
  website?: string | null
  notes?: string | null
  sourceLeadId?: string | null
  createdByUserId?: string | null
  createdAt: string
  isActive: boolean
  projects: Project[]
}

export interface ConvertLeadData {
  name: string
  plan?: string | null
  primaryContactName?: string | null
  primaryContactEmail?: string | null
  primaryContactPhone?: string | null
  notes?: string | null
}

export async function getClients(): Promise<Client[]> {
  const res = await apiClient.get<Client[]>('/api/clients')
  return res.data
}

export async function getClient(id: string): Promise<Client> {
  const res = await apiClient.get<Client>(`/api/clients/${id}`)
  return res.data
}

export async function convertLeadToClient(leadId: string, data: ConvertLeadData): Promise<Client> {
  const res = await apiClient.post<Client>(`/api/leads/${leadId}/convert`, data)
  return res.data
}

export async function updateClient(id: string, data: Partial<Client>): Promise<Client> {
  const res = await apiClient.put<Client>(`/api/clients/${id}`, data)
  return res.data
}

export async function deleteClient(id: string): Promise<void> {
  await apiClient.delete(`/api/clients/${id}`)
}

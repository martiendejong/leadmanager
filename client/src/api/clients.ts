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
  engagementCategory?: string | null
  sourceLeadId?: string | null
  createdByUserId?: string | null
  createdAt: string
  isActive: boolean
  projects: Project[]
  priorityValue?: number | null
  priorityUrgency?: number | null
  priorityEffort?: number | null
  score?: number
  nextActionAt?: string | null
  lastContactAt?: string | null
}

export interface ConvertLeadData {
  name: string
  plan?: string | null
  primaryContactName?: string | null
  primaryContactEmail?: string | null
  primaryContactPhone?: string | null
  notes?: string | null
}

export interface CreateClientData {
  name: string
  plan?: string | null
  primaryContactName?: string | null
  primaryContactEmail?: string | null
  primaryContactPhone?: string | null
  city?: string | null
  sector?: string | null
  website?: string | null
  notes?: string | null
  engagementCategory?: string | null
  projects?: { name: string; description?: string | null; status?: string | null }[]
}

export async function createClient(data: CreateClientData): Promise<Client> {
  const res = await apiClient.post<Client>('/api/clients', data)
  return res.data
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

export async function createProject(
  clientId: string,
  data: { name: string; description?: string | null; status?: string | null },
): Promise<Project> {
  const res = await apiClient.post<Project>(`/api/clients/${clientId}/projects`, data)
  return res.data
}

export async function updateProject(
  id: string,
  data: { name: string; description?: string | null; status?: string | null },
): Promise<Project> {
  const res = await apiClient.put<Project>(`/api/projects/${id}`, data)
  return res.data
}

export async function deleteProject(id: string): Promise<void> {
  await apiClient.delete(`/api/projects/${id}`)
}

export async function updatePriority(
  id: string,
  data: { value: number | null; urgency: number | null; effort: number | null },
): Promise<Client> {
  const res = await apiClient.put<Client>(`/api/clients/${id}/priority`, data)
  return res.data
}

import apiClient from './client'

export interface Organization {
  id: string
  name: string
  slug: string
  /** "admin" (manages members) or "member" */
  role: 'admin' | 'member'
  memberCount: number
  leadCount: number
}

export interface OrganizationMember {
  id: string
  email: string
  role: 'admin' | 'member'
  /** No account yet: the membership attaches when this person first logs in */
  pending: boolean
  name: string | null
  createdAt: string
}

export async function fetchOrganizations(): Promise<Organization[]> {
  const res = await apiClient.get<Organization[]>('/api/organizations')
  return res.data
}

export async function createOrganization(name: string): Promise<Organization> {
  const res = await apiClient.post<Organization>('/api/organizations', { name })
  return res.data
}

export async function fetchOrganizationMembers(orgId: string): Promise<OrganizationMember[]> {
  const res = await apiClient.get<OrganizationMember[]>(`/api/organizations/${orgId}/members`)
  return res.data
}

export async function addOrganizationMember(orgId: string, email: string, role: 'admin' | 'member'): Promise<OrganizationMember> {
  const res = await apiClient.post<OrganizationMember>(`/api/organizations/${orgId}/members`, { email, role })
  return res.data
}

export async function updateOrganizationMember(orgId: string, memberId: string, role: 'admin' | 'member'): Promise<OrganizationMember> {
  const res = await apiClient.put<OrganizationMember>(`/api/organizations/${orgId}/members/${memberId}`, { role })
  return res.data
}

export async function removeOrganizationMember(orgId: string, memberId: string): Promise<void> {
  await apiClient.delete(`/api/organizations/${orgId}/members/${memberId}`)
}

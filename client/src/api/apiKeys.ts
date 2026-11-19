import apiClient from './client'

export interface ApiKeyInfo {
  id: string
  name: string
  prefix: string
  createdAt: string
  lastUsedAt?: string | null
  revokedAt?: string | null
}

export interface CreatedApiKey {
  id: string
  name: string
  prefix: string
  key: string
}

export async function getApiKeys(): Promise<ApiKeyInfo[]> {
  const res = await apiClient.get<ApiKeyInfo[]>('/api/api-keys')
  return res.data
}

export async function createApiKey(name: string): Promise<CreatedApiKey> {
  const res = await apiClient.post<CreatedApiKey>('/api/api-keys', { name })
  return res.data
}

export async function revokeApiKey(id: string): Promise<void> {
  await apiClient.delete(`/api/api-keys/${id}`)
}

import apiClient from './client'

export interface Notification {
  id: string
  type: string
  message: string
  linkedLeadId?: string | null
  isRead: boolean
  createdAt: string
}

export async function getNotifications(): Promise<Notification[]> {
  const res = await apiClient.get<Notification[]>('/api/notifications')
  return res.data
}

export async function getNotificationCount(): Promise<number> {
  const res = await apiClient.get<{ count: number }>('/api/notifications/count')
  return res.data.count
}

export async function markRead(id: string): Promise<void> {
  await apiClient.put(`/api/notifications/${id}/read`)
}

export async function markAllRead(): Promise<void> {
  await apiClient.put('/api/notifications/read-all')
}

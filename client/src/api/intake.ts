import apiClient from './client'

export type IntakeProductType = 'Website' | 'AIEmployee' | 'AITeam' | 'Custom'
export type IntakeBundleType = 'Starter' | 'Team' | 'PayPerHour'
export type IntakeStatus =
  | 'Draft'
  | 'Estimated'
  | 'AwaitingApproval'
  | 'Approved'
  | 'InProgress'
  | 'Complete'
  | 'Cancelled'

export interface ClientIntake {
  id: string
  clientId: string
  clientName: string
  productType: IntakeProductType
  bundleType: IntakeBundleType
  status: IntakeStatus
  requirements: string
  firstTask?: string
  additionalNotes?: string
  estimatedHours?: number
  estimatedPrice?: number
  estimationReasoning?: string
  approvedAt?: string
  workQueueItemId?: string
  createdAt: string
  updatedAt: string
}

export interface ClientBundle {
  id: string
  clientId: string
  bundleType: IntakeBundleType
  totalHours: number
  hoursUsed: number
  hoursRemaining: number
  monthlyPrice: number
  hourlyRate: number
  periodStart: string
  periodEnd: string
  isActive: boolean
  isOverBudget: boolean
}

export interface CreateIntakePayload {
  clientId: string
  productType: IntakeProductType
  bundleType: IntakeBundleType
  requirements: string
  firstTask?: string
  additionalNotes?: string
}

export const intakeApi = {
  list: (clientId?: string) =>
    apiClient.get<ClientIntake[]>('/api/intake', { params: clientId ? { clientId } : undefined }),

  get: (id: string) => apiClient.get<ClientIntake>(`/api/intake/${id}`),

  create: (payload: CreateIntakePayload) => apiClient.post<ClientIntake>('/api/intake', payload),

  update: (id: string, payload: Partial<CreateIntakePayload>) =>
    apiClient.put<ClientIntake>(`/api/intake/${id}`, payload),

  submit: (id: string) => apiClient.post<ClientIntake>(`/api/intake/${id}/submit`),

  approve: (id: string, approved: boolean, rejectionReason?: string) =>
    apiClient.post<ClientIntake>(`/api/intake/${id}/approve`, { approved, rejectionReason }),

  getBundles: (clientId?: string) =>
    apiClient.get<ClientBundle[]>('/api/intake/bundles', { params: clientId ? { clientId } : undefined }),
}

export const BUNDLE_LABELS: Record<IntakeBundleType, string> = {
  Starter: 'Starter — 50 uur / €125 p/m',
  Team: 'Team — 200 uur / €500 p/m',
  PayPerHour: 'Losse uren — €3/uur',
}

export const PRODUCT_LABELS: Record<IntakeProductType, string> = {
  Website: 'Website + AI assistent',
  AIEmployee: 'AI Medewerker',
  AITeam: 'AI Programmeerteam',
  Custom: 'Maatwerk',
}

export const STATUS_LABELS: Record<IntakeStatus, string> = {
  Draft: 'Concept',
  Estimated: 'Geschat',
  AwaitingApproval: 'Wacht op akkoord',
  Approved: 'Akkoord',
  InProgress: 'In uitvoering',
  Complete: 'Afgerond',
  Cancelled: 'Geannuleerd',
}

export const STATUS_COLORS: Record<IntakeStatus, string> = {
  Draft: 'bg-gray-100 text-gray-700',
  Estimated: 'bg-yellow-100 text-yellow-800',
  AwaitingApproval: 'bg-blue-100 text-blue-800',
  Approved: 'bg-green-100 text-green-800',
  InProgress: 'bg-indigo-100 text-indigo-800',
  Complete: 'bg-emerald-100 text-emerald-800',
  Cancelled: 'bg-red-100 text-red-700',
}

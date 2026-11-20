import apiClient from './client'

export interface HuntCapabilities {
  /** The JengoWork connection (and the public address the agent calls back on) is configured on this server. */
  huntAvailable: boolean
  /** The signed in user may start a hunt (an administrator, or on the allow list). */
  canStartHunt: boolean
  dispatchConfigured: boolean
  googlePlacesConfigured: boolean
  maxPerDay: number
  message: string | null
}

export interface HuntCriteria {
  segment: string
  region: string
  minScore: number
  targetCount: number
  maxCandidates: number
  ratchet: boolean
}

export type HuntStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Cancelled'

export interface HuntJob {
  id: string
  kind: 'Hunt' | 'Verify' | 'Review'
  status: HuntStatus
  segment: string
  region: string
  minScore: number
  targetCount: number
  maxCandidates: number
  ratchet: boolean
  total: number
  screened: number
  qualified: number
  best: number
  current: string | null
  error: string | null
  taskId: string | null
  taskUrl: string | null
  summary: string | null
  lastAgentCallAt: string | null
  createdAt: string
  startedAt: string | null
  completedAt: string | null
}

export interface HuntCandidate {
  id: string
  leadId: string | null
  name: string
  website: string
  score: number | null
  maxReachable: number | null
  outcome: string
  reason: string
  at?: string
}

export interface LeadEvidence {
  available: boolean
  outcome?: string
  score?: number | null
  reason?: string
  at?: string
  evidence?: {
    website: string
    pages: string[]
    verified: { field: string; value: string; source: string; quote: string }[]
    dropped: { field: string; value: string; reason: string }[]
    google?: string | null
    insight?: {
      observation: { quote: string; source: string } | null
      needs: { need: string; quote: string; source: string }[]
      productFit: { product: string; reason: string; quote: string; source: string }[]
      noClearFit: boolean
      dropped: { field: string; value: string; reason: string }[]
    }
  }
}

export interface Product {
  key: string
  name: string
  whatItIs: string
  fitsWhen: string
}

/** Our products (the catalog a lead's product fit may name). */
export async function getProducts(): Promise<Product[]> {
  return (await apiClient.get<Product[]>('/api/leads/products')).data
}

export const isActive = (s: HuntStatus) => s === 'Pending' || s === 'Running'

/** The server message of a failed call (the 503 "Agent hunting is not configured on this server" included). */
export function errorMessage(err: unknown, fallback = 'Something went wrong'): string {
  const e = err as { response?: { data?: { error?: string; message?: string } | string } }
  const data = e?.response?.data
  if (typeof data === 'string' && data.trim()) return data
  if (data && typeof data === 'object') return data.error || data.message || fallback
  return fallback
}

export async function getHuntCapabilities(): Promise<HuntCapabilities> {
  return (await apiClient.get<HuntCapabilities>('/api/leads/hunt/capabilities')).data
}

export async function startHunt(criteria: HuntCriteria): Promise<{ jobId: string; taskUrl: string | null }> {
  return (await apiClient.post<{ jobId: string; taskUrl: string | null }>('/api/leads/hunt', criteria)).data
}

/** Ask a Jengo agent to research up to 5 of your leads on the web; the server verifies what it finds. Needs the JengoWork connection. */
export async function startAgentReview(ids: string[]): Promise<{ jobId: string; taskUrl: string | null }> {
  return (await apiClient.post<{ jobId: string; taskUrl: string | null }>('/api/leads/hunt/review', { ids })).data
}

/** Verify and rescore. Values a person typed are kept unless `confirm` is true; use previewVerify first to see what would change. */
export async function startVerify(ids: string[], confirm = false): Promise<{ jobId: string; leads: number; skipped: number }> {
  return (await apiClient.post('/api/leads/verify', { ids, confirm })).data
}

export interface VerifyPreview {
  leadId: string
  reachable: boolean
  scoreBefore: number | null
  scoreAfter: number | null
  wouldClear: boolean
  wouldRemove: string[]
  keptBecauseEntered: string[]
  verified: { field: string; value: string; source: string; quote: string }[]
  notCounted: { field: string; reason: string }[]
}

/** A dry run: what a verify would do to one lead, without changing anything. */
export async function previewVerify(id: string, confirm = false): Promise<VerifyPreview> {
  return (await apiClient.post<VerifyPreview>(`/api/leads/${id}/verify`, null, { params: { dryRun: true, confirm } })).data
}

export async function getHunt(id: string): Promise<{ job: HuntJob; candidates: HuntCandidate[] }> {
  return (await apiClient.get(`/api/leads/hunt/${id}`)).data
}

export async function getRecentHunts(): Promise<HuntJob[]> {
  return (await apiClient.get<HuntJob[]>('/api/leads/hunt')).data
}

export async function cancelHunt(id: string): Promise<HuntJob> {
  return (await apiClient.post<HuntJob>(`/api/leads/hunt/${id}/cancel`)).data
}

export async function getLeadEvidence(leadId: string): Promise<LeadEvidence> {
  return (await apiClient.get<LeadEvidence>(`/api/leads/${leadId}/evidence`)).data
}

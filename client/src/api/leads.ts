import apiClient from './client'

export interface Lead {
  id: string
  name: string
  website: string
  sector: string
  city: string
  phone: string
  companyEmail: string
  ownerName: string
  ownerFirstName: string
  ownerLastName: string
  personalEmail: string
  linkedInUrl?: string
  isEnriched: boolean
  enrichedAt: string | null
  importedAt: string
  source: string
  // RAG enrichment fields
  ownerTitle?: string
  description?: string
  services?: string
  targetAudience?: string
  websiteStatus?: string
  resolvedUrl?: string
  crawledAt?: string | null
  enrichmentVersion?: number
  pagesCrawled?: number
  chunksIndexed?: number
  aiSummary?: string | null
  salesPitch?: string | null
}

export interface LeadsResponse {
  items: Lead[]
  total: number
  page: number
  pageSize: number
}

export interface LeadFilter {
  enriched?: boolean
  enrichedAfter?: string
  enrichedBefore?: string
  page?: number
  pageSize?: number
  sortBy?: string
  sortDesc?: boolean
}

export interface LeadStats {
  total: number
  enriched: number
  notEnriched: number
}

export async function fetchLeads(filter: LeadFilter): Promise<LeadsResponse> {
  const params: Record<string, string | number | boolean> = {}
  if (filter.enriched !== undefined) params.enriched = filter.enriched
  if (filter.enrichedAfter) params.enrichedAfter = filter.enrichedAfter
  if (filter.enrichedBefore) params.enrichedBefore = filter.enrichedBefore
  if (filter.page !== undefined) params.page = filter.page
  if (filter.pageSize !== undefined) params.pageSize = filter.pageSize
  if (filter.sortBy) params.sortBy = filter.sortBy
  if (filter.sortDesc !== undefined) params.sortDesc = filter.sortDesc

  const res = await apiClient.get<LeadsResponse>('/api/leads', { params })
  return res.data
}

export async function fetchLeadStats(): Promise<LeadStats> {
  const res = await apiClient.get<LeadStats>('/api/leads/stats')
  return res.data
}

export async function importLeads(file: File): Promise<void> {
  const formData = new FormData()
  formData.append('file', file)
  await apiClient.post('/api/leads/import', formData, {
    headers: { 'Content-Type': 'multipart/form-data' },
  })
}

export async function enrichLeads(ids: string[]): Promise<{ jobId: string }> {
  const res = await apiClient.post<{ jobId: string }>('/api/leads/enrich', { ids })
  return res.data
}

export interface EnrichmentJobStatus {
  id: string
  status: string
  totalLeads: number
  processedLeads: number
  successCount: number
  errorCount: number
  completedAt: string | null
}

export async function fetchEnrichmentJobStatus(jobId: string): Promise<EnrichmentJobStatus> {
  const res = await apiClient.get<EnrichmentJobStatus>(`/api/leads/enrich/${jobId}`)
  return res.data
}

export interface LeadSearchResult {
  name: string
  website: string
  city: string
  sector: string
  phone: string
  email: string
  source: string
}

export interface ImportResultDto {
  imported: number
  skipped: number
  errors: number
  errorDetails: string[]
}

export async function searchLeads(
  sector: string,
  location: string,
  limit: number
): Promise<LeadSearchResult[]> {
  const res = await apiClient.post<LeadSearchResult[]>('/api/leads/search', {
    sector,
    location,
    limit,
  })
  return res.data
}

export async function importSearchResults(leads: LeadSearchResult[]): Promise<ImportResultDto> {
  const res = await apiClient.post<ImportResultDto>('/api/leads/search/import', { leads })
  return res.data
}

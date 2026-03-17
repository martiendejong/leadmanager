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
  // KvK enrichment fields
  kvkNumber?: string | null
  vatNumber?: string | null
  street?: string | null
  zipCode?: string | null
  employeeCount?: string | null
  branchCount?: number | null
  foundingYear?: number | null
  legalForm?: string | null
  // Google enrichment fields
  googleRating?: number | null
  googleReviewCount?: number | null
  googleMapsUrl?: string | null
  // Social media fields
  facebookUrl?: string | null
  instagramUrl?: string | null
  twitterUrl?: string | null
  // Business intelligence fields
  isPartOfGroup?: boolean
  groupName?: string | null
  notableClients?: string | null
  salesPriorityScore?: number | null
  // Multi-input support fields
  manualInput?: string | null
  hasUploadedDocuments?: boolean
  enrichmentSources?: string | null
  // AI Sales Approach
  salesApproach?: string | null
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

export interface CreateLeadDto {
  name: string
  website?: string | null
  sector: string
  city: string
  phone: string
  companyEmail: string
  source: string
  manualInput?: string | null
}

export async function createLead(dto: CreateLeadDto): Promise<Lead> {
  const res = await apiClient.post<Lead>('/api/leads', dto)
  return res.data
}

export async function uploadLeadDocuments(leadId: string, files: File[]): Promise<{
  filesProcessed: number
  totalFiles: number
  message: string
}> {
  const formData = new FormData()
  files.forEach((file) => formData.append('files', file))

  const res = await apiClient.post(`/api/leads/${leadId}/documents`, formData, {
    headers: { 'Content-Type': 'multipart/form-data' },
  })
  return res.data
}

export interface SalesApproachResult {
  linkedinMessage: string
  phoneOpener: string
  emailIntro: string
}

export async function regenerateSalesApproach(leadId: string): Promise<SalesApproachResult> {
  const res = await apiClient.post<SalesApproachResult>(`/api/leads/${leadId}/regenerate-sales-approach`)
  return res.data
}

// Lead Notes
export interface LeadNote {
  id: string
  leadId: string
  content: string
  createdAt: string
  createdByUserId: string
  createdByName?: string | null
}

export interface CreateLeadNoteDto {
  content: string
}

export async function fetchLeadNotes(leadId: string): Promise<LeadNote[]> {
  const res = await apiClient.get<LeadNote[]>(`/api/leads/${leadId}/notes`)
  return res.data
}

export async function createLeadNote(leadId: string, dto: CreateLeadNoteDto): Promise<LeadNote> {
  const res = await apiClient.post<LeadNote>(`/api/leads/${leadId}/notes`, dto)
  return res.data
}

export async function deleteLeadNote(leadId: string, noteId: string): Promise<void> {
  await apiClient.delete(`/api/leads/${leadId}/notes/${noteId}`)
}

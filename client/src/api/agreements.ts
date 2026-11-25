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
  billingInterval: BillingInterval
  billingAmount?: number | null
  hourlyRate?: number | null
  vatRate?: number | null
  paidUntil?: string | null
  freeUntil?: string | null
  freeReason?: string | null
  invoiceNote?: string | null
}

export type BillingInterval = 'Monthly' | 'Quarterly' | 'Yearly' | 'OneOff' | 'Hourly'
export const INTERVAL_LABELS: Record<BillingInterval, string> = {
  Monthly: 'Monthly', Quarterly: 'Quarterly', Yearly: 'Yearly', OneOff: 'One-off', Hourly: 'Hourly',
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
  billingInterval?: BillingInterval | null
  billingAmount?: number | null
  hourlyRate?: number | null
  vatRate?: number | null
  paidUntil?: string | null
  freeUntil?: string | null
  freeReason?: string | null
  invoiceNote?: string | null
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
  billing?: BillingSummary | null
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

export type InvoiceStatus = 'Draft' | 'Sent' | 'Paid' | 'Cancelled'

export interface ClientInvoice {
  id: string
  clientId: string
  serviceId?: string | null
  number: string
  invoiceDate: string
  periodStart?: string | null
  periodEnd?: string | null
  amountExclVat: number
  vatRate: number
  amountInclVat: number
  status: InvoiceStatus
  sentTo?: string | null
  notes?: string | null
  sourceRef?: string | null
  paidAt?: string | null
  sentAt?: string | null
  sendUnknown?: boolean
  outstandingAmount?: number | null
  syncedAt?: string | null
}

export interface BillingSummary {
  concepts: number
  sent: number
  openAmount: number
  paid: number
  sendUnknown: number
  cancelled: number
  lastInvoiceDate?: string | null
  recent: ClientInvoice[]
}

export interface UpsertInvoice {
  number: string
  amountExclVat: number
  invoiceDate?: string | null
  periodStart?: string | null
  periodEnd?: string | null
  vatRate?: number | null
  status?: InvoiceStatus | null
  sentTo?: string | null
  notes?: string | null
  serviceId?: string | null
}

export type DueState = 'Due' | 'Upcoming' | 'Covered' | 'NeedsInput' | 'Nothing'

export interface DueItem {
  serviceId: string
  clientId: string
  serviceName: string
  interval: string
  periodStart?: string | null
  periodEnd?: string | null
  amountExclVat?: number | null
  vatRate: number
  amountInclVat?: number | null
  state: DueState
  periodsOverdue: number
  isFree: boolean
  freeUntil?: string | null
  coveredThrough?: string | null
  explanation: string
}

export interface ClientDue {
  clientId: string
  clientName: string
  items: DueItem[]
}

export async function getInvoices(clientId: string): Promise<ClientInvoice[]> {
  const res = await apiClient.get<ClientInvoice[]>(`/api/clients/${clientId}/invoices`)
  return res.data
}

export async function createInvoice(clientId: string, body: UpsertInvoice): Promise<ClientInvoice> {
  const res = await apiClient.post<ClientInvoice>(`/api/clients/${clientId}/invoices`, body)
  return res.data
}

export async function updateInvoice(id: string, body: UpsertInvoice): Promise<ClientInvoice> {
  const res = await apiClient.put<ClientInvoice>(`/api/invoices/${id}`, body)
  return res.data
}

export async function deleteInvoice(id: string): Promise<void> {
  await apiClient.delete(`/api/invoices/${id}`)
}

export async function getInvoicingDue(clientId?: string): Promise<ClientDue[]> {
  const res = await apiClient.get<ClientDue[]>('/api/invoicing/due', { params: { clientId } })
  return res.data
}

export const eur = (n?: number | null) => (n == null ? '' : n.toLocaleString('en-GB', { style: 'currency', currency: 'EUR' }))

export interface UnmatchedContact {
  fedhaContactId: number
  name: string
  invoiceCount: number
  openAmount: number
  numbers: string[]
}

export interface FedhaSyncStatus {
  configured: boolean
  lastRun?: { startedAt: string; finishedAt?: string | null; trigger: string; created: number; updated: number; unchanged: number; unmatched: number; error?: string | null } | null
  lastSuccessAt?: string | null
  unmatched: UnmatchedContact[]
}

export async function getFedhaSyncStatus(): Promise<FedhaSyncStatus> {
  const res = await apiClient.get<FedhaSyncStatus>('/api/fedha-sync/status')
  return res.data
}

export async function runFedhaSync(): Promise<{ created: number; updated: number; unchanged: number; unmatched: UnmatchedContact[] }> {
  const res = await apiClient.post('/api/fedha-sync/run')
  return res.data
}

export async function linkFedhaContact(fedhaContactId: number, fedhaContactName: string, clientId: string): Promise<void> {
  await apiClient.post('/api/fedha-sync/links', { fedhaContactId, fedhaContactName, clientId })
}

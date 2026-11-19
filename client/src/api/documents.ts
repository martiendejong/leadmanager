import apiClient from './client'

export type DocumentKind = 'Quote' | 'Order' | 'Agreement' | 'Dpa' | 'Nda' | 'Invoice' | 'Other'
export type DocumentStatus = 'Draft' | 'Sent' | 'Signed' | 'Rejected' | 'Expired' | 'Cancelled'

export const KIND_LABELS: Record<DocumentKind, string> = {
  Quote: 'Quote',
  Order: 'Order',
  Agreement: 'Agreement',
  Dpa: 'Data processing agreement',
  Nda: 'NDA',
  Invoice: 'Invoice',
  Other: 'Other',
}

export const STATUS_LABELS: Record<DocumentStatus, string> = {
  Draft: 'Draft',
  Sent: 'Sent',
  Signed: 'Signed',
  Rejected: 'Rejected',
  Expired: 'Expired',
  Cancelled: 'Withdrawn',
}

/** The usual next step in the chain: a quote becomes an order, an order an agreement. */
export const NEXT_KIND: Partial<Record<DocumentKind, DocumentKind>> = { Quote: 'Order', Order: 'Agreement' }

export interface DocumentFile {
  id: string
  fileName: string
  contentType: string
  sizeBytes: number
  label?: string | null
  uploadedAt: string
}

export interface ClientDocument {
  id: string
  clientId: string
  projectId?: string | null
  projectName?: string | null
  predecessorId?: string | null
  kind: DocumentKind
  status: DocumentStatus
  title: string
  reference?: string | null
  documentDate: string
  signedAt?: string | null
  validUntil?: string | null
  amount?: number | null
  notes?: string | null
  url?: string | null
  createdAt: string
  files: DocumentFile[]
}

export interface UpsertDocument {
  kind: DocumentKind
  title: string
  status?: DocumentStatus | null
  reference?: string | null
  documentDate?: string | null
  signedAt?: string | null
  validUntil?: string | null
  amount?: number | null
  notes?: string | null
  url?: string | null
  projectId?: string | null
  predecessorId?: string | null
}

export async function getDocuments(clientId: string): Promise<ClientDocument[]> {
  const res = await apiClient.get<ClientDocument[]>(`/api/clients/${clientId}/documents`)
  return res.data
}

export async function createDocument(clientId: string, data: UpsertDocument): Promise<ClientDocument> {
  const res = await apiClient.post<ClientDocument>(`/api/clients/${clientId}/documents`, data)
  return res.data
}

export async function updateDocument(id: string, data: UpsertDocument): Promise<ClientDocument> {
  const res = await apiClient.put<ClientDocument>(`/api/documents/${id}`, data)
  return res.data
}

export async function deleteDocument(id: string): Promise<void> {
  await apiClient.delete(`/api/documents/${id}`)
}

export async function uploadDocumentFile(id: string, file: File, label?: string): Promise<DocumentFile> {
  const form = new FormData()
  form.append('file', file)
  if (label) form.append('label', label)
  // axios drops this header for FormData so the browser can add the multipart boundary itself.
  const res = await apiClient.post<DocumentFile>(`/api/documents/${id}/files`, form, {
    headers: { 'Content-Type': 'multipart/form-data' },
  })
  return res.data
}

export async function deleteDocumentFile(fileId: string): Promise<void> {
  await apiClient.delete(`/api/documents/files/${fileId}`)
}

/** Downloads through the API client so the login token is sent; a plain link would not carry it. */
export async function downloadDocumentFile(file: DocumentFile): Promise<void> {
  let blob: Blob
  try {
    const res = await apiClient.get<Blob>(`/api/documents/files/${file.id}`, { responseType: 'blob' })
    blob = res.data
  } catch (err) {
    // With responseType blob an error body is a Blob too: read the message out of it.
    const data = (err as { response?: { data?: unknown } })?.response?.data
    if (data instanceof Blob) {
      try {
        const text = JSON.parse(await data.text()) as { error?: string }
        if (text.error) throw Object.assign(new Error(text.error), { response: { data: text.error } })
      } catch (inner) {
        if (inner instanceof Error && 'response' in inner) throw inner
      }
    }
    throw err
  }
  const url = URL.createObjectURL(blob)
  try {
    const a = document.createElement('a')
    a.href = url
    a.download = file.fileName
    document.body.appendChild(a)
    a.click()
    a.remove()
  } finally {
    setTimeout(() => URL.revokeObjectURL(url), 10_000)
  }
}

export function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${Math.round(n / 1024)} kB`
  return `${(n / (1024 * 1024)).toFixed(1)} MB`
}

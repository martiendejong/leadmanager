import apiClient from './client'
import type { ProductFit } from './leads'

/** The app never sends anything: a draft is text, "Sent" only records that the user sent it themselves. */
export const CHANNELS = ['Email', 'LinkedIn', 'WhatsApp', 'Phone'] as const
export type Channel = (typeof CHANNELS)[number]

export const CHANNEL_LABELS: Record<Channel, string> = {
  Email: 'E-mail',
  LinkedIn: 'LinkedIn',
  WhatsApp: 'WhatsApp',
  Phone: 'Phone call note',
}

export const MAX_SUBJECT = 200
export const MAX_BODY = 5000

export interface LeadMessage {
  id: string
  leadId: string
  leadName: string
  channel: Channel
  subject: string
  body: string
  status: 'Draft' | 'Sent'
  createdAt: string
  updatedAt: string
  sentAt: string | null
  leadPipelineStatus: string | null
  leadScore: number | null
  organizationId: string
}

export interface Prefill {
  channel: Channel
  subject: string
  body: string
  language: string
  product: string | null
  templateSource: string
  toAddress: string | null
  warnings: string[]
}

export interface MessagePage {
  items: LeadMessage[]
  total: number
  page: number
  pageSize: number
}

export interface MarkSentResult {
  message: LeadMessage
  pipelineStatus: string
  statusChanged: boolean
  lastContactedAt: string
}

export interface QueueItem {
  id: string
  name: string
  score: number | null
  label: string | null
  sector: string | null
  city: string | null
  ownerName: string | null
  whyLead: string | null
  needs: string | null
  productFit: ProductFit[] | null
  hasDraft: boolean
  lastContactedAt: string | null
  organizationId: string
}

export interface MessageTemplate {
  id: string
  channel: Channel
  productKey: string
  subject: string
  body: string
  updatedAt: string
}

export interface TemplateInfo {
  placeholders: { name: string; meaning: string }[]
  rule: string
  channels: Channel[]
  products: { key: string; name: string }[]
  templates: MessageTemplate[]
  builtIn: { channel: Channel; language: string; subject: string; body: string }[]
}

export interface BulkResult {
  created: number
  skipped: number
  items: { leadId: string; name: string; messageId: string; warnings: string[] }[]
  skippedItems: { leadId: string; name: string | null; reason: string }[]
}

export async function getPrefill(leadId: string, channel: Channel, product?: string): Promise<Prefill> {
  const res = await apiClient.get<Prefill>(`/api/leads/${leadId}/messages/prefill`, { params: { channel, product: product || undefined } })
  return res.data
}

export async function getLeadMessages(leadId: string): Promise<LeadMessage[]> {
  const res = await apiClient.get<LeadMessage[]>(`/api/leads/${leadId}/messages`)
  return res.data
}

export async function listMessages(params: { status?: string; channel?: string; q?: string; page?: number; pageSize?: number; organizationId?: string }): Promise<MessagePage> {
  const res = await apiClient.get<MessagePage>('/api/messages', { params })
  return res.data
}

export async function createDraft(leadId: string, dto: { channel: Channel; subject: string; body: string }): Promise<LeadMessage> {
  const res = await apiClient.post<LeadMessage>(`/api/leads/${leadId}/messages`, dto)
  return res.data
}

export async function updateDraft(id: string, dto: { channel: Channel; subject: string; body: string }): Promise<LeadMessage> {
  const res = await apiClient.put<LeadMessage>(`/api/messages/${id}`, dto)
  return res.data
}

export async function deleteDraft(id: string): Promise<void> {
  await apiClient.delete(`/api/messages/${id}`)
}

export async function markSent(id: string): Promise<MarkSentResult> {
  const res = await apiClient.post<MarkSentResult>(`/api/messages/${id}/sent`, { confirmed: true })
  return res.data
}

export async function getMailto(id: string): Promise<{ url: string; address: string }> {
  const res = await apiClient.get<{ url: string; address: string }>(`/api/messages/${id}/mailto`)
  return res.data
}

export async function bulkDrafts(leadIds: string[], channel: Channel, product?: string): Promise<BulkResult> {
  const res = await apiClient.post<BulkResult>('/api/messages/bulk-drafts', { leadIds, channel, product: product || null })
  return res.data
}

export async function getQueue(take = 20, minScore = 6, organizationId?: string): Promise<QueueItem[]> {
  const res = await apiClient.get<QueueItem[]>('/api/messages/queue', { params: { take, minScore, organizationId } })
  return res.data
}

export async function getTemplates(): Promise<TemplateInfo> {
  const res = await apiClient.get<TemplateInfo>('/api/message-templates')
  return res.data
}

export async function saveTemplate(dto: { channel: Channel; productKey: string; subject: string; body: string }): Promise<MessageTemplate> {
  const res = await apiClient.put<MessageTemplate>('/api/message-templates', dto)
  return res.data
}

export async function deleteTemplate(id: string): Promise<void> {
  await apiClient.delete(`/api/message-templates/${id}`)
}

// ---- AI help in the message section (regenerate with an instruction, demo ideas). Optional: 503 ai_not_configured without a key. ----

export const MAX_AI_INSTRUCTION = 500

export interface AiStatus { configured: boolean; model: string | null; maxInstruction: number; message: string | null }

export interface AiMessage {
  channel: Channel
  subject: string
  body: string
  language: string
  model: string
  notes: string[]
  startedFrom: 'editor' | 'template'
}

export interface DemoIdea {
  id: string
  title: string
  shows: string
  whyFit: string
  productKey: string
  prompt: string
  instruction: string
  model: string
  generatedAt: string
}

export interface DemoIdeaSet { items: DemoIdea[]; generatedAt: string | null; noFitReason?: string | null; configured?: boolean }

export async function getAiStatus(): Promise<AiStatus> {
  const res = await apiClient.get<AiStatus>('/api/ai/status')
  return res.data
}

/** A new draft text from the stored facts of the lead. Saves nothing: the user still saves the draft. */
export async function aiMessage(leadId: string, dto: { channel: Channel; product?: string; instruction?: string; subject?: string; body?: string }): Promise<AiMessage> {
  const res = await apiClient.post<AiMessage>(`/api/leads/${leadId}/ai/message`, dto, { timeout: 90_000 })
  return res.data
}

export async function getDemoIdeas(leadId: string): Promise<DemoIdeaSet> {
  const res = await apiClient.get<DemoIdeaSet>(`/api/leads/${leadId}/ai/demos`)
  return res.data
}

export async function generateDemoIdeas(leadId: string, instruction?: string): Promise<DemoIdeaSet> {
  const res = await apiClient.post<DemoIdeaSet>(`/api/leads/${leadId}/ai/demos`, { instruction: instruction || null }, { timeout: 90_000 })
  return res.data
}

import apiClient from './client'

// Jengo Clients CRM core (tasks 8023/8024). One API for leads and clients.

export type ParentType = 'leads' | 'clients'

export type InteractionKind = 'Call' | 'Email' | 'WhatsApp' | 'Meeting' | 'Note'
export type InteractionDirection = 'Outbound' | 'Inbound' | 'Internal'

export type EntityLinkKind =
  | 'JengoWorkBoard'
  | 'JengoWorkTask'
  | 'KnowledgeProject'
  | 'MailThread'
  | 'MeetSession'
  | 'WhatsAppChat'
  | 'GitHubRepo'
  | 'Document'
  | 'Web'

export interface TimelineItem {
  source: 'interaction' | 'activity'
  id: string
  kind: string
  direction: string | null
  at: string
  summary: string
  outcome: string | null
  sourceRef: string | null
}

export interface NextAction {
  id: string
  leadId: string | null
  clientId: string | null
  contactId: string | null
  title: string
  dueAt: string
  assignedToUserId: string | null
  doneAt: string | null
  createdAt: string
}

export interface TodayAction {
  id: string
  parentType: ParentType
  parentId: string
  parentName: string
  phone: string | null
  title: string
  dueAt: string
  overdue: boolean
}

export interface Contact {
  id: string
  name: string
  role: string | null
  email: string | null
  phone: string | null
  mobile: string | null
  linkedInUrl: string | null
  preferredChannel: string | null
  notes: string | null
}

export interface UpsertContact {
  name: string
  role?: string | null
  email?: string | null
  phone?: string | null
  mobile?: string | null
  linkedInUrl?: string | null
  preferredChannel?: string | null
  notes?: string | null
}

export interface EntityLink {
  id: string
  kind: EntityLinkKind
  label: string
  url: string | null
  externalId: string | null
  createdAt: string
}

export const INTERACTION_LABELS: Record<InteractionKind, string> = {
  Call: 'Gebeld',
  Email: 'E-mail',
  WhatsApp: 'WhatsApp',
  Meeting: 'Gesprek / meeting',
  Note: 'Notitie',
}

export const LINK_LABELS: Record<EntityLinkKind, string> = {
  JengoWorkBoard: 'JengoWork-bord',
  JengoWorkTask: 'JengoWork-taak',
  KnowledgeProject: 'JengoKnowledge-project',
  MailThread: 'Mailthread',
  MeetSession: 'Meet-sessie',
  WhatsAppChat: 'WhatsApp-chat',
  GitHubRepo: 'GitHub-repo',
  Document: 'Document',
  Web: 'Website',
}

/** Quick outcomes for a call, so logging one takes two clicks. */
export const CALL_OUTCOMES = ['Geen gehoor', 'Terugbellen', 'Interesse', 'Geen interesse', 'Afspraak gemaakt']

export async function getTimeline(parentType: ParentType, parentId: string): Promise<TimelineItem[]> {
  const res = await apiClient.get<TimelineItem[]>(`/api/${parentType}/${parentId}/timeline`)
  return res.data
}

export async function logInteraction(
  parentType: ParentType,
  parentId: string,
  data: { kind: InteractionKind; direction?: InteractionDirection; summary: string; outcome?: string | null; contactId?: string | null },
): Promise<void> {
  await apiClient.post(`/api/${parentType}/${parentId}/interactions`, data)
}

export async function getNextActions(parentType: ParentType, parentId: string): Promise<NextAction[]> {
  const res = await apiClient.get<NextAction[]>(`/api/${parentType}/${parentId}/next-actions`)
  return res.data
}

export async function addNextAction(
  parentType: ParentType,
  parentId: string,
  data: { title: string; dueAt: string; contactId?: string | null },
): Promise<NextAction> {
  const res = await apiClient.post<NextAction>(`/api/${parentType}/${parentId}/next-actions`, data)
  return res.data
}

export async function completeNextAction(id: string): Promise<void> {
  await apiClient.post(`/api/next-actions/${id}/complete`)
}

export async function getTodayActions(): Promise<TodayAction[]> {
  const res = await apiClient.get<TodayAction[]>('/api/next-actions/today')
  return res.data
}

export async function getContacts(parentType: ParentType, parentId: string): Promise<Contact[]> {
  const res = await apiClient.get<Contact[]>(`/api/${parentType}/${parentId}/contacts`)
  return res.data
}

export async function addContact(parentType: ParentType, parentId: string, data: UpsertContact): Promise<Contact> {
  const res = await apiClient.post<Contact>(`/api/${parentType}/${parentId}/contacts`, data)
  return res.data
}

export async function deleteContact(id: string): Promise<void> {
  await apiClient.delete(`/api/contacts/${id}`)
}

export async function getLinks(parentType: ParentType, parentId: string): Promise<EntityLink[]> {
  const res = await apiClient.get<EntityLink[]>(`/api/${parentType}/${parentId}/links`)
  return res.data
}

export async function addLink(
  parentType: ParentType,
  parentId: string,
  data: { kind: EntityLinkKind; label: string; url?: string | null; externalId?: string | null },
): Promise<EntityLink> {
  const res = await apiClient.post<EntityLink>(`/api/${parentType}/${parentId}/links`, data)
  return res.data
}

export async function deleteLink(id: string): Promise<void> {
  await apiClient.delete(`/api/links/${id}`)
}

/** Only http(s) targets are rendered as clickable links. */
export function safeHref(url: string | null | undefined): string | undefined {
  return url && /^https?:\/\//i.test(url) ? url : undefined
}

export function errorMessage(err: unknown, fallback: string): string {
  const data = (err as { response?: { data?: unknown } })?.response?.data
  return typeof data === 'string' && data ? data : fallback
}

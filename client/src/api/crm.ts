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
  projectId?: string | null
  projectName?: string | null
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
  projectId?: string | null
}

export const INTERACTION_LABELS: Record<InteractionKind, string> = {
  Call: 'Call',
  Email: 'Email',
  WhatsApp: 'WhatsApp',
  Meeting: 'Meeting',
  Note: 'Note',
}

export const LINK_LABELS: Record<EntityLinkKind, string> = {
  JengoWorkBoard: 'JengoWork board',
  JengoWorkTask: 'JengoWork task',
  KnowledgeProject: 'Knowledge project',
  MailThread: 'Mail thread',
  MeetSession: 'Meet session',
  WhatsAppChat: 'WhatsApp chat',
  GitHubRepo: 'GitHub repo',
  Document: 'Document',
  Web: 'Web link',
}

/** Quick outcomes for a call, so logging one takes two clicks. */
export const CALL_OUTCOMES = ['No answer', 'Call back', 'Interested', 'Not interested', 'Meeting booked']

export async function getTimeline(parentType: ParentType, parentId: string, projectId?: string): Promise<TimelineItem[]> {
  const res = await apiClient.get<TimelineItem[]>(`/api/${parentType}/${parentId}/timeline`, {
    params: projectId ? { projectId } : undefined,
  })
  return res.data
}

export async function logInteraction(
  parentType: ParentType,
  parentId: string,
  data: { kind: InteractionKind; direction?: InteractionDirection; summary: string; outcome?: string | null; contactId?: string | null; occurredAt?: string | null; projectId?: string | null },
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

export async function getLinks(
  parentType: ParentType,
  parentId: string,
  opts?: { projectId?: string; includeProjects?: boolean },
): Promise<EntityLink[]> {
  const res = await apiClient.get<EntityLink[]>(`/api/${parentType}/${parentId}/links`, { params: opts })
  return res.data
}

export async function addLink(
  parentType: ParentType,
  parentId: string,
  data: { kind: EntityLinkKind; label: string; url?: string | null; externalId?: string | null; projectId?: string | null },
): Promise<EntityLink> {
  const res = await apiClient.post<EntityLink>(`/api/${parentType}/${parentId}/links`, data)
  return res.data
}

export async function deleteLink(id: string): Promise<void> {
  await apiClient.delete(`/api/links/${id}`)
}

export interface TrackClick {
  ts: number
  url: string | null
  label: string | null
  campaign: string | null
  channel: string | null
  device: string | null
  reason: string | null
}

export interface TrackTimeline {
  enabled: boolean
  available: boolean
  humanClickCount: number
  human: TrackClick[]
  machine: TrackClick[]
}

/** Click history from Jengo Track. Comes back empty (enabled=false) when Track is off. */
export async function getTrack(parentType: ParentType, parentId: string): Promise<TrackTimeline> {
  const res = await apiClient.get<TrackTimeline>(`/api/${parentType}/${parentId}/track`)
  return res.data
}

export async function createTrackedLink(
  parentType: ParentType,
  parentId: string,
  data: { url: string; campaign?: string | null; label?: string | null },
): Promise<{ shortUrl: string; token: string }> {
  const res = await apiClient.post<{ shortUrl: string; token: string }>(`/api/${parentType}/${parentId}/track/link`, data)
  return res.data
}

/** Only http(s) targets are rendered as clickable links. */
export function safeHref(url: string | null | undefined): string | undefined {
  return url && /^https?:\/\//i.test(url) ? url : undefined
}

export function errorMessage(err: unknown, fallback: string): string {
  const data = (err as { response?: { data?: unknown } })?.response?.data
  if (typeof data === 'string' && data) return data
  const msg = (data as { error?: unknown } | undefined)?.error
  return typeof msg === 'string' && msg ? msg : fallback
}

export interface ActionRow {
  id: string
  parentType: ParentType
  parentId: string
  parentName: string
  title: string
  dueAt: string
  doneAt: string | null
  overdue: boolean
}

export async function getActions(): Promise<ActionRow[]> {
  const res = await apiClient.get<ActionRow[]>('/api/next-actions')
  return res.data
}

export interface CalendarFeed {
  token: string
  url: string
}

export async function getCalendarFeed(): Promise<CalendarFeed> {
  const res = await apiClient.get<CalendarFeed>('/api/calendar/feed')
  return res.data
}

export async function rotateCalendarFeed(): Promise<CalendarFeed> {
  const res = await apiClient.post<CalendarFeed>('/api/calendar/feed/rotate')
  return res.data
}

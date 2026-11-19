import { useCallback, useEffect, useMemo, useState } from 'react'
import { useToast } from '../Toast'
import {
  INTERACTION_LABELS,
  LINK_LABELS,
  addLink,
  deleteLink,
  errorMessage,
  getLinks,
  getTimeline,
  logInteraction,
  safeHref,
  type EntityLink,
  type EntityLinkKind,
  type InteractionKind,
  type TimelineItem,
} from '../../api/crm'
import { createProject, updateProject, deleteProject, type Project } from '../../api/clients'

const STATUSES = ['Concept', 'Pilot', 'Active', 'Maintenance', 'On Hold', 'Completed', 'Archived'] as const
const STATUS_LABEL: Record<string, string> = {
  Concept: 'Internal concept',
  Pilot: 'Pilot',
  Active: 'Active',
  Maintenance: 'Maintenance',
  'On Hold': 'On hold',
  Completed: 'Completed',
  Archived: 'Archived',
}
const STATUS_CLS: Record<string, string> = {
  Concept: 'bg-gray-50 text-gray-600 border-gray-300',
  Pilot: 'bg-purple-50 text-purple-700 border-purple-200',
  Active: 'bg-green-50 text-green-700 border-green-200',
  Maintenance: 'bg-teal-50 text-teal-700 border-teal-200',
  Completed: 'bg-blue-50 text-blue-700 border-blue-200',
  'On Hold': 'bg-amber-50 text-amber-700 border-amber-200',
  Archived: 'bg-gray-100 text-gray-500 border-gray-300',
}
const KIND_ICON: Record<string, string> = { Call: '📞', Email: '✉️', WhatsApp: '💬', Meeting: '🤝', Note: '📝' }

const input = 'w-full text-sm border border-gray-300 rounded-lg px-3 py-1.5 focus:outline-none focus:ring-2 focus:ring-indigo-300'
const primaryBtn = 'text-sm px-3 py-1.5 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50'
const ghostBtn = 'text-xs px-2 py-1 rounded-md text-gray-500 hover:bg-gray-100'

function fmt(iso: string) {
  return new Date(iso).toLocaleString('en-GB', { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })
}

function toLocalInput(d: Date) {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

/** Links of one project to the other Jengo tools and to code: GitHub repo, JengoWork board, knowledge project, docs. */
function ProjectLinks({
  clientId, project, links, onChanged,
}: {
  clientId: string
  project: Project
  links: EntityLink[]
  onChanged: () => void
}) {
  const { showToast } = useToast()
  const [adding, setAdding] = useState(false)
  const [kind, setKind] = useState<EntityLinkKind>('GitHubRepo')
  const [label, setLabel] = useState('')
  const [url, setUrl] = useState('')
  const [busy, setBusy] = useState(false)

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!label.trim() || !url.trim() || busy) return
    setBusy(true)
    try {
      await addLink('clients', clientId, { kind, label: label.trim(), url: url.trim(), projectId: project.id })
      setLabel('')
      setUrl('')
      setAdding(false)
      onChanged()
    } catch (err) {
      showToast(errorMessage(err, 'Could not add the link'), 'error')
    } finally {
      setBusy(false)
    }
  }

  const remove = async (l: EntityLink) => {
    if (!window.confirm(`Remove the link "${l.label}"?`)) return
    try {
      await deleteLink(l.id)
      onChanged()
    } catch (err) {
      showToast(errorMessage(err, 'Could not remove the link'), 'error')
    }
  }

  return (
    <div className="mt-3 pl-3 border-l-2 border-gray-200">
      <div className="flex items-center justify-between">
        <p className="text-xs font-semibold text-gray-500 uppercase tracking-wider">Links ({links.length})</p>
        <button className={ghostBtn} onClick={() => setAdding((v) => !v)} aria-expanded={adding}>{adding ? 'Cancel' : '+ Link'}</button>
      </div>
      {links.length === 0 && !adding && (
        <p className="text-xs text-gray-400">Link the GitHub repo, the JengoWork board or the knowledge project of this project.</p>
      )}
      <ul className="space-y-1 mt-1">
        {links.map((l) => {
          const href = safeHref(l.url)
          return (
            <li key={l.id} className="flex items-center gap-2 text-sm">
              <span className="text-xs px-2 py-0.5 rounded-full bg-gray-100 text-gray-600 flex-shrink-0">{LINK_LABELS[l.kind] ?? l.kind}</span>
              {href ? (
                <a href={href} target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:underline truncate">{l.label}</a>
              ) : (
                <span className="text-gray-700 truncate">{l.label}</span>
              )}
              <button className={`${ghostBtn} ml-auto`} onClick={() => remove(l)} aria-label={`Remove link ${l.label}`}>✕</button>
            </li>
          )
        })}
      </ul>
      {adding && (
        <form onSubmit={save} className="space-y-2 mt-2">
          <select className={input} value={kind} onChange={(e) => setKind(e.target.value as EntityLinkKind)} aria-label="Type of link">
            {(Object.keys(LINK_LABELS) as EntityLinkKind[]).map((k) => <option key={k} value={k}>{LINK_LABELS[k]}</option>)}
          </select>
          <input className={input} placeholder="Name, e.g. Backend repo" aria-label="Link name" value={label} onChange={(e) => setLabel(e.target.value)} maxLength={200} />
          <input className={input} placeholder="https://..." aria-label="Link address" value={url} onChange={(e) => setUrl(e.target.value)} />
          <div className="flex justify-end">
            <button type="submit" disabled={busy || !label.trim() || !url.trim()} className={primaryBtn}>Save</button>
          </div>
        </form>
      )}
    </div>
  )
}

/** The history of one project: a list of items with a form to paste in new ones. */
function ProjectHistory({ clientId, project, onLogged }: { clientId: string; project: Project; onLogged: () => void }) {
  const { showToast } = useToast()
  const [items, setItems] = useState<TimelineItem[] | null>(null)
  const [kind, setKind] = useState<InteractionKind>('Note')
  const [summary, setSummary] = useState('')
  const [when, setWhen] = useState(() => toLocalInput(new Date()))
  const [busy, setBusy] = useState(false)

  const load = useCallback(() => {
    getTimeline('clients', clientId, project.id)
      .then(setItems)
      .catch(() => showToast('Could not load the history', 'error'))
  }, [clientId, project.id, showToast])

  useEffect(load, [load])

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!summary.trim() || busy) return
    setBusy(true)
    try {
      await logInteraction('clients', clientId, {
        kind,
        direction: 'Internal',
        summary: summary.trim(),
        occurredAt: new Date(when).toISOString(),
        projectId: project.id,
      })
      setSummary('')
      setWhen(toLocalInput(new Date()))
      load()
      onLogged()
    } catch (err) {
      showToast(errorMessage(err, 'Could not save the item'), 'error')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="mt-3 pl-3 border-l-2 border-gray-200 space-y-3">
      <p className="text-xs font-semibold text-gray-500 uppercase tracking-wider">History</p>
      <form onSubmit={submit} className="space-y-2">
        <div className="flex gap-2 flex-wrap">
          <select className={`${input} !w-auto`} value={kind} onChange={(e) => setKind(e.target.value as InteractionKind)} aria-label="Type">
            {(Object.keys(INTERACTION_LABELS) as InteractionKind[]).map((k) => (
              <option key={k} value={k}>{INTERACTION_LABELS[k]}</option>
            ))}
          </select>
          <input type="datetime-local" className={`${input} !w-auto`} value={when} onChange={(e) => setWhen(e.target.value)} aria-label="When" />
        </div>
        <textarea
          className={input}
          rows={3}
          placeholder="Paste a message, mail or note here..."
          aria-label="Message or note"
          value={summary}
          onChange={(e) => setSummary(e.target.value)}
          maxLength={8000}
        />
        <div className="flex justify-end">
          <button type="submit" disabled={busy || !summary.trim()} className={primaryBtn}>Add</button>
        </div>
      </form>

      {items === null ? (
        <p className="text-xs text-gray-400">Loading...</p>
      ) : items.length === 0 ? (
        <p className="text-xs text-gray-400">No history for this project yet.</p>
      ) : (
        <ol className="space-y-3">
          {items.map((i) => (
            <li key={i.id} className="flex gap-3 text-sm">
              <span className="w-5 text-center flex-shrink-0">{KIND_ICON[i.kind] ?? '•'}</span>
              <div className="min-w-0 flex-1">
                <div className="flex items-baseline gap-2 flex-wrap">
                  <span className="font-medium text-gray-800">{INTERACTION_LABELS[i.kind as InteractionKind] ?? i.kind}</span>
                  <span className="text-xs text-gray-400 ml-auto">{fmt(i.at)}</span>
                </div>
                <p className="text-gray-600 whitespace-pre-wrap break-words">{i.summary}</p>
              </div>
            </li>
          ))}
        </ol>
      )}
    </div>
  )
}

export default function ProjectsSection({
  clientId,
  projects,
  onChanged,
  onHistoryChanged,
}: {
  clientId: string
  projects: Project[]
  onChanged: () => void
  onHistoryChanged: () => void
}) {
  const { showToast } = useToast()
  const [open, setOpen] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [newStatus, setNewStatus] = useState<string>('Active')
  const [busy, setBusy] = useState(false)
  const [statusBusy, setStatusBusy] = useState(false)
  const [showArchived, setShowArchived] = useState(false)
  const [links, setLinks] = useState<EntityLink[]>([])

  // All project links of this client in one request; each project filters its own.
  const loadLinks = useCallback(() => {
    getLinks('clients', clientId, { includeProjects: true })
      .then((all) => setLinks(all.filter((l) => l.projectId)))
      .catch(() => showToast('Could not load project links', 'error'))
  }, [clientId, showToast])

  useEffect(loadLinks, [loadLinks])

  const linksOf = useMemo(() => {
    const m = new Map<string, EntityLink[]>()
    for (const l of links) {
      if (!l.projectId) continue
      m.set(l.projectId, [...(m.get(l.projectId) ?? []), l])
    }
    return m
  }, [links])

  const visible = projects.filter((p) => showArchived || p.status !== 'Archived')
  const archivedCount = projects.length - projects.filter((p) => p.status !== 'Archived').length

  const add = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!name.trim() || busy) return
    setBusy(true)
    try {
      const p = await createProject(clientId, { name: name.trim(), description: description.trim() || null, status: newStatus })
      setName('')
      setDescription('')
      setAdding(false)
      setOpen(p.id)
      onChanged()
    } catch (err) {
      showToast(errorMessage(err, 'Could not add the project'), 'error')
    } finally {
      setBusy(false)
    }
  }

  const setStatus = async (p: Project, status: string) => {
    if (status === p.status) return
    // The select is controlled by p.status, so declining the confirmation leaves it as it was.
    const from = STATUS_LABEL[p.status] ?? p.status
    const to = STATUS_LABEL[status] ?? status
    if (!window.confirm(`Change the status of "${p.name}" from ${from} to ${to}?`)) return
    setStatusBusy(true)
    try {
      await updateProject(p.id, { name: p.name, description: p.description ?? null, status })
      onChanged()
    } catch (err) {
      showToast(errorMessage(err, 'Could not change the status'), 'error')
    } finally {
      setStatusBusy(false)
    }
  }

  const remove = async (p: Project) => {
    if (!window.confirm(`Delete project "${p.name}"? Its history stays with the client. Consider archiving instead.`)) return
    try {
      await deleteProject(p.id)
      if (open === p.id) setOpen(null)
      onChanged()
    } catch (err) {
      showToast(errorMessage(err, 'Could not delete the project'), 'error')
    }
  }

  return (
    <div className="bg-white rounded-xl border border-gray-200 p-6">
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-base font-semibold text-gray-900">
          Projects
          <span className="ml-2 text-sm font-normal text-gray-400">({projects.length - archivedCount})</span>
        </h2>
        <button onClick={() => setAdding((v) => !v)} className={ghostBtn} aria-expanded={adding}>{adding ? 'Cancel' : '+ Project'}</button>
      </div>

      {adding && (
        <form onSubmit={add} className="mb-4 space-y-2 p-3 bg-gray-50 rounded-lg">
          <input className={input} placeholder="Project name" aria-label="Project name" value={name} onChange={(e) => setName(e.target.value)} maxLength={200} autoFocus />
          <textarea className={input} rows={2} placeholder="Short description (optional)" aria-label="Description" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={4000} />
          <select className={input} value={newStatus} onChange={(e) => setNewStatus(e.target.value)} aria-label="Status of the new project">
            {STATUSES.map((s) => <option key={s} value={s}>{STATUS_LABEL[s]}</option>)}
          </select>
          <div className="flex justify-end">
            <button type="submit" disabled={busy || !name.trim()} className={primaryBtn}>Add project</button>
          </div>
        </form>
      )}

      {visible.length === 0 ? (
        <p className="text-sm text-gray-400">No projects yet.</p>
      ) : (
        <div className="space-y-3">
          {visible.map((p) => {
            const pl = linksOf.get(p.id) ?? []
            return (
              <div key={p.id} className={`p-3 rounded-lg ${p.status === 'Archived' ? 'bg-gray-100/60' : 'bg-gray-50'}`}>
                <div className="flex items-start justify-between gap-3">
                  <button
                    className="text-left min-w-0 flex-1"
                    onClick={() => setOpen(open === p.id ? null : p.id)}
                    aria-expanded={open === p.id}
                  >
                    <p className="text-sm font-medium text-gray-900">{open === p.id ? '▾' : '▸'} {p.name}</p>
                    {p.description && <p className="text-xs text-gray-500 mt-0.5 whitespace-pre-wrap break-words line-clamp-3">{p.description}</p>}
                  </button>
                  <div className="flex items-center gap-2 flex-shrink-0">
                    <select
                      value={p.status}
                      onChange={(e) => setStatus(p, e.target.value)}
                      aria-label="Status"
                      disabled={statusBusy}
                      className={`text-xs font-medium border rounded-full px-2 py-0.5 ${STATUS_CLS[p.status] ?? 'bg-gray-50 text-gray-700 border-gray-200'}`}
                    >
                      {STATUSES.map((s) => <option key={s} value={s}>{STATUS_LABEL[s]}</option>)}
                    </select>
                    <button onClick={() => remove(p)} className={ghostBtn} aria-label={`Delete ${p.name}`}>✕</button>
                  </div>
                </div>
                {pl.length > 0 && (
                  <div className="flex gap-2 flex-wrap mt-2">
                    {pl.map((l) => {
                      const href = safeHref(l.url)
                      return href ? (
                        <a key={l.id} href={href} target="_blank" rel="noopener noreferrer"
                           className="text-xs px-2 py-0.5 rounded-full bg-white border border-gray-200 text-indigo-600 hover:underline">
                          {LINK_LABELS[l.kind] ?? l.kind}: {l.label}
                        </a>
                      ) : null
                    })}
                  </div>
                )}
                {open === p.id && (
                  <>
                    <ProjectLinks clientId={clientId} project={p} links={pl} onChanged={loadLinks} />
                    <ProjectHistory clientId={clientId} project={p} onLogged={onHistoryChanged} />
                  </>
                )}
              </div>
            )
          })}
        </div>
      )}

      {archivedCount > 0 && (
        <button onClick={() => setShowArchived((v) => !v)} className={`${ghostBtn} mt-3`} aria-pressed={showArchived}>
          {showArchived ? 'Hide archived projects' : `Show archived projects (${archivedCount})`}
        </button>
      )}
    </div>
  )
}

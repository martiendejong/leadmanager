import { useCallback, useEffect, useState } from 'react'
import { useToast } from '../Toast'
import {
  INTERACTION_LABELS,
  errorMessage,
  getTimeline,
  logInteraction,
  type InteractionKind,
  type TimelineItem,
} from '../../api/crm'
import { createProject, updateProject, deleteProject, type Project } from '../../api/clients'

const STATUSES = ['Active', 'On Hold', 'Completed'] as const
const STATUS_LABEL: Record<string, string> = { Active: 'Actief', 'On Hold': 'On hold', Completed: 'Afgerond' }
const STATUS_CLS: Record<string, string> = {
  Active: 'bg-green-50 text-green-700 border-green-200',
  Completed: 'bg-blue-50 text-blue-700 border-blue-200',
  'On Hold': 'bg-amber-50 text-amber-700 border-amber-200',
}
const KIND_ICON: Record<string, string> = { Call: '📞', Email: '✉️', WhatsApp: '💬', Meeting: '🤝', Note: '📝' }

const input = 'w-full text-sm border border-gray-300 rounded-lg px-3 py-1.5 focus:outline-none focus:ring-2 focus:ring-indigo-300'
const primaryBtn = 'text-sm px-3 py-1.5 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50'
const ghostBtn = 'text-xs px-2 py-1 rounded-md text-gray-500 hover:bg-gray-100'

function fmt(iso: string) {
  return new Date(iso).toLocaleString('nl-NL', { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })
}

function toLocalInput(d: Date) {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
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
      .catch(() => showToast('Geschiedenis laden mislukt', 'error'))
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
      showToast(errorMessage(err, 'Vastleggen mislukt'), 'error')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="mt-3 pl-3 border-l-2 border-gray-200 space-y-3">
      <form onSubmit={submit} className="space-y-2">
        <div className="flex gap-2 flex-wrap">
          <select className={`${input} !w-auto`} value={kind} onChange={(e) => setKind(e.target.value as InteractionKind)} aria-label="Soort">
            {(Object.keys(INTERACTION_LABELS) as InteractionKind[]).map((k) => (
              <option key={k} value={k}>{INTERACTION_LABELS[k]}</option>
            ))}
          </select>
          <input type="datetime-local" className={`${input} !w-auto`} value={when} onChange={(e) => setWhen(e.target.value)} aria-label="Wanneer" />
        </div>
        <textarea
          className={input}
          rows={3}
          placeholder="Plak hier een bericht, mail of notitie..."
          aria-label="Bericht of notitie"
          value={summary}
          onChange={(e) => setSummary(e.target.value)}
          maxLength={8000}
        />
        <div className="flex justify-end">
          <button type="submit" disabled={busy || !summary.trim()} className={primaryBtn}>Toevoegen</button>
        </div>
      </form>

      {items === null ? (
        <p className="text-xs text-gray-400">Laden...</p>
      ) : items.length === 0 ? (
        <p className="text-xs text-gray-400">Nog geen geschiedenis bij dit project.</p>
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
  const [busy, setBusy] = useState(false)
  const [statusBusy, setStatusBusy] = useState(false)

  const add = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!name.trim() || busy) return
    setBusy(true)
    try {
      const p = await createProject(clientId, { name: name.trim(), description: description.trim() || null })
      setName('')
      setDescription('')
      setAdding(false)
      setOpen(p.id)
      onChanged()
    } catch (err) {
      showToast(errorMessage(err, 'Project toevoegen mislukt'), 'error')
    } finally {
      setBusy(false)
    }
  }

  const setStatus = async (p: Project, status: string) => {
    setStatusBusy(true)
    try {
      await updateProject(p.id, { name: p.name, description: p.description ?? null, status })
      onChanged()
    } catch (err) {
      showToast(errorMessage(err, 'Status wijzigen mislukt'), 'error')
    } finally {
      setStatusBusy(false)
    }
  }

  const remove = async (p: Project) => {
    if (!window.confirm(`Project "${p.name}" verwijderen? De geschiedenis blijft bij de klant staan.`)) return
    try {
      await deleteProject(p.id)
      if (open === p.id) setOpen(null)
      onChanged()
    } catch (err) {
      showToast(errorMessage(err, 'Verwijderen mislukt'), 'error')
    }
  }

  return (
    <div className="bg-white rounded-xl border border-gray-200 p-6">
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-base font-semibold text-gray-900">
          Projecten
          <span className="ml-2 text-sm font-normal text-gray-400">({projects.length})</span>
        </h2>
        <button onClick={() => setAdding((v) => !v)} className={ghostBtn} aria-expanded={adding}>{adding ? 'Annuleren' : '+ Project'}</button>
      </div>

      {adding && (
        <form onSubmit={add} className="mb-4 space-y-2 p-3 bg-gray-50 rounded-lg">
          <input className={input} placeholder="Naam van het project" aria-label="Projectnaam" value={name} onChange={(e) => setName(e.target.value)} maxLength={200} autoFocus />
          <textarea className={input} rows={2} placeholder="Korte omschrijving (optioneel)" aria-label="Omschrijving" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={4000} />
          <div className="flex justify-end">
            <button type="submit" disabled={busy || !name.trim()} className={primaryBtn}>Project toevoegen</button>
          </div>
        </form>
      )}

      {projects.length === 0 ? (
        <p className="text-sm text-gray-400">Nog geen projecten.</p>
      ) : (
        <div className="space-y-3">
          {projects.map((p) => (
            <div key={p.id} className="p-3 bg-gray-50 rounded-lg">
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
                  <button onClick={() => remove(p)} className={ghostBtn} aria-label={`Verwijder ${p.name}`}>✕</button>
                </div>
              </div>
              {open === p.id && <ProjectHistory clientId={clientId} project={p} onLogged={onHistoryChanged} />}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

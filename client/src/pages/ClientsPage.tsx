import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { getClients, createClient, type Client, type CreateClientData } from '../api/clients'
import { useToast } from '../components/Toast'
import { EngagementCategoryBadge, ENGAGEMENT_CATEGORIES } from '../components/EngagementCategory'
import { ScoreBadge } from '../components/crm/PriorityCard'

function PlanBadge({ plan }: { plan?: string | null }) {
  if (!plan) return null
  const colorMap: Record<string, string> = {
    Starter: 'bg-blue-50 text-blue-700 border-blue-200',
    Pro: 'bg-purple-50 text-purple-700 border-purple-200',
    Enterprise: 'bg-amber-50 text-amber-700 border-amber-200',
  }
  const cls = colorMap[plan] ?? 'bg-gray-50 text-gray-700 border-gray-200'
  return (
    <span className={`inline-flex items-center text-xs font-medium border rounded-full px-2 py-0.5 ${cls}`}>
      {plan}
    </span>
  )
}

const EMPTY_FORM: CreateClientData = {
  name: '', primaryContactName: '', primaryContactEmail: '', primaryContactPhone: '',
  city: '', sector: '', website: '', notes: '', engagementCategory: '',
}

function NewClientDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (c: Client) => void }) {
  const { showToast } = useToast()
  const [form, setForm] = useState<CreateClientData>(EMPTY_FORM)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const set = (key: keyof CreateClientData) => (e: { target: { value: string } }) =>
    setForm((f) => ({ ...f, [key]: e.target.value }))

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    if (saving) return
    if (!form.name.trim()) { setError('Naam is verplicht.'); return }
    setSaving(true)
    setError(null)
    try {
      const client = await createClient(form)
      showToast('Klant toegevoegd', 'success')
      onCreated(client)
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { error?: string } } })?.response?.data?.error
      setError(msg ?? 'Toevoegen mislukt')
    } finally {
      setSaving(false)
    }
  }

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape' && !saving) onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [saving, onClose])

  const input = 'w-full rounded-lg border border-gray-300 px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500'
  const label = 'block text-xs font-medium text-gray-600 mb-1'

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" role="dialog" aria-modal="true" aria-labelledby="nc-title">
      <form onSubmit={submit} className="bg-white rounded-xl shadow-xl w-full max-w-lg max-h-[90vh] overflow-y-auto p-6 space-y-4">
        <h2 id="nc-title" className="text-lg font-semibold text-gray-900">Nieuwe klant</h2>
        <div>
          <label className={label} htmlFor="nc-name">Naam *</label>
          <input id="nc-name" className={input} value={form.name} onChange={set('name')} autoFocus maxLength={200} required />
        </div>
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className={label} htmlFor="nc-contact">Contactpersoon</label>
            <input id="nc-contact" className={input} value={form.primaryContactName ?? ''} onChange={set('primaryContactName')} maxLength={200} />
          </div>
          <div>
            <label className={label} htmlFor="nc-phone">Telefoon</label>
            <input id="nc-phone" className={input} value={form.primaryContactPhone ?? ''} onChange={set('primaryContactPhone')} maxLength={200} />
          </div>
        </div>
        <div>
          <label className={label} htmlFor="nc-email">E-mail</label>
          <input id="nc-email" type="email" className={input} value={form.primaryContactEmail ?? ''} onChange={set('primaryContactEmail')} />
        </div>
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className={label} htmlFor="nc-city">Plaats</label>
            <input id="nc-city" className={input} value={form.city ?? ''} onChange={set('city')} maxLength={200} />
          </div>
          <div>
            <label className={label} htmlFor="nc-sector">Sector</label>
            <input id="nc-sector" className={input} value={form.sector ?? ''} onChange={set('sector')} maxLength={200} />
          </div>
        </div>
        <div>
          <label className={label} htmlFor="nc-site">Website</label>
          <input id="nc-site" className={input} value={form.website ?? ''} onChange={set('website')} placeholder="bedrijf.nl" />
        </div>
        <div>
          <label className={label} htmlFor="nc-cat">Omgang</label>
          <select id="nc-cat" className={input} value={form.engagementCategory ?? ''} onChange={set('engagementCategory')}>
            <option value="">Nog niet bepaald</option>
            {ENGAGEMENT_CATEGORIES.map((c) => <option key={c} value={c}>{c}</option>)}
          </select>
        </div>
        <div>
          <label className={label} htmlFor="nc-notes">Notities</label>
          <textarea id="nc-notes" rows={3} className={input} value={form.notes ?? ''} onChange={set('notes')} maxLength={4000} />
        </div>
        {error && <p className="text-sm text-red-600" role="alert">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" onClick={onClose} disabled={saving} className="px-4 py-2 text-sm rounded-lg border border-gray-300 text-gray-700 hover:bg-gray-50">Annuleren</button>
          <button type="submit" disabled={saving} className="px-4 py-2 text-sm rounded-lg bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50">
            {saving ? 'Opslaan...' : 'Klant toevoegen'}
          </button>
        </div>
      </form>
    </div>
  )
}

type View = 'cards' | 'list'
type SortKey = 'score' | 'name' | 'next' | 'contact'

const SORTS: { key: SortKey; label: string }[] = [
  { key: 'score', label: 'Prioriteit' },
  { key: 'name', label: 'Naam' },
  { key: 'next', label: 'Volgende actie' },
  { key: 'contact', label: 'Laatste contact' },
]

function sortClients(list: Client[], key: SortKey): Client[] {
  const time = (v?: string | null, empty = Infinity) => (v ? new Date(v).getTime() : empty)
  const out = [...list]
  if (key === 'score') out.sort((a, b) => (b.score ?? 0) - (a.score ?? 0) || a.name.localeCompare(b.name, 'nl'))
  else if (key === 'name') out.sort((a, b) => a.name.localeCompare(b.name, 'nl'))
  else if (key === 'next') out.sort((a, b) => time(a.nextActionAt) - time(b.nextActionAt) || a.name.localeCompare(b.name, 'nl'))
  else out.sort((a, b) => time(b.lastContactAt, -Infinity) - time(a.lastContactAt, -Infinity) || a.name.localeCompare(b.name, 'nl'))
  return out
}

function fmtDate(iso?: string | null) {
  return iso ? new Date(iso).toLocaleDateString('nl-NL', { day: 'numeric', month: 'short', year: 'numeric' }) : '–'
}

function ClientTable({ clients }: { clients: Client[] }) {
  return (
    <div className="bg-white rounded-xl border border-gray-200 overflow-x-auto">
      <table className="min-w-full text-sm">
        <thead className="bg-gray-50 text-xs text-gray-500 uppercase tracking-wider">
          <tr>
            <th className="text-left font-medium px-4 py-2">Klant</th>
            <th className="text-left font-medium px-4 py-2">Prioriteit</th>
            <th className="text-left font-medium px-4 py-2">Omgang</th>
            <th className="text-left font-medium px-4 py-2">Contact</th>
            <th className="text-right font-medium px-4 py-2">Projecten</th>
            <th className="text-left font-medium px-4 py-2">Volgende actie</th>
            <th className="text-left font-medium px-4 py-2">Laatste contact</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-gray-100">
          {clients.map((c) => (
            <tr key={c.id} className="hover:bg-indigo-50/40">
              <td className="px-4 py-2">
                <Link to={`/clients/${c.id}`} className="font-medium text-gray-900 hover:text-indigo-700">{c.name}</Link>
                {c.website && <div className="text-xs text-gray-400 truncate max-w-[220px]">{c.website}</div>}
              </td>
              <td className="px-4 py-2"><ScoreBadge score={c.score} /></td>
              <td className="px-4 py-2"><EngagementCategoryBadge category={c.engagementCategory} /></td>
              <td className="px-4 py-2 text-gray-600">{c.primaryContactName ?? '–'}</td>
              <td className="px-4 py-2 text-right text-gray-600">{c.projects.length}</td>
              <td className={`px-4 py-2 ${c.nextActionAt && new Date(c.nextActionAt) < new Date() ? 'text-red-600 font-medium' : 'text-gray-600'}`}>{fmtDate(c.nextActionAt)}</td>
              <td className="px-4 py-2 text-gray-600">{fmtDate(c.lastContactAt)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

export default function ClientsPage() {
  const { showToast } = useToast()
  const [clients, setClients] = useState<Client[]>([])
  const [loading, setLoading] = useState(true)
  const [showNew, setShowNew] = useState(false)
  const [view, setViewState] = useState<View>(() => {
    try { return localStorage.getItem('clients.view') === 'cards' ? 'cards' : 'list' } catch { return 'list' }
  })
  const [sort, setSortState] = useState<SortKey>(() => {
    try { const v = localStorage.getItem('clients.sort'); return SORTS.some((s) => s.key === v) ? (v as SortKey) : 'score' } catch { return 'score' }
  })
  const setView = (v: View) => { setViewState(v); try { localStorage.setItem('clients.view', v) } catch { /* optional */ } }
  const setSort = (v: SortKey) => { setSortState(v); try { localStorage.setItem('clients.sort', v) } catch { /* optional */ } }
  const navigate = useNavigate()

  useEffect(() => {
    getClients()
      .then(setClients)
      .catch(() => showToast('Klanten laden mislukt', 'error'))
      .finally(() => setLoading(false))
  }, [])

  if (loading) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600" />
      </div>
    )
  }

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Klanten</h1>
          <p className="text-sm text-gray-500 mt-1">{clients.length} klant{clients.length !== 1 ? 'en' : ''}</p>
        </div>
        <div className="flex items-center gap-2 ml-auto mr-3">
          <select
            value={sort}
            onChange={(e) => setSort(e.target.value as SortKey)}
            className="text-sm border border-gray-300 rounded-lg px-2 py-1.5"
            aria-label="Sorteren op"
          >
            {SORTS.map((o) => <option key={o.key} value={o.key}>{o.label}</option>)}
          </select>
          <div className="flex rounded-lg border border-gray-300 overflow-hidden" role="group" aria-label="Weergave">
            <button onClick={() => setView('list')} aria-pressed={view === 'list'} className={`px-3 py-1.5 text-sm ${view === 'list' ? 'bg-indigo-600 text-white' : 'bg-white text-gray-600'}`}>Lijst</button>
            <button onClick={() => setView('cards')} aria-pressed={view === 'cards'} className={`px-3 py-1.5 text-sm ${view === 'cards' ? 'bg-indigo-600 text-white' : 'bg-white text-gray-600'}`}>Kaarten</button>
          </div>
        </div>
        <button
          onClick={() => setShowNew(true)}
          className="px-4 py-2 text-sm font-medium rounded-lg bg-indigo-600 text-white hover:bg-indigo-700"
        >
          + Nieuwe klant
        </button>
      </div>

      {clients.length === 0 ? (
        <div className="text-center py-16 text-gray-500">
          <svg className="w-12 h-12 mx-auto mb-4 text-gray-300" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M17 20h5v-2a3 3 0 00-5.356-1.857M17 20H7m10 0v-2c0-.656-.126-1.283-.356-1.857M7 20H2v-2a3 3 0 015.356-1.857M7 20v-2c0-.656.126-1.283.356-1.857m0 0a5.002 5.002 0 019.288 0M15 7a3 3 0 11-6 0 3 3 0 016 0z" />
          </svg>
          <p className="text-sm font-medium">Nog geen klanten</p>
          <p className="text-xs mt-1 text-gray-400">Voeg een klant toe, of converteer een lead naar klant.</p>
          <Link to="/leads" className="mt-4 inline-block text-sm text-indigo-600 hover:underline">
            Naar leads →
          </Link>
        </div>
      ) : view === 'list' ? (
        <ClientTable clients={sortClients(clients, sort)} />
      ) : (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
          {sortClients(clients, sort).map((client) => (
            <Link
              key={client.id}
              to={`/clients/${client.id}`}
              className="bg-white rounded-xl border border-gray-200 p-5 hover:shadow-md hover:border-indigo-200 transition-all group"
            >
              <div className="flex items-start justify-between mb-3">
                <h3 className="font-semibold text-gray-900 group-hover:text-indigo-700 transition-colors line-clamp-1">
                  {client.name}
                </h3>
                <div className="flex items-center gap-1.5 flex-shrink-0">
                  <ScoreBadge score={client.score} />
                  <EngagementCategoryBadge category={client.engagementCategory} />
                  <PlanBadge plan={client.plan} />
                </div>
              </div>

              <div className="space-y-1.5 text-sm text-gray-600">
                {client.primaryContactName && (
                  <div className="flex items-center gap-1.5">
                    <svg className="w-3.5 h-3.5 text-gray-400 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M16 7a4 4 0 11-8 0 4 4 0 018 0zM12 14a7 7 0 00-7 7h14a7 7 0 00-7-7z" />
                    </svg>
                    <span className="truncate">{client.primaryContactName}</span>
                  </div>
                )}
                {client.primaryContactEmail && (
                  <div className="flex items-center gap-1.5">
                    <svg className="w-3.5 h-3.5 text-gray-400 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 8l7.89 5.26a2 2 0 002.22 0L21 8M5 19h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
                    </svg>
                    <span className="truncate">{client.primaryContactEmail}</span>
                  </div>
                )}
                {(client.city || client.sector) && (
                  <div className="flex items-center gap-1.5">
                    <svg className="w-3.5 h-3.5 text-gray-400 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M17.657 16.657L13.414 20.9a1.998 1.998 0 01-2.827 0l-4.244-4.243a8 8 0 1111.314 0z" />
                    </svg>
                    <span className="truncate">{[client.city, client.sector].filter(Boolean).join(' · ')}</span>
                  </div>
                )}
                {client.website && (
                  <div className="flex items-center gap-1.5">
                    <svg className="w-3.5 h-3.5 text-gray-400 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 12a9 9 0 01-9 9m9-9a9 9 0 00-9-9m9 9H3m9 9a9 9 0 01-9-9m9 9c1.657 0 3-4.03 3-9s-1.343-9-3-9m0 18c-1.657 0-3-4.03-3-9s1.343-9 3-9" />
                    </svg>
                    <span className="truncate text-indigo-600">{client.website}</span>
                  </div>
                )}
              </div>

              <div className="mt-3 pt-3 border-t border-gray-100 flex items-center justify-between text-xs text-gray-400">
                <span>{client.projects.length} project{client.projects.length !== 1 ? 'en' : ''}</span>
                <span>{new Date(client.createdAt).toLocaleDateString('nl-NL')}</span>
              </div>
            </Link>
          ))}
        </div>
      )}
      {showNew && (
        <NewClientDialog
          onClose={() => setShowNew(false)}
          onCreated={(c) => { setShowNew(false); navigate(`/clients/${c.id}`) }}
        />
      )}
    </div>
  )
}

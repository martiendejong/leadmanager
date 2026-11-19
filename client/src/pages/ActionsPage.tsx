import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { useToast } from '../components/Toast'
import {
  addNextAction,
  completeNextAction,
  errorMessage,
  getActions,
  getCalendarFeed,
  rotateCalendarFeed,
  type ActionRow,
  type CalendarFeed,
} from '../api/crm'
import { getClients, type Client } from '../api/clients'

const WEEKDAYS = ['ma', 'di', 'wo', 'do', 'vr', 'za', 'zo']

function dayKey(d: Date) {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

function fmtTime(iso: string) {
  return new Date(iso).toLocaleTimeString('nl-NL', { hour: '2-digit', minute: '2-digit' })
}

function fmtDay(iso: string) {
  return new Date(iso).toLocaleDateString('nl-NL', { weekday: 'short', day: 'numeric', month: 'short' })
}

function parentLink(a: ActionRow) {
  return `/${a.parentType}/${a.parentId}`
}

function toLocalInput(d: Date) {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

function ActionLine({ a, onDone }: { a: ActionRow; onDone: (id: string) => void }) {
  return (
    <li className="flex items-start gap-3 py-2">
      <button
        onClick={() => onDone(a.id)}
        className="mt-0.5 w-5 h-5 rounded-full border-2 border-gray-300 hover:border-green-500 hover:bg-green-50 flex-shrink-0"
        aria-label={`Afronden: ${a.title}`}
      />
      <div className="min-w-0 flex-1">
        <p className="text-sm text-gray-900 break-words">{a.title}</p>
        <p className="text-xs text-gray-500">
          <Link to={parentLink(a)} className="text-indigo-600 hover:underline">{a.parentName}</Link>
          {' · '}
          <span className={a.overdue ? 'text-red-600 font-medium' : ''}>{fmtDay(a.dueAt)} {fmtTime(a.dueAt)}</span>
        </p>
      </div>
    </li>
  )
}

function ListView({ rows, onDone }: { rows: ActionRow[]; onDone: (id: string) => void }) {
  const groups = useMemo(() => {
    const now = new Date()
    const endToday = new Date(now.getFullYear(), now.getMonth(), now.getDate(), 23, 59, 59, 999)
    const endWeek = new Date(endToday)
    endWeek.setDate(endWeek.getDate() + 7)
    const out: { title: string; tone: string; items: ActionRow[] }[] = [
      { title: 'Achterstallig', tone: 'text-red-700', items: [] },
      { title: 'Vandaag', tone: 'text-indigo-700', items: [] },
      { title: 'Komende 7 dagen', tone: 'text-gray-700', items: [] },
      { title: 'Later', tone: 'text-gray-500', items: [] },
    ]
    for (const r of rows) {
      const d = new Date(r.dueAt)
      const g = r.overdue && d < new Date(now.getFullYear(), now.getMonth(), now.getDate()) ? 0
        : d <= endToday ? 1 : d <= endWeek ? 2 : 3
      out[g].items.push(r)
    }
    return out.filter((g) => g.items.length > 0)
  }, [rows])

  if (groups.length === 0) {
    return <div className="bg-white rounded-xl border border-gray-200 p-6 text-sm text-gray-500">Geen open acties. Plan er een met de knop hierboven.</div>
  }
  return (
    <div className="space-y-4">
      {groups.map((g) => (
        <section key={g.title} className="bg-white rounded-xl border border-gray-200 p-4">
          <h3 className={`text-xs font-semibold uppercase tracking-wider mb-1 ${g.tone}`}>
            {g.title} <span className="font-normal text-gray-400">({g.items.length})</span>
          </h3>
          <ul className="divide-y divide-gray-100">
            {g.items.map((a) => <ActionLine key={a.id} a={a} onDone={onDone} />)}
          </ul>
        </section>
      ))}
    </div>
  )
}

function CalendarView({ rows, onDone }: { rows: ActionRow[]; onDone: (id: string) => void }) {
  const [month, setMonth] = useState(() => { const n = new Date(); return new Date(n.getFullYear(), n.getMonth(), 1) })
  const [selected, setSelected] = useState<string>(() => dayKey(new Date()))

  const byDay = useMemo(() => {
    const m = new Map<string, ActionRow[]>()
    for (const r of rows) {
      const k = dayKey(new Date(r.dueAt))
      const list = m.get(k) ?? []
      list.push(r)
      m.set(k, list)
    }
    return m
  }, [rows])

  // Monday-first grid of whole weeks that covers the month.
  const days = useMemo(() => {
    const first = new Date(month)
    const offset = (first.getDay() + 6) % 7
    const start = new Date(first)
    start.setDate(first.getDate() - offset)
    const last = new Date(month.getFullYear(), month.getMonth() + 1, 0)
    const total = Math.ceil((offset + last.getDate()) / 7) * 7
    return Array.from({ length: total }, (_, i) => {
      const d = new Date(start)
      d.setDate(start.getDate() + i)
      return d
    })
  }, [month])

  const todayKey = dayKey(new Date())
  const selectedRows = byDay.get(selected) ?? []
  const title = month.toLocaleDateString('nl-NL', { month: 'long', year: 'numeric' })
  const shift = (n: number) => setMonth(new Date(month.getFullYear(), month.getMonth() + n, 1))

  return (
    <div className="grid grid-cols-1 lg:grid-cols-3 gap-4">
      <div className="lg:col-span-2 bg-white rounded-xl border border-gray-200 p-4">
        <div className="flex items-center justify-between mb-3">
          <h3 className="text-sm font-semibold text-gray-900 capitalize">{title}</h3>
          <div className="flex gap-1">
            <button onClick={() => shift(-1)} className="px-2 py-1 text-sm rounded-md hover:bg-gray-100" aria-label="Vorige maand">‹</button>
            <button
              onClick={() => { const n = new Date(); setMonth(new Date(n.getFullYear(), n.getMonth(), 1)); setSelected(dayKey(n)) }}
              className="px-2 py-1 text-xs rounded-md hover:bg-gray-100"
            >
              Vandaag
            </button>
            <button onClick={() => shift(1)} className="px-2 py-1 text-sm rounded-md hover:bg-gray-100" aria-label="Volgende maand">›</button>
          </div>
        </div>
        <div className="grid grid-cols-7 text-center text-xs text-gray-400 mb-1">
          {WEEKDAYS.map((d) => <div key={d}>{d}</div>)}
        </div>
        <div className="grid grid-cols-7 gap-px bg-gray-100 rounded-lg overflow-hidden">
          {days.map((d) => {
            const k = dayKey(d)
            const items = byDay.get(k) ?? []
            const inMonth = d.getMonth() === month.getMonth()
            return (
              <button
                key={k}
                onClick={() => setSelected(k)}
                className={`min-h-[64px] p-1 text-left bg-white hover:bg-indigo-50 ${inMonth ? '' : 'opacity-40'} ${selected === k ? 'ring-2 ring-inset ring-indigo-400' : ''}`}
                aria-label={`${d.toLocaleDateString('nl-NL', { day: 'numeric', month: 'long' })}, ${items.length} acties`}
              >
                <span className={`text-xs ${k === todayKey ? 'inline-flex w-5 h-5 items-center justify-center rounded-full bg-indigo-600 text-white' : 'text-gray-600'}`}>
                  {d.getDate()}
                </span>
                {items.slice(0, 2).map((i) => (
                  <span key={i.id} className={`block truncate text-[10px] leading-tight mt-0.5 rounded px-1 ${i.overdue ? 'bg-red-50 text-red-700' : 'bg-indigo-50 text-indigo-700'}`}>
                    {fmtTime(i.dueAt)} {i.title}
                  </span>
                ))}
                {items.length > 2 && <span className="block text-[10px] text-gray-400 mt-0.5">+{items.length - 2} meer</span>}
              </button>
            )
          })}
        </div>
      </div>

      <div className="bg-white rounded-xl border border-gray-200 p-4">
        <h3 className="text-xs font-semibold text-gray-500 uppercase tracking-wider mb-2">
          {new Date(selected + 'T12:00:00').toLocaleDateString('nl-NL', { weekday: 'long', day: 'numeric', month: 'long' })}
        </h3>
        {selectedRows.length === 0 ? (
          <p className="text-sm text-gray-400">Niets gepland.</p>
        ) : (
          <ul className="divide-y divide-gray-100">
            {selectedRows.map((a) => <ActionLine key={a.id} a={a} onDone={onDone} />)}
          </ul>
        )}
      </div>
    </div>
  )
}

function NewActionForm({ onAdded, onCancel }: { onAdded: () => void; onCancel: () => void }) {
  const { showToast } = useToast()
  const [clients, setClients] = useState<Client[]>([])
  const [clientId, setClientId] = useState('')
  const [title, setTitle] = useState('')
  const [due, setDue] = useState(() => { const d = new Date(); d.setDate(d.getDate() + 1); d.setHours(9, 0, 0, 0); return toLocalInput(d) })
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    getClients().then((c) => { setClients(c); if (c.length > 0) setClientId(c[0].id) }).catch(() => showToast('Klanten laden mislukt', 'error'))
  }, [showToast])

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!title.trim() || !clientId || busy) return
    if (!due || Number.isNaN(new Date(due).getTime())) { showToast('Kies een datum en tijd', 'error'); return }
    setBusy(true)
    try {
      await addNextAction('clients', clientId, { title: title.trim(), dueAt: new Date(due).toISOString() })
      showToast('Actie gepland', 'success')
      onAdded()
    } catch (err) {
      showToast(errorMessage(err, 'Plannen mislukt'), 'error')
    } finally {
      setBusy(false)
    }
  }

  const input = 'text-sm border border-gray-300 rounded-lg px-3 py-1.5 focus:outline-none focus:ring-2 focus:ring-indigo-300'
  return (
    <form onSubmit={submit} className="bg-white rounded-xl border border-gray-200 p-4 mb-4 flex flex-wrap gap-2 items-end">
      <div className="flex-1 min-w-[180px]">
        <label className="block text-xs text-gray-600 mb-1" htmlFor="na-client">Klant</label>
        <select id="na-client" className={`${input} w-full`} value={clientId} onChange={(e) => setClientId(e.target.value)}>
          {clients.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
      </div>
      <div className="flex-[2] min-w-[220px]">
        <label className="block text-xs text-gray-600 mb-1" htmlFor="na-title">Wat moet er gebeuren?</label>
        <input id="na-title" className={`${input} w-full`} value={title} onChange={(e) => setTitle(e.target.value)} maxLength={300} autoFocus />
      </div>
      <div>
        <label className="block text-xs text-gray-600 mb-1" htmlFor="na-due">Wanneer</label>
        <input id="na-due" type="datetime-local" className={input} value={due} onChange={(e) => setDue(e.target.value)} />
      </div>
      <button type="submit" disabled={busy || !title.trim() || !clientId} className="text-sm px-3 py-1.5 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50">Plannen</button>
      <button type="button" onClick={onCancel} className="text-sm px-3 py-1.5 rounded-lg text-gray-600 hover:bg-gray-100">Annuleren</button>
    </form>
  )
}

function FeedCard() {
  const { showToast } = useToast()
  const [feed, setFeed] = useState<CalendarFeed | null>(null)
  const [open, setOpen] = useState(false)

  useEffect(() => {
    if (!open || feed) return
    getCalendarFeed().then(setFeed).catch((err) => showToast(errorMessage(err, 'Feed laden mislukt'), 'error'))
  }, [open, feed, showToast])

  const copy = async () => {
    if (!feed) return
    try {
      await navigator.clipboard.writeText(feed.url)
      showToast('Link gekopieerd', 'success')
    } catch {
      showToast('Kopiëren mislukt: selecteer de link en kopieer handmatig', 'error')
    }
  }

  const rotate = async () => {
    if (!window.confirm('Een nieuwe link maken? De oude link stopt direct met werken en je moet de agenda opnieuw koppelen.')) return
    try {
      setFeed(await rotateCalendarFeed())
    } catch (err) {
      showToast(errorMessage(err, 'Vernieuwen mislukt'), 'error')
    }
  }

  return (
    <div className="bg-white rounded-xl border border-gray-200 p-4 mb-4">
      <button onClick={() => setOpen((v) => !v)} className="text-sm font-medium text-gray-800 w-full text-left" aria-expanded={open}>
        {open ? '▾' : '▸'} Koppel met Google Agenda (of een andere agenda)
      </button>
      {open && (
        <div className="mt-3 space-y-2 text-sm text-gray-600">
          <p>
            Je acties verschijnen in je agenda via een persoonlijke link. Google Agenda: <em>Andere agenda&apos;s → + → Via URL</em>, plak de link
            en kies Toevoegen. Wijzigingen zijn in Google meestal binnen enkele uren zichtbaar. De koppeling werkt één kant op: acties
            afvinken of verplaatsen doe je hier.
          </p>
          {feed ? (
            <div className="flex gap-2">
              <input readOnly value={feed.url} onFocus={(e) => e.target.select()} className="flex-1 text-xs font-mono border border-gray-300 rounded-lg px-3 py-1.5 bg-gray-50" aria-label="Agenda-link" />
              <button onClick={copy} className="text-sm px-3 py-1.5 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700">Kopieer</button>
              <button onClick={rotate} className="text-sm px-3 py-1.5 rounded-lg text-gray-600 hover:bg-gray-100">Nieuwe link</button>
            </div>
          ) : (
            <p className="text-xs text-gray-400">Laden...</p>
          )}
          <p className="text-xs text-gray-400">Iedereen met deze link kan je geplande acties lezen. Deel hem niet.</p>
        </div>
      )}
    </div>
  )
}

/** All next actions across leads and clients, as a list and as a month calendar. */
export default function ActionsPage() {
  const { showToast } = useToast()
  const [rows, setRows] = useState<ActionRow[] | null>(null)
  const [tab, setTab] = useState<'list' | 'calendar'>('list')
  const [adding, setAdding] = useState(false)
  const [tick, setTick] = useState(0)

  useEffect(() => {
    let cancelled = false
    getActions()
      .then((d) => { if (!cancelled) setRows(d) })
      .catch((err) => {
        if (cancelled) return
        showToast(errorMessage(err, 'Acties laden mislukt'), 'error')
        setRows([])
      })
    return () => { cancelled = true }
  }, [tick, showToast])

  const done = useCallback(async (id: string) => {
    try {
      await completeNextAction(id)
      setTick((n) => n + 1)
    } catch (err) {
      showToast(errorMessage(err, 'Afronden mislukt'), 'error')
    }
  }, [showToast])

  const tabCls = (t: string) => `px-3 py-1.5 text-sm rounded-lg ${tab === t ? 'bg-indigo-600 text-white' : 'text-gray-600 hover:bg-gray-100'}`

  return (
    <div className="max-w-5xl">
      <div className="flex items-center justify-between mb-4 flex-wrap gap-2">
        <div>
          <h1 className="text-xl font-semibold text-gray-900">Acties</h1>
          <p className="text-sm text-gray-500">Alle volgende stappen bij leads en klanten.</p>
        </div>
        <div className="flex items-center gap-2">
          <div className="flex gap-1" role="tablist">
            <button role="tab" aria-selected={tab === 'list'} className={tabCls('list')} onClick={() => setTab('list')}>Lijst</button>
            <button role="tab" aria-selected={tab === 'calendar'} className={tabCls('calendar')} onClick={() => setTab('calendar')}>Kalender</button>
          </div>
          <button onClick={() => setAdding((v) => !v)} className="text-sm px-3 py-1.5 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700">+ Actie</button>
        </div>
      </div>

      {adding && <NewActionForm onAdded={() => { setAdding(false); setTick((n) => n + 1) }} onCancel={() => setAdding(false)} />}
      <FeedCard />

      {rows === null ? (
        <p className="text-sm text-gray-400">Laden...</p>
      ) : tab === 'list' ? (
        <ListView rows={rows} onDone={done} />
      ) : (
        <CalendarView rows={rows} onDone={done} />
      )}
    </div>
  )
}

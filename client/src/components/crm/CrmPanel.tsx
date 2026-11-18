import { useCallback, useEffect, useRef, useState } from 'react'
import { useToast } from '../Toast'
import {
  CALL_OUTCOMES,
  INTERACTION_LABELS,
  LINK_LABELS,
  addContact,
  addLink,
  addNextAction,
  completeNextAction,
  deleteContact,
  deleteLink,
  errorMessage,
  getContacts,
  getLinks,
  getNextActions,
  getTimeline,
  logInteraction,
  safeHref,
  type Contact,
  type EntityLink,
  type EntityLinkKind,
  type InteractionDirection,
  type InteractionKind,
  type NextAction,
  type ParentType,
  type TimelineItem,
} from '../../api/crm'

interface Props {
  parentType: ParentType
  parentId: string
}

const card = 'bg-white rounded-xl border border-gray-200 p-4'
const heading = 'text-xs font-semibold text-gray-500 uppercase tracking-wider mb-3'
const input = 'w-full text-sm border border-gray-300 rounded-lg px-3 py-1.5 focus:outline-none focus:ring-2 focus:ring-indigo-300'
const primaryBtn = 'text-sm px-3 py-1.5 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50'
const ghostBtn = 'text-xs px-2 py-1 rounded-md text-gray-500 hover:bg-gray-100'

function fmtDateTime(iso: string) {
  return new Date(iso).toLocaleString('nl-NL', { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })
}

function toLocalInput(d: Date) {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

function atNine(daysFromNow: number) {
  const d = new Date()
  d.setDate(d.getDate() + daysFromNow)
  d.setHours(9, 0, 0, 0)
  return toLocalInput(d)
}

/**
 * The relationship screen for a lead or a client: next actions, log a conversation,
 * timeline, contacts and links to the other Jengo tools. One component, used on both
 * LeadDetailPage and ClientDetailPage.
 */
export default function CrmPanel({ parentType, parentId }: Props) {
  const { showToast } = useToast()
  const [timeline, setTimeline] = useState<TimelineItem[]>([])
  const [actions, setActions] = useState<NextAction[]>([])
  const [contacts, setContacts] = useState<Contact[]>([])
  const [links, setLinks] = useState<EntityLink[]>([])
  const [loading, setLoading] = useState(true)

  // Only the newest request may write state: switching lead or client quickly (or reloading after a
  // change) must not let a slow, older response overwrite the screen with the previous parent's data.
  const latest = useRef(0)

  const load = useCallback(async () => {
    const request = ++latest.current
    try {
      const [t, a, c, l] = await Promise.all([
        getTimeline(parentType, parentId),
        getNextActions(parentType, parentId),
        getContacts(parentType, parentId),
        getLinks(parentType, parentId),
      ])
      if (request !== latest.current) return
      setTimeline(t)
      setActions(a)
      setContacts(c)
      setLinks(l)
    } catch (err) {
      if (request !== latest.current) return
      showToast(errorMessage(err, 'Gespreksgegevens laden mislukt'), 'error')
    } finally {
      if (request === latest.current) setLoading(false)
    }
  }, [parentType, parentId, showToast])

  useEffect(() => {
    setLoading(true)
    load()
  }, [load])

  if (loading) return <p className="text-sm text-gray-400">Gespreksgegevens laden…</p>

  return (
    <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
      <div className="space-y-4">
        <NextActionsCard parentType={parentType} parentId={parentId} actions={actions} onChanged={load} />
        <LogInteractionCard parentType={parentType} parentId={parentId} contacts={contacts} onChanged={load} />
        <TimelineCard items={timeline} />
      </div>
      <div className="space-y-4">
        <ContactsCard parentType={parentType} parentId={parentId} contacts={contacts} onChanged={load} />
        <LinksCard parentType={parentType} parentId={parentId} links={links} onChanged={load} />
      </div>
    </div>
  )
}

function NextActionsCard({ parentType, parentId, actions, onChanged }: Props & { actions: NextAction[]; onChanged: () => void }) {
  const { showToast } = useToast()
  const [title, setTitle] = useState('')
  const [due, setDue] = useState(atNine(1))
  const [busy, setBusy] = useState(false)

  const add = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!title.trim()) return
    setBusy(true)
    try {
      await addNextAction(parentType, parentId, { title: title.trim(), dueAt: new Date(due).toISOString() })
      setTitle('')
      setDue(atNine(1))
      onChanged()
    } catch (err) {
      showToast(errorMessage(err, 'Actie toevoegen mislukt'), 'error')
    } finally {
      setBusy(false)
    }
  }

  const done = async (id: string) => {
    try {
      await completeNextAction(id)
      onChanged()
    } catch (err) {
      showToast(errorMessage(err, 'Afronden mislukt'), 'error')
    }
  }

  return (
    <div className={card}>
      <h3 className={heading}>Volgende actie</h3>
      {actions.length === 0 ? (
        <p className="text-sm text-gray-400 mb-3">Geen open acties. Plan de volgende stap zodat niets blijft liggen.</p>
      ) : (
        <ul className="space-y-2 mb-3">
          {actions.map((a) => {
            const overdue = new Date(a.dueAt) < new Date()
            return (
              <li key={a.id} className="flex items-center gap-2 text-sm">
                <button onClick={() => done(a.id)} className={ghostBtn} title="Markeer als gedaan">✓ Klaar</button>
                <span className="flex-1 text-gray-800">{a.title}</span>
                <span className={`text-xs px-2 py-0.5 rounded-full ${overdue ? 'bg-red-100 text-red-700' : 'bg-gray-100 text-gray-600'}`}>
                  {fmtDateTime(a.dueAt)}
                </span>
              </li>
            )
          })}
        </ul>
      )}
      <form onSubmit={add} className="space-y-2">
        <input className={input} placeholder="Bijvoorbeeld: terugbellen over de offerte" value={title} onChange={(e) => setTitle(e.target.value)} />
        <div className="flex gap-2 items-center flex-wrap">
          <input type="datetime-local" className={`${input} !w-auto`} value={due} onChange={(e) => setDue(e.target.value)} />
          <button type="button" className={ghostBtn} onClick={() => setDue(atNine(1))}>Morgen</button>
          <button type="button" className={ghostBtn} onClick={() => setDue(atNine(7))}>Over een week</button>
          <button type="submit" disabled={busy || !title.trim()} className={`${primaryBtn} ml-auto`}>Plannen</button>
        </div>
      </form>
    </div>
  )
}

function LogInteractionCard({ parentType, parentId, contacts, onChanged }: Props & { contacts: Contact[]; onChanged: () => void }) {
  const { showToast } = useToast()
  const [kind, setKind] = useState<InteractionKind>('Call')
  const [direction, setDirection] = useState<InteractionDirection>('Outbound')
  const [summary, setSummary] = useState('')
  const [outcome, setOutcome] = useState('')
  const [contactId, setContactId] = useState('')
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!summary.trim()) return
    setBusy(true)
    try {
      await logInteraction(parentType, parentId, {
        kind,
        direction: kind === 'Note' ? 'Internal' : direction,
        summary: summary.trim(),
        outcome: outcome || null,
        contactId: contactId || null,
      })
      setSummary('')
      setOutcome('')
      showToast('Gesprek vastgelegd', 'success')
      onChanged()
    } catch (err) {
      showToast(errorMessage(err, 'Vastleggen mislukt'), 'error')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className={card}>
      <h3 className={heading}>Gesprek vastleggen</h3>
      <form onSubmit={submit} className="space-y-2">
        <div className="flex gap-2 flex-wrap">
          <select className={`${input} !w-auto`} value={kind} onChange={(e) => setKind(e.target.value as InteractionKind)}>
            {(Object.keys(INTERACTION_LABELS) as InteractionKind[]).map((k) => (
              <option key={k} value={k}>{INTERACTION_LABELS[k]}</option>
            ))}
          </select>
          {kind !== 'Note' && (
            <select className={`${input} !w-auto`} value={direction} onChange={(e) => setDirection(e.target.value as InteractionDirection)}>
              <option value="Outbound">Uitgaand</option>
              <option value="Inbound">Inkomend</option>
            </select>
          )}
          {contacts.length > 0 && (
            <select className={`${input} !w-auto`} value={contactId} onChange={(e) => setContactId(e.target.value)}>
              <option value="">Met wie? (optioneel)</option>
              {contacts.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          )}
        </div>
        {kind === 'Call' && (
          <div className="flex gap-1.5 flex-wrap">
            {CALL_OUTCOMES.map((o) => (
              <button
                key={o}
                type="button"
                onClick={() => setOutcome(outcome === o ? '' : o)}
                className={`text-xs px-2.5 py-1 rounded-full border ${outcome === o ? 'bg-indigo-600 text-white border-indigo-600' : 'border-gray-300 text-gray-600 hover:bg-gray-50'}`}
              >
                {o}
              </button>
            ))}
          </div>
        )}
        <textarea className={input} rows={3} placeholder="Wat is er besproken of afgesproken?" value={summary} onChange={(e) => setSummary(e.target.value)} />
        <div className="flex justify-end">
          <button type="submit" disabled={busy || !summary.trim()} className={primaryBtn}>Vastleggen</button>
        </div>
      </form>
    </div>
  )
}

const KIND_ICON: Record<string, string> = {
  Call: '📞', Email: '✉️', WhatsApp: '💬', Meeting: '🤝', Note: '📝',
  Created: '✨', Enriched: '🔎', StatusChanged: '🔀', NoteAdded: '📝', EmailSent: '✉️', Called: '📞', Converted: '🏁',
}

function TimelineCard({ items }: { items: TimelineItem[] }) {
  return (
    <div className={card}>
      <h3 className={heading}>Geschiedenis <span className="font-normal normal-case text-gray-400">({items.length})</span></h3>
      {items.length === 0 ? (
        <p className="text-sm text-gray-400">Nog geen contact vastgelegd.</p>
      ) : (
        <ol className="space-y-3">
          {items.map((i) => (
            <li key={`${i.source}-${i.id}`} className="flex gap-3 text-sm">
              <span className="w-5 text-center flex-shrink-0">{KIND_ICON[i.kind] ?? '•'}</span>
              <div className="min-w-0 flex-1">
                <div className="flex items-baseline gap-2 flex-wrap">
                  <span className="font-medium text-gray-800">
                    {i.source === 'activity' ? i.kind : INTERACTION_LABELS[i.kind as InteractionKind] ?? i.kind}
                  </span>
                  {i.direction && i.direction !== 'Internal' && (
                    <span className="text-xs text-gray-400">{i.direction === 'Inbound' ? 'inkomend' : 'uitgaand'}</span>
                  )}
                  {i.outcome && <span className="text-xs px-2 py-0.5 rounded-full bg-indigo-50 text-indigo-700">{i.outcome}</span>}
                  <span className="text-xs text-gray-400 ml-auto">{fmtDateTime(i.at)}</span>
                </div>
                {i.summary && <p className="text-gray-600 whitespace-pre-wrap break-words">{i.summary}</p>}
              </div>
            </li>
          ))}
        </ol>
      )}
    </div>
  )
}

function ContactsCard({ parentType, parentId, contacts, onChanged }: Props & { contacts: Contact[]; onChanged: () => void }) {
  const { showToast } = useToast()
  const [adding, setAdding] = useState(false)
  const [form, setForm] = useState({ name: '', role: '', email: '', phone: '', mobile: '' })
  const [busy, setBusy] = useState(false)

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!form.name.trim()) return
    setBusy(true)
    try {
      await addContact(parentType, parentId, form)
      setForm({ name: '', role: '', email: '', phone: '', mobile: '' })
      setAdding(false)
      onChanged()
    } catch (err) {
      showToast(errorMessage(err, 'Contact toevoegen mislukt'), 'error')
    } finally {
      setBusy(false)
    }
  }

  const remove = async (c: Contact) => {
    if (!confirm(`${c.name} verwijderen?`)) return
    try {
      await deleteContact(c.id)
      onChanged()
    } catch (err) {
      showToast(errorMessage(err, 'Verwijderen mislukt'), 'error')
    }
  }

  return (
    <div className={card}>
      <div className="flex items-center justify-between">
        <h3 className={heading}>Contacten <span className="font-normal normal-case text-gray-400">({contacts.length})</span></h3>
        <button className={ghostBtn} onClick={() => setAdding(!adding)}>{adding ? 'Annuleren' : '+ Toevoegen'}</button>
      </div>
      {contacts.length === 0 && !adding && <p className="text-sm text-gray-400">Nog geen contactpersonen.</p>}
      <ul className="space-y-3">
        {contacts.map((c) => (
          <li key={c.id} className="text-sm">
            <div className="flex items-center gap-2">
              <span className="font-medium text-gray-800">{c.name}</span>
              {c.role && <span className="text-xs text-gray-400">{c.role}</span>}
              <button className={`${ghostBtn} ml-auto`} onClick={() => remove(c)}>Verwijderen</button>
            </div>
            <div className="flex gap-3 flex-wrap text-xs mt-0.5">
              {c.mobile && <a className="text-indigo-600 hover:underline" href={`tel:${c.mobile.replace(/\s/g, '')}`}>{c.mobile}</a>}
              {c.phone && <a className="text-indigo-600 hover:underline" href={`tel:${c.phone.replace(/\s/g, '')}`}>{c.phone}</a>}
              {c.email && <a className="text-indigo-600 hover:underline" href={`mailto:${c.email}`}>{c.email}</a>}
            </div>
          </li>
        ))}
      </ul>
      {adding && (
        <form onSubmit={save} className="space-y-2 mt-3 border-t border-gray-100 pt-3">
          <input className={input} placeholder="Naam" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
          <div className="grid grid-cols-2 gap-2">
            <input className={input} placeholder="Functie" value={form.role} onChange={(e) => setForm({ ...form, role: e.target.value })} />
            <input className={input} placeholder="E-mail" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} />
            <input className={input} placeholder="Telefoon" value={form.phone} onChange={(e) => setForm({ ...form, phone: e.target.value })} />
            <input className={input} placeholder="Mobiel" value={form.mobile} onChange={(e) => setForm({ ...form, mobile: e.target.value })} />
          </div>
          <div className="flex justify-end">
            <button type="submit" disabled={busy || !form.name.trim()} className={primaryBtn}>Opslaan</button>
          </div>
        </form>
      )}
    </div>
  )
}

function LinksCard({ parentType, parentId, links, onChanged }: Props & { links: EntityLink[]; onChanged: () => void }) {
  const { showToast } = useToast()
  const [adding, setAdding] = useState(false)
  const [kind, setKind] = useState<EntityLinkKind>('JengoWorkBoard')
  const [label, setLabel] = useState('')
  const [url, setUrl] = useState('')
  const [busy, setBusy] = useState(false)

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!label.trim() || !url.trim()) return
    setBusy(true)
    try {
      await addLink(parentType, parentId, { kind, label: label.trim(), url: url.trim() })
      setLabel('')
      setUrl('')
      setAdding(false)
      onChanged()
    } catch (err) {
      showToast(errorMessage(err, 'Koppeling toevoegen mislukt'), 'error')
    } finally {
      setBusy(false)
    }
  }

  const remove = async (l: EntityLink) => {
    try {
      await deleteLink(l.id)
      onChanged()
    } catch (err) {
      showToast(errorMessage(err, 'Verwijderen mislukt'), 'error')
    }
  }

  return (
    <div className={card}>
      <div className="flex items-center justify-between">
        <h3 className={heading}>Koppelingen <span className="font-normal normal-case text-gray-400">({links.length})</span></h3>
        <button className={ghostBtn} onClick={() => setAdding(!adding)}>{adding ? 'Annuleren' : '+ Toevoegen'}</button>
      </div>
      {links.length === 0 && !adding && (
        <p className="text-sm text-gray-400">Koppel het JengoWork-bord, het JengoKnowledge-project of een mailthread.</p>
      )}
      <ul className="space-y-2">
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
              <button className={`${ghostBtn} ml-auto`} onClick={() => remove(l)}>Verwijderen</button>
            </li>
          )
        })}
      </ul>
      {adding && (
        <form onSubmit={save} className="space-y-2 mt-3 border-t border-gray-100 pt-3">
          <select className={input} value={kind} onChange={(e) => setKind(e.target.value as EntityLinkKind)}>
            {(Object.keys(LINK_LABELS) as EntityLinkKind[]).map((k) => <option key={k} value={k}>{LINK_LABELS[k]}</option>)}
          </select>
          <input className={input} placeholder="Naam, bijvoorbeeld Bord Hogenkamp" value={label} onChange={(e) => setLabel(e.target.value)} />
          <input className={input} placeholder="https://…" value={url} onChange={(e) => setUrl(e.target.value)} />
          <div className="flex justify-end">
            <button type="submit" disabled={busy || !label.trim() || !url.trim()} className={primaryBtn}>Opslaan</button>
          </div>
        </form>
      )}
    </div>
  )
}

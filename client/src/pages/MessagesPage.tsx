import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { useToast } from '../components/Toast'
import { errorMessage } from '../api/crm'
import {
  CHANNELS, CHANNEL_LABELS, MAX_BODY, MAX_SUBJECT, deleteDraft, listMessages, markSent, updateDraft,
  type Channel, type LeadMessage,
} from '../api/messages'
import OrganizationFilter from '../components/organizations/OrganizationFilter'
import OrganizationBadge from '../components/organizations/OrganizationBadge'
import { useOrganizations } from '../contexts/OrganizationContext'

function fmt(iso: string | null) {
  return iso ? new Date(iso).toLocaleString('en-GB', { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' }) : ''
}

/** All messages over all leads. The app sends nothing: "Mark as sent" only records that you sent a message yourself. */
export default function MessagesPage() {
  const { showToast } = useToast()
  const { filterParam } = useOrganizations()
  const [status, setStatus] = useState('')
  const [channel, setChannel] = useState('')
  const [q, setQ] = useState('')
  const [page, setPage] = useState(1)
  const [data, setData] = useState<{ items: LeadMessage[]; total: number; pageSize: number } | null>(null)
  const [editing, setEditing] = useState<LeadMessage | null>(null)
  const [confirm, setConfirm] = useState<LeadMessage | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try {
      const d = await listMessages({ status: status || undefined, channel: channel || undefined, q: q || undefined, page, pageSize: 25, organizationId: filterParam })
      setData(d)
    } catch (err) {
      showToast(errorMessage(err, 'Could not load the messages'), 'error')
      setData({ items: [], total: 0, pageSize: 25 })
    }
  }, [status, channel, q, page, showToast, filterParam])
  useEffect(() => { void load() }, [load])

  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1

  const saveEdit = async () => {
    if (!editing) return
    setBusy(true)
    try {
      await updateDraft(editing.id, { channel: editing.channel, subject: editing.channel === 'Email' ? editing.subject : '', body: editing.body })
      setEditing(null)
      showToast('Draft saved. Nothing was sent.', 'success')
      await load()
    } catch (err) { showToast(errorMessage(err, 'Could not save the draft'), 'error') } finally { setBusy(false) }
  }

  const remove = async (m: LeadMessage) => {
    if (!window.confirm(`Delete the draft for ${m.leadName}?`)) return
    try { await deleteDraft(m.id); await load() } catch (err) { showToast(errorMessage(err, 'Could not delete the draft'), 'error') }
  }

  const sent = async (m: LeadMessage) => {
    setBusy(true)
    try {
      const res = await markSent(m.id)
      setConfirm(null)
      showToast(res.statusChanged ? 'Marked as sent. The lead is now Contacted.' : 'Marked as sent.', 'success')
      await load()
    } catch (err) { showToast(errorMessage(err, 'Could not mark the message as sent'), 'error') } finally { setBusy(false) }
  }

  const input = 'border border-gray-300 rounded-lg px-3 py-1.5 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500'

  return (
    <div className="max-w-5xl">
      <h1 className="text-xl font-semibold text-gray-900 mb-1">Messages</h1>
      <p className="text-sm text-gray-500 mb-4">Drafts and sent messages for all your leads. This app sends nothing: you send a message yourself and then mark it as sent.</p>

      <OrganizationFilter className="mb-3" />
      <div className="flex flex-wrap gap-2 mb-4">
        <select aria-label="Filter by status" className={input} value={status} onChange={(e) => { setStatus(e.target.value); setPage(1) }}>
          <option value="">All statuses</option>
          <option value="Draft">Drafts</option>
          <option value="Sent">Sent</option>
        </select>
        <select aria-label="Filter by channel" className={input} value={channel} onChange={(e) => { setChannel(e.target.value); setPage(1) }}>
          <option value="">All channels</option>
          {CHANNELS.map((c) => <option key={c} value={c}>{CHANNEL_LABELS[c]}</option>)}
        </select>
        <input aria-label="Search by lead" className={input} placeholder="Lead name" value={q} onChange={(e) => { setQ(e.target.value); setPage(1) }} />
      </div>

      {data === null && <p className="text-sm text-gray-400">Loading…</p>}
      {data?.items.length === 0 && <div className="bg-white rounded-xl border border-gray-200 p-6 text-sm text-gray-500">No messages yet. Open a lead and write one, or select leads and choose "Draft messages".</div>}

      <ul className="space-y-3">
        {data?.items.map((m) => (
          <li key={m.id} className="bg-white rounded-xl border border-gray-200 p-4">
            <div className="flex flex-wrap items-center gap-2 mb-1">
              <Link to={`/leads/${m.leadId}`} className="text-sm font-semibold text-gray-900 hover:text-indigo-600">{m.leadName}</Link>
              <OrganizationBadge organizationId={m.organizationId} />
              <span className="text-xs px-2 py-0.5 rounded-full bg-gray-100 text-gray-600">{CHANNEL_LABELS[m.channel]}</span>
              <span className={`text-xs px-2 py-0.5 rounded-full ${m.status === 'Sent' ? 'bg-green-100 text-green-800' : 'bg-amber-100 text-amber-800'}`}>{m.status}</span>
              {m.leadPipelineStatus && <span className="text-xs text-gray-400">Lead: {m.leadPipelineStatus}</span>}
              <span className="text-xs text-gray-400 ml-auto">{m.status === 'Sent' ? `Sent ${fmt(m.sentAt)}` : `Updated ${fmt(m.updatedAt)}`}</span>
            </div>
            {m.subject && <p className="text-sm text-gray-700 font-medium">{m.subject}</p>}
            <p className="text-sm text-gray-600 whitespace-pre-line line-clamp-3">{m.body}</p>
            <div className="flex flex-wrap gap-2 mt-3">
              <Link to={`/leads/${m.leadId}#message`} className="text-xs px-2.5 py-1 rounded-lg border border-gray-300 text-gray-700 hover:bg-gray-50">Open lead</Link>
              {m.status === 'Draft' && (
                <>
                  <button onClick={() => setEditing({ ...m })} className="text-xs px-2.5 py-1 rounded-lg border border-gray-300 text-gray-700 hover:bg-gray-50">Edit</button>
                  <button onClick={() => remove(m)} className="text-xs px-2.5 py-1 rounded-lg border border-red-200 text-red-700 hover:bg-red-50">Delete</button>
                  <button onClick={() => setConfirm(m)} className="text-xs px-2.5 py-1 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700">Mark as sent</button>
                </>
              )}
            </div>
            {confirm?.id === m.id && (
              <div role="dialog" aria-label="Confirm sent" className="mt-3 rounded-lg border border-indigo-200 bg-indigo-50 p-3 text-sm">
                <p className="text-gray-800 mb-2">Only confirm if you sent this message yourself. A lead that is New becomes Contacted.</p>
                <div className="flex gap-2">
                  <button disabled={busy} onClick={() => sent(m)} className="px-3 py-1.5 rounded-lg bg-indigo-600 text-white text-sm hover:bg-indigo-700 disabled:opacity-50">I sent this message myself</button>
                  <button onClick={() => setConfirm(null)} className="px-3 py-1.5 rounded-lg border border-gray-300 text-sm text-gray-700 hover:bg-white">Cancel</button>
                </div>
              </div>
            )}
          </li>
        ))}
      </ul>

      {data && data.total > data.pageSize && (
        <div className="flex items-center gap-3 mt-4 text-sm text-gray-600">
          <button disabled={page <= 1} onClick={() => setPage(page - 1)} className="px-3 py-1 border border-gray-300 rounded disabled:opacity-40">Previous</button>
          <span>Page {page} of {pages}</span>
          <button disabled={page >= pages} onClick={() => setPage(page + 1)} className="px-3 py-1 border border-gray-300 rounded disabled:opacity-40">Next</button>
        </div>
      )}

      {editing && (
        <div className="fixed inset-0 bg-black/40 z-50 flex items-center justify-center p-4" role="dialog" aria-label="Edit draft">
          <div className="bg-white rounded-xl p-5 w-full max-w-xl space-y-3">
            <h2 className="text-base font-semibold text-gray-900">Edit draft for {editing.leadName}</h2>
            <select aria-label="Channel" className={`${input} w-full`} value={editing.channel} onChange={(e) => setEditing({ ...editing, channel: e.target.value as Channel })}>
              {CHANNELS.map((c) => <option key={c} value={c}>{CHANNEL_LABELS[c]}</option>)}
            </select>
            {editing.channel === 'Email' && <input aria-label="Subject" className={`${input} w-full`} value={editing.subject} maxLength={MAX_SUBJECT} onChange={(e) => setEditing({ ...editing, subject: e.target.value })} />}
            <textarea aria-label="Message text" className={`${input} w-full`} rows={10} value={editing.body} maxLength={MAX_BODY} onChange={(e) => setEditing({ ...editing, body: e.target.value })} />
            <div className="flex gap-2 justify-end">
              <button onClick={() => setEditing(null)} className="px-3 py-1.5 rounded-lg border border-gray-300 text-sm text-gray-700">Cancel</button>
              <button disabled={busy || !editing.body.trim()} onClick={saveEdit} className="px-3 py-1.5 rounded-lg bg-indigo-600 text-white text-sm disabled:opacity-50">Save draft</button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

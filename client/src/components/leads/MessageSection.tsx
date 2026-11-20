import { useCallback, useEffect, useRef, useState } from 'react'
import { useToast } from '../Toast'
import { errorMessage } from '../../api/crm'
import {
  CHANNELS, CHANNEL_LABELS, MAX_BODY, MAX_SUBJECT,
  createDraft, deleteDraft, getLeadMessages, getMailto, getPrefill, markSent, updateDraft,
  type Channel, type LeadMessage, type Prefill,
} from '../../api/messages'
import { useProducts } from './ProductChips'
import type { Lead } from '../../api/leads'

interface Props {
  lead: Lead
  /** Called after "Mark as sent" so the screen can show the new pipeline status. */
  onLeadChanged?: (patch: Partial<Lead>) => void
  /** Open the section with this channel preselected (from the work queue). */
  initialChannel?: Channel
}

function fmt(iso: string) {
  return new Date(iso).toLocaleString('en-GB', { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })
}

/**
 * Compose a message for a lead. The app NEVER sends anything: you copy the text or open your mail app, send it yourself and then
 * choose "Mark as sent". Only that, after a confirmation, moves the lead from New to Contacted. Saving a draft changes nothing.
 */
export default function MessageSection({ lead, onLeadChanged, initialChannel }: Props) {
  const { showToast } = useToast()
  const products = useProducts()
  const [channel, setChannel] = useState<Channel>(initialChannel ?? 'Email')
  const [product, setProduct] = useState('')
  const [subject, setSubject] = useState('')
  const [body, setBody] = useState('')
  const [editing, setEditing] = useState<LeadMessage | null>(null)
  const [warnings, setWarnings] = useState<string[]>([])
  const [info, setInfo] = useState<Prefill | null>(null)
  const [history, setHistory] = useState<LeadMessage[]>([])
  const [busy, setBusy] = useState(false)
  const [confirming, setConfirming] = useState(false)
  const dirty = useRef(false)

  const fits = (lead.productFit ?? []).filter((f) => products[f.product])

  const loadHistory = useCallback(async () => {
    try { setHistory(await getLeadMessages(lead.id)) } catch { /* the history is optional */ }
  }, [lead.id])

  const applyPrefill = useCallback(async (ch: Channel, prod: string) => {
    try {
      const p = await getPrefill(lead.id, ch, prod)
      setInfo(p)
      setSubject(p.subject)
      setBody(p.body)
      setWarnings(p.warnings)
      dirty.current = false
    } catch (err) {
      showToast(errorMessage(err, 'Could not prepare a message'), 'error')
    }
  }, [lead.id, showToast])

  // A new lead (or a new start channel): start from the template and load the history.
  useEffect(() => {
    setEditing(null)
    setConfirming(false)
    setProduct('')
    setChannel(initialChannel ?? 'Email')
    void applyPrefill(initialChannel ?? 'Email', '')
    void loadHistory()
  }, [lead.id, initialChannel, applyPrefill, loadHistory])

  const changeChannel = (ch: Channel) => {
    if (dirty.current && !window.confirm('Replace your text with the template for this channel?')) return
    setChannel(ch)
    setEditing(null)
    void applyPrefill(ch, product)
  }

  const changeProduct = (p: string) => {
    if (dirty.current && !window.confirm('Replace your text with the template for this product?')) return
    setProduct(p)
    setEditing(null)
    void applyPrefill(channel, p)
  }

  const save = async (): Promise<LeadMessage | null> => {
    if (!body.trim()) { showToast('Write a message first', 'error'); return null }
    setBusy(true)
    try {
      const dto = { channel, subject: channel === 'Email' ? subject : '', body }
      const saved = editing ? await updateDraft(editing.id, dto) : await createDraft(lead.id, dto)
      setEditing(saved)
      dirty.current = false
      await loadHistory()
      return saved
    } catch (err) {
      showToast(errorMessage(err, 'Could not save the draft'), 'error')
      return null
    } finally {
      setBusy(false)
    }
  }

  const saveDraft = async () => { if (await save()) showToast('Draft saved. Nothing was sent.', 'success') }

  const copy = async () => {
    const text = channel === 'Email' && subject ? `${subject}\n\n${body}` : body
    try { await navigator.clipboard.writeText(text); showToast('Copied to the clipboard', 'success') }
    catch { showToast('Could not copy. Select the text and copy it yourself.', 'error') }
  }

  const openMailApp = async () => {
    const saved = await save()
    if (!saved) return
    try {
      const { url } = await getMailto(saved.id)
      window.location.href = url
    } catch (err) {
      showToast(errorMessage(err, 'Could not open the mail app'), 'error')
    }
  }

  const sent = async () => {
    const saved = await save()
    if (!saved) return
    setBusy(true)
    try {
      const res = await markSent(saved.id)
      setEditing(null)
      setConfirming(false)
      onLeadChanged?.({ pipelineStatus: res.pipelineStatus, ...({ lastContactedAt: res.lastContactedAt } as Partial<Lead>) })
      showToast(res.statusChanged ? 'Marked as sent. The lead is now Contacted.' : 'Marked as sent.', 'success')
      await loadHistory()
      await applyPrefill(channel, product)
    } catch (err) {
      showToast(errorMessage(err, 'Could not mark the message as sent'), 'error')
    } finally {
      setBusy(false)
    }
  }

  const remove = async (m: LeadMessage) => {
    if (!window.confirm('Delete this draft?')) return
    try {
      await deleteDraft(m.id)
      if (editing?.id === m.id) setEditing(null)
      await loadHistory()
    } catch (err) {
      showToast(errorMessage(err, 'Could not delete the draft'), 'error')
    }
  }

  const openDraft = (m: LeadMessage) => {
    setEditing(m); setChannel(m.channel); setSubject(m.subject); setBody(m.body); dirty.current = false; setConfirming(false)
  }

  const canMail = channel === 'Email' && !!info?.toAddress
  const input = 'w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500'

  return (
    <section aria-label="Message" className="rounded-lg border border-gray-200 p-4 bg-white">
      <h3 className="text-sm font-semibold text-gray-700 mb-1">Message</h3>
      <p className="text-xs text-gray-500 mb-3">
        This app sends nothing. Copy the text or open your mail app, send it yourself, then choose "Mark as sent".
      </p>

      {warnings.map((w) => (
        <p key={w} role="alert" className="text-xs bg-amber-50 border border-amber-200 text-amber-800 rounded px-2 py-1 mb-2">{w}</p>
      ))}

      <div className="grid grid-cols-2 gap-2 mb-2">
        <label className="text-xs text-gray-600">Channel
          <select aria-label="Channel" className={input} value={channel} onChange={(e) => changeChannel(e.target.value as Channel)}>
            {CHANNELS.map((c) => <option key={c} value={c}>{CHANNEL_LABELS[c]}</option>)}
          </select>
        </label>
        <label className="text-xs text-gray-600">Product template
          <select aria-label="Product template" className={input} value={product} onChange={(e) => changeProduct(e.target.value)}>
            <option value="">Best fit / default</option>
            {fits.map((f) => <option key={f.product} value={f.product}>{products[f.product].name}</option>)}
          </select>
        </label>
      </div>

      {info && <p className="text-xs text-gray-400 mb-2">Prefilled from: {info.templateSource}{info.language === 'nl' ? ' (Dutch)' : ' (English)'}</p>}

      {channel === 'Email' && (
        <label className="block text-xs text-gray-600 mb-2">Subject
          <input aria-label="Subject" className={input} value={subject} maxLength={MAX_SUBJECT}
            onChange={(e) => { setSubject(e.target.value); dirty.current = true }} />
        </label>
      )}
      <label className="block text-xs text-gray-600 mb-1">{channel === 'Phone' ? 'Call note' : 'Message'}
        <textarea aria-label="Message text" className={input} rows={9} value={body} maxLength={MAX_BODY}
          onChange={(e) => { setBody(e.target.value); dirty.current = true }} />
      </label>
      <p className="text-xs text-gray-400 mb-3">{body.length} / {MAX_BODY}</p>

      <div className="flex flex-wrap gap-2">
        <button disabled={busy} onClick={saveDraft} className="text-sm px-3 py-1.5 rounded-lg border border-gray-300 text-gray-700 hover:bg-gray-50 disabled:opacity-50">Save draft</button>
        <button disabled={busy} onClick={copy} className="text-sm px-3 py-1.5 rounded-lg border border-gray-300 text-gray-700 hover:bg-gray-50 disabled:opacity-50">Copy</button>
        {channel === 'Email' && (
          <button disabled={busy || !canMail} onClick={openMailApp}
            title={canMail ? 'Opens your mail app with this text. Nothing is sent until you send it there.' : 'No valid published e-mail address for this lead'}
            className="text-sm px-3 py-1.5 rounded-lg border border-indigo-200 text-indigo-700 hover:bg-indigo-50 disabled:opacity-40">Open in mail app</button>
        )}
        <button disabled={busy} onClick={() => setConfirming(true)} className="text-sm px-3 py-1.5 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50">Mark as sent</button>
      </div>

      {confirming && (
        <div role="dialog" aria-label="Confirm sent" className="mt-3 rounded-lg border border-indigo-200 bg-indigo-50 p-3 text-sm">
          <p className="text-gray-800 mb-2">
            Only confirm if you sent this message yourself{channel === 'Phone' ? ' (or made the call)' : ''}.
            {lead.pipelineStatus === 'New' || !lead.pipelineStatus ? ' The lead will move from New to Contacted.' : ' The status of the lead stays as it is.'}
          </p>
          <div className="flex gap-2">
            <button disabled={busy} onClick={sent} className="px-3 py-1.5 rounded-lg bg-indigo-600 text-white text-sm hover:bg-indigo-700 disabled:opacity-50">I sent this message myself</button>
            <button onClick={() => setConfirming(false)} className="px-3 py-1.5 rounded-lg border border-gray-300 text-sm text-gray-700 hover:bg-white">Cancel</button>
          </div>
        </div>
      )}

      {history.length > 0 && (
        <div className="mt-4">
          <h4 className="text-xs font-semibold text-gray-500 uppercase tracking-wider mb-2">History</h4>
          <ul className="space-y-2">
            {history.map((m) => (
              <li key={m.id} className="text-xs border border-gray-100 rounded-lg p-2 bg-gray-50">
                <div className="flex items-center justify-between gap-2">
                  <span className="font-medium text-gray-700">{CHANNEL_LABELS[m.channel]} · {m.status === 'Sent' ? `Sent ${m.sentAt ? fmt(m.sentAt) : ''}` : `Draft ${fmt(m.updatedAt)}`}</span>
                  {m.status === 'Draft' && (
                    <span className="flex gap-2">
                      <button onClick={() => openDraft(m)} className="text-indigo-600 hover:underline">Open</button>
                      <button onClick={() => remove(m)} className="text-red-600 hover:underline">Delete</button>
                    </span>
                  )}
                </div>
                {m.subject && <p className="text-gray-600 truncate">{m.subject}</p>}
                <p className="text-gray-500 line-clamp-2 whitespace-pre-line">{m.body}</p>
              </li>
            ))}
          </ul>
        </div>
      )}
    </section>
  )
}

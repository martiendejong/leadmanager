import { useCallback, useEffect, useRef, useState } from 'react'
import { errorMessage } from '../../api/crm'
import {
  CHANNELS, CHANNEL_LABELS, MAX_AI_INSTRUCTION, MAX_BODY, MAX_SUBJECT,
  aiMessage, createDraft, deleteDraft, getAiStatus, getLeadMessages, getMailto, getPrefill, markSent, updateDraft,
  type Channel, type LeadMessage, type Prefill,
} from '../../api/messages'
import { useProducts } from './ProductChips'
import DemoIdeas from './DemoIdeas'
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
  // AI help (regenerate with an instruction). null = not known yet; false = no key on the server: the controls explain it and templates keep working.
  const [aiConfigured, setAiConfigured] = useState<boolean | null>(null)
  const [instruction, setInstruction] = useState('')
  const [aiBusy, setAiBusy] = useState(false)
  const [aiNotes, setAiNotes] = useState<string[]>([])
  // Inline feedback next to the buttons: a toast is easy to miss and gone after a moment
  const [notice, setNotice] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null)
  const noticeTimer = useRef<number | undefined>(undefined)
  const say = useCallback((kind: 'ok' | 'error', text: string) => {
    setNotice({ kind, text })
    window.clearTimeout(noticeTimer.current)
    if (kind === 'ok') noticeTimer.current = window.setTimeout(() => setNotice(null), 6000)
  }, [])
  useEffect(() => () => window.clearTimeout(noticeTimer.current), [])

  useEffect(() => {
    let alive = true
    getAiStatus().then((s) => { if (alive) setAiConfigured(s.configured) }).catch(() => { if (alive) setAiConfigured(null) })
    return () => { alive = false }
  }, [])

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
      say('error', errorMessage(err, 'Could not prepare a message'))
    }
  }, [lead.id, say])

  // A new lead (or a new start channel): start from the template and load the history.
  useEffect(() => {
    setEditing(null)
    setConfirming(false)
    setNotice(null)
    setInstruction('')
    setAiNotes([])
    setProduct('')
    setChannel(initialChannel ?? 'Email')
    void applyPrefill(initialChannel ?? 'Email', '')
    void loadHistory()
  }, [lead.id, initialChannel, applyPrefill, loadHistory])

  // Regenerate the text with the instruction. The editor text is the starting point. It replaces the editor text only: still a draft, saved by "Save draft".
  const regenerate = async () => {
    setAiBusy(true)
    setNotice(null)
    try {
      const r = await aiMessage(lead.id, { channel, product: product || undefined, instruction: instruction.trim() || undefined, subject, body })
      setSubject(r.subject)
      setBody(r.body)
      setAiNotes(r.notes)
      dirty.current = true
      say('ok', 'New draft text. Check it, then save it or send it yourself. Nothing was sent.')
    } catch (err) {
      const code = (err as { response?: { data?: { code?: string } } })?.response?.data?.code
      if (code === 'ai_not_configured') setAiConfigured(false)
      say('error', errorMessage(err, 'Could not regenerate the message.'))
    } finally {
      setAiBusy(false)
    }
  }

  const backToTemplate = () => {
    if (dirty.current && !window.confirm('Replace your text with the template?')) return
    setAiNotes([])
    void applyPrefill(channel, product)
  }

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
    if (!body.trim()) { say('error', 'Write a message first.'); return null }
    setBusy(true)
    try {
      const dto = { channel, subject: channel === 'Email' ? subject : '', body }
      const saved = editing ? await updateDraft(editing.id, dto) : await createDraft(lead.id, dto)
      setEditing(saved)
      dirty.current = false
      await loadHistory()
      return saved
    } catch (err) {
      say('error', errorMessage(err, 'Could not save the draft.'))
      return null
    } finally {
      setBusy(false)
    }
  }

  const saveDraft = async () => { if (await save()) say('ok', 'Draft saved. Nothing was sent.') }

  const copy = async () => {
    const text = channel === 'Email' && subject ? `${subject}\n\n${body}` : body
    try { await navigator.clipboard.writeText(text); say('ok', 'Copied to the clipboard.') }
    catch { say('error', 'Could not copy. Select the text and copy it yourself.') }
  }

  const openMailApp = async () => {
    const saved = await save()
    if (!saved) return
    try {
      const { url } = await getMailto(saved.id)
      window.location.href = url
    } catch (err) {
      say('error', errorMessage(err, 'Could not open the mail app.'))
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
      await loadHistory()
      await applyPrefill(channel, product)
      say('ok', res.statusChanged ? 'Marked as sent. The lead is now Contacted.' : 'Marked as sent.')
    } catch (err) {
      say('error', errorMessage(err, 'Could not mark the message as sent.'))
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
      say('error', errorMessage(err, 'Could not delete the draft.'))
    }
  }

  const openDraft = (m: LeadMessage) => {
    setEditing(m); setChannel(m.channel); setSubject(m.subject); setBody(m.body); dirty.current = false; setConfirming(false)
  }

  const canMail = channel === 'Email' && !!info?.toAddress
  const input = 'w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500'

  return (
    <>
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

      <div className="rounded-lg border border-indigo-100 bg-indigo-50/40 p-3 mb-3 min-w-0">
        <label className="block text-xs text-gray-600 mb-1">Regenerate with a prompt
          <input aria-label="AI instruction" className={input} value={instruction} maxLength={MAX_AI_INSTRUCTION} disabled={aiConfigured === false || aiBusy}
            placeholder="for example: shorter, more informal, mention their opening hours"
            onChange={(e) => setInstruction(e.target.value)} />
        </label>
        <div className="flex flex-wrap items-center gap-2">
          <button disabled={busy || aiBusy || aiConfigured === false} onClick={regenerate}
            title="Writes a new draft from the stored facts of this lead. It replaces the text above; nothing is saved or sent."
            className="whitespace-nowrap text-sm px-3 py-1.5 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50">
            {aiBusy ? 'Writing...' : 'Regenerate'}
          </button>
          <button disabled={busy || aiBusy} onClick={backToTemplate}
            className="whitespace-nowrap text-sm px-3 py-1.5 rounded-lg border border-gray-300 text-gray-700 hover:bg-white disabled:opacity-50">
            Back to template
          </button>
          <span className="text-xs text-gray-400">{instruction.length} / {MAX_AI_INSTRUCTION}</span>
        </div>
        {aiConfigured === false && (
          <p role="status" className="mt-2 text-xs bg-amber-50 border border-amber-200 text-amber-800 rounded px-2 py-1">
            AI is not configured on this server. The templates still work.
          </p>
        )}
        {aiNotes.length > 0 && (
          <ul aria-label="Notes from the AI" className="mt-2 text-xs text-gray-600 list-disc pl-4 break-words">
            {aiNotes.map((n) => <li key={n}>{n}</li>)}
          </ul>
        )}
      </div>

      <div className="flex flex-wrap gap-2">
        <button disabled={busy} onClick={saveDraft} className="whitespace-nowrap text-sm px-3 py-1.5 rounded-lg border border-gray-300 text-gray-700 hover:bg-gray-50 disabled:opacity-50">Save draft</button>
        <button disabled={busy} onClick={copy} className="whitespace-nowrap text-sm px-3 py-1.5 rounded-lg border border-gray-300 text-gray-700 hover:bg-gray-50 disabled:opacity-50">Copy</button>
        {channel === 'Email' && (
          <button disabled={busy || !canMail} onClick={openMailApp}
            title={canMail ? 'Opens your mail app with this text. Nothing is sent until you send it there.' : 'No valid published e-mail address for this lead'}
            className="whitespace-nowrap text-sm px-3 py-1.5 rounded-lg border border-indigo-200 text-indigo-700 hover:bg-indigo-50 disabled:opacity-40">Open in mail app</button>
        )}
        <button disabled={busy} onClick={() => setConfirming(true)} className="whitespace-nowrap text-sm px-3 py-1.5 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50">Mark as sent</button>
      </div>

      {notice && (
        <p role={notice.kind === 'error' ? 'alert' : 'status'}
          className={`mt-2 text-sm rounded px-2 py-1 border ${notice.kind === 'ok' ? 'bg-green-50 border-green-200 text-green-800' : 'bg-red-50 border-red-200 text-red-800'}`}>
          {notice.text}
        </p>
      )}

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
    <DemoIdeas lead={lead} aiConfigured={aiConfigured} />
    </>
  )
}

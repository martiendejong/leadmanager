import { useCallback, useEffect, useState } from 'react'
import { useToast } from '../Toast'
import { errorMessage } from '../../api/crm'
import {
  CHANNELS, CHANNEL_LABELS, MAX_BODY, MAX_SUBJECT, deleteTemplate, getTemplates, saveTemplate,
  type Channel, type TemplateInfo,
} from '../../api/messages'

/** Templates for the message prefill: per channel, and per product of the catalog. Client-facing text is Dutch. */
export default function MessageTemplatesPanel() {
  const { showToast } = useToast()
  const [info, setInfo] = useState<TemplateInfo | null>(null)
  const [channel, setChannel] = useState<Channel>('Email')
  const [productKey, setProductKey] = useState('')
  const [subject, setSubject] = useState('')
  const [body, setBody] = useState('')
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try { setInfo(await getTemplates()) } catch (err) { showToast(errorMessage(err, 'Could not load the templates'), 'error') }
  }, [showToast])
  useEffect(() => { void load() }, [load])

  const mine = info?.templates.find((t) => t.channel === channel && t.productKey === productKey)
  // The text shown: the saved template, else the built-in one (Dutch, or English for the Kenya products; the starting point to edit)
  useEffect(() => {
    if (!info) return
    const english = info.builtInEnglishFirst
    const builtIn = english?.products.includes(productKey)
      ? english.templates.find((b) => b.channel === channel)
      : info.builtIn.find((b) => b.channel === channel && b.language === 'nl')
    setSubject(mine?.subject ?? builtIn?.subject ?? '')
    setBody(mine?.body ?? builtIn?.body ?? '')
  }, [info, channel, productKey, mine])

  const save = async () => {
    setBusy(true)
    try {
      await saveTemplate({ channel, productKey, subject: channel === 'Email' ? subject : '', body })
      showToast('Template saved', 'success')
      await load()
    } catch (err) {
      showToast(errorMessage(err, 'Could not save the template'), 'error')
    } finally { setBusy(false) }
  }

  const reset = async () => {
    if (!mine || !window.confirm('Go back to the built-in template?')) return
    try { await deleteTemplate(mine.id); showToast('Back to the built-in template', 'success'); await load() }
    catch (err) { showToast(errorMessage(err, 'Could not reset the template'), 'error') }
  }

  const input = 'w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500'
  if (!info) return <p className="text-sm text-gray-400">Loading…</p>

  return (
    <div className="space-y-4">
      <p className="text-sm text-gray-500">
        These texts prefill a message. Write them in the language of the people who read them (Dutch). The app never sends a message.
      </p>
      <div className="text-xs text-gray-600 bg-gray-50 rounded-lg p-3">
        <p className="font-medium mb-1">Placeholders</p>
        <ul className="space-y-0.5">
          {info.placeholders.map((p) => <li key={p.name}><code className="text-indigo-700">{'{' + p.name + '}'}</code> {p.meaning}</li>)}
        </ul>
        <p className="mt-2">{info.rule}</p>
      </div>

      <div className="grid grid-cols-2 gap-3">
        <label className="text-xs text-gray-600">Channel
          <select aria-label="Template channel" className={input} value={channel} onChange={(e) => setChannel(e.target.value as Channel)}>
            {CHANNELS.map((c) => <option key={c} value={c}>{CHANNEL_LABELS[c]}</option>)}
          </select>
        </label>
        <label className="text-xs text-gray-600">Product
          <select aria-label="Template product" className={input} value={productKey} onChange={(e) => setProductKey(e.target.value)}>
            <option value="">Default for this channel</option>
            {info.products.map((p) => <option key={p.key} value={p.key}>{p.name}</option>)}
          </select>
        </label>
      </div>

      <p className="text-xs text-gray-400">
        {mine ? 'Your template.' : productKey ? 'No template for this product yet: the default of the channel is used until you save one.' : 'Built-in template shown. Save to make it yours.'}
      </p>

      {channel === 'Email' && (
        <label className="block text-xs text-gray-600">Subject
          <input aria-label="Template subject" className={input} value={subject} maxLength={MAX_SUBJECT} onChange={(e) => setSubject(e.target.value)} />
        </label>
      )}
      <label className="block text-xs text-gray-600">Text
        <textarea aria-label="Template text" className={input} rows={12} value={body} maxLength={MAX_BODY} onChange={(e) => setBody(e.target.value)} />
      </label>

      <div className="flex gap-2">
        <button disabled={busy || !body.trim()} onClick={save} className="px-4 py-2 bg-indigo-600 text-white rounded-lg text-sm font-medium hover:bg-indigo-700 disabled:opacity-50">Save template</button>
        {mine && <button onClick={reset} className="px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-700 hover:bg-gray-50">Use built-in</button>}
      </div>
    </div>
  )
}

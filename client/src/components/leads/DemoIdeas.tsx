import { useCallback, useEffect, useRef, useState } from 'react'
import { errorMessage } from '../../api/crm'
import { MAX_AI_INSTRUCTION, generateDemoIdeas, getDemoIdeas, type DemoIdea } from '../../api/messages'
import { useProducts } from './ProductChips'
import type { Lead } from '../../api/leads'

interface Props {
  lead: Lead
  /** False when the server has no AI key: the section explains it and nothing is called. */
  aiConfigured: boolean | null
}

function fmt(iso: string) {
  return new Date(iso).toLocaleString('en-GB', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
}

/**
 * Demos we could build for this lead, written by the AI from the stored facts only (why, needs, product fit). Each has an instruction
 * prompt to copy into a coding or AI agent. The latest set is saved on the server, so it is still here after a reload. Nothing is sent.
 */
export default function DemoIdeas({ lead, aiConfigured }: Props) {
  const products = useProducts()
  const [ideas, setIdeas] = useState<DemoIdea[]>([])
  const [generatedAt, setGeneratedAt] = useState<string | null>(null)
  const [noFit, setNoFit] = useState<string | null>(null)
  const [instruction, setInstruction] = useState('')
  const [busy, setBusy] = useState(false)
  const [loaded, setLoaded] = useState(false)
  const [notice, setNotice] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null)
  const [copied, setCopied] = useState<string | null>(null)
  const timer = useRef<number | undefined>(undefined)
  useEffect(() => () => window.clearTimeout(timer.current), [])

  const load = useCallback(async () => {
    try {
      const set = await getDemoIdeas(lead.id)
      setIdeas(set.items)
      setGeneratedAt(set.generatedAt)
    } catch { /* the saved ideas are optional */ }
    setLoaded(true)
  }, [lead.id])

  useEffect(() => {
    setIdeas([]); setGeneratedAt(null); setNoFit(null); setNotice(null); setInstruction(''); setLoaded(false)
    void load()
  }, [lead.id, load])

  const generate = async () => {
    setBusy(true)
    setNotice(null)
    try {
      const set = await generateDemoIdeas(lead.id, instruction.trim())
      setIdeas(set.items)
      setGeneratedAt(set.generatedAt)
      setNoFit(set.items.length === 0 ? set.noFitReason ?? 'The AI found no demo that fits.' : null)
    } catch (err) {
      setNotice({ kind: 'error', text: errorMessage(err, 'Could not generate demo ideas.') })
    } finally {
      setBusy(false)
    }
  }

  const copy = async (d: DemoIdea) => {
    try {
      await navigator.clipboard.writeText(d.prompt)
      setCopied(d.id)
      window.clearTimeout(timer.current)
      timer.current = window.setTimeout(() => setCopied(null), 3000)
    } catch {
      setNotice({ kind: 'error', text: 'Could not copy. Select the prompt and copy it yourself.' })
    }
  }

  const input = 'w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500'
  const off = aiConfigured === false

  return (
    <section aria-label="Demo ideas" className="rounded-lg border border-gray-200 p-4 bg-white mt-3 min-w-0">
      <h3 className="text-sm font-semibold text-gray-700 mb-1">Demo ideas</h3>
      <p className="text-xs text-gray-500 mb-3">
        Demos we could build for this lead, from what is stored about it (why, needs, product fit). Copy the prompt into a coding or AI agent. Nothing is sent.
      </p>

      {off && (
        <p role="status" className="text-xs bg-amber-50 border border-amber-200 text-amber-800 rounded px-2 py-1 mb-2">
          AI is not configured on this server. The templates still work.
        </p>
      )}

      <label className="block text-xs text-gray-600 mb-1">What should the demos focus on? (optional)
        <input aria-label="Demo instruction" className={input} value={instruction} maxLength={MAX_AI_INSTRUCTION} disabled={off || busy}
          placeholder="for example: something we can build in a day, about booking"
          onChange={(e) => setInstruction(e.target.value)} />
      </label>
      <div className="flex flex-wrap items-center gap-2 mb-2">
        <button disabled={off || busy} onClick={generate}
          className="whitespace-nowrap text-sm px-3 py-1.5 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50">
          {busy ? 'Thinking...' : ideas.length > 0 || noFit ? 'Regenerate demo ideas' : 'Suggest demos'}
        </button>
        {generatedAt && ideas.length > 0 && <span className="text-xs text-gray-400">Saved {fmt(generatedAt)}</span>}
      </div>

      {notice && (
        <p role="alert" className="text-sm rounded px-2 py-1 border bg-red-50 border-red-200 text-red-800 mb-2 break-words">{notice.text}</p>
      )}
      {noFit && <p role="status" className="text-sm text-gray-700 bg-gray-50 border border-gray-200 rounded px-2 py-1 mb-2 break-words">{noFit}</p>}
      {loaded && ideas.length === 0 && !noFit && !notice && !off && (
        <p className="text-xs text-gray-400">No demo ideas yet.</p>
      )}

      <ul className="space-y-3">
        {ideas.map((d) => (
          <li key={d.id} className="border border-gray-200 rounded-lg p-3 bg-gray-50 min-w-0">
            <div className="flex flex-wrap items-center gap-2 mb-1">
              <h4 className="text-sm font-semibold text-gray-800 break-words min-w-0">{d.title}</h4>
              <span className="text-xs px-2 py-0.5 rounded-full bg-indigo-50 text-indigo-700 border border-indigo-100 whitespace-nowrap">
                {products[d.productKey]?.name ?? d.productKey}
              </span>
            </div>
            <p className="text-xs text-gray-700 mb-1 break-words"><span className="font-medium">Shows:</span> {d.shows}</p>
            <p className="text-xs text-gray-700 mb-2 break-words"><span className="font-medium">Why it fits this lead:</span> {d.whyFit}</p>
            <details>
              <summary className="text-xs text-indigo-700 cursor-pointer select-none">Instruction prompt for an agent</summary>
              <pre className="mt-2 text-xs bg-white border border-gray-200 rounded p-2 whitespace-pre-wrap break-words max-h-64 overflow-y-auto">{d.prompt}</pre>
            </details>
            <div className="mt-2 flex flex-wrap items-center gap-2">
              <button onClick={() => copy(d)} aria-label={`Copy prompt for ${d.title}`}
                className="whitespace-nowrap text-sm px-3 py-1.5 rounded-lg border border-gray-300 text-gray-700 hover:bg-white">
                Copy prompt
              </button>
              {copied === d.id && <span role="status" className="text-xs text-green-700">Copied.</span>}
            </div>
          </li>
        ))}
      </ul>
    </section>
  )
}

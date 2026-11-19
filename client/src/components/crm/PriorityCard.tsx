import { useState } from 'react'
import { useToast } from '../Toast'
import { errorMessage } from '../../api/crm'
import { updatePriority, type Client } from '../../api/clients'

export const FIB = [1, 2, 3, 5, 8, 13, 21, 34, 55, 89, 144]

export function formatScore(score: number | undefined | null) {
  if (!score) return '–'
  return score >= 100 ? Math.round(score).toString() : score.toFixed(score >= 10 ? 1 : 2).replace(/\.?0+$/, '')
}

export function ScoreBadge({ score }: { score: number | undefined | null }) {
  const cls = !score
    ? 'bg-gray-50 text-gray-400 border-gray-200'
    : score >= 4
      ? 'bg-red-50 text-red-700 border-red-200'
      : score >= 1.5
        ? 'bg-amber-50 text-amber-800 border-amber-200'
        : 'bg-blue-50 text-blue-700 border-blue-200'
  return (
    <span
      title="Priority = value × urgency ÷ effort²"
      aria-label={score ? `Priority ${formatScore(score)}` : 'Priority not set yet'}
      className={`inline-flex items-center text-xs font-semibold border rounded-full px-2 py-0.5 ${cls}`}
    >
      {formatScore(score)}
    </span>
  )
}

const label = 'block text-xs font-medium text-gray-600 mb-1'
const select = 'w-full text-sm border border-gray-300 rounded-lg px-3 py-1.5 focus:outline-none focus:ring-2 focus:ring-indigo-300'

/**
 * Priority of a client, built like the JengoWork priority score: value × urgency ÷ effort²
 * on the Fibonacci scale. The score is derived by the server and is 0 until all three are set.
 */
export default function PriorityCard({ client, onSaved }: { client: Client; onSaved: (c: Client) => void }) {
  const { showToast } = useToast()
  const [value, setValue] = useState<string>(client.priorityValue?.toString() ?? '')
  const [urgency, setUrgency] = useState<string>(client.priorityUrgency?.toString() ?? '')
  const [effort, setEffort] = useState<string>(client.priorityEffort?.toString() ?? '')
  const [busy, setBusy] = useState(false)

  const dirty =
    value !== (client.priorityValue?.toString() ?? '') ||
    urgency !== (client.priorityUrgency?.toString() ?? '') ||
    effort !== (client.priorityEffort?.toString() ?? '')

  const num = (v: string) => (v === '' ? null : Number(v))

  const save = async () => {
    setBusy(true)
    try {
      const saved = await updatePriority(client.id, { value: num(value), urgency: num(urgency), effort: num(effort) })
      onSaved(saved)
      showToast('Priority saved', 'success')
    } catch (err) {
      showToast(errorMessage(err, 'Could not save'), 'error')
    } finally {
      setBusy(false)
    }
  }

  const options = (
    <>
      <option value="">Not set yet</option>
      {FIB.map((n) => <option key={n} value={n}>{n}</option>)}
    </>
  )

  return (
    <div className="bg-white rounded-xl border border-gray-200 p-6 mb-6">
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-base font-semibold text-gray-900">Priority</h2>
        <div className="flex items-center gap-2 text-sm text-gray-500">
          Score <ScoreBadge score={client.score} />
        </div>
      </div>
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
        <div>
          <label className={label} htmlFor="pr-value">Value (revenue, strategic importance)</label>
          <select id="pr-value" className={select} value={value} onChange={(e) => setValue(e.target.value)}>{options}</select>
        </div>
        <div>
          <label className={label} htmlFor="pr-urgency">Urgency (how soon)</label>
          <select id="pr-urgency" className={select} value={urgency} onChange={(e) => setUrgency(e.target.value)}>{options}</select>
        </div>
        <div>
          <label className={label} htmlFor="pr-effort">Effort (how much work)</label>
          <select id="pr-effort" className={select} value={effort} onChange={(e) => setEffort(e.target.value)}>{options}</select>
        </div>
      </div>
      <div className="flex items-center justify-between mt-3">
        <p className="text-xs text-gray-400">Priority = value × urgency ÷ effort², as in JengoWork. Without all three the score stays empty.</p>
        <button
          onClick={save}
          disabled={busy || !dirty}
          className="text-sm px-3 py-1.5 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50"
        >
          Save
        </button>
      </div>
    </div>
  )
}

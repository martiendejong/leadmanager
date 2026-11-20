import { useEffect, useState } from 'react'
import {
  errorMessage, getHuntCapabilities, getRecentHunts, isActive, startHunt,
  type HuntCapabilities, type HuntCriteria, type HuntJob,
} from '../../api/hunt'
import HuntJobView from './HuntJobView'

interface Props {
  /** Called when a job finishes, so the lead list can refresh. */
  onFinished?: (job: HuntJob) => void
}

const DEFAULTS: HuntCriteria = { segment: '', region: '', minScore: 8, targetCount: 3, maxCandidates: 30, ratchet: true }

/**
 * Find new leads: describe a segment, a background Jengo agent searches the web for real companies and submits them; the server reads
 * each company's own website itself, accepts only the facts whose quotes are on those pages, and computes the score itself.
 */
export default function HuntPanel({ onFinished }: Props) {
  const [caps, setCaps] = useState<HuntCapabilities | null>(null)
  const [form, setForm] = useState<HuntCriteria>(DEFAULTS)
  const [jobId, setJobId] = useState<string | null>(null)
  const [starting, setStarting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [finished, setFinished] = useState(false)
  const [recent, setRecent] = useState<HuntJob[]>([])

  useEffect(() => {
    getHuntCapabilities().then(setCaps).catch((err) => setError(errorMessage(err, 'Could not check the server')))
    getRecentHunts()
      .then((jobs) => {
        setRecent(jobs.filter((j) => j.kind === 'Hunt'))
        // Only a Find-leads search is picked up again here: a review or verify run has its own place and must not take over this panel.
        const running = jobs.find((j) => j.kind === 'Hunt' && isActive(j.status))
        if (running) setJobId(running.id)
      })
      .catch(() => undefined)
  }, [])

  const set = <K extends keyof HuntCriteria>(key: K, value: HuntCriteria[K]) => setForm((f) => ({ ...f, [key]: value }))
  const segmentOk = form.segment.trim().length >= 3
  const canStart = !!caps?.canStartHunt && segmentOk && !starting

  async function handleStart(e: React.FormEvent) {
    e.preventDefault()
    if (!canStart) return
    setStarting(true)
    setError(null)
    try {
      const { jobId } = await startHunt({ ...form, segment: form.segment.trim(), region: form.region.trim() })
      setFinished(false)
      setJobId(jobId)
    } catch (err) {
      setError(errorMessage(err, 'Could not start the search'))
    } finally {
      setStarting(false)
    }
  }

  if (jobId) {
    return (
      <div className="space-y-4">
        <HuntJobView jobId={jobId} onFinished={(j) => { setFinished(true); onFinished?.(j) }} />
        {finished && (
          <button onClick={() => { setJobId(null); setFinished(false) }}
            className="px-3 py-1.5 text-sm border border-gray-300 rounded-md text-gray-700 hover:bg-gray-50 font-medium">
            Start another search
          </button>
        )}
      </div>
    )
  }

  return (
    <form onSubmit={handleStart} className="space-y-4">
      {caps && !caps.huntAvailable && (
        <div role="alert" className="rounded-md bg-amber-50 border border-amber-300 p-3 text-sm text-amber-900">
          <p className="font-semibold">{caps.message ?? 'Agent hunting is not configured on this server'}</p>
          <p className="mt-0.5">Finding new leads starts a Jengo agent through a JengoWork task. Ask an administrator to set the JengoWork connection for this environment. Verify and rescore on existing leads works without it.</p>
        </div>
      )}
      {caps && caps.huntAvailable && !caps.canStartHunt && (
        <div role="alert" className="rounded-md bg-amber-50 border border-amber-300 p-3 text-sm text-amber-900">
          <p className="font-semibold">You are not allowed to start a lead hunt.</p>
          <p className="mt-0.5">Ask an administrator to add you to the allowed users.</p>
        </div>
      )}
      {error && <div role="alert" className="rounded-md bg-red-50 border border-red-200 p-3 text-sm text-red-700">{error}</div>}

      <div>
        <label htmlFor="hunt-segment" className="block text-sm font-medium text-gray-700">Segment <span className="text-red-500">*</span></label>
        <input id="hunt-segment" value={form.segment} onChange={(e) => set('segment', e.target.value)} maxLength={120} required
          placeholder="e.g. independent graphic designers, solo osteopaths, one-person bookkeeping practices"
          className="mt-1 w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500" />
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        <div>
          <label htmlFor="hunt-region" className="block text-sm font-medium text-gray-700">Region (optional)</label>
          <input id="hunt-region" value={form.region} onChange={(e) => set('region', e.target.value)} maxLength={80} placeholder="e.g. Utrecht, or leave empty for all of the Netherlands"
            className="mt-1 w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500" />
        </div>
        <div>
          <label htmlFor="hunt-min" className="block text-sm font-medium text-gray-700">Minimum score</label>
          <select id="hunt-min" value={form.minScore} onChange={(e) => set('minScore', Number(e.target.value))}
            className="mt-1 w-full border border-gray-300 rounded-md px-3 py-2 text-sm bg-white focus:outline-none focus:ring-2 focus:ring-indigo-500">
            {[9, 8, 7, 6, 5, 4].map((n) => <option key={n} value={n}>{n}{n === 8 ? ' (recommended)' : n === 9 ? ' (rare: needs a Google listing)' : ''}</option>)}
          </select>
          <p className="mt-1 text-xs text-gray-500">The score measures how directly you can reach the owner. The highest possible score is 9.</p>
        </div>
        <div>
          <label htmlFor="hunt-target" className="block text-sm font-medium text-gray-700">Leads to find</label>
          <input id="hunt-target" type="number" min={1} max={10} value={form.targetCount} onChange={(e) => set('targetCount', Math.min(10, Math.max(1, Number(e.target.value) || 1)))}
            className="mt-1 w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500" />
        </div>
        <div>
          <label htmlFor="hunt-max" className="block text-sm font-medium text-gray-700">Companies to look at, at most</label>
          <input id="hunt-max" type="number" min={5} max={40} value={form.maxCandidates} onChange={(e) => set('maxCandidates', Math.min(40, Math.max(5, Number(e.target.value) || 5)))}
            className="mt-1 w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500" />
        </div>
      </div>

      <label className="flex items-start gap-2 text-sm text-gray-700">
        <input type="checkbox" checked={form.ratchet} onChange={(e) => set('ratchet', e.target.checked)} className="mt-0.5 rounded border-gray-300" />
        <span>Raise the bar as better leads are found. A company that cannot beat the best score so far is saved but not researched further.</span>
      </label>

      <div className="rounded-md bg-gray-50 border border-gray-200 p-3 text-xs text-gray-600 space-y-1">
        <p><span className="font-semibold">Public data only.</span> Only what a business publishes itself is used: its own website. Every fact is saved with its source and the exact words, and the score is computed from verified facts only.</p>
        <p><span className="font-semibold">Check before you call or mail.</span> A mobile number or a personal e-mail address of a sole proprietor is personal data: check the GDPR rules and the Do-Not-Call register first.</p>
        <p><span className="font-semibold">A Jengo agent researches this in the background (10 to 30 minutes once it has started).</span> {caps && !caps.agentStartsAutomatically ? `The task is created on JengoWork and does not start by itself: a person has to start it (open the task and move it to todo). A search that nobody starts within ${caps.startWithinMinutes} minutes is ended. ` : ''}You can leave this page: found companies appear in the list as the agent submits them, and every one is checked again by the server before it gets a score.{caps && !caps.googlePlacesConfigured ? ' Google ratings are not checked on this server (no Google Places key), so a score of 9 is out of reach.' : ''}{caps ? ` You can start ${caps.maxPerDay} hunts per day.` : ''}</p>
      </div>

      <div className="flex justify-end">
        <button type="submit" disabled={!canStart}
          className={`px-4 py-2 rounded-md text-sm font-medium ${canStart ? 'bg-indigo-600 text-white hover:bg-indigo-700' : 'bg-indigo-200 text-indigo-400 cursor-not-allowed'}`}>
          {starting ? 'Starting...' : 'Find new leads'}
        </button>
      </div>

      {recent.length > 0 && (
        <div>
          <h4 className="text-xs font-semibold text-gray-500 uppercase tracking-wide mb-1">Recent searches</h4>
          <ul className="divide-y divide-gray-100 border border-gray-200 rounded-md">
            {recent.slice(0, 5).map((j) => (
              <li key={j.id} className="flex items-center justify-between px-3 py-2 text-sm">
                <span className="truncate text-gray-700">{j.segment}{j.region ? `, ${j.region}` : ''}</span>
                <button type="button" onClick={() => { setFinished(!isActive(j.status)); setJobId(j.id) }} className="text-indigo-600 hover:underline text-xs flex-shrink-0 ml-3">
                  {j.status} · {j.qualified} found · best {j.best || '-'}
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}
    </form>
  )
}

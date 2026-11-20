import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import * as signalR from '@microsoft/signalr'
import { cancelHunt, errorMessage, getHunt, isActive, type HuntCandidate, type HuntJob } from '../../api/hunt'

const OUTCOME_LABEL: Record<string, string> = {
  Qualified: 'Qualified',
  BelowMinimum: 'Below the minimum',
  StoppedEarly: 'Stopped early',
  Duplicate: 'Already a lead',
  Unreachable: 'Website did not answer',
  Verified: 'Verified and rescored',
  Error: 'Failed',
  Rejected: 'Not accepted',
}

export function ScoreBadge({ score }: { score: number | null | undefined }) {
  if (score == null) return <span className="text-gray-400">-</span>
  const tone = score >= 8 ? 'bg-green-100 text-green-800' : score >= 7 ? 'bg-emerald-50 text-emerald-700' : score >= 4 ? 'bg-amber-50 text-amber-700' : 'bg-gray-100 text-gray-600'
  return <span className={`inline-flex items-center justify-center min-w-[1.75rem] px-2 py-0.5 rounded-full text-xs font-semibold ${tone}`}>{score}</span>
}

interface Props {
  jobId: string
  /** Called once when the job reaches a final state, so the lead list can refresh. */
  onFinished?: (job: HuntJob) => void
}

/** Live progress of a hunt or verify job: counters, a progress bar, and one row per company looked at. */
export default function HuntJobView({ jobId, onFinished }: Props) {
  const [job, setJob] = useState<HuntJob | null>(null)
  const [candidates, setCandidates] = useState<HuntCandidate[]>([])
  const [error, setError] = useState<string | null>(null)
  const [cancelling, setCancelling] = useState(false)
  const finishedRef = useRef(false)
  const onFinishedRef = useRef(onFinished)
  onFinishedRef.current = onFinished

  const refresh = useCallback(async () => {
    try {
      const data = await getHunt(jobId)
      setJob(data.job)
      setCandidates(data.candidates)
      setError(null)
      if (!isActive(data.job.status) && !finishedRef.current) {
        finishedRef.current = true
        onFinishedRef.current?.(data.job)
      }
    } catch (err) {
      setError(errorMessage(err, 'Could not load the job'))
    }
  }, [jobId])

  useEffect(() => {
    finishedRef.current = false
    setJob(null)
    setCandidates([])
    refresh()

    // Real time updates over SignalR; the slow poll below keeps it correct when the socket is not available.
    const token = localStorage.getItem('lm_token')
    const baseUrl = import.meta.env.VITE_API_URL ?? 'http://localhost:5000'
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`${baseUrl}/hubs/enrichment`, { accessTokenFactory: () => token ?? '' })
      .withAutomaticReconnect()
      .build()
    let timer: ReturnType<typeof setTimeout> | null = null
    connection.on('HuntProgress', () => {
      if (timer) return
      timer = setTimeout(() => { timer = null; refresh() }, 400)
    })
    connection.start().then(() => connection.invoke('JoinJob', jobId)).catch(() => { /* polling covers it */ })

    const poll = setInterval(() => { if (!finishedRef.current) refresh() }, 4000)
    return () => {
      clearInterval(poll)
      if (timer) clearTimeout(timer)
      connection.stop().catch(() => undefined)
    }
  }, [jobId, refresh])

  if (error && !job) return <div className="rounded-md bg-red-50 border border-red-200 p-3 text-sm text-red-700">{error}</div>
  if (!job) return <div className="text-sm text-gray-500">Loading...</div>

  const active = isActive(job.status)
  const isHunt = job.kind !== 'Verify'   // a hunt or a review: both are done by a Jengo agent
  const isReview = job.kind === 'Review'
  const pct = job.total > 0 ? Math.min(100, Math.round((job.screened / job.total) * 100)) : 0

  const statusTone =
    job.status === 'Completed' ? 'bg-green-100 text-green-800'
      : job.status === 'Failed' ? 'bg-red-100 text-red-800'
        : job.status === 'Cancelled' ? 'bg-gray-100 text-gray-700'
          : 'bg-indigo-100 text-indigo-800'

  async function handleCancel() {
    setCancelling(true)
    try { await cancelHunt(job!.id); await refresh() } catch (err) { setError(errorMessage(err)) } finally { setCancelling(false) }
  }

  return (
    <div className="space-y-3" aria-live="polite">
      <div className="flex items-center justify-between gap-3">
        <div className="flex items-center gap-2 text-sm">
          <span className={`px-2 py-0.5 rounded-full text-xs font-medium ${statusTone}`}>{job.status}</span>
          <span className="text-gray-700">
            {isReview ? `Reviewed ${job.screened} of ${job.total} leads` : isHunt ? `Looked at ${job.screened} of at most ${job.total} companies` : `Verified ${job.screened} of ${job.total} leads`}
          </span>
        </div>
        {active && (
          <button onClick={handleCancel} disabled={cancelling}
            className="px-3 py-1 text-sm border border-gray-300 rounded-md text-gray-700 hover:bg-gray-50 disabled:opacity-50">
            {cancelling ? 'Cancelling...' : 'Cancel'}
          </button>
        )}
      </div>

      <div className="h-2 bg-gray-100 rounded-full overflow-hidden">
        <div className={`h-full ${active ? 'bg-indigo-500' : 'bg-green-500'} transition-all`} style={{ width: `${active || job.screened > 0 ? pct : 0}%` }} />
      </div>

      <div className="grid grid-cols-2 sm:grid-cols-4 gap-2 text-sm">
        <Stat label={isReview ? 'Reviewed' : isHunt ? `Found (target ${job.targetCount})` : 'Rescored'} value={job.qualified} />
        <Stat label="Best score" value={job.best || '-'} />
        {isHunt && !isReview && <Stat label="Minimum score" value={job.minScore} />}
        {isHunt && <Stat label="Agent last reported" value={job.lastAgentCallAt ? new Date(job.lastAgentCallAt).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' }) : 'not yet'} />}
      </div>

      {isHunt && job.taskUrl && /^https?:\/\//i.test(job.taskUrl) && (
        <p className="text-sm text-gray-600">
          A Jengo agent is doing this in the background: <a href={job.taskUrl} target="_blank" rel="noreferrer" className="text-indigo-700 hover:underline">open its task</a>.
        </p>
      )}
      {isHunt && job.status === 'Pending' && (
        <p className="text-sm text-gray-600">Waiting for the Jengo agent to pick up the task. This usually takes a few minutes, the research itself 10 to 30.</p>
      )}
      {isHunt && job.summary && <div className="rounded-md bg-gray-50 border border-gray-200 p-3 text-sm text-gray-700 whitespace-pre-line">{job.summary}</div>}
      {active && job.current && <p className="text-sm text-gray-600">Checking <span className="font-medium">{job.current}</span>...</p>}
      {job.error && <div className="rounded-md bg-amber-50 border border-amber-200 p-3 text-sm text-amber-800">{job.error}</div>}
      {error && <div className="rounded-md bg-red-50 border border-red-200 p-3 text-sm text-red-700">{error}</div>}

      {candidates.length > 0 && (
        <div className="border border-gray-200 rounded-lg overflow-hidden">
          <table className="min-w-full text-sm">
            <thead className="bg-gray-50 text-xs uppercase tracking-wide text-gray-500">
              <tr>
                <th className="text-left px-3 py-2 font-medium">Company</th>
                <th className="text-center px-2 py-2 font-medium">Score</th>
                <th className="text-center px-2 py-2 font-medium" title="Highest score the quick look thought possible">At most</th>
                <th className="text-left px-3 py-2 font-medium">Result</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {candidates.map((c) => (
                <tr key={c.id} className={c.outcome === 'Qualified' ? 'bg-green-50/50' : ''}>
                  <td className="px-3 py-2 align-top">
                    {c.leadId ? <Link to={`/leads/${c.leadId}`} className="font-medium text-indigo-700 hover:underline">{c.name}</Link> : <span className="font-medium">{c.name}</span>}
                    <div className="text-xs text-gray-400 break-all">{c.website}</div>
                  </td>
                  <td className="px-2 py-2 text-center align-top"><ScoreBadge score={c.score} /></td>
                  <td className="px-2 py-2 text-center align-top text-gray-500">{c.maxReachable ?? '-'}</td>
                  <td className="px-3 py-2 align-top">
                    <div className="font-medium text-gray-800">{OUTCOME_LABEL[c.outcome] ?? c.outcome}</div>
                    <div className="text-xs text-gray-500">{c.reason}</div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {!active && candidates.length === 0 && !job.error && <p className="text-sm text-gray-500">No companies were looked at.</p>}
    </div>
  )
}

function Stat({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="rounded-md border border-gray-200 bg-white px-3 py-2">
      <div className="text-xs text-gray-500">{label}</div>
      <div className="text-base font-semibold text-gray-900">{value}</div>
    </div>
  )
}

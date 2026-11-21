import { useState, useEffect, useCallback, useRef } from 'react'
import { useParams, Link } from 'react-router-dom'
import { getLead, updateLeadWorkflow } from '../api/leads'
import LeadOrganization from '../components/organizations/LeadOrganization'
import type { Lead } from '../api/leads'
import api from '../api/client'
import CrmPanel from '../components/crm/CrmPanel'
import MessageSection from '../components/leads/MessageSection'
import LeadInsightBlock from '../components/leads/LeadInsightBlock'

// ── Types ────────────────────────────────────────────────────────────────────

interface WorkflowData {
  intake?: {
    pijnpunten?: string[]
    kansen?: string[]
    applicatieType?: string
    verkoopargument?: string
    techStack?: string[]
    complexiteit?: string
    schattingWeken?: number
  }
  proposal?: {
    titel?: string
    samenvatting?: string
    functionaliteiten?: string[]
    meerwaarde?: string
    technologie?: string
    fasering?: { fase: string; omschrijving: string }[]
    prijs?: string
  }
  poc?: {
    boardId?: number
    boardUrl?: string
    boardName?: string
    repoUrl?: string
    repoName?: string
    agiTaskUrl?: string
    createdAt?: string
  }
  belscript?: {
    segment?: string
    belMet?: string
    waaromBellen?: string
    script?: string
    qa?: { vraag: string; antwoord: string }[]
    prospectNummer?: number
  }
  offerte?: {
    geaccordeerd?: boolean
    prijs?: string
    datum?: string
  }
}

interface Activity {
  id: string
  leadId: string
  userId: string
  activityType: string
  note?: string
  createdAt: string
}

// ── Step definitions ─────────────────────────────────────────────────────────

const STEPS = [
  { num: 1, title: 'Lead generated',     desc: 'Lead created in the system' },
  { num: 2, title: 'Information added', desc: 'Website, KvK and AI enrichment' },
  { num: 3, title: 'Intake',               desc: 'Analyse the website for sales context' },
  { num: 4, title: 'Application proposal',   desc: 'AI generates a tailored, concrete proposal' },
  { num: 5, title: 'Generate PoC',        desc: 'Create board, GitHub repo and Jengo AGI project' },
  { num: 6, title: 'Generate script',     desc: 'Personalised call or email script' },
  { num: 7, title: 'Make contact',      desc: 'Call, email or schedule a meeting' },
  { num: 8, title: 'Quote',              desc: 'Prepare the quote and get it approved' },
  { num: 9, title: 'Build out PoC',        desc: 'Build the PoC out into a full application' },
  { num: 10, title: 'Invoice',          desc: 'Create an invoice in Fedha' },
]

// ── Helpers ──────────────────────────────────────────────────────────────────

function getStepStatus(num: number, lead: Lead): 'done' | 'active' | 'pending' {
  if (num === 1) return 'done'
  const ws = lead.workflowStep ?? 1
  if (num === 2 && lead.isEnriched) return 'done'
  if (ws > num) return 'done'
  if (ws === num) return 'active'
  return 'pending'
}

function parseWorkflowData(json?: string | null): WorkflowData {
  if (!json) return {}
  try { return JSON.parse(json) as WorkflowData } catch { return {} }
}

function StepCircle({ num, status }: { num: number; status: 'done' | 'active' | 'pending' }) {
  if (status === 'done')
    return (
      <div className="w-8 h-8 rounded-full bg-green-500 flex items-center justify-center flex-shrink-0">
        <svg className="w-4 h-4 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={3}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
        </svg>
      </div>
    )
  if (status === 'active')
    return (
      <div className="w-8 h-8 rounded-full bg-indigo-600 flex items-center justify-center flex-shrink-0">
        <span className="text-white text-xs font-bold">{num}</span>
      </div>
    )
  return (
    <div className="w-8 h-8 rounded-full bg-gray-100 border border-gray-200 flex items-center justify-center flex-shrink-0">
      <span className="text-gray-400 text-xs font-medium">{num}</span>
    </div>
  )
}

function Spinner({ small }: { small?: boolean }) {
  return <span className={`animate-spin rounded-full border-b-2 border-indigo-600 inline-block ${small ? 'h-3 w-3' : 'h-4 w-4'}`} />
}

function Btn({ onClick, loading, disabled, children, variant = 'primary', className = '' }:
  { onClick?: () => void; loading?: boolean; disabled?: boolean; children: React.ReactNode; variant?: 'primary' | 'secondary' | 'success' | 'danger'; className?: string }) {
  const base = 'inline-flex items-center gap-2 px-4 py-2 text-sm font-medium rounded-lg transition-colors disabled:opacity-50'
  const variants = {
    primary: 'bg-indigo-600 hover:bg-indigo-700 text-white',
    secondary: 'border border-gray-300 text-gray-700 hover:bg-gray-50',
    success: 'bg-green-600 hover:bg-green-700 text-white',
    danger: 'bg-red-600 hover:bg-red-700 text-white',
  }
  return (
    <button onClick={onClick} disabled={disabled || loading} className={`${base} ${variants[variant]} ${className}`}>
      {loading && <Spinner small />}
      {children}
    </button>
  )
}

function InfoRow({ label, value }: { label: string; value?: string | number | null }) {
  if (!value && value !== 0) return null
  return (
    <div className="flex flex-col">
      <span className="text-xs text-gray-400">{label}</span>
      <span className="text-sm text-gray-800 font-medium">{String(value)}</span>
    </div>
  )
}

const PIPELINE_LABELS: Record<string, { label: string; color: string }> = {
  New: { label: 'New', color: 'bg-gray-100 text-gray-600' },
  Contacted: { label: 'Contacted', color: 'bg-blue-100 text-blue-700' },
  Qualified: { label: 'Qualified', color: 'bg-indigo-100 text-indigo-700' },
  ProposalSent: { label: 'Quote sent', color: 'bg-yellow-100 text-yellow-700' },
  Won: { label: 'Won', color: 'bg-green-100 text-green-700' },
  Lost: { label: 'Lost', color: 'bg-red-100 text-red-700' },
}

// ── Step 2 — Enrichment ──────────────────────────────────────────────────────

function Step2Content({ lead, onAdvance, saving, onLeadUpdate }:
  { lead: Lead; onAdvance: (n: number) => void; saving: boolean; onLeadUpdate: (l: Lead) => void }) {
  const [enriching, setEnriching] = useState(false)
  const [progress, setProgress] = useState('')
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null)

  const startEnrich = async () => {
    setEnriching(true)
    setProgress('Enrichment started…')
    try {
      const { data } = await api.post('/api/leads/enrich', { ids: [lead.id] })
      const jobId: string = data.jobId
      pollRef.current = setInterval(async () => {
        try {
          const { data: status } = await api.get(`/api/leads/enrich/${jobId}`)
          setProgress(status.message || 'Working…')
          if (status.status === 'Completed' || status.status === 'Failed') {
            clearInterval(pollRef.current!)
            setEnriching(false)
            if (status.status === 'Completed') {
              const { data: updated } = await api.get(`/api/leads/${lead.id}`)
              onLeadUpdate(updated)
            } else {
              setProgress('Enrichment failed')
            }
          }
        } catch { clearInterval(pollRef.current!); setEnriching(false) }
      }, 2000)
    } catch { setEnriching(false); setProgress('Could not start enrichment') }
  }

  useEffect(() => () => { if (pollRef.current) clearInterval(pollRef.current) }, [])

  if (lead.isEnriched) {
    return (
      <div className="pt-3 space-y-3">
        <div className="text-sm text-gray-600 space-y-1">
          <p className="flex items-center gap-2">
            <span className="w-2 h-2 rounded-full bg-green-500 inline-block" />
            Enriched on {lead.enrichedAt ? new Date(lead.enrichedAt).toLocaleDateString('en-GB') : '—'}
          </p>
          {lead.pagesCrawled ? <p className="text-xs text-gray-400">{lead.pagesCrawled} pages crawled · {lead.chunksIndexed} chunks indexed</p> : null}
          {lead.aiSummary && (
            <p className="mt-2 text-gray-700 bg-gray-50 rounded-lg p-3 text-xs leading-relaxed">{lead.aiSummary.slice(0, 400)}{lead.aiSummary.length > 400 ? '…' : ''}</p>
          )}
        </div>
        <Btn onClick={() => onAdvance(3)} loading={saving} variant="primary">Go to step 3 →</Btn>
      </div>
    )
  }

  return (
    <div className="pt-3 space-y-3">
      <p className="text-sm text-gray-500">AI crawls the website and enriches contact details, sector, KvK and more.</p>
      {progress && <p className="text-xs text-indigo-600">{progress}</p>}
      <div className="flex gap-2 flex-wrap">
        <Btn onClick={startEnrich} loading={enriching} variant="primary">
          {enriching ? 'Enriching…' : 'Start enrichment'}
        </Btn>
        {!enriching && (
          <Btn onClick={() => onAdvance(3)} loading={saving} variant="secondary">Skip →</Btn>
        )}
      </div>
    </div>
  )
}

// ── Step 3 — Intake ──────────────────────────────────────────────────────────

function Step3Content({ lead, wfData, onAdvance, saving, onDataUpdate }:
  { lead: Lead; wfData: WorkflowData; onAdvance: (n: number) => void; saving: boolean; onDataUpdate: (json: string) => void }) {
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')

  const runIntake = async () => {
    setLoading(true); setError('')
    try {
      const { data } = await api.post(`/api/leads/${lead.id}/intake`)
      onDataUpdate(data.workflowDataJson)
    } catch (e: any) {
      setError(e?.response?.data || 'Intake failed')
    } finally { setLoading(false) }
  }

  const intake = wfData.intake

  return (
    <div className="pt-3 space-y-3">
      {intake ? (
        <div className="space-y-3">
          <div className="grid grid-cols-2 gap-3">
            <div className="p-3 bg-blue-50 rounded-lg border border-blue-100">
              <p className="text-xs font-semibold text-blue-700 mb-1">Application type</p>
              <p className="text-sm text-blue-800">{intake.applicatieType}</p>
            </div>
            <div className="p-3 bg-indigo-50 rounded-lg border border-indigo-100">
              <p className="text-xs font-semibold text-indigo-700 mb-1">Complexity</p>
              <p className="text-sm text-indigo-800 capitalize">{intake.complexiteit} · ~{intake.schattingWeken} weeks</p>
            </div>
          </div>
          {intake.pijnpunten && intake.pijnpunten.length > 0 && (
            <div>
              <p className="text-xs font-semibold text-gray-500 mb-1">Pain points</p>
              <ul className="space-y-1">{intake.pijnpunten.map((p, i) => (
                <li key={i} className="text-xs text-gray-700 flex items-start gap-1.5">
                  <span className="text-red-400 mt-0.5 flex-shrink-0">⚡</span>{p}
                </li>
              ))}</ul>
            </div>
          )}
          {intake.kansen && intake.kansen.length > 0 && (
            <div>
              <p className="text-xs font-semibold text-gray-500 mb-1">Opportunities</p>
              <ul className="space-y-1">{intake.kansen.map((k, i) => (
                <li key={i} className="text-xs text-gray-700 flex items-start gap-1.5">
                  <span className="text-green-500 mt-0.5 flex-shrink-0">✓</span>{k}
                </li>
              ))}</ul>
            </div>
          )}
          {intake.verkoopargument && (
            <div className="p-3 bg-green-50 rounded-lg border border-green-100">
              <p className="text-xs font-semibold text-green-700 mb-1">Sales argument</p>
              <p className="text-sm text-green-800 italic">"{intake.verkoopargument}"</p>
            </div>
          )}
          <div className="flex gap-2 flex-wrap">
            <Btn onClick={runIntake} loading={loading} variant="secondary">Regenerate</Btn>
            <Btn onClick={() => onAdvance(4)} loading={saving} variant="primary">Go to step 4 →</Btn>
          </div>
        </div>
      ) : (
        <div className="space-y-3">
          <div className="p-3 bg-blue-50 rounded-lg border border-blue-100 text-sm text-blue-800">
            <p className="font-medium mb-1">Website intake analysis</p>
            <p className="text-blue-700 text-xs">AI analyses the available company information: pain points, opportunities, application type and sales argument for {lead.name}.</p>
          </div>
          {error && <p className="text-xs text-red-600">{error}</p>}
          <div className="flex gap-2">
            <Btn onClick={runIntake} loading={loading} variant="primary">Start intake</Btn>
            <Btn onClick={() => onAdvance(4)} loading={saving} variant="secondary">Skip →</Btn>
          </div>
        </div>
      )}
    </div>
  )
}

// ── Step 4: Application proposal ──────────────────────────────────────────────

function Step4Content({ lead, wfData, onAdvance, saving, onDataUpdate }:
  { lead: Lead; wfData: WorkflowData; onAdvance: (n: number) => void; saving: boolean; onDataUpdate: (json: string) => void }) {
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')

  const runProposal = async () => {
    setLoading(true); setError('')
    try {
      const { data } = await api.post(`/api/leads/${lead.id}/proposal`)
      onDataUpdate(data.workflowDataJson)
    } catch (e: any) {
      setError(e?.response?.data || 'Proposal failed')
    } finally { setLoading(false) }
  }

  const proposal = wfData.proposal

  return (
    <div className="pt-3 space-y-3">
      {proposal ? (
        <div className="space-y-3">
          <div className="p-3 bg-purple-50 rounded-lg border border-purple-100">
            <p className="text-sm font-bold text-purple-900">{proposal.titel}</p>
            <p className="text-xs text-purple-700 mt-1">{proposal.samenvatting}</p>
          </div>
          {proposal.functionaliteiten && proposal.functionaliteiten.length > 0 && (
            <div>
              <p className="text-xs font-semibold text-gray-500 mb-1">Features</p>
              <ul className="space-y-1">{proposal.functionaliteiten.map((f, i) => (
                <li key={i} className="text-xs text-gray-700 flex items-start gap-1.5">
                  <span className="text-purple-400 mt-0.5 flex-shrink-0">▸</span>{f}
                </li>
              ))}</ul>
            </div>
          )}
          <div className="grid grid-cols-2 gap-2">
            {proposal.technologie && (
              <div className="p-2 bg-gray-50 rounded border text-xs">
                <span className="font-medium text-gray-600">Tech: </span>
                <span className="text-gray-700">{proposal.technologie}</span>
              </div>
            )}
            {proposal.prijs && (
              <div className="p-2 bg-green-50 rounded border border-green-100 text-xs">
                <span className="font-medium text-green-700">Price: </span>
                <span className="text-green-800 font-semibold">{proposal.prijs}</span>
              </div>
            )}
          </div>
          {proposal.meerwaarde && (
            <p className="text-xs text-gray-600 italic border-l-2 border-indigo-200 pl-2">{proposal.meerwaarde}</p>
          )}
          <div className="flex gap-2 flex-wrap">
            <Btn onClick={runProposal} loading={loading} variant="secondary">Regenerate</Btn>
            <Btn onClick={() => onAdvance(5)} loading={saving} variant="primary">Go to step 5 →</Btn>
          </div>
        </div>
      ) : (
        <div className="space-y-3">
          <div className="p-3 bg-purple-50 rounded-lg border border-purple-100 text-sm text-purple-800">
            <p className="font-medium mb-1">AI application proposal</p>
            <p className="text-purple-700 text-xs">Based on the intake, AI generates a concrete proposal: features, technology and price indication for {lead.name}.</p>
          </div>
          {error && <p className="text-xs text-red-600">{error}</p>}
          <div className="flex gap-2">
            <Btn onClick={runProposal} loading={loading} variant="primary">Generate proposal</Btn>
            <Btn onClick={() => onAdvance(5)} loading={saving} variant="secondary">Skip →</Btn>
          </div>
        </div>
      )}
    </div>
  )
}

// ── Step 5: Generate PoC ──────────────────────────────────────────────────────

function Step5Content({ lead, wfData, onAdvance, saving, onDataUpdate }:
  { lead: Lead; wfData: WorkflowData; onAdvance: (n: number) => void; saving: boolean; onDataUpdate: (json: string) => void }) {
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')

  const createPoc = async () => {
    setLoading(true); setError('')
    try {
      const { data } = await api.post(`/api/leads/${lead.id}/poc`)
      onDataUpdate(data.workflowDataJson)
      onAdvance(6)
    } catch (e: any) {
      setError(e?.response?.data || 'Could not create PoC')
    } finally { setLoading(false) }
  }

  const poc = wfData.poc

  if (poc?.boardUrl) {
    return (
      <div className="pt-3 space-y-3">
        <div className="p-3 bg-amber-50 rounded-lg border border-amber-100 space-y-2">
          <p className="text-xs font-semibold text-amber-700">PoC created</p>
          <p className="text-sm font-medium text-amber-900">{poc.boardName}</p>
          <a href={poc.boardUrl} target="_blank" rel="noopener noreferrer"
            className="inline-flex items-center gap-1 text-xs text-indigo-600 hover:underline">
            <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14" /></svg>
            Open board in TaskManager
          </a>
          {poc.repoUrl && (
            <a href={poc.repoUrl} target="_blank" rel="noopener noreferrer"
              className="flex items-center gap-1 text-xs text-gray-600 hover:underline">
              <svg className="w-3 h-3" viewBox="0 0 16 16" fill="currentColor"><path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z"/></svg>
              GitHub: {poc.repoName}
            </a>
          )}
          {poc.agiTaskUrl && (
            <a href={poc.agiTaskUrl} target="_blank" rel="noopener noreferrer"
              className="flex items-center gap-1 text-xs text-purple-600 hover:underline">
              <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9.663 17h4.673M12 3v1m6.364 1.636l-.707.707M21 12h-1M4 12H3m3.343-5.657l-.707-.707m2.828 9.9a5 5 0 117.072 0l-.548.547A3.374 3.374 0 0014 18.469V19a2 2 0 11-4 0v-.531c0-.895-.356-1.754-.988-2.386l-.548-.547z" /></svg>
              Jengo AGI task
            </a>
          )}
        </div>
        <Btn onClick={() => onAdvance(6)} loading={saving} variant="primary">Go to step 6 →</Btn>
      </div>
    )
  }

  return (
    <div className="pt-3 space-y-3">
      <div className="p-3 bg-amber-50 rounded-lg border border-amber-100 text-sm text-amber-800">
        <p className="font-medium mb-1">Generate PoC</p>
        <p className="text-amber-700 text-xs">If yes: automatically create a board in TaskManager with initial tasks based on the application proposal.</p>
      </div>
      {error && <p className="text-xs text-red-600">{error}</p>}
      <div className="flex gap-2 flex-wrap">
        <Btn onClick={createPoc} loading={loading} variant="primary">Yes, create PoC</Btn>
        <Btn onClick={() => onAdvance(6)} loading={saving} variant="secondary">No, skip →</Btn>
      </div>
    </div>
  )
}

// ── Step 6: Generate script ───────────────────────────────────────────────────

function Step6Content({ lead, wfData, onAdvance, saving }:
  { lead: Lead; wfData: WorkflowData; onAdvance: (n: number) => void; saving: boolean }) {
  const belscript = wfData.belscript
  const [scriptType, setScriptType] = useState<'bel' | 'email'>('bel')
  const [loading, setLoading] = useState(false)
  const [script, setScript] = useState(belscript?.script || '')
  const [copied, setCopied] = useState(false)
  const [extraContext, setExtraContext] = useState('')
  const [showQa, setShowQa] = useState(false)

  const generate = async () => {
    setLoading(true)
    try {
      const { data } = await api.post('/api/sales-settings/generate-script', {
        leadId: lead.id,
        extraContext: extraContext || undefined,
      })
      setScript(data.script)
    } catch { setScript('Generation failed') }
    finally { setLoading(false) }
  }

  const copy = () => {
    navigator.clipboard.writeText(script)
    setCopied(true)
    setTimeout(() => setCopied(false), 2000)
  }

  return (
    <div className="pt-3 space-y-3">
      {belscript && (
        <div className="bg-indigo-50 border border-indigo-200 rounded-lg p-3 text-xs text-indigo-800 space-y-1">
          <div className="font-semibold">Call script available (prospect #{belscript.prospectNummer} · {belscript.segment})</div>
          {belscript.belMet && <div><span className="text-indigo-500">Call with:</span> {belscript.belMet}</div>}
          {belscript.waaromBellen && <div><span className="text-indigo-500">Why:</span> {belscript.waaromBellen}</div>}
        </div>
      )}
      <div className="flex gap-2 items-center">
        <span className="text-sm text-gray-600 font-medium">Type:</span>
        {(['bel', 'email'] as const).map(t => (
          <button key={t} onClick={() => setScriptType(t)}
            className={`px-3 py-1 rounded-lg text-xs font-medium border transition-colors ${scriptType === t ? 'bg-indigo-50 border-indigo-200 text-indigo-700' : 'border-gray-200 text-gray-600 hover:border-gray-300'}`}>
            {t === 'bel' ? 'Call script' : 'Email script'}
          </button>
        ))}
      </div>
      <textarea
        value={extraContext}
        onChange={e => setExtraContext(e.target.value)}
        placeholder="Extra context (optional): e.g. 'They recently opened a new location'"
        className="w-full text-xs border border-gray-200 rounded-lg px-3 py-2 resize-none focus:outline-none focus:ring-2 focus:ring-indigo-300"
        rows={2}
      />
      <Btn onClick={generate} loading={loading} variant="primary">
        {belscript?.script ? 'Generate new AI script' : 'Generate script'}
      </Btn>
      {script && (
        <div className="relative">
          <pre className="text-xs text-gray-700 bg-gray-50 rounded-lg p-3 border overflow-auto max-h-64 whitespace-pre-wrap leading-relaxed">{script}</pre>
          <button onClick={copy}
            className="absolute top-2 right-2 px-2 py-1 text-xs bg-white border border-gray-200 rounded hover:bg-gray-50 text-gray-600 transition-colors">
            {copied ? '✓ Copied' : 'Copy'}
          </button>
        </div>
      )}
      {belscript?.qa && belscript.qa.length > 0 && (
        <div>
          <button onClick={() => setShowQa(v => !v)}
            className="text-xs text-indigo-600 hover:text-indigo-800 font-medium">
            {showQa ? 'Hide' : 'Show'} objections & answers ({belscript.qa.length})
          </button>
          {showQa && (
            <div className="mt-2 space-y-2">
              {belscript.qa.map((qa, i) => (
                <div key={i} className="bg-yellow-50 border border-yellow-200 rounded-lg p-2 text-xs">
                  <div className="font-semibold text-yellow-800">{qa.vraag}</div>
                  <div className="text-gray-700 mt-1">{qa.antwoord}</div>
                </div>
              ))}
            </div>
          )}
        </div>
      )}
      <div className="flex gap-2">
        <Btn onClick={() => onAdvance(7)} loading={saving} variant="primary">Script saved → Step 7</Btn>
        <Btn onClick={() => onAdvance(7)} loading={saving} variant="secondary">Skip</Btn>
      </div>
    </div>
  )
}

// ── Step 7: Make contact ──────────────────────────────────────────────────────

function Step7Content({ lead, onAdvance, saving }:
  { lead: Lead; onAdvance: (n: number) => void; saving: boolean }) {
  const [activities, setActivities] = useState<Activity[]>([])
  const [noteType, setNoteType] = useState<string>('Called')
  const [note, setNote] = useState('')
  const [submitting, setSubmitting] = useState(false)

  const loadActivities = useCallback(async () => {
    try {
      const { data } = await api.get(`/api/leads/${lead.id}/activities`)
      setActivities(data)
    } catch { /* ignore */ }
  }, [lead.id])

  useEffect(() => { loadActivities() }, [loadActivities])

  const addActivity = async (type: string, noteText?: string) => {
    setSubmitting(true)
    try {
      await api.post(`/api/leads/${lead.id}/activities`, { activityType: type, note: noteText || undefined })
      await loadActivities()
      setNote('')
    } finally { setSubmitting(false) }
  }

  const TYPE_LABELS: Record<string, string> = {
    Called: '📞 Called', EmailSent: '📧 Email sent', NoteAdded: '📝 Note', Enriched: '🔍 Enriched', StatusChanged: '🔄 Status changed'
  }

  return (
    <div className="pt-3 space-y-4">
      <div className="grid grid-cols-3 gap-2">
        {[
          { label: '📞 Call', value: lead.phone || lead.ownerMobile, type: 'Called' },
          { label: '📧 Email', value: lead.companyEmail || lead.personalEmail, type: 'EmailSent' },
          { label: '📅 Meeting', value: 'Schedule soon', type: 'NoteAdded' },
        ].map(item => (
          <button key={item.label}
            onClick={() => { setNoteType(item.type); setNote(`${item.label} with ${lead.name}`) }}
            className="p-3 bg-gray-50 rounded-lg border border-gray-200 text-center hover:border-indigo-300 hover:bg-indigo-50 transition-colors">
            <div className="text-base mb-0.5">{item.label.split(' ')[0]}</div>
            <div className="text-xs font-medium text-gray-700">{item.label.split(' ').slice(1).join(' ')}</div>
            <div className="text-xs text-gray-400 truncate">{item.value || '—'}</div>
          </button>
        ))}
      </div>

      <div className="space-y-2">
        <select value={noteType} onChange={e => setNoteType(e.target.value)}
          className="text-xs border border-gray-200 rounded px-2 py-1 focus:outline-none focus:ring-2 focus:ring-indigo-300">
          {Object.entries(TYPE_LABELS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
        </select>
        <textarea value={note} onChange={e => setNote(e.target.value)}
          placeholder="Add a note…"
          className="w-full text-xs border border-gray-200 rounded-lg px-3 py-2 resize-none focus:outline-none focus:ring-2 focus:ring-indigo-300"
          rows={2} />
        <div className="flex gap-2">
          <Btn onClick={() => addActivity(noteType, note)} loading={submitting} variant="primary">Log activity</Btn>
          <Btn onClick={() => onAdvance(8)} loading={saving} variant="secondary">Contact logged → Step 8</Btn>
        </div>
      </div>

      {activities.length > 0 && (
        <div className="space-y-1.5">
          <p className="text-xs font-semibold text-gray-500">Activities</p>
          {activities.slice(0, 8).map(a => (
            <div key={a.id} className="flex items-start gap-2 text-xs text-gray-600">
              <span className="text-gray-400 flex-shrink-0 w-20">
                {new Date(a.createdAt).toLocaleDateString('en-GB', { day: '2-digit', month: '2-digit' })}
              </span>
              <span className="font-medium">{TYPE_LABELS[a.activityType] ?? a.activityType}</span>
              {a.note && <span className="text-gray-500 truncate">{a.note}</span>}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

// ── Step 8: Quote ─────────────────────────────────────────────────────────────

function Step8Content({ lead, wfData, onAdvance, saving }:
  { lead: Lead; wfData: WorkflowData; onAdvance: (n: number, wfJson?: string) => void; saving: boolean }) {
  const proposalPrijs = wfData.proposal?.prijs ?? ''
  const existing = wfData.offerte

  const [productType, setProductType] = useState('Website')
  const [bundleType, setBundleType] = useState('Starter')
  const [prijs, setPrijs] = useState(proposalPrijs)
  const [generating, setGenerating] = useState(false)
  const [generated, setGenerated] = useState(!!existing?.geaccordeerd)

  const generateOfferte = async () => {
    setGenerating(true)
    try {
      const resp = await api.post('/api/offerte/generate', {
        leadId: lead.id, productType, bundleType
      }, { responseType: 'blob' })
      const url = URL.createObjectURL(new Blob([resp.data], { type: 'application/pdf' }))
      const a = document.createElement('a')
      a.href = url
      a.download = `Offerte-${lead.name.replace(/ /g, '_')}.pdf`
      a.click()
      URL.revokeObjectURL(url)
      setGenerated(true)
    } catch { alert('Could not generate quote') }
    finally { setGenerating(false) }
  }

  const accordeer = () => {
    const updated: WorkflowData = {
      ...wfData,
      offerte: { geaccordeerd: true, prijs: prijs || undefined, datum: new Date().toISOString() },
    }
    onAdvance(9, JSON.stringify(updated))
  }

  if (existing?.geaccordeerd) {
    return (
      <div className="pt-3 space-y-3">
        <div className="p-3 bg-green-50 rounded-lg border border-green-200">
          <p className="text-xs font-semibold text-green-700">Quote approved</p>
          {existing.prijs && <p className="text-sm text-green-800 font-medium mt-1">{existing.prijs}</p>}
          {existing.datum && (
            <p className="text-xs text-green-600 mt-0.5">
              {new Date(existing.datum).toLocaleDateString('en-GB', { day: '2-digit', month: '2-digit', year: 'numeric' })}
            </p>
          )}
        </div>
        <Btn onClick={() => onAdvance(9)} loading={saving} variant="primary">Go to step 9 →</Btn>
      </div>
    )
  }

  return (
    <div className="pt-3 space-y-3">
      <div className="grid grid-cols-2 gap-3">
        <div>
          <label className="text-xs text-gray-500 block mb-1">Product</label>
          <select value={productType} onChange={e => setProductType(e.target.value)}
            className="w-full text-sm border border-gray-200 rounded-lg px-2 py-1.5 focus:outline-none focus:ring-2 focus:ring-indigo-300">
            {['Website', 'AIEmployee', 'AITeam', 'Custom'].map(p => (
              <option key={p} value={p}>{p === 'AIEmployee' ? 'AI Employee' : p === 'AITeam' ? 'AI Team' : p}</option>
            ))}
          </select>
        </div>
        <div>
          <label className="text-xs text-gray-500 block mb-1">Bundle</label>
          <select value={bundleType} onChange={e => setBundleType(e.target.value)}
            className="w-full text-sm border border-gray-200 rounded-lg px-2 py-1.5 focus:outline-none focus:ring-2 focus:ring-indigo-300">
            <option value="Starter">Starter</option>
            <option value="Team">Team</option>
            <option value="PayPerHour">Pay-per-Hour</option>
          </select>
        </div>
      </div>
      <div>
        <label className="text-xs text-gray-500 block mb-1">Agreed price</label>
        <input
          type="text"
          value={prijs}
          onChange={e => setPrijs(e.target.value)}
          placeholder="e.g. €4,500 one-off"
          className="w-full text-sm border border-gray-200 rounded-lg px-3 py-1.5 focus:outline-none focus:ring-2 focus:ring-indigo-300"
        />
      </div>
      <div className="flex gap-2 flex-wrap">
        <Btn onClick={generateOfferte} loading={generating} variant="primary">Generate quote (PDF)</Btn>
        {generated && <Btn onClick={accordeer} loading={saving} variant="success">Quote approved → Step 9</Btn>}
        {!generated && <Btn onClick={() => onAdvance(9)} loading={saving} variant="secondary">Skip →</Btn>}
      </div>
    </div>
  )
}

// ── Step 9: Build out PoC ─────────────────────────────────────────────────────

function Step9Content({ wfData, onAdvance, saving }:
  { wfData: WorkflowData; onAdvance: (n: number) => void; saving: boolean }) {
  const poc = wfData.poc
  return (
    <div className="pt-3 space-y-3">
      <p className="text-sm text-gray-600">The PoC is being built out into a full application. Tracked via the board and the GitHub repo.</p>
      {!poc?.boardUrl && <p className="text-xs text-amber-600">No PoC board · step 5 was skipped.</p>}
      {poc?.boardUrl && (
        <a href={poc.boardUrl} target="_blank" rel="noopener noreferrer"
          className="flex items-center gap-2 px-3 py-2 border border-indigo-200 text-indigo-700 text-xs font-medium rounded-lg hover:bg-indigo-50 transition-colors">
          <svg className="w-3 h-3 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14" /></svg>
          {poc.boardName || 'Open board in TaskManager'}
        </a>
      )}
      {poc?.repoUrl && (
        <a href={poc.repoUrl} target="_blank" rel="noopener noreferrer"
          className="flex items-center gap-2 px-3 py-2 border border-gray-200 text-gray-700 text-xs font-medium rounded-lg hover:bg-gray-50 transition-colors">
          <svg className="w-3 h-3 flex-shrink-0" viewBox="0 0 16 16" fill="currentColor"><path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z"/></svg>
          GitHub: {poc.repoName}
        </a>
      )}
      <Btn onClick={() => onAdvance(10)} loading={saving} variant="success">Application delivered → Step 10</Btn>
    </div>
  )
}

// ── Step 10: Invoice ──────────────────────────────────────────────────────────

function Step10Content({ lead, wfData, onAdvance, saving }:
  { lead: Lead; wfData: WorkflowData; onAdvance: (n: number) => void; saving: boolean }) {
  const offerte = wfData.offerte
  const prijs = offerte?.prijs || wfData.proposal?.prijs || ''

  const fedhaParams = new URLSearchParams()
  fedhaParams.set('klant', lead.name)
  if (prijs) fedhaParams.set('bedrag', prijs)
  const fedhaUrl = `https://fedha.martiendejong.nl/facturen/nieuw?${fedhaParams.toString()}`

  return (
    <div className="pt-3 space-y-3">
      <div className="p-3 bg-indigo-50 rounded-lg border border-indigo-100">
        <p className="text-sm font-medium text-indigo-900 mb-1">Invoice via Fedha</p>
        {prijs ? (
          <p className="text-xs text-indigo-700">Agreed price: <span className="font-semibold">{prijs}</span></p>
        ) : (
          <p className="text-xs text-indigo-700">Create an invoice based on the agreed price.</p>
        )}
      </div>
      <div className="flex gap-2">
        <a href={fedhaUrl} target="_blank" rel="noopener noreferrer"
          className="inline-flex items-center gap-2 px-4 py-2 border border-indigo-300 text-indigo-700 text-sm font-medium rounded-lg hover:bg-indigo-50 transition-colors">
          Open Fedha
          <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14" />
          </svg>
        </a>
        <Btn onClick={() => onAdvance(10)} loading={saving} variant="success">Invoiced ✓</Btn>
      </div>
    </div>
  )
}

// ── Step 1 ────────────────────────────────────────────────────────────────────

function Step1Content({ lead }: { lead: Lead }) {
  return (
    <div className="pt-3 text-sm text-gray-600 space-y-1">
      <p>Created: {lead.createdAt ? new Date(lead.createdAt).toLocaleDateString('en-GB') : '—'}</p>
      <p>Source: <span className="font-medium">{lead.source || '—'}</span></p>
    </div>
  )
}

// ── Main component ────────────────────────────────────────────────────────────

export default function LeadDetailPage() {
  const { id } = useParams<{ id: string }>()
  const [lead, setLead] = useState<Lead | null>(null)
  const [loading, setLoading] = useState(true)
  const [expandedStep, setExpandedStep] = useState<number>(1)
  const [saving, setSaving] = useState(false)
  const [notFound, setNotFound] = useState(false)

  const fetchLead = useCallback(async () => {
    if (!id) return
    setLoading(true)
    try {
      const data = await getLead(id)
      setLead(data)
      setExpandedStep(data.workflowStep ?? 1)
    } catch { setNotFound(true) }
    finally { setLoading(false) }
  }, [id])

  useEffect(() => { fetchLead() }, [fetchLead])

  // "Write message" links open the lead at the message section
  const leadLoaded = !!lead
  useEffect(() => {
    if (leadLoaded && window.location.hash === '#message') document.getElementById('message')?.scrollIntoView({ block: 'start' })
  }, [leadLoaded])

  const advanceStep = useCallback(async (nextStep: number, workflowDataJson?: string) => {
    if (!lead || !id) return
    setSaving(true)
    try {
      const res = await updateLeadWorkflow(id, nextStep, workflowDataJson)
      setLead(prev => prev ? {
        ...prev,
        workflowStep: res.workflowStep,
        workflowDataJson: res.workflowDataJson ?? prev.workflowDataJson,
      } : prev)
      setExpandedStep(res.workflowStep)
    } finally { setSaving(false) }
  }, [lead, id])

  const updateWorkflowData = useCallback((json: string) => {
    setLead(prev => prev ? { ...prev, workflowDataJson: json } : prev)
  }, [])

  if (loading) {
    return (
      <div className="flex items-center justify-center h-64">
        <span className="animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600" />
      </div>
    )
  }

  if (notFound || !lead) {
    return (
      <div className="p-8 text-center">
        <p className="text-gray-500 mb-4">Lead not found.</p>
        <Link to="/leads" className="text-indigo-600 hover:underline text-sm">← Back to leads</Link>
      </div>
    )
  }

  const pipeline = PIPELINE_LABELS[lead.pipelineStatus ?? 'New'] ?? PIPELINE_LABELS.New
  const workflowStep = lead.workflowStep ?? 1
  const progress = Math.round(((workflowStep - 1) / 9) * 100)
  const wfData = parseWorkflowData(lead.workflowDataJson)

  function renderStepContent(num: number, _status: 'done' | 'active' | 'pending') {
    switch (num) {
      case 1: return <Step1Content lead={lead!} />
      case 2: return <Step2Content lead={lead!} onAdvance={advanceStep} saving={saving} onLeadUpdate={setLead} />
      case 3: return <Step3Content lead={lead!} wfData={wfData} onAdvance={advanceStep} saving={saving} onDataUpdate={updateWorkflowData} />
      case 4: return <Step4Content lead={lead!} wfData={wfData} onAdvance={advanceStep} saving={saving} onDataUpdate={updateWorkflowData} />
      case 5: return <Step5Content lead={lead!} wfData={wfData} onAdvance={advanceStep} saving={saving} onDataUpdate={updateWorkflowData} />
      case 6: return <Step6Content lead={lead!} wfData={wfData} onAdvance={advanceStep} saving={saving} />
      case 7: return <Step7Content lead={lead!} onAdvance={advanceStep} saving={saving} />
      case 8: return <Step8Content lead={lead!} wfData={wfData} onAdvance={advanceStep} saving={saving} />
      case 9: return <Step9Content wfData={wfData} onAdvance={advanceStep} saving={saving} />
      case 10: return <Step10Content lead={lead!} wfData={wfData} onAdvance={advanceStep} saving={saving} />
      default: return null
    }
  }

  return (
    <div className="p-4 sm:p-6 max-w-7xl mx-auto">
      {/* Header: wraps on a phone, so the badge and the city and sector text are never clipped */}
      <div className="flex flex-wrap items-start justify-between gap-x-3 gap-y-2 mb-6">
        <div className="flex items-center gap-3 min-w-0">
          <Link to="/leads" className="text-gray-400 hover:text-gray-600 transition-colors" title="Back to leads">
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 19l-7-7 7-7" />
            </svg>
          </Link>
          <div className="min-w-0">
            <h1 className="text-xl sm:text-2xl font-bold text-gray-900 break-words">{lead.name}</h1>
            <div className="mt-1"><LeadOrganization lead={lead} onMoved={(updated) => setLead((prev) => (prev ? { ...prev, organizationId: updated.organizationId } : prev))} /></div>
            <div className="flex flex-wrap items-center gap-x-2 gap-y-1 mt-1">
              {lead.website && (
                <a href={lead.website.startsWith('http') ? lead.website : `https://${lead.website}`}
                  target="_blank" rel="noopener noreferrer"
                  className="text-xs text-indigo-600 hover:underline flex items-center gap-1">
                  {lead.website.replace(/^https?:\/\//, '').replace(/\/$/, '')}
                  <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14" />
                  </svg>
                </a>
              )}
              {lead.city && <span className="text-xs text-gray-400">· {lead.city}</span>}
              {lead.sector && <span className="text-xs text-gray-400">· {lead.sector}</span>}
            </div>
          </div>
        </div>
        <span className={`px-3 py-1 rounded-full text-xs font-medium whitespace-nowrap shrink-0 ${pipeline.color}`}>{pipeline.label}</span>
      </div>

      {/* Why this lead, what it needs, how we can help (the same block as in the slide-over) */}
      <div className="mb-6"><LeadInsightBlock lead={lead} /></div>

      {/* Progress bar */}
      <div className="mb-6">
        <div className="flex items-center justify-between text-xs text-gray-500 mb-1">
          <span>Step {workflowStep} of 10</span>
          <span>{progress}% complete</span>
        </div>
        <div className="h-1.5 bg-gray-100 rounded-full overflow-hidden">
          <div className="h-full bg-indigo-500 rounded-full transition-all duration-500" style={{ width: `${progress}%` }} />
        </div>
      </div>

      {/* Body: two columns */}
      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Left: Lead info */}
        <div className="lg:col-span-1 space-y-4">
          <div className="bg-white rounded-xl border border-gray-200 p-4">
            <h3 className="text-xs font-semibold text-gray-500 uppercase tracking-wider mb-3">Contact</h3>
            <div className="space-y-2.5">
              <InfoRow label="Owner" value={[lead.ownerFirstName, lead.ownerLastName].filter(Boolean).join(' ') || lead.ownerName} />
              <InfoRow label="Phone" value={lead.phone} />
              <InfoRow label="Mobile" value={lead.ownerMobile} />
              <InfoRow label="Company email" value={lead.companyEmail} />
              <InfoRow label="Personal email" value={lead.personalEmail} />
              {lead.linkedInUrl && (
                <div className="flex flex-col">
                  <span className="text-xs text-gray-400">LinkedIn</span>
                  <a href={lead.linkedInUrl} target="_blank" rel="noopener noreferrer"
                    className="text-sm text-indigo-600 hover:underline truncate">View profile</a>
                </div>
              )}
            </div>
          </div>

          <div className="bg-white rounded-xl border border-gray-200 p-4">
            <h3 className="text-xs font-semibold text-gray-500 uppercase tracking-wider mb-3">Company</h3>
            <div className="space-y-2.5">
              <InfoRow label="Sector" value={lead.sector} />
              <InfoRow label="City" value={lead.city} />
              <InfoRow label="KvK" value={lead.kvkNumber} />
              <InfoRow label="Employees" value={lead.employeeCount} />
              <InfoRow label="Founded" value={lead.foundingYear} />
              <InfoRow label="Legal form" value={lead.legalForm} />
              {lead.googleRating && (
                <div className="flex flex-col">
                  <span className="text-xs text-gray-400">Google rating</span>
                  <span className="text-sm text-gray-800 font-medium">⭐ {lead.googleRating} ({lead.googleReviewCount} reviews)</span>
                </div>
              )}
            </div>
          </div>

          {lead.aiSummary && (
            <div className="bg-white rounded-xl border border-gray-200 p-4">
              <h3 className="text-xs font-semibold text-gray-500 uppercase tracking-wider mb-3">AI summary</h3>
              <p className="text-xs text-gray-700 leading-relaxed">{lead.aiSummary}</p>
            </div>
          )}

          {(lead.salesPriorityScore != null || lead.salesPriorityLabel) && (
            <div className="bg-white rounded-xl border border-gray-200 p-4">
              <h3 className="text-xs font-semibold text-gray-500 uppercase tracking-wider mb-3">Sales opportunity</h3>
              <div className="space-y-2.5">
                {lead.salesPriorityScore != null && (
                  <div className="flex items-center gap-2">
                    <div className="flex-1 h-2 bg-gray-100 rounded-full overflow-hidden">
                      <div className="h-full bg-indigo-500 rounded-full" style={{ width: `${(lead.salesPriorityScore / 10) * 100}%` }} />
                    </div>
                    <span className="text-sm font-bold text-gray-700">{lead.salesPriorityScore}/10</span>
                  </div>
                )}
                {lead.salesPriorityLabel && <InfoRow label="Label" value={lead.salesPriorityLabel} />}
                {lead.salesPriorityReasoning && (
                  <p className="text-xs text-gray-500 leading-relaxed">{lead.salesPriorityReasoning}</p>
                )}
              </div>
            </div>
          )}
        </div>

        {/* Right: Workflow */}
        <div className="lg:col-span-2 min-w-0">
          <div id="message" className="mb-4">
            <MessageSection lead={lead} onLeadChanged={(patch) => setLead((prev) => (prev ? { ...prev, ...patch } : prev))} />
          </div>
          <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
            {STEPS.map((step, idx) => {
              const status = getStepStatus(step.num, lead)
              const isExpanded = expandedStep === step.num

              return (
                <div key={step.num} className={idx < STEPS.length - 1 ? 'border-b border-gray-100' : ''}>
                  <button
                    onClick={() => setExpandedStep(isExpanded ? 0 : step.num)}
                    className="w-full flex items-center gap-4 px-5 py-4 hover:bg-gray-50 transition-colors text-left">
                    <StepCircle num={step.num} status={status} />
                    <div className="flex-1 min-w-0">
                      <div className={`text-sm font-medium ${status === 'pending' ? 'text-gray-400' : 'text-gray-800'}`}>{step.title}</div>
                      <div className="text-xs text-gray-400 truncate">{step.desc}</div>
                    </div>
                    {status === 'active' && (
                      <span className="text-xs px-2 py-0.5 rounded-full bg-indigo-100 text-indigo-700 font-medium flex-shrink-0">Active</span>
                    )}
                    <svg className={`w-4 h-4 text-gray-300 flex-shrink-0 transition-transform ${isExpanded ? 'rotate-180' : ''}`}
                      fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
                    </svg>
                  </button>

                  {isExpanded && (
                    <div className="px-5 pb-5 border-t border-gray-50 bg-gray-50/40">
                      {renderStepContent(step.num, status)}
                    </div>
                  )}
                </div>
              )
            })}
          </div>
        </div>
      </div>

      {/* Relationship screen: next actions, conversations, contacts, links (Jengo Clients) */}
      <div className="mt-6">
        <CrmPanel parentType="leads" parentId={lead.id} />
      </div>
    </div>
  )
}

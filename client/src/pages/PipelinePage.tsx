import { useEffect, useState, useRef } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  fetchLeadsByPipeline,
  updateLeadPipelineStatus,
  PIPELINE_STATUSES,
  PIPELINE_STATUS_LABELS,
} from '../api/leads'
import type { Lead, PipelineStatus } from '../api/leads'
import { useToast } from '../components/Toast'

const COLUMN_COLORS: Record<PipelineStatus, string> = {
  New: 'bg-gray-50 border-gray-200',
  Contacted: 'bg-blue-50 border-blue-200',
  Qualified: 'bg-indigo-50 border-indigo-200',
  ProposalSent: 'bg-yellow-50 border-yellow-200',
  Won: 'bg-green-50 border-green-200',
  Lost: 'bg-red-50 border-red-200',
}

const COLUMN_HEADER_COLORS: Record<PipelineStatus, string> = {
  New: 'text-gray-700 bg-gray-100',
  Contacted: 'text-blue-700 bg-blue-100',
  Qualified: 'text-indigo-700 bg-indigo-100',
  ProposalSent: 'text-yellow-700 bg-yellow-100',
  Won: 'text-green-700 bg-green-100',
  Lost: 'text-red-700 bg-red-100',
}

function ScoreBadge({ score }: { score?: number | null }) {
  if (score == null) return null
  const color =
    score >= 7 ? 'bg-green-100 text-green-700' :
    score >= 4 ? 'bg-yellow-100 text-yellow-700' :
    'bg-red-100 text-red-700'
  return (
    <span className={`inline-flex items-center text-xs font-semibold px-1.5 py-0.5 rounded-full ${color}`}>
      {score}
    </span>
  )
}

interface KanbanCardProps {
  lead: Lead
  onDragStart: (e: React.DragEvent, leadId: string) => void
}

function KanbanCard({ lead, onDragStart }: KanbanCardProps) {
  return (
    <div
      draggable
      onDragStart={(e) => onDragStart(e, lead.id)}
      className="bg-white border border-gray-200 rounded-lg p-3 shadow-sm cursor-grab active:cursor-grabbing hover:shadow-md transition-shadow select-none"
    >
      <div className="flex items-start justify-between gap-1 mb-1">
        <p className="text-sm font-semibold text-gray-900 leading-tight truncate flex-1">{lead.name}</p>
        <ScoreBadge score={lead.salesPriorityScore} />
      </div>
      {lead.city && (
        <p className="text-xs text-gray-500 flex items-center gap-1 mt-1">
          <svg className="w-3 h-3 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M17.657 16.657L13.414 20.9a1.998 1.998 0 01-2.827 0l-4.244-4.243a8 8 0 1111.314 0z" />
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 11a3 3 0 11-6 0 3 3 0 016 0z" />
          </svg>
          {lead.city}
        </p>
      )}
      {lead.sector && (
        <p className="text-xs text-gray-500 flex items-center gap-1 mt-0.5">
          <svg className="w-3 h-3 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 21V5a2 2 0 00-2-2H7a2 2 0 00-2 2v16m14 0h2m-2 0h-5m-9 0H3m2 0h5M9 7h1m-1 4h1m4-4h1m-1 4h1m-5 10v-5a1 1 0 011-1h2a1 1 0 011 1v5m-4 0h4" />
          </svg>
          {lead.sector}
        </p>
      )}
    </div>
  )
}

interface ColumnProps {
  status: PipelineStatus
  leads: Lead[]
  onDragStart: (e: React.DragEvent, leadId: string) => void
  onDrop: (e: React.DragEvent, status: PipelineStatus) => void
}

function KanbanColumn({ status, leads, onDragStart, onDrop }: ColumnProps) {
  const [isDragOver, setIsDragOver] = useState(false)

  const handleDragOver = (e: React.DragEvent) => {
    e.preventDefault()
    setIsDragOver(true)
  }

  const handleDragLeave = () => {
    setIsDragOver(false)
  }

  const handleDrop = (e: React.DragEvent) => {
    setIsDragOver(false)
    onDrop(e, status)
  }

  return (
    <div
      className={`flex flex-col min-w-[220px] w-[220px] rounded-xl border ${COLUMN_COLORS[status]} transition-colors ${isDragOver ? 'ring-2 ring-indigo-400' : ''}`}
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onDrop={handleDrop}
    >
      {/* Column header */}
      <div className={`flex items-center justify-between px-3 py-2 rounded-t-xl ${COLUMN_HEADER_COLORS[status]}`}>
        <span className="text-xs font-semibold uppercase tracking-wide">
          {PIPELINE_STATUS_LABELS[status]}
        </span>
        <span className="text-xs font-bold bg-white/60 rounded-full px-2 py-0.5 ml-2">
          {leads.length}
        </span>
      </div>

      {/* Cards */}
      <div className="flex-1 p-2 space-y-2 min-h-[120px]">
        {leads.map((lead) => (
          <KanbanCard key={lead.id} lead={lead} onDragStart={onDragStart} />
        ))}
        {leads.length === 0 && (
          <div className="flex items-center justify-center h-16 text-xs text-gray-400 italic">
            Sleep hier naartoe
          </div>
        )}
      </div>
    </div>
  )
}

export default function PipelinePage() {
  const { showToast } = useToast()
  const navigate = useNavigate()
  const [leads, setLeads] = useState<Lead[]>([])
  const [isLoading, setIsLoading] = useState(false)
  const dragLeadId = useRef<string | null>(null)

  const load = async () => {
    setIsLoading(true)
    try {
      const data = await fetchLeadsByPipeline()
      setLeads(data)
    } catch {
      showToast('Leads laden mislukt', 'error')
    } finally {
      setIsLoading(false)
    }
  }

  useEffect(() => {
    load()
  }, [])

  const handleDragStart = (e: React.DragEvent, leadId: string) => {
    dragLeadId.current = leadId
    e.dataTransfer.effectAllowed = 'move'
  }

  const handleDrop = async (e: React.DragEvent, targetStatus: PipelineStatus) => {
    e.preventDefault()
    const leadId = dragLeadId.current
    if (!leadId) return

    const lead = leads.find((l) => l.id === leadId)
    if (!lead) return
    if (lead.pipelineStatus === targetStatus) return

    // Optimistic update
    setLeads((prev) =>
      prev.map((l) => l.id === leadId ? { ...l, pipelineStatus: targetStatus } : l)
    )

    try {
      await updateLeadPipelineStatus(leadId, targetStatus)
    } catch {
      showToast('Status bijwerken mislukt', 'error')
      // Revert
      setLeads((prev) =>
        prev.map((l) => l.id === leadId ? { ...l, pipelineStatus: lead.pipelineStatus } : l)
      )
    }

    dragLeadId.current = null
  }

  const grouped = PIPELINE_STATUSES.reduce<Record<PipelineStatus, Lead[]>>((acc, status) => {
    acc[status] = leads.filter((l) => (l.pipelineStatus ?? 'New') === status)
    return acc
  }, {} as Record<PipelineStatus, Lead[]>)

  return (
    <div className="flex flex-col gap-4 h-full">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-2xl font-bold text-gray-900">Pipeline</h2>
          <p className="text-sm text-gray-500 mt-0.5">
            {leads.length.toLocaleString('nl-NL')} leads in de pipeline
          </p>
        </div>
        <div className="flex items-center gap-2">
          {/* View toggle */}
          <div className="flex items-center border border-gray-300 rounded-lg overflow-hidden">
            <button
              onClick={() => navigate('/leads')}
              className="px-3 py-2 text-sm text-gray-600 hover:bg-gray-100"
              title="Lijstweergave"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 6h16M4 10h16M4 14h16M4 18h16" />
              </svg>
            </button>
            <button
              className="px-3 py-2 text-sm bg-indigo-600 text-white font-medium border-l border-gray-300"
              title="Pipeline weergave"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 17V7m0 10a2 2 0 01-2 2H5a2 2 0 01-2-2V7a2 2 0 012-2h2a2 2 0 012 2m0 10a2 2 0 002 2h2a2 2 0 002-2M9 7a2 2 0 012-2h2a2 2 0 012 2m0 10V7m0 10a2 2 0 002 2h2a2 2 0 002-2V7a2 2 0 00-2-2h-2a2 2 0 00-2 2" />
              </svg>
            </button>
          </div>
          <button
            onClick={load}
            disabled={isLoading}
            className="text-sm px-3 py-1.5 border border-gray-300 rounded-lg hover:bg-gray-50 disabled:opacity-50 flex items-center gap-1.5"
          >
            <svg className={`w-4 h-4 ${isLoading ? 'animate-spin' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
            </svg>
            Vernieuwen
          </button>
        </div>
      </div>

      {/* Kanban board */}
      {isLoading && leads.length === 0 ? (
        <div className="flex items-center justify-center flex-1">
          <div className="flex items-center gap-2 text-gray-500">
            <svg className="animate-spin h-5 w-5 text-indigo-500" fill="none" viewBox="0 0 24 24">
              <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
              <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
            </svg>
            <span className="text-sm">Laden…</span>
          </div>
        </div>
      ) : (
        <div className="flex gap-3 overflow-x-auto pb-4 flex-1">
          {PIPELINE_STATUSES.map((status) => (
            <KanbanColumn
              key={status}
              status={status}
              leads={grouped[status]}
              onDragStart={handleDragStart}
              onDrop={handleDrop}
            />
          ))}
        </div>
      )}
    </div>
  )
}

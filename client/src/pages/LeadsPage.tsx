import { useState, useEffect, useCallback, useMemo, useRef } from 'react'
import { useSearchParams } from 'react-router-dom'
import { fetchLeads, fetchLeadStats, importLeads, enrichLeads } from '../api/leads'
import type { Lead, LeadFilter, LeadStats } from '../api/leads'
import { useLeadSelection } from '../hooks/useLeadSelection'
import { useToast } from '../components/Toast'
import LeadsTable from '../components/leads/LeadsTable'
import FilterBar from '../components/leads/FilterBar'
import LeadsToolbar from '../components/leads/LeadsToolbar'
import EnrichmentProgress from '../components/leads/EnrichmentProgress'

const PAGE_SIZE = 50

function buildInitialFilter(searchParams: URLSearchParams): LeadFilter {
  const filter: LeadFilter = { page: 1, pageSize: PAGE_SIZE }
  const enriched = searchParams.get('enriched')
  if (enriched !== null) filter.enriched = enriched === 'true'
  const enrichedAfter = searchParams.get('enrichedAfter')
  if (enrichedAfter) filter.enrichedAfter = enrichedAfter
  const enrichedBefore = searchParams.get('enrichedBefore')
  if (enrichedBefore) filter.enrichedBefore = enrichedBefore
  const page = searchParams.get('page')
  if (page) filter.page = parseInt(page)
  const sortBy = searchParams.get('sortBy')
  if (sortBy) filter.sortBy = sortBy
  const sortDesc = searchParams.get('sortDesc')
  if (sortDesc) filter.sortDesc = sortDesc === 'true'
  return filter
}

export default function LeadsPage() {
  const [searchParams] = useSearchParams()
  const { showToast } = useToast()

  const [filter, setFilter] = useState<LeadFilter>(() => buildInitialFilter(searchParams))
  const [leads, setLeads] = useState<Lead[]>([])
  const [total, setTotal] = useState(0)
  const [stats, setStats] = useState<LeadStats | null>(null)
  const [isLoading, setIsLoading] = useState(false)
  const [activeJobId, setActiveJobId] = useState<string | null>(null)
  const [isEnriching, setIsEnriching] = useState(false)

  const tableContainerRef = useRef<HTMLDivElement>(null)

  const orderedIds = useMemo(() => leads.map((l) => l.id), [leads])

  const {
    selectedIds,
    handleRowClick,
    handleRowMouseDown,
    clearSelection,
    selectAll,
    isDragging,
    dragRange,
  } = useLeadSelection(orderedIds)

  // Load leads
  const loadLeads = useCallback(async (f: LeadFilter) => {
    setIsLoading(true)
    try {
      const data = await fetchLeads(f)
      setLeads(data.items)
      setTotal(data.total)
    } catch (err) {
      showToast('Fout bij laden van leads', 'error')
    } finally {
      setIsLoading(false)
    }
  }, [showToast])

  // Load stats
  const loadStats = useCallback(async () => {
    try {
      const data = await fetchLeadStats()
      setStats(data)
    } catch {
      // Stats are non-critical
    }
  }, [])

  useEffect(() => {
    loadLeads(filter)
  }, [filter, loadLeads])

  useEffect(() => {
    loadStats()
  }, [loadStats])

  // Auto-scroll during drag
  useEffect(() => {
    if (!isDragging) return
    const container = tableContainerRef.current
    if (!container) return

    const SCROLL_ZONE = 80
    const SCROLL_SPEED = 8

    const onMouseMove = (e: MouseEvent) => {
      const rect = container.getBoundingClientRect()
      const distFromBottom = rect.bottom - e.clientY
      const distFromTop = e.clientY - rect.top

      if (distFromBottom < SCROLL_ZONE) {
        container.scrollTop += SCROLL_SPEED
      } else if (distFromTop < SCROLL_ZONE) {
        container.scrollTop -= SCROLL_SPEED
      }
    }

    document.addEventListener('mousemove', onMouseMove)
    return () => document.removeEventListener('mousemove', onMouseMove)
  }, [isDragging])

  // Keyboard: Escape to clear selection
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        clearSelection()
      }
    }
    document.addEventListener('keydown', onKeyDown)
    return () => document.removeEventListener('keydown', onKeyDown)
  }, [clearSelection])

  const handleSort = useCallback((col: string) => {
    setFilter((prev) => ({
      ...prev,
      sortBy: col,
      sortDesc: prev.sortBy === col ? !prev.sortDesc : false,
      page: 1,
    }))
  }, [])

  const handleFilterChange = useCallback((newFilter: LeadFilter) => {
    setFilter(newFilter)
    clearSelection()
  }, [clearSelection])

  const handleImport = useCallback(async (file: File) => {
    try {
      showToast('Bezig met importeren…', 'info')
      await importLeads(file)
      showToast('Import geslaagd!', 'success')
      await loadLeads(filter)
      await loadStats()
    } catch {
      showToast('Import mislukt', 'error')
    }
  }, [filter, loadLeads, loadStats, showToast])

  const handleEnrich = useCallback(async () => {
    if (selectedIds.size === 0) return

    const confirmed = window.confirm(
      `Weet je zeker dat je ${selectedIds.size} lead${selectedIds.size !== 1 ? 's' : ''} wil verrijken?`
    )
    if (!confirmed) return

    setIsEnriching(true)
    try {
      const { jobId } = await enrichLeads(Array.from(selectedIds))
      setActiveJobId(jobId)
    } catch {
      showToast('Verrijking starten mislukt', 'error')
      setIsEnriching(false)
    }
  }, [selectedIds, showToast])

  const handleEnrichmentComplete = useCallback(async () => {
    setActiveJobId(null)
    setIsEnriching(false)
    clearSelection()
    showToast('Verrijking voltooid!', 'success')
    await loadLeads(filter)
    await loadStats()
  }, [filter, loadLeads, loadStats, clearSelection, showToast])

  const totalPages = Math.ceil(total / (filter.pageSize ?? PAGE_SIZE))
  const currentPage = filter.page ?? 1

  return (
    <div className="flex flex-col gap-4 h-full">
      {/* Page header */}
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-2xl font-bold text-gray-900">Leads</h2>
          {!isLoading && (
            <p className="text-sm text-gray-500 mt-0.5">
              {total.toLocaleString('nl-NL')} leads in totaal
            </p>
          )}
        </div>
      </div>

      {/* Filter bar */}
      <FilterBar filter={filter} onChange={handleFilterChange} stats={stats} />

      {/* Toolbar */}
      <LeadsToolbar
        selectedCount={selectedIds.size}
        onEnrich={handleEnrich}
        onImport={handleImport}
        onClearSelection={clearSelection}
        isEnriching={isEnriching}
      />

      {/* Table */}
      <div
        ref={tableContainerRef}
        className="bg-white border border-gray-200 rounded-lg overflow-hidden flex-1 overflow-y-auto relative"
      >
        {isLoading && (
          <div className="absolute inset-0 bg-white/60 z-10 flex items-center justify-center">
            <div className="flex items-center gap-2 text-gray-500">
              <svg className="animate-spin h-5 w-5 text-indigo-500" fill="none" viewBox="0 0 24 24">
                <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
              </svg>
              <span className="text-sm">Laden…</span>
            </div>
          </div>
        )}

        <LeadsTable
          leads={leads}
          selectedIds={selectedIds}
          orderedIds={orderedIds}
          onRowMouseDown={handleRowMouseDown}
          onRowClick={handleRowClick}
          sortBy={filter.sortBy ?? ''}
          sortDesc={filter.sortDesc ?? false}
          onSort={handleSort}
          isDragging={isDragging}
          onSelectAll={() => selectAll(orderedIds)}
          onClearSelection={clearSelection}
        />
      </div>

      {/* Pagination */}
      {totalPages > 1 && (
        <div className="flex items-center justify-between text-sm text-gray-600">
          <span>
            Pagina {currentPage} van {totalPages} ({total.toLocaleString('nl-NL')} leads)
          </span>
          <div className="flex gap-2">
            <button
              onClick={() => setFilter((f) => ({ ...f, page: Math.max(1, (f.page ?? 1) - 1) }))}
              disabled={currentPage === 1}
              className="px-3 py-1 border border-gray-300 rounded disabled:opacity-40 hover:bg-gray-50"
            >
              Vorige
            </button>
            {/* Page number buttons (max 5 around current) */}
            {Array.from({ length: Math.min(5, totalPages) }, (_, i) => {
              const startPage = Math.max(1, Math.min(currentPage - 2, totalPages - 4))
              const page = startPage + i
              if (page > totalPages) return null
              return (
                <button
                  key={page}
                  onClick={() => setFilter((f) => ({ ...f, page }))}
                  className={`px-3 py-1 border rounded ${
                    page === currentPage
                      ? 'bg-indigo-600 text-white border-indigo-600'
                      : 'border-gray-300 hover:bg-gray-50'
                  }`}
                >
                  {page}
                </button>
              )
            })}
            <button
              onClick={() =>
                setFilter((f) => ({ ...f, page: Math.min(totalPages, (f.page ?? 1) + 1) }))
              }
              disabled={currentPage === totalPages}
              className="px-3 py-1 border border-gray-300 rounded disabled:opacity-40 hover:bg-gray-50"
            >
              Volgende
            </button>
          </div>
        </div>
      )}

      {/* Drag range indicator (subtle) */}
      {dragRange && (
        <div className="fixed bottom-4 left-1/2 -translate-x-1/2 bg-indigo-700 text-white text-xs px-3 py-1.5 rounded-full shadow-lg z-50 pointer-events-none">
          {selectedIds.size} leads geselecteerd
        </div>
      )}

      {/* Enrichment progress panel */}
      <EnrichmentProgress jobId={activeJobId} onComplete={handleEnrichmentComplete} />
    </div>
  )
}

import React, { useState, useEffect, useCallback, useMemo, useRef } from 'react'
import { useSearchParams, Link, useNavigate } from 'react-router-dom'
import { fetchLeads, fetchLeadStats, importLeads, enrichLeads, exportLeads } from '../api/leads'
import type { Lead, LeadFilter, LeadStats, CsvImportResult } from '../api/leads'
import { useLeadSelection } from '../hooks/useLeadSelection'
import { useToast } from '../components/Toast'
import JobDialog from '../components/leads/JobDialog'
import HuntPanel from '../components/leads/HuntPanel'
import HuntJobView from '../components/leads/HuntJobView'
import { errorMessage, startAgentReview, startVerify } from '../api/hunt'
import LeadsTable from '../components/leads/LeadsTable'
import FilterBar from '../components/leads/FilterBar'
import LeadsToolbar from '../components/leads/LeadsToolbar'
import EnrichmentProgress from '../components/leads/EnrichmentProgress'
import LeadDetailPanel from '../components/leads/LeadDetailPanel'
import CreateLeadForm from '../components/leads/CreateLeadForm'
import CsvImportDropzone from '../components/leads/CsvImportDropzone'
import BulkDraftDialog from '../components/messages/BulkDraftDialog'
import OrganizationFilter from '../components/organizations/OrganizationFilter'
import { useOrganizations } from '../contexts/OrganizationContext'

const DESC_FIRST = new Set(['createdAt', 'updatedAt', 'importedAt', 'enrichedAt', 'salesPriorityScore'])

const PAGE_SIZE = 50

function buildInitialFilter(searchParams: URLSearchParams): LeadFilter {
  const filter: LeadFilter = { page: 1, pageSize: PAGE_SIZE, sortBy: 'salesPriorityScore', sortDesc: true }
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
  const hasOwner = searchParams.get('hasOwner')
  if (hasOwner !== null) filter.hasOwner = hasOwner === 'true'
  const hasLinkedIn = searchParams.get('hasLinkedIn')
  if (hasLinkedIn !== null) filter.hasLinkedIn = hasLinkedIn === 'true'
  const priorityLabel = searchParams.get('priorityLabel')
  if (priorityLabel) filter.priorityLabel = priorityLabel
  const pipelineStatus = searchParams.get('pipelineStatus')
  if (pipelineStatus) filter.pipelineStatus = pipelineStatus
  return filter
}

/**
 * The leads list. Changing the organisation filter starts it afresh (page 1, no selection): the list shown is always the one the
 * filter asks for.
 */
export default function LeadsPage() {
  const { filterParam } = useOrganizations()
  return <LeadsPageContent key={filterParam ?? 'all'} />
}

function LeadsPageContent() {
  const { filterParam, defaultOrgId, rememberOrg } = useOrganizations()
  const [searchParams] = useSearchParams()
  const { showToast } = useToast()
  const navigate = useNavigate()

  const [filter, setFilter] = useState<LeadFilter>(() => buildInitialFilter(searchParams))
  const [leads, setLeads] = useState<Lead[]>([])
  const [total, setTotal] = useState(0)
  const [stats, setStats] = useState<LeadStats | null>(null)
  const [isLoading, setIsLoading] = useState(false)
  const [activeJobId, setActiveJobId] = useState<string | null>(null)
  const [isEnriching, setIsEnriching] = useState(false)
  const [detailLead, setDetailLead] = useState<Lead | null>(null)
  const [showBulkDraft, setShowBulkDraft] = useState(false)
  const [showCreateForm, setShowCreateForm] = useState(false)
  const [showCsvImport, setShowCsvImport] = useState(false)
  const [showHunt, setShowHunt] = useState(false)
  const [verifyJobId, setVerifyJobId] = useState<string | null>(null)
  const [reviewJobId, setReviewJobId] = useState<string | null>(null)
  const [onboardingDismissed, setOnboardingDismissed] = useState(
    () => localStorage.getItem('lm_onboarding_dismissed') === '1'
  )

  const tableContainerRef = useRef<HTMLDivElement>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)

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
      const data = await fetchLeads({ ...f, organizationId: filterParam })
      setLeads(data.items)
      setTotal(data.total)
    } catch (err) {
      showToast('Could not load leads', 'error')
    } finally {
      setIsLoading(false)
    }
  }, [showToast, filterParam])

  // Load stats
  const loadStats = useCallback(async () => {
    try {
      const data = await fetchLeadStats(filterParam)
      setStats(data)
    } catch {
      // Stats are non-critical
    }
  }, [filterParam])

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
      // Dates and the score start with the newest / highest on top; text columns start A to Z.
      sortDesc: prev.sortBy === col ? !prev.sortDesc : DESC_FIRST.has(col),
      page: 1,
    }))
  }, [])

  const handleFilterChange = useCallback((newFilter: LeadFilter) => {
    setFilter(newFilter)
    clearSelection()
  }, [clearSelection])

  const handleImport = useCallback(async (file: File) => {
    if (!defaultOrgId) {
      showToast('Choose an organisation first: pick one in the organisation filter above the list.', 'error')
      return
    }
    try {
      showToast('Importing…', 'info')
      await importLeads(file, defaultOrgId)
      rememberOrg(defaultOrgId)
      showToast('Import successful!', 'success')
      await loadLeads(filter)
      await loadStats()
    } catch (err) {
      showToast(errorMessage(err, 'Could not import'), 'error')
    }
  }, [filter, loadLeads, loadStats, showToast, defaultOrgId, rememberOrg])

  const handleOpenFilePicker = useCallback(() => {
    fileInputRef.current?.click()
  }, [])

  const handleFileChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (file) {
      handleImport(file)
      e.target.value = ''
    }
  }, [handleImport])

  const dismissOnboarding = useCallback(() => {
    localStorage.setItem('lm_onboarding_dismissed', '1')
    setOnboardingDismissed(true)
  }, [])

  const handleEnrich = useCallback(async () => {
    if (selectedIds.size === 0) return

    const confirmed = window.confirm(
      `Are you sure you want to enrich ${selectedIds.size} lead${selectedIds.size !== 1 ? 's' : ''}?`
    )
    if (!confirmed) return

    setIsEnriching(true)
    try {
      const { jobId } = await enrichLeads(Array.from(selectedIds))
      setActiveJobId(jobId)
    } catch (err) {
      showToast(errorMessage(err, 'Could not start enrichment'), 'error')
      setIsEnriching(false)
    }
  }, [selectedIds, showToast])

  const handleVerify = useCallback(async () => {
    if (selectedIds.size === 0) return
    const confirmed = window.confirm(
      `Verify ${selectedIds.size} lead${selectedIds.size !== 1 ? 's' : ''} against their own websites? Values that cannot be proven are removed and the score is recomputed.`
    )
    if (!confirmed) return
    try {
      const { jobId } = await startVerify(Array.from(selectedIds))
      setVerifyJobId(jobId)
    } catch (err) {
      showToast(errorMessage(err, 'Could not start the verification'), 'error')
    }
  }, [selectedIds, showToast])

  const handleAskAgent = useCallback(async () => {
    if (selectedIds.size === 0) return
    if (selectedIds.size > 5) {
      showToast('Ask for a review of at most 5 leads at a time.', 'error')
      return
    }
    const confirmed = window.confirm(
      `Ask a Jengo agent to research ${selectedIds.size} lead${selectedIds.size !== 1 ? 's' : ''} on the web? It uses public information only and runs in the background (a JengoWork task is created). The server verifies everything it finds; nothing you typed is overwritten. Check the GDPR rules and the Do-Not-Call register before you contact anyone.`
    )
    if (!confirmed) return
    try {
      const { jobId } = await startAgentReview(Array.from(selectedIds))
      setReviewJobId(jobId)
    } catch (err) {
      showToast(errorMessage(err, 'Could not start the agent review'), 'error')
    }
  }, [selectedIds, showToast])

  const handleExport = useCallback(async () => {
    if (selectedIds.size === 0) return

    try {
      const ids = Array.from(selectedIds).join(',')
      const token = localStorage.getItem('lm_token')
      const baseUrl = import.meta.env.VITE_API_URL ?? 'http://localhost:5000'
      const exportUrl = `${baseUrl}/api/export/leads?ids=${ids}`

      // Fetch HTML with auth header
      const response = await fetch(exportUrl, {
        headers: {
          'Authorization': `Bearer ${token}`
        }
      })

      if (!response.ok) {
        throw new Error('Export failed')
      }

      const html = await response.text()

      // Create blob and open in new window
      const blob = new Blob([html], { type: 'text/html' })
      const url = URL.createObjectURL(blob)
      window.open(url, '_blank')

      // Clean up blob URL after a delay
      setTimeout(() => URL.revokeObjectURL(url), 100)

      showToast(`${selectedIds.size} lead(s) exported`, 'success')
    } catch {
      showToast('Could not export', 'error')
    }
  }, [selectedIds, showToast])

  const handleExportCsv = useCallback(async () => {
    try {
      await exportLeads('csv', { ...filter, organizationId: filterParam })
      showToast('CSV export downloaded', 'success')
    } catch {
      showToast('Could not export CSV', 'error')
    }
  }, [filter, showToast, filterParam])

  const handleExportExcel = useCallback(async () => {
    try {
      await exportLeads('xlsx', { ...filter, organizationId: filterParam })
      showToast('Excel export downloaded', 'success')
    } catch {
      showToast('Could not export Excel', 'error')
    }
  }, [filter, showToast, filterParam])

  const handleCsvImportSuccess = useCallback(async (result: CsvImportResult) => {
    const enrichMsg = result.enrichmentJobId ? ' Enrichment started.' : ''
    showToast(`${result.created} leads imported!${enrichMsg}`, 'success')
    if (result.enrichmentJobId) {
      setActiveJobId(result.enrichmentJobId)
      setIsEnriching(true)
    }
    await loadLeads(filter)
    await loadStats()
  }, [filter, loadLeads, loadStats, showToast])

  const handleEnrichmentComplete = useCallback(async () => {
    setActiveJobId(null)
    setIsEnriching(false)
    clearSelection()
    showToast('Enrichment complete!', 'success')
    await loadLeads(filter)
    await loadStats()
  }, [filter, loadLeads, loadStats, clearSelection, showToast])

  const handleCreateLeadSuccess = useCallback(async () => {
    setShowCreateForm(false)
    await loadLeads(filter)
    await loadStats()
  }, [filter, loadLeads, loadStats])

  const totalPages = Math.ceil(total / (filter.pageSize ?? PAGE_SIZE))
  const currentPage = filter.page ?? 1

  const showOnboarding = !onboardingDismissed && !isLoading && total === 0

  return (
    <div className="flex flex-col gap-4 h-full">
      {/* Hidden file input for empty state CTA */}
      <input
        ref={fileInputRef}
        type="file"
        accept=".xlsx,.xls"
        onChange={handleFileChange}
        className="hidden"
      />

      {/* Onboarding banner (first visit, no leads) */}
      {showOnboarding && (
        <div className="bg-indigo-600 rounded-xl p-5 text-white relative">
          <button
            onClick={dismissOnboarding}
            className="absolute top-3 right-3 text-indigo-200 hover:text-white transition-colors"
            aria-label="Close"
          >
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
          <h3 className="font-semibold text-lg mb-3">Welcome to Clients!</h3>
          <div className="flex flex-col sm:flex-row gap-4">
            <div className="flex items-start gap-3">
              <span className="flex-shrink-0 w-6 h-6 rounded-full bg-indigo-500 flex items-center justify-center text-xs font-bold">1</span>
              <div>
                <Link to="/profile" className="font-medium underline hover:no-underline">Create your company profile</Link>
                <p className="text-indigo-200 text-xs mt-0.5">So the AI knows which leads fit you</p>
              </div>
            </div>
            <div className="flex items-start gap-3">
              <span className="flex-shrink-0 w-6 h-6 rounded-full bg-indigo-500 flex items-center justify-center text-xs font-bold">2</span>
              <div>
                <button onClick={() => setShowHunt(true)} className="font-medium underline hover:no-underline text-left">Find new leads</button>
                <p className="text-indigo-200 text-xs mt-0.5">Real companies, scored from their own websites</p>
              </div>
            </div>
            <div className="flex items-start gap-3">
              <span className="flex-shrink-0 w-6 h-6 rounded-full bg-indigo-500 flex items-center justify-center text-xs font-bold">3</span>
              <div>
                <button onClick={handleOpenFilePicker} className="font-medium underline hover:no-underline text-left">Import leads from Excel</button>
                <p className="text-indigo-200 text-xs mt-0.5">Upload your existing lead list</p>
              </div>
            </div>
          </div>
        </div>
      )}

      <OrganizationFilter />

      {/* Page header */}
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <h2 className="text-2xl font-bold text-gray-900">Leads</h2>
          {!isLoading && (
            <p className="text-sm text-gray-500 mt-0.5">
              {total.toLocaleString('en-GB')} {total === 1 ? 'lead' : 'leads'} in total
            </p>
          )}
        </div>
        <div className="flex items-center gap-2">
          {/* View toggle */}
          <div className="flex items-center border border-gray-300 rounded-lg overflow-hidden">
            <button
              className="px-3 py-2 text-sm bg-indigo-600 text-white font-medium"
              title="List view"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 6h16M4 10h16M4 14h16M4 18h16" />
              </svg>
            </button>
            <button
              onClick={() => navigate('/leads/pipeline')}
              className="px-3 py-2 text-sm text-gray-600 hover:bg-gray-100 border-l border-gray-300"
              title="Pipeline view"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 17V7m0 10a2 2 0 01-2 2H5a2 2 0 01-2-2V7a2 2 0 012-2h2a2 2 0 012 2m0 10a2 2 0 002 2h2a2 2 0 002-2M9 7a2 2 0 012-2h2a2 2 0 012 2m0 10V7m0 10a2 2 0 002 2h2a2 2 0 002-2V7a2 2 0 00-2-2h-2a2 2 0 00-2 2" />
              </svg>
            </button>
          </div>
          <button
            onClick={() => setShowCreateForm(true)}
            className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 flex items-center gap-2"
          >
            <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
            </svg>
            New lead
          </button>
        </div>
      </div>

      {/* Filter bar */}
      <FilterBar filter={filter} onChange={handleFilterChange} stats={stats} />

      {/* Toolbar */}
      <LeadsToolbar
        selectedCount={selectedIds.size}
        onEnrich={handleEnrich}
        onVerify={handleVerify}
        onAskAgent={handleAskAgent}
        onDraftMessages={() => setShowBulkDraft(true)}
        onFind={() => setShowHunt(true)}
        onExport={handleExport}
        onExportCsv={handleExportCsv}
        onExportExcel={handleExportExcel}
        onImport={handleImport}
        onImportCsv={() => setShowCsvImport(true)}
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
              <span className="text-sm">Loading…</span>
            </div>
          </div>
        )}

        <LeadsTable
          leads={leads}
          selectedIds={selectedIds}
          orderedIds={orderedIds}
          onRowMouseDown={handleRowMouseDown}
          onRowClick={handleRowClick}
          onRowOpen={setDetailLead}
          sortBy={filter.sortBy ?? ''}
          sortDesc={filter.sortDesc ?? false}
          onSort={handleSort}
          isDragging={isDragging}
          onSelectAll={() => selectAll(orderedIds)}
          onClearSelection={clearSelection}
          onImport={handleOpenFilePicker}
        />
      </div>

      {/* Pagination, always visible */}
      {!isLoading && total > 0 && (
        <div className="flex items-center justify-between text-sm text-gray-600 flex-wrap gap-2">
          <div className="flex items-center gap-3">
            <span>
              {totalPages > 1
                ? `Page ${currentPage} of ${totalPages} (${total.toLocaleString('en-GB')} ${total === 1 ? 'lead' : 'leads'})`
                : `${total.toLocaleString('en-GB')} ${total === 1 ? 'lead' : 'leads'}`}
            </span>
            <label className="flex items-center gap-1.5 text-xs text-gray-500">
              Per page:
              <select
                value={filter.pageSize ?? PAGE_SIZE}
                onChange={(e) => setFilter((f) => ({ ...f, pageSize: Number(e.target.value), page: 1 }))}
                className="border border-gray-300 rounded px-1.5 py-0.5 text-xs focus:outline-none focus:ring-1 focus:ring-indigo-400"
              >
                {[25, 50, 100, 250].map((n) => <option key={n} value={n}>{n}</option>)}
              </select>
            </label>
          </div>
          {totalPages > 1 && (
            <div className="flex gap-2">
              <button
                onClick={() => setFilter((f) => ({ ...f, page: Math.max(1, (f.page ?? 1) - 1) }))}
                disabled={currentPage === 1}
                className="px-3 py-1 border border-gray-300 rounded disabled:opacity-40 hover:bg-gray-50"
              >
                Previous
              </button>
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
                Next
              </button>
            </div>
          )}
        </div>
      )}

      {/* Drag range indicator (subtle) */}
      {dragRange && (
        <div className="fixed bottom-4 left-1/2 -translate-x-1/2 bg-indigo-700 text-white text-xs px-3 py-1.5 rounded-full shadow-lg z-50 pointer-events-none">
          {selectedIds.size} {selectedIds.size === 1 ? 'lead' : 'leads'} selected
        </div>
      )}

      {/* Enrichment progress panel */}
      <EnrichmentProgress jobId={activeJobId} onComplete={handleEnrichmentComplete} />

      {/* Lead detail panel */}
      <LeadDetailPanel lead={detailLead} onClose={() => setDetailLead(null)} onLeadUpdated={(l) => { setDetailLead(l); loadLeads(filter) }} />

      {showBulkDraft && <BulkDraftDialog leadIds={Array.from(selectedIds)} onClose={() => setShowBulkDraft(false)} />}

      {/* Create lead form modal */}
      {showCreateForm && (
        <CreateLeadForm
          onSuccess={handleCreateLeadSuccess}
          onCancel={() => setShowCreateForm(false)}
        />
      )}

      {/* Find new leads (evidence based hunt) */}
      {showHunt && (
        <JobDialog title="Find new leads" onClose={() => { setShowHunt(false); loadLeads(filter); loadStats() }}>
          <HuntPanel onFinished={() => { loadLeads(filter); loadStats() }} />
        </JobDialog>
      )}

      {/* Verify and rescore selected leads */}
      {verifyJobId && (
        <JobDialog title="Verify & rescore" onClose={() => { setVerifyJobId(null); loadLeads(filter); loadStats() }}>
          <HuntJobView jobId={verifyJobId} onFinished={() => { loadLeads(filter); loadStats() }} />
        </JobDialog>
      )}

      {/* Agent review of selected leads */}
      {reviewJobId && (
        <JobDialog title="Agent review" onClose={() => { setReviewJobId(null); loadLeads(filter); loadStats() }}>
          <HuntJobView jobId={reviewJobId} onFinished={() => { loadLeads(filter); loadStats() }} />
        </JobDialog>
      )}

      {/* CSV import modal */}
      {showCsvImport && (
        <CsvImportDropzone
          onSuccess={handleCsvImportSuccess}
          onCancel={() => setShowCsvImport(false)}
        />
      )}
    </div>
  )
}

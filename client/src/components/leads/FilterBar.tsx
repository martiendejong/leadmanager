import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import type { LeadFilter, LeadStats, Assignee } from '../../api/leads'
import { getAssignees } from '../../api/leads'

interface FilterBarProps {
  filter: LeadFilter
  onChange: (filter: LeadFilter) => void
  stats: LeadStats | null
}

function daysAgo(days: number): string {
  const d = new Date()
  d.setDate(d.getDate() - days)
  return d.toISOString().split('T')[0]
}

function isFilterActive(filter: LeadFilter): boolean {
  return (
    filter.enriched !== undefined ||
    !!filter.enrichedAfter ||
    !!filter.enrichedBefore ||
    !!filter.assignedToUserId
  )
}

export default function FilterBar({ filter, onChange, stats }: FilterBarProps) {
  const [searchParams, setSearchParams] = useSearchParams()
  const [assignees, setAssignees] = useState<Assignee[]>([])

  useEffect(() => {
    getAssignees().then(setAssignees).catch(() => {/* non-critical */})
  }, [])

  // Sync filter → URL
  useEffect(() => {
    const params = new URLSearchParams()
    if (filter.enriched !== undefined) params.set('enriched', String(filter.enriched))
    if (filter.enrichedAfter) params.set('enrichedAfter', filter.enrichedAfter)
    if (filter.enrichedBefore) params.set('enrichedBefore', filter.enrichedBefore)
    if (filter.page && filter.page > 1) params.set('page', String(filter.page))
    if (filter.sortBy) params.set('sortBy', filter.sortBy)
    if (filter.sortDesc) params.set('sortDesc', 'true')
    setSearchParams(params, { replace: true })
  }, [filter, setSearchParams])

  // Read initial filter from URL (once on mount)
  useEffect(() => {
    const enrichedParam = searchParams.get('enriched')
    const enrichedAfter = searchParams.get('enrichedAfter')
    const enrichedBefore = searchParams.get('enrichedBefore')
    const page = searchParams.get('page')
    const sortBy = searchParams.get('sortBy')
    const sortDesc = searchParams.get('sortDesc')

    const initial: LeadFilter = {}
    if (enrichedParam !== null) initial.enriched = enrichedParam === 'true'
    if (enrichedAfter) initial.enrichedAfter = enrichedAfter
    if (enrichedBefore) initial.enrichedBefore = enrichedBefore
    if (page) initial.page = parseInt(page)
    if (sortBy) initial.sortBy = sortBy
    if (sortDesc) initial.sortDesc = sortDesc === 'true'

    if (Object.keys(initial).length > 0) {
      onChange({ ...filter, ...initial })
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const setEnrichedFilter = (enriched: boolean | undefined) => {
    onChange({ ...filter, enriched, page: 1 })
  }

  const setDateFilter = (key: 'enrichedAfter' | 'enrichedBefore', value: string) => {
    onChange({ ...filter, [key]: value || undefined, page: 1 })
  }

  const clearFilters = () => {
    onChange({ page: 1, pageSize: filter.pageSize, sortBy: filter.sortBy, sortDesc: filter.sortDesc })
  }

  const setAssigneeFilter = (userId: string) => {
    onChange({ ...filter, assignedToUserId: userId || undefined, page: 1 })
  }

  const activeTab = filter.enriched === true ? 'enriched' : filter.enriched === false ? 'not-enriched' : 'all'

  return (
    <div className="bg-white border border-gray-200 rounded-lg p-4 space-y-4">
      {/* Enrichment toggle tabs */}
      <div className="flex items-center gap-1">
        <button
          onClick={() => setEnrichedFilter(undefined)}
          className={`px-3 py-1.5 text-sm rounded-md font-medium transition-colors ${
            activeTab === 'all'
              ? 'bg-indigo-100 text-indigo-700'
              : 'text-gray-600 hover:bg-gray-100'
          }`}
        >
          Alle ({stats?.total ?? '…'})
        </button>
        <button
          onClick={() => setEnrichedFilter(true)}
          className={`px-3 py-1.5 text-sm rounded-md font-medium transition-colors ${
            activeTab === 'enriched'
              ? 'bg-green-100 text-green-700'
              : 'text-gray-600 hover:bg-gray-100'
          }`}
        >
          Verrijkt ({stats?.enriched ?? '…'})
        </button>
        <button
          onClick={() => setEnrichedFilter(false)}
          className={`px-3 py-1.5 text-sm rounded-md font-medium transition-colors ${
            activeTab === 'not-enriched'
              ? 'bg-gray-200 text-gray-700'
              : 'text-gray-600 hover:bg-gray-100'
          }`}
        >
          Niet verrijkt ({stats?.notEnriched ?? '…'})
        </button>

        {isFilterActive(filter) && (
          <button
            onClick={clearFilters}
            className="ml-auto text-xs text-red-500 hover:text-red-700 underline"
          >
            Filters wissen
          </button>
        )}
      </div>

      {/* Assignee filter (869ck3j4u) */}
      {assignees.length > 0 && (
        <div className="flex items-center gap-2">
          <label className="text-xs text-gray-500 font-medium whitespace-nowrap">Toegewezen aan:</label>
          <select
            value={filter.assignedToUserId ?? ''}
            onChange={(e) => setAssigneeFilter(e.target.value)}
            className="text-sm border border-gray-300 rounded-md px-2 py-1 focus:outline-none focus:ring-1 focus:ring-indigo-500"
          >
            <option value="">Iedereen</option>
            {assignees.map((a) => (
              <option key={a.userId} value={a.userId}>
                {a.displayName} ({a.leadCount})
              </option>
            ))}
          </select>
        </div>
      )}

      {/* Date filters */}
      <div className="flex flex-wrap items-end gap-4">
        <div className="flex flex-col gap-1">
          <label className="text-xs text-gray-500 font-medium">Verrijkt na:</label>
          <input
            type="date"
            value={filter.enrichedAfter ?? ''}
            onChange={(e) => setDateFilter('enrichedAfter', e.target.value)}
            className="text-sm border border-gray-300 rounded-md px-2 py-1 focus:outline-none focus:ring-1 focus:ring-indigo-500"
          />
        </div>
        <div className="flex flex-col gap-1">
          <label className="text-xs text-gray-500 font-medium">Verrijkt voor:</label>
          <input
            type="date"
            value={filter.enrichedBefore ?? ''}
            onChange={(e) => setDateFilter('enrichedBefore', e.target.value)}
            className="text-sm border border-gray-300 rounded-md px-2 py-1 focus:outline-none focus:ring-1 focus:ring-indigo-500"
          />
        </div>

        {/* Presets */}
        <div className="flex flex-col gap-1">
          <label className="text-xs text-gray-500 font-medium">Snelfilter:</label>
          <div className="flex gap-1">
            <button
              onClick={() => onChange({ ...filter, enrichedAfter: daysAgo(7), page: 1 })}
              className="px-2 py-1 text-xs border border-gray-300 rounded hover:bg-gray-50 text-gray-600"
            >
              Afgelopen week
            </button>
            <button
              onClick={() => onChange({ ...filter, enrichedAfter: daysAgo(14), page: 1 })}
              className="px-2 py-1 text-xs border border-gray-300 rounded hover:bg-gray-50 text-gray-600"
            >
              2 weken
            </button>
            <button
              onClick={() => onChange({ ...filter, enrichedAfter: daysAgo(30), page: 1 })}
              className="px-2 py-1 text-xs border border-gray-300 rounded hover:bg-gray-50 text-gray-600"
            >
              Maand
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}

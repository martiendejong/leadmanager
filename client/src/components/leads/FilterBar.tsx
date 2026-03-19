import { useEffect } from 'react'
import { useSearchParams } from 'react-router-dom'
import type { LeadFilter, LeadStats } from '../../api/leads'

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
    filter.hasOwner !== undefined ||
    filter.hasLinkedIn !== undefined ||
    !!filter.priorityLabel
  )
}

export default function FilterBar({ filter, onChange, stats }: FilterBarProps) {
  const [searchParams, setSearchParams] = useSearchParams()

  // Sync filter → URL
  useEffect(() => {
    const params = new URLSearchParams()
    if (filter.enriched !== undefined) params.set('enriched', String(filter.enriched))
    if (filter.enrichedAfter) params.set('enrichedAfter', filter.enrichedAfter)
    if (filter.enrichedBefore) params.set('enrichedBefore', filter.enrichedBefore)
    if (filter.page && filter.page > 1) params.set('page', String(filter.page))
    if (filter.sortBy) params.set('sortBy', filter.sortBy)
    if (filter.sortDesc) params.set('sortDesc', 'true')
    if (filter.hasOwner !== undefined) params.set('hasOwner', String(filter.hasOwner))
    if (filter.hasLinkedIn !== undefined) params.set('hasLinkedIn', String(filter.hasLinkedIn))
    if (filter.priorityLabel) params.set('priorityLabel', filter.priorityLabel)
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

  const setPriorityFilter = (label: string | undefined) => {
    onChange({ ...filter, priorityLabel: label, page: 1 })
  }

  const clearFilters = () => {
    onChange({ page: 1, pageSize: filter.pageSize, sortBy: filter.sortBy, sortDesc: filter.sortDesc })
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

      {/* Priority + contact quick filters */}
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-xs text-gray-500 font-medium">Prioriteit:</span>
        {(['Hoog', 'Normaal', 'Laag'] as const).map((label) => {
          const colorMap: Record<string, string> = {
            Hoog: filter.priorityLabel === label ? 'bg-green-100 text-green-700 border-green-300' : 'border-gray-300 text-gray-600 hover:bg-gray-50',
            Normaal: filter.priorityLabel === label ? 'bg-yellow-100 text-yellow-700 border-yellow-300' : 'border-gray-300 text-gray-600 hover:bg-gray-50',
            Laag: filter.priorityLabel === label ? 'bg-red-100 text-red-700 border-red-300' : 'border-gray-300 text-gray-600 hover:bg-gray-50',
          }
          return (
            <button
              key={label}
              onClick={() => setPriorityFilter(filter.priorityLabel === label ? undefined : label)}
              className={`px-2.5 py-1 text-xs border rounded font-medium transition-colors ${colorMap[label]}`}
            >
              {label}
            </button>
          )
        })}
        <span className="ml-3 text-xs text-gray-500 font-medium">Contact:</span>
        <button
          onClick={() => onChange({ ...filter, hasOwner: filter.hasOwner === true ? undefined : true, page: 1 })}
          className={`px-2.5 py-1 text-xs border rounded font-medium transition-colors ${
            filter.hasOwner === true ? 'bg-indigo-100 text-indigo-700 border-indigo-300' : 'border-gray-300 text-gray-600 hover:bg-gray-50'
          }`}
        >
          Heeft eigenaar
        </button>
        <button
          onClick={() => onChange({ ...filter, hasLinkedIn: filter.hasLinkedIn === true ? undefined : true, page: 1 })}
          className={`px-2.5 py-1 text-xs border rounded font-medium transition-colors ${
            filter.hasLinkedIn === true ? 'bg-indigo-100 text-indigo-700 border-indigo-300' : 'border-gray-300 text-gray-600 hover:bg-gray-50'
          }`}
        >
          Heeft LinkedIn
        </button>
      </div>

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

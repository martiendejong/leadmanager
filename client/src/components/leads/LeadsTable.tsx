import React, { useMemo } from 'react'
import { Link } from 'react-router-dom'
import type { Lead } from '../../api/leads'

interface LeadsTableProps {
  leads: Lead[]
  selectedIds: Set<string>
  orderedIds: string[]
  onRowMouseDown: (id: string, event: React.MouseEvent) => void
  onRowClick: (id: string, event: React.MouseEvent) => void
  onRowOpen: (lead: Lead) => void
  sortBy: string
  sortDesc: boolean
  onSort: (col: string) => void
  isDragging: boolean
  onSelectAll: () => void
  onClearSelection: () => void
  onImport?: () => void
}

const COLUMNS: { key: string; label: string; sortable: boolean; hideOnMobile: boolean }[] = [
  { key: 'name',                label: 'Bedrijf',      sortable: true,  hideOnMobile: false },
  { key: 'website',             label: 'Website',      sortable: true,  hideOnMobile: true  },
  { key: 'sector',              label: 'Sector',       sortable: true,  hideOnMobile: true  },
  { key: 'city',                label: 'Stad',         sortable: true,  hideOnMobile: true  },
  { key: 'ownerName',           label: 'Eigenaar',     sortable: true,  hideOnMobile: true  },
  { key: 'companyEmail',        label: 'Email',        sortable: false, hideOnMobile: true  },
  { key: 'salesPriorityScore',  label: 'Score',        sortable: true,  hideOnMobile: false },
  { key: 'isEnriched',          label: 'Status',       sortable: true,  hideOnMobile: false },
  { key: 'enrichedAt',          label: 'Verrijkt op',  sortable: true,  hideOnMobile: true  },
]

function SortIcon({ active, desc }: { active: boolean; desc: boolean }) {
  if (!active) {
    return <span className="ml-1 text-gray-300 text-xs">↕</span>
  }
  return <span className="ml-1 text-indigo-600 text-xs">{desc ? '↓' : '↑'}</span>
}

function SalesScoreBadge({ score }: { score?: number | null }) {
  if (score === null || score === undefined) {
    return <span className="text-gray-400 text-xs">—</span>
  }

  let colorClasses = ''
  if (score >= 7) {
    colorClasses = 'bg-green-100 text-green-800'
  } else if (score >= 4) {
    colorClasses = 'bg-yellow-100 text-yellow-800'
  } else {
    colorClasses = 'bg-red-100 text-red-800'
  }

  return (
    <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${colorClasses}`}>
      {score}/10
    </span>
  )
}

function EmptyState({ onImport }: { onImport?: () => void }) {
  return (
    <tr>
      <td colSpan={COLUMNS.length + 2} className="px-4 py-16 text-center">
        <div className="flex flex-col items-center gap-4">
          <svg className="w-14 h-14 text-indigo-200" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1} d="M19 11H5m14 0a2 2 0 012 2v6a2 2 0 01-2 2H5a2 2 0 01-2-2v-6a2 2 0 012-2m14 0V9a2 2 0 00-2-2M5 11V9a2 2 0 012-2m0 0V5a2 2 0 012-2h6a2 2 0 012 2v2M7 7h10" />
          </svg>
          <div>
            <p className="text-sm font-medium text-gray-700">Nog geen leads</p>
            <p className="text-xs text-gray-400 mt-1">Importeer je bestaande leads of laat AI nieuwe leads zoeken</p>
          </div>
          <div className="flex items-center gap-3 flex-wrap justify-center">
            {onImport && (
              <button
                onClick={onImport}
                className="px-4 py-2 text-sm border border-gray-300 rounded-lg text-gray-700 hover:bg-gray-50 font-medium transition-colors"
              >
                Importeer leads via Excel
              </button>
            )}
            <Link
              to="/leads/zoeken"
              className="px-4 py-2 text-sm bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 font-medium transition-colors"
            >
              Zoek leads met AI
            </Link>
          </div>
        </div>
      </td>
    </tr>
  )
}

export default function LeadsTable({
  leads,
  selectedIds,
  onRowMouseDown,
  onRowClick,
  onRowOpen,
  sortBy,
  sortDesc,
  onSort,
  isDragging,
  onSelectAll,
  onClearSelection,
  onImport,
}: LeadsTableProps) {
  const allSelected = leads.length > 0 && leads.every((l) => selectedIds.has(l.id))
  const someSelected = !allSelected && leads.some((l) => selectedIds.has(l.id))

  const headerCheckboxRef = React.useRef<HTMLInputElement>(null)
  React.useEffect(() => {
    if (headerCheckboxRef.current) {
      headerCheckboxRef.current.indeterminate = someSelected
    }
  }, [someSelected])

  const handleHeaderCheckbox = () => {
    if (allSelected) {
      onClearSelection()
    } else {
      onSelectAll()
    }
  }

  const formatDate = useMemo(
    () => (iso: string | null) => {
      if (!iso) return '—'
      try {
        return new Date(iso).toLocaleDateString('nl-NL', {
          day: '2-digit',
          month: '2-digit',
          year: 'numeric',
        })
      } catch {
        return iso
      }
    },
    []
  )

  const isStale = (lead: Lead): boolean => {
    const sevenDaysAgo = Date.now() - 7 * 24 * 60 * 60 * 1000
    const ref = lead.importedAt || lead.createdAt
    if (!ref) return false
    return new Date(ref).getTime() < sevenDaysAgo
  }

  return (
    <div className="overflow-x-auto">
      <table className={`min-w-full divide-y divide-gray-200 ${isDragging ? 'select-none' : ''}`}>
        <thead className="bg-gray-50">
          <tr>
            <th className="px-4 py-3 w-10">
              <input
                ref={headerCheckboxRef}
                type="checkbox"
                checked={allSelected}
                onChange={handleHeaderCheckbox}
                className="rounded border-gray-300 text-indigo-600 focus:ring-indigo-500 cursor-pointer"
              />
            </th>
            {COLUMNS.map((col) => (
              <th
                key={col.key}
                className={`px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider whitespace-nowrap ${
                  col.sortable ? 'cursor-pointer hover:text-gray-700 hover:bg-gray-100' : ''
                } ${col.hideOnMobile ? 'hidden sm:table-cell' : ''}`}
                onClick={col.sortable ? () => onSort(col.key) : undefined}
              >
                {col.label}
                {col.sortable && <SortIcon active={sortBy === col.key} desc={sortDesc} />}
              </th>
            ))}
            <th className="px-4 py-3 w-10" />
          </tr>
        </thead>
        <tbody className="bg-white divide-y divide-gray-200">
          {leads.length === 0 && <EmptyState onImport={onImport} />}
          {leads.map((lead) => {
            const selected = selectedIds.has(lead.id)
            return (
              <tr
                key={lead.id}
                data-lead-id={lead.id}
                onMouseDown={(e) => onRowMouseDown(lead.id, e)}
                onClick={(e) => onRowClick(lead.id, e)}
                className={`cursor-pointer transition-colors ${
                  selected
                    ? 'bg-indigo-50 border-l-2 border-indigo-500'
                    : 'hover:bg-gray-50 border-l-2 border-transparent'
                }`}
              >
                <td className="px-4 py-3 w-10" onClick={(e) => e.stopPropagation()}>
                  <input
                    type="checkbox"
                    checked={selected}
                    onChange={() => {
                      // handled by row click
                    }}
                    onClick={(e) => {
                      e.stopPropagation()
                      onRowClick(lead.id, e as unknown as React.MouseEvent)
                    }}
                    className="rounded border-gray-300 text-indigo-600 focus:ring-indigo-500 cursor-pointer"
                  />
                </td>
                <td className="px-4 py-3 text-sm font-medium text-gray-900 max-w-48 truncate">
                  <span className="flex items-center gap-1">
                    {lead.name || '—'}
                    {isStale(lead) && (
                      <span
                        title="Lead niet bijgewerkt in 7+ dagen"
                        className="flex-shrink-0 text-orange-400"
                      >
                        <svg className="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 20 20">
                          <path fillRule="evenodd" d="M8.257 3.099c.765-1.36 2.722-1.36 3.486 0l5.58 9.92c.75 1.334-.213 2.98-1.742 2.98H4.42c-1.53 0-2.493-1.646-1.743-2.98l5.58-9.92zM11 13a1 1 0 11-2 0 1 1 0 012 0zm-1-8a1 1 0 00-1 1v3a1 1 0 002 0V6a1 1 0 00-1-1z" clipRule="evenodd" />
                        </svg>
                      </span>
                    )}
                  </span>
                </td>
                <td className="hidden sm:table-cell px-4 py-3 text-sm text-gray-500 max-w-40 truncate">
                  {lead.website ? (
                    <a
                      href={
                        lead.website.startsWith('http') ? lead.website : `https://${lead.website}`
                      }
                      target="_blank"
                      rel="noopener noreferrer"
                      className="text-indigo-600 hover:text-indigo-800 hover:underline"
                      onClick={(e) => e.stopPropagation()}
                    >
                      {lead.website}
                    </a>
                  ) : (
                    '—'
                  )}
                </td>
                <td className="hidden sm:table-cell px-4 py-3 text-sm text-gray-500">{lead.sector || '—'}</td>
                <td className="hidden sm:table-cell px-4 py-3 text-sm text-gray-500">{lead.city || '—'}</td>
                <td className="hidden sm:table-cell px-4 py-3 text-sm text-gray-500">
                  {lead.ownerName ||
                    [lead.ownerFirstName, lead.ownerLastName].filter(Boolean).join(' ') ||
                    '—'}
                </td>
                <td className="hidden sm:table-cell px-4 py-3 text-sm text-gray-500 max-w-40 truncate">
                  {lead.companyEmail || lead.personalEmail || '—'}
                </td>
                <td className="px-4 py-3 text-sm">
                  <SalesScoreBadge score={lead.salesPriorityScore} />
                </td>
                <td className="px-4 py-3 text-sm">
                  {lead.isEnriched ? (
                    <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-green-100 text-green-800">
                      Verrijkt
                    </span>
                  ) : (
                    <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-gray-100 text-gray-600">
                      Niet verrijkt
                    </span>
                  )}
                </td>
                <td className="hidden sm:table-cell px-4 py-3 text-sm text-gray-500 whitespace-nowrap">
                  {formatDate(lead.enrichedAt)}
                </td>
                <td className="px-4 py-3 w-10" onClick={(e) => e.stopPropagation()}>
                  <button
                    onClick={() => onRowOpen(lead)}
                    className="p-1 text-gray-400 hover:text-indigo-600 rounded transition-colors"
                    title="Details bekijken"
                  >
                    <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                    </svg>
                  </button>
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

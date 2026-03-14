import React, { useMemo } from 'react'
import type { Lead } from '../../api/leads'

interface LeadsTableProps {
  leads: Lead[]
  selectedIds: Set<string>
  orderedIds: string[]
  onRowMouseDown: (id: string, event: React.MouseEvent) => void
  onRowClick: (id: string, event: React.MouseEvent) => void
  sortBy: string
  sortDesc: boolean
  onSort: (col: string) => void
  isDragging: boolean
  onSelectAll: () => void
  onClearSelection: () => void
}

const COLUMNS: { key: string; label: string; sortable: boolean }[] = [
  { key: 'name', label: 'Bedrijf', sortable: true },
  { key: 'website', label: 'Website', sortable: false },
  { key: 'sector', label: 'Sector', sortable: true },
  { key: 'city', label: 'Stad', sortable: true },
  { key: 'ownerName', label: 'Eigenaar', sortable: true },
  { key: 'companyEmail', label: 'Email', sortable: false },
  { key: 'isEnriched', label: 'Status', sortable: true },
  { key: 'enrichedAt', label: 'Verrijkt op', sortable: true },
]

function SortIcon({ active, desc }: { active: boolean; desc: boolean }) {
  if (!active) {
    return <span className="ml-1 text-gray-300 text-xs">↕</span>
  }
  return <span className="ml-1 text-indigo-600 text-xs">{desc ? '↓' : '↑'}</span>
}

export default function LeadsTable({
  leads,
  selectedIds,
  onRowMouseDown,
  onRowClick,
  sortBy,
  sortDesc,
  onSort,
  isDragging,
  onSelectAll,
  onClearSelection,
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
                }`}
                onClick={col.sortable ? () => onSort(col.key) : undefined}
              >
                {col.label}
                {col.sortable && <SortIcon active={sortBy === col.key} desc={sortDesc} />}
              </th>
            ))}
          </tr>
        </thead>
        <tbody className="bg-white divide-y divide-gray-200">
          {leads.length === 0 && (
            <tr>
              <td
                colSpan={COLUMNS.length + 1}
                className="px-4 py-12 text-center text-sm text-gray-400"
              >
                Geen leads gevonden
              </td>
            </tr>
          )}
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
                  {lead.name || '—'}
                </td>
                <td className="px-4 py-3 text-sm text-gray-500 max-w-40 truncate">
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
                <td className="px-4 py-3 text-sm text-gray-500">{lead.sector || '—'}</td>
                <td className="px-4 py-3 text-sm text-gray-500">{lead.city || '—'}</td>
                <td className="px-4 py-3 text-sm text-gray-500">
                  {lead.ownerName ||
                    [lead.ownerFirstName, lead.ownerLastName].filter(Boolean).join(' ') ||
                    '—'}
                </td>
                <td className="px-4 py-3 text-sm text-gray-500 max-w-40 truncate">
                  {lead.companyEmail || lead.personalEmail || '—'}
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
                <td className="px-4 py-3 text-sm text-gray-500 whitespace-nowrap">
                  {formatDate(lead.enrichedAt)}
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

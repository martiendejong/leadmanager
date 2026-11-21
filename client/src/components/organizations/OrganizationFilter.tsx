import { useOrganizations } from '../../contexts/OrganizationContext'
import { orgColour } from './OrganizationBadge'

/**
 * The persistent organisation filter: "All my organisations" or one or more organisations (chips). The choice is
 * remembered per user and shared by the leads list, messages, today and the queue.
 * Hidden when the person is in at most one organisation: there is nothing to choose.
 */
export default function OrganizationFilter({ className = '' }: { className?: string }) {
  const { organizations, selectedIds, setSelectedIds } = useOrganizations()
  if (organizations.length < 2) return null

  const toggle = (id: string) =>
    setSelectedIds(selectedIds.includes(id) ? selectedIds.filter((x) => x !== id) : [...selectedIds, id])

  return (
    <div className={`flex flex-wrap items-center gap-1.5 ${className}`} role="group" aria-label="Filter by organisation">
      <button
        type="button"
        onClick={() => setSelectedIds([])}
        aria-pressed={selectedIds.length === 0}
        className={`px-3 py-1 rounded-full border text-xs font-medium transition-colors ${
          selectedIds.length === 0
            ? 'bg-gray-800 text-white border-gray-800'
            : 'bg-white text-gray-600 border-gray-300 hover:bg-gray-50'
        }`}
      >
        All my organisations
      </button>
      {organizations.map((o) => {
        const on = selectedIds.includes(o.id)
        return (
          <button
            key={o.id}
            type="button"
            onClick={() => toggle(o.id)}
            aria-pressed={on}
            className={`px-3 py-1 rounded-full border text-xs font-medium transition-colors max-w-full truncate ${
              on ? `${orgColour(o.id)} ring-2 ring-offset-1 ring-indigo-400` : 'bg-white text-gray-600 border-gray-300 hover:bg-gray-50'
            }`}
          >
            {o.name}
          </button>
        )
      })}
    </div>
  )
}

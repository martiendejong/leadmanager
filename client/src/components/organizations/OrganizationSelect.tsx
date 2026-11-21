import { useOrganizations } from '../../contexts/OrganizationContext'

/**
 * Picker for the organisation of a new or edited lead. With one organisation it is a fixed label.
 * `value` empty = nothing chosen yet (the parent must not submit).
 */
export default function OrganizationSelect({
  value,
  onChange,
  id = 'organization',
  label = 'Organisation',
  className = '',
}: {
  value: string
  onChange: (id: string) => void
  id?: string
  label?: string
  className?: string
}) {
  const { organizations } = useOrganizations()
  if (organizations.length === 0) return null

  if (organizations.length === 1) {
    return (
      <div className={className}>
        <span className="block text-sm font-medium text-gray-700 mb-1">{label}</span>
        <span className="inline-block px-3 py-2 text-sm text-gray-700 bg-gray-50 border border-gray-200 rounded-md">{organizations[0].name}</span>
      </div>
    )
  }

  return (
    <div className={className}>
      <label htmlFor={id} className="block text-sm font-medium text-gray-700 mb-1">{label}</label>
      <select
        id={id}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm bg-white focus:outline-none focus:ring-2 focus:ring-indigo-400"
      >
        <option value="" disabled>Choose an organisation</option>
        {organizations.map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
      </select>
    </div>
  )
}

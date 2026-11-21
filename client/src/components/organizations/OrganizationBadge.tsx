import { useOrganizations } from '../../contexts/OrganizationContext'

// A stable colour per organisation, so the same organisation looks the same on every row and card.
const PALETTE = [
  'bg-indigo-50 text-indigo-700 border-indigo-200',
  'bg-emerald-50 text-emerald-700 border-emerald-200',
  'bg-amber-50 text-amber-800 border-amber-200',
  'bg-rose-50 text-rose-700 border-rose-200',
  'bg-sky-50 text-sky-700 border-sky-200',
  'bg-violet-50 text-violet-700 border-violet-200',
  'bg-teal-50 text-teal-700 border-teal-200',
]

// eslint-disable-next-line react-refresh/only-export-components
export function orgColour(id: string): string {
  let h = 0
  for (let i = 0; i < id.length; i++) h = (h * 31 + id.charCodeAt(i)) >>> 0
  return PALETTE[h % PALETTE.length]
}

/** The organisation of a lead, as a small pill. Renders nothing while the organisations are loading. */
export default function OrganizationBadge({ organizationId, className = '' }: { organizationId: string | undefined | null; className?: string }) {
  const { orgName } = useOrganizations()
  if (!organizationId) return null
  const name = orgName(organizationId)
  if (!name) return null
  return (
    <span
      title={`Organisation: ${name}`}
      className={`inline-block max-w-full truncate align-middle px-2 py-0.5 rounded-full border text-xs font-medium ${orgColour(organizationId)} ${className}`}
    >
      {name}
    </span>
  )
}

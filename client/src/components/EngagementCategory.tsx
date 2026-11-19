// Engagement category of a client: how much (free) effort we put into the client.
// The matching rules can be edited under Settings → Client rules.

export const ENGAGEMENT_CATEGORIES = ['Active', 'Maintenance', 'Reactive'] as const
export type EngagementCategory = (typeof ENGAGEMENT_CATEGORIES)[number]

export const CATEGORY_LABELS: Record<string, string> = {
  Active: 'Active',
  Maintenance: 'Maintenance contract',
  Reactive: 'Reactive',
}

const CATEGORY_STYLES: Record<string, string> = {
  Active: 'bg-green-50 text-green-700 border-green-200',
  Maintenance: 'bg-blue-50 text-blue-700 border-blue-200',
  Reactive: 'bg-amber-50 text-amber-800 border-amber-300',
}

export const CATEGORY_DESCRIPTIONS: Record<string, string> = {
  Active: 'Ongoing collaboration · normal follow-up.',
  Maintenance: 'Maintenance contract in place · upkeep and changes fall under the contract.',
  Reactive: 'Paid requests only · no free research, no reminders.',
}

export function EngagementCategoryBadge({ category }: { category?: string | null }) {
  if (!category) return null
  const cls = CATEGORY_STYLES[category] ?? 'bg-gray-50 text-gray-700 border-gray-200'
  return (
    <span
      className={`inline-flex items-center text-xs font-medium border rounded-full px-2 py-0.5 ${cls}`}
      title={CATEGORY_DESCRIPTIONS[category]}
    >
      {CATEGORY_LABELS[category] ?? category}
    </span>
  )
}

export function ReactiveBanner() {
  return (
    <div className="mb-4 rounded-xl border border-amber-300 bg-amber-50 p-4">
      <p className="text-sm font-semibold text-amber-900 mb-1.5">
        Reactive client · paid requests only
      </p>
      <ul className="text-sm text-amber-900 space-y-1 list-disc list-inside">
        <li>No research, scan or repair without a maintenance contract or a paid order up front.</li>
        <li>One message per topic, no chasing with reminders.</li>
        <li>If they are interested, the conversation starts with the contract, not with the work.</li>
      </ul>
      <p className="text-xs text-amber-700 mt-2">
        Full client rules: Settings → Client rules.
      </p>
    </div>
  )
}

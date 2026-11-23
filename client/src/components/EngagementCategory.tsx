// Engagement category of a client: how much (free) effort we put into the client.
// The matching rules can be edited under Settings → Client rules.

export const ENGAGEMENT_CATEGORIES = ['Active', 'Maintenance', 'Reactive', 'Prospect', 'WarmLead', 'OneOff', 'Conflict', 'RelationshipEnded', 'Inactive'] as const
export type EngagementCategory = (typeof ENGAGEMENT_CATEGORIES)[number]

export const CATEGORY_LABELS: Record<string, string> = {
  Active: 'Active',
  Maintenance: 'Maintenance contract',
  Reactive: 'Reactive',
  Prospect: 'Prospect',
  WarmLead: 'Warm lead',
  OneOff: 'One-off',
  Conflict: 'Conflict',
  RelationshipEnded: 'Relationship ended',
  Inactive: 'Inactive',
}

const CATEGORY_STYLES: Record<string, string> = {
  Active: 'bg-green-50 text-green-700 border-green-200',
  Maintenance: 'bg-blue-50 text-blue-700 border-blue-200',
  Reactive: 'bg-amber-50 text-amber-800 border-amber-300',
  Prospect: 'bg-sky-50 text-sky-700 border-sky-200',
  WarmLead: 'bg-orange-50 text-orange-700 border-orange-200',
  OneOff: 'bg-purple-50 text-purple-700 border-purple-200',
  Conflict: 'bg-red-50 text-red-700 border-red-300',
  RelationshipEnded: 'bg-gray-100 text-gray-600 border-gray-300',
  Inactive: 'bg-gray-50 text-gray-500 border-gray-200',
}

export const CATEGORY_DESCRIPTIONS: Record<string, string> = {
  Active: 'Ongoing collaboration · normal follow-up.',
  Maintenance: 'Maintenance contract in place · upkeep and changes fall under the contract.',
  Reactive: 'Paid requests only · no free research, no reminders.',
  Prospect: 'Not a client yet · a conversation or offer is the next step.',
  WarmLead: 'Interested, has asked for something · follow up actively.',
  OneOff: 'A single assignment, no ongoing relationship.',
  Conflict: 'Dispute in progress · handle with care, legal mode, nothing automatic.',
  RelationshipEnded: 'The relationship has ended · no outreach.',
  Inactive: 'Dormant · no activity expected.',
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

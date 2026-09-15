// Omgangscategorie voor klanten: bepaalt hoeveel (gratis) moeite we in een klant steken.
// De bijbehorende regels zijn te beheren onder Instellingen → Klantregels.

export const ENGAGEMENT_CATEGORIES = ['Actief', 'Beheercontract', 'Reactief'] as const
export type EngagementCategory = (typeof ENGAGEMENT_CATEGORIES)[number]

const CATEGORY_STYLES: Record<string, string> = {
  Actief: 'bg-green-50 text-green-700 border-green-200',
  Beheercontract: 'bg-blue-50 text-blue-700 border-blue-200',
  Reactief: 'bg-amber-50 text-amber-800 border-amber-300',
}

export const CATEGORY_DESCRIPTIONS: Record<string, string> = {
  Actief: 'Lopende samenwerking · normale opvolging.',
  Beheercontract: 'Beheercontract actief · onderhoud en wijzigingen vallen onder het contract.',
  Reactief: 'Alleen op betaalde aanvraag · geen gratis onderzoek, geen reminders.',
}

export function EngagementCategoryBadge({ category }: { category?: string | null }) {
  if (!category) return null
  const cls = CATEGORY_STYLES[category] ?? 'bg-gray-50 text-gray-700 border-gray-200'
  return (
    <span
      className={`inline-flex items-center text-xs font-medium border rounded-full px-2 py-0.5 ${cls}`}
      title={CATEGORY_DESCRIPTIONS[category]}
    >
      {category}
    </span>
  )
}

export function ReactiefBanner() {
  return (
    <div className="mb-4 rounded-xl border border-amber-300 bg-amber-50 p-4">
      <p className="text-sm font-semibold text-amber-900 mb-1.5">
        Reactieve klant · alleen op betaalde aanvraag
      </p>
      <ul className="text-sm text-amber-900 space-y-1 list-disc list-inside">
        <li>Geen onderzoek, scan of herstel zonder beheercontract of betaalde opdracht vooraf.</li>
        <li>Eén bericht per onderwerp, geen reminders achteraan sturen.</li>
        <li>Bij interesse begint het gesprek bij het contract, niet bij het werk.</li>
      </ul>
      <p className="text-xs text-amber-700 mt-2">
        Volledige klantregels: Instellingen → Klantregels.
      </p>
    </div>
  )
}

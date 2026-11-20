import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { useToast } from '../Toast'
import { errorMessage } from '../../api/crm'
import { getQueue, type QueueItem } from '../../api/messages'
import { ProductChips } from '../leads/ProductChips'

/** The best leads that are still New and have not been contacted. One click opens the lead with the message section. */
export default function NextToContact() {
  const { showToast } = useToast()
  const [items, setItems] = useState<QueueItem[] | null>(null)

  useEffect(() => {
    let cancelled = false
    getQueue(8, 6)
      .then((d) => { if (!cancelled) setItems(d) })
      .catch((err) => { if (!cancelled) { showToast(errorMessage(err, 'Could not load the leads to contact'), 'error'); setItems([]) } })
    return () => { cancelled = true }
  }, [showToast])

  return (
    <section aria-label="Next to contact" className="mb-6">
      <h2 className="text-xs font-semibold text-gray-500 uppercase tracking-wider mb-2">Next to contact</h2>
      {items === null && <p className="text-sm text-gray-400">Loading…</p>}
      {items?.length === 0 && (
        <div className="bg-white rounded-xl border border-gray-200 p-4 text-sm text-gray-500">
          No new leads with a high score are waiting. Find new leads on the Leads page.
        </div>
      )}
      <ul className="space-y-3">
        {items?.map((l) => (
          <li key={l.id} className="bg-white rounded-xl border border-gray-200 p-4">
            <div className="flex items-start gap-3">
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-2">
                  <Link to={`/leads/${l.id}`} className="text-sm font-semibold text-gray-900 hover:text-indigo-600 truncate">{l.name}</Link>
                  {l.score != null && <span className="text-xs px-2 py-0.5 rounded-full bg-green-100 text-green-800 font-medium">{l.score}/10</span>}
                  {l.hasDraft && <span className="text-xs px-2 py-0.5 rounded-full bg-amber-100 text-amber-800">Draft saved</span>}
                </div>
                <p className="text-xs text-gray-400">{[l.sector, l.city, l.ownerName].filter(Boolean).join(' · ')}</p>
                {l.whyLead && <p className="text-xs text-gray-600 mt-1 line-clamp-2"><span className="font-medium">Why:</span> {l.whyLead}</p>}
                {l.needs && <p className="text-xs text-gray-600 mt-0.5 line-clamp-2"><span className="font-medium">Needs:</span> {l.needs.split('\n')[0]}</p>}
                {l.productFit && l.productFit.length > 0 && <div className="mt-1"><ProductChips fit={l.productFit} compact /></div>}
              </div>
              <Link to={`/leads/${l.id}#message`} className="text-sm px-3 py-1.5 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700 whitespace-nowrap">
                Write message
              </Link>
            </div>
          </li>
        ))}
      </ul>
    </section>
  )
}

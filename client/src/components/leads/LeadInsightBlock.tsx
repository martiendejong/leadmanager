import type { Lead } from '../../api/leads'
import { ProductChips } from './ProductChips'

/** Splits a stored need line `need: "quote"` into its parts; a line without a quote is shown as it is. */
function splitNeed(line: string): { need: string; quote: string | null } {
  const m = /^(.*?): "(.*)"$/.exec(line)
  return m ? { need: m[1], quote: m[2] } : { need: line, quote: null }
}

/**
 * The top of the lead panel: why this is a lead, what the business needs, and which of our products can help. Everything shown here was
 * verified against the business's own pages (the quotes are the proof), or is plainly marked as not assessed.
 */
export default function LeadInsightBlock({ lead }: { lead: Lead }) {
  const needs = (lead.needs ?? '').split('\n').map((l) => l.trim()).filter(Boolean).map(splitNeed)
  const fit = lead.productFit

  return (
    <div className="bg-white border border-indigo-200 rounded-lg p-4 space-y-4">
      <section>
        <h3 className="text-xs font-semibold text-indigo-700 uppercase tracking-wide mb-1">Why this lead</h3>
        {lead.whyLead
          ? <p className="text-sm text-gray-800 leading-relaxed">{lead.whyLead}</p>
          : <p className="text-sm text-gray-400">Not available yet.</p>}
      </section>

      <section>
        <h3 className="text-xs font-semibold text-indigo-700 uppercase tracking-wide mb-1">What they need</h3>
        {needs.length > 0 ? (
          <ul className="space-y-1.5">
            {needs.map((n, i) => (
              <li key={i} className="text-sm text-gray-800">
                <span className="font-medium">{n.need}</span>
                {n.quote && <blockquote className="text-xs text-gray-600 italic">"{n.quote}"</blockquote>}
              </li>
            ))}
          </ul>
        ) : (
          <p className="text-sm text-gray-400">No need was found on the business's own pages.</p>
        )}
      </section>

      <section>
        <h3 className="text-xs font-semibold text-indigo-700 uppercase tracking-wide mb-1">How we can help</h3>
        {fit == null && <p className="text-sm text-gray-400">Not assessed yet.</p>}
        {fit != null && fit.length === 0 && <p className="text-sm text-gray-600">No clear fit with our products.</p>}
        {fit != null && fit.length > 0 && <ProductChips fit={fit} />}
      </section>
    </div>
  )
}

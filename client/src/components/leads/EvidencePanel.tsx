import { useEffect, useState } from 'react'
import { getLeadEvidence, type LeadEvidence } from '../../api/hunt'
import { useProducts } from './ProductChips'

const FIELD_LABEL: Record<string, string> = {
  ownerName: 'Owner name',
  ownerLinkedInUrl: 'Owner LinkedIn',
  companyLinkedInUrl: 'Company LinkedIn',
  ownerMobile: 'Mobile number',
  personalEmail: 'Personal e-mail',
  foundingYear: 'Founding year',
  smallCompany: 'Small company',
  kvkNumber: 'KvK number',
}

function SourceLink({ url }: { url: string }) {
  return /^https?:\/\//i.test(url)
    ? <a href={url} target="_blank" rel="noopener noreferrer" className="text-xs text-indigo-600 hover:underline break-all">{url}</a>
    : <span className="text-xs text-gray-500 break-all">{url}</span>
}

/** Where the score of a lead comes from: the sources and exact words of the latest verification, and what was not counted. */
export default function EvidencePanel({ leadId, refreshKey }: { leadId: string; refreshKey?: number }) {
  const [data, setData] = useState<LeadEvidence | null>(null)
  const [open, setOpen] = useState(false)
  const products = useProducts()

  useEffect(() => {
    let alive = true
    setData(null)
    getLeadEvidence(leadId).then((d) => { if (alive) setData(d) }).catch(() => { if (alive) setData({ available: false }) })
    return () => { alive = false }
  }, [leadId, refreshKey])

  if (!data || !data.available || !data.evidence) return null
  const ev = data.evidence

  return (
    <div className="bg-gray-50 border border-gray-200 rounded-lg p-4">
      <button onClick={() => setOpen((o) => !o)} className="w-full flex items-center justify-between text-left" aria-expanded={open}>
        <span className="text-xs font-semibold text-gray-700 uppercase tracking-wide">
          Evidence behind the score{data.score != null ? ` (${data.score})` : ''}
        </span>
        <span className="text-xs text-indigo-600">{open ? 'Hide' : 'Show'}</span>
      </button>
      {open && (
        <div className="mt-3 space-y-3 text-sm">
          <p className="text-xs text-gray-500">{data.reason}{data.at ? ` Checked ${new Date(data.at).toLocaleDateString('en-GB')}.` : ''}</p>
          {ev.verified.length === 0 && <p className="text-gray-600">Nothing score bearing could be verified.</p>}
          {ev.verified.length > 0 && (
            <ul className="space-y-2">
              {ev.verified.map((v) => (
                <li key={v.field} className="border-l-2 border-green-400 pl-3">
                  <div className="font-medium text-gray-800">{FIELD_LABEL[v.field] ?? v.field}: <span className="font-normal">{v.value}</span></div>
                  <blockquote className="text-xs text-gray-600 italic">"{v.quote}"</blockquote>
                  {/^https?:\/\//i.test(v.source) ? <a href={v.source} target="_blank" rel="noopener noreferrer" className="text-xs text-indigo-600 hover:underline break-all">{v.source}</a> : <span className="text-xs text-gray-500 break-all">{v.source}</span>}
                </li>
              ))}
            </ul>
          )}
          {ev.dropped.length > 0 && (
            <div>
              <h5 className="text-xs font-semibold text-gray-500 uppercase tracking-wide mb-1">Not counted</h5>
              <ul className="space-y-1">
                {ev.dropped.map((d, i) => (
                  <li key={i} className="border-l-2 border-amber-300 pl-3 text-xs text-gray-600">
                    <span className="font-medium text-gray-700">{FIELD_LABEL[d.field] ?? d.field}</span>{d.value ? ` (${d.value})` : ''}: {d.reason}
                  </li>
                ))}
              </ul>
            </div>
          )}
          {ev.google && <p className="text-xs text-gray-600"><span className="font-medium">Google:</span> {ev.google}</p>}
          {ev.insight && (ev.insight.observation || ev.insight.needs.length > 0 || ev.insight.productFit.length > 0 || ev.insight.dropped.length > 0) && (
            <div className="space-y-2">
              <h5 className="text-xs font-semibold text-gray-500 uppercase tracking-wide">Needs and product fit: the quotes</h5>
              <ul className="space-y-2">
                {ev.insight.observation && (
                  <li className="border-l-2 border-green-400 pl-3">
                    <div className="font-medium text-gray-800">Why it is interesting</div>
                    <blockquote className="text-xs text-gray-600 italic">"{ev.insight.observation.quote}"</blockquote>
                    <SourceLink url={ev.insight.observation.source} />
                  </li>
                )}
                {ev.insight.needs.map((n, i) => (
                  <li key={'n' + i} className="border-l-2 border-green-400 pl-3">
                    <div className="font-medium text-gray-800">Need: <span className="font-normal">{n.need}</span></div>
                    <blockquote className="text-xs text-gray-600 italic">"{n.quote}"</blockquote>
                    <SourceLink url={n.source} />
                  </li>
                ))}
                {ev.insight.productFit.map((f) => (
                  <li key={f.product} className="border-l-2 border-indigo-400 pl-3">
                    <div className="font-medium text-gray-800">Fit: {products[f.product]?.name ?? `${f.product} (retired product)`} <span className="font-normal">{f.reason}</span></div>
                    <blockquote className="text-xs text-gray-600 italic">"{f.quote}"</blockquote>
                    <SourceLink url={f.source} />
                  </li>
                ))}
                {ev.insight.noClearFit && ev.insight.productFit.length === 0 && <li className="text-xs text-gray-600">The agent found no clear fit with our products.</li>}
              </ul>
              {ev.insight.dropped.length > 0 && (
                <ul className="space-y-1">
                  {ev.insight.dropped.map((d, i) => (
                    <li key={i} className="border-l-2 border-amber-300 pl-3 text-xs text-gray-600">
                      <span className="font-medium text-gray-700">Not stored: {d.field}</span>{d.value ? ` (${d.value})` : ''}: {d.reason}
                    </li>
                  ))}
                </ul>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  )
}

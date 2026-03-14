import { useEffect } from 'react'
import type { Lead } from '../../api/leads'

interface Props {
  lead: Lead | null
  onClose: () => void
}

function Field({ label, value }: { label: string; value?: string | number | null }) {
  return (
    <div>
      <dt className="text-xs font-medium text-gray-500 uppercase tracking-wide">{label}</dt>
      <dd className="mt-0.5 text-sm text-gray-900">
        {value ? (
          typeof value === 'string' && value.startsWith('http') ? (
            <a
              href={value}
              target="_blank"
              rel="noopener noreferrer"
              className="text-indigo-600 hover:underline break-all"
            >
              {value}
            </a>
          ) : (
            <span className="break-words">{value}</span>
          )
        ) : (
          <span className="text-gray-400">—</span>
        )}
      </dd>
    </div>
  )
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div>
      <h3 className="text-sm font-semibold text-gray-700 mb-3 pb-1.5 border-b border-gray-100">
        {title}
      </h3>
      <dl className="space-y-3">{children}</dl>
    </div>
  )
}

export default function LeadDetailPanel({ lead, onClose }: Props) {
  // Close on Escape
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <>
      {/* Backdrop */}
      {lead && (
        <div
          className="fixed inset-0 bg-black/20 z-40"
          onClick={onClose}
        />
      )}

      {/* Panel */}
      <div
        className={`fixed top-0 right-0 h-full w-[480px] bg-white shadow-2xl z-50 flex flex-col transform transition-transform duration-300 ease-in-out ${
          lead ? 'translate-x-0' : 'translate-x-full'
        }`}
      >
        {lead && (
          <>
            {/* Header */}
            <div className="flex items-start justify-between p-5 border-b border-gray-200">
              <div className="flex-1 min-w-0">
                <h2 className="text-lg font-bold text-gray-900 truncate">{lead.name}</h2>
                {lead.website && (
                  <a
                    href={lead.resolvedUrl || lead.website}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="text-sm text-indigo-600 hover:underline"
                  >
                    {lead.website}
                  </a>
                )}
              </div>
              <button
                onClick={onClose}
                className="ml-4 p-1.5 rounded-md text-gray-400 hover:text-gray-600 hover:bg-gray-100 flex-shrink-0"
              >
                <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                </svg>
              </button>
            </div>

            {/* Enrichment status badge */}
            <div className="px-5 py-2 border-b border-gray-100 flex items-center gap-2">
              {lead.isEnriched ? (
                <span className="inline-flex items-center gap-1 text-xs font-medium text-green-700 bg-green-50 border border-green-200 rounded-full px-2.5 py-0.5">
                  <svg className="w-3 h-3" fill="currentColor" viewBox="0 0 20 20">
                    <path fillRule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z" clipRule="evenodd" />
                  </svg>
                  Verrijkt
                </span>
              ) : (
                <span className="text-xs text-gray-400 italic">Nog niet verrijkt</span>
              )}
              {lead.enrichmentVersion === 2 && (
                <span className="text-xs text-indigo-600 font-medium bg-indigo-50 border border-indigo-200 rounded-full px-2 py-0.5">
                  RAG v2
                </span>
              )}
              {lead.websiteStatus === 'Unreachable' && (
                <span className="text-xs text-red-600 bg-red-50 border border-red-200 rounded-full px-2 py-0.5">
                  Website onbereikbaar
                </span>
              )}
            </div>

            {/* Scrollable body */}
            <div className="flex-1 overflow-y-auto p-5 space-y-6">

              {/* Sales Pitch — most prominent, top of panel */}
              {lead.salesPitch && (
                <div className="bg-green-50 border border-green-200 rounded-lg p-4">
                  <div className="flex items-center gap-1.5 mb-2">
                    <svg className="w-4 h-4 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 12h.01M12 12h.01M16 12h.01M21 12c0 4.418-4.03 8-9 8a9.863 9.863 0 01-4.255-.949L3 20l1.395-3.72C3.512 15.042 3 13.574 3 12c0-4.418 4.03-8 9-8s9 3.582 9 8z" />
                    </svg>
                    <span className="text-xs font-semibold text-green-700 uppercase tracking-wide">Salespitch</span>
                  </div>
                  <p className="text-sm text-green-900 leading-relaxed whitespace-pre-wrap">{lead.salesPitch}</p>
                </div>
              )}

              {/* AI Summary */}
              {lead.aiSummary && (
                <div className="bg-indigo-50 border border-indigo-200 rounded-lg p-4">
                  <div className="flex items-center gap-1.5 mb-2">
                    <svg className="w-4 h-4 text-indigo-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9.663 17h4.673M12 3v1m6.364 1.636l-.707.707M21 12h-1M4 12H3m1.636 6.364l.707-.707M6.343 6.343l-.707-.707M12 21v-1" />
                    </svg>
                    <span className="text-xs font-semibold text-indigo-700 uppercase tracking-wide">AI Samenvatting</span>
                  </div>
                  <p className="text-sm text-indigo-900 leading-relaxed">{lead.aiSummary}</p>
                </div>
              )}

              <Section title="Bedrijfsinfo">
                <Field label="Naam" value={lead.name} />
                <Field label="Sector" value={lead.sector} />
                <Field label="Stad" value={lead.city} />
                <Field label="Telefoon" value={lead.phone} />
                <Field label="Zakelijk e-mail" value={lead.companyEmail} />
                <Field label="Bron" value={lead.source} />
              </Section>

              <Section title="Eigenaar / contact">
                <Field label="Naam eigenaar" value={lead.ownerName} />
                <Field label="Voornaam" value={lead.ownerFirstName} />
                <Field label="Achternaam" value={lead.ownerLastName} />
                <Field label="Functietitel" value={lead.ownerTitle} />
                <Field label="Persoonlijk e-mail" value={lead.personalEmail} />
                <Field label="LinkedIn" value={lead.linkedInUrl} />
              </Section>

              {(lead.description || lead.services || lead.targetAudience) && (
                <Section title="AI-analyse">
                  {lead.description && <Field label="Omschrijving" value={lead.description} />}
                  {lead.services && <Field label="Diensten / producten" value={lead.services} />}
                  {lead.targetAudience && <Field label="Doelgroep" value={lead.targetAudience} />}
                </Section>
              )}

              {lead.enrichmentVersion === 2 && (
                <Section title="Crawl metadata">
                  <Field label="Gecrawled op" value={lead.crawledAt ? new Date(lead.crawledAt).toLocaleString('nl-NL') : null} />
                  <Field label="Pagina's gecrawled" value={lead.pagesCrawled} />
                  <Field label="Chunks geïndexeerd" value={lead.chunksIndexed} />
                  <Field label="Resolved URL" value={lead.resolvedUrl} />
                  <Field label="Website status" value={lead.websiteStatus} />
                </Section>
              )}
            </div>
          </>
        )}
      </div>
    </>
  )
}

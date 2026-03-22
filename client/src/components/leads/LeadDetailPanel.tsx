import { useEffect, useState } from 'react'
import type { Lead, UserDto } from '../../api/leads'
import { regenerateSalesApproach, enrichLeads, assignLead, fetchUsers } from '../../api/leads'
import { useToast } from '../Toast'
import OutreachEmailPanel from './OutreachEmailPanel'
import LeadActivityTimeline from './LeadActivityTimeline'

interface Props {
  lead: Lead | null
  onClose: () => void
  onLeadUpdated?: (lead: Lead) => void
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

export default function LeadDetailPanel({ lead, onClose, onLeadUpdated }: Props) {
  const { showToast } = useToast()
  const [isRegenerating, setIsRegenerating] = useState(false)
  const [isEnriching, setIsEnriching] = useState(false)
  const [activeTab, setActiveTab] = useState<'linkedin' | 'phone' | 'email'>('linkedin')
  const [users, setUsers] = useState<UserDto[]>([])
  const [isAssigning, setIsAssigning] = useState(false)

  // Close on Escape
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [onClose])

  // Load users for assignment dropdown
  useEffect(() => {
    fetchUsers().then(setUsers).catch(() => {/* non-critical */})
  }, [])

  const handleAssign = async (userId: string | null) => {
    if (!lead) return
    setIsAssigning(true)
    try {
      const updated = await assignLead(lead.id, userId)
      showToast(userId ? 'Lead toegewezen!' : 'Toewijzing verwijderd', 'success')
      if (onLeadUpdated) onLeadUpdated(updated)
    } catch {
      showToast('Toewijzen mislukt', 'error')
    } finally {
      setIsAssigning(false)
    }
  }

  const copyToClipboard = async (text: string, label: string) => {
    try {
      await navigator.clipboard.writeText(text)
      showToast(`${label} gekopieerd naar klembord!`, 'success')
    } catch {
      showToast('Kopiëren mislukt', 'error')
    }
  }

  const handleRegenerateSalesApproach = async () => {
    if (!lead) return

    setIsRegenerating(true)
    try {
      await regenerateSalesApproach(lead.id)
      showToast('Sales approach opnieuw gegenereerd!', 'success')
      // Update the lead in parent component would require callback - for now just show success
      window.location.reload() // Simple refresh - in production use proper state management
    } catch (err: any) {
      showToast(err.response?.data || 'Regenereren mislukt', 'error')
    } finally {
      setIsRegenerating(false)
    }
  }

  const handleSetReminder = async (date: string) => {
    if (!lead) return
    setIsSavingReminder(true)
    try {
      await setReminder(lead.id, date || null)
      showToast(date ? `Herinnering ingesteld op ${date}` : 'Herinnering verwijderd', 'success')
    } catch {
      showToast('Herinnering opslaan mislukt', 'error')
    } finally {
      setIsSavingReminder(false)
    }
  }

  const handleEnrichNow = async () => {
    if (!lead) return

    setIsEnriching(true)
    try {
      await enrichLeads([lead.id])
      showToast('Verrijking gestart!', 'success')
      setTimeout(() => window.location.reload(), 2000) // Reload after 2s to show updated data
    } catch (err: any) {
      showToast(err.response?.data || 'Verrijking starten mislukt', 'error')
    } finally {
      setIsEnriching(false)
    }
  }

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
            <div className="px-5 py-2 border-b border-gray-100 flex items-center justify-between">
              <div className="flex items-center gap-2">
                {lead.isEnriched ? (
                  <>
                    <span className="inline-flex items-center gap-1 text-xs font-medium text-green-700 bg-green-50 border border-green-200 rounded-full px-2.5 py-0.5">
                      <svg className="w-3 h-3" fill="currentColor" viewBox="0 0 20 20">
                        <path fillRule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z" clipRule="evenodd" />
                      </svg>
                      Verrijkt
                    </span>
                    {lead.enrichedAt && (
                      <span className="text-xs text-gray-500">
                        op {new Date(lead.enrichedAt).toLocaleDateString('nl-NL')}
                      </span>
                    )}
                  </>
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
              <button
                onClick={handleEnrichNow}
                disabled={isEnriching}
                className="text-xs px-3 py-1 bg-indigo-600 text-white rounded hover:bg-indigo-700 disabled:bg-gray-400 disabled:cursor-not-allowed"
              >
                {isEnriching ? 'Bezig...' : 'Verrijk nu'}
              </button>
            </div>

            {/* Scrollable body */}
            <div className="flex-1 overflow-y-auto p-5 space-y-6">

              {/* Sales Priority Score - most prominent */}
              {lead.salesPriorityScore !== null && lead.salesPriorityScore !== undefined && (
                <div className={`border rounded-lg p-4 ${
                  lead.salesPriorityScore >= 7 ? 'bg-green-50 border-green-200' :
                  lead.salesPriorityScore >= 4 ? 'bg-yellow-50 border-yellow-200' :
                  'bg-red-50 border-red-200'
                }`}>
                  <div className="flex items-center gap-1.5 mb-2">
                    <svg className={`w-4 h-4 ${
                      lead.salesPriorityScore >= 7 ? 'text-green-600' :
                      lead.salesPriorityScore >= 4 ? 'text-yellow-600' :
                      'text-red-600'
                    }`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 7h8m0 0v8m0-8l-8 8-4-4-6 6" />
                    </svg>
                    <span className={`text-xs font-semibold uppercase tracking-wide ${
                      lead.salesPriorityScore >= 7 ? 'text-green-700' :
                      lead.salesPriorityScore >= 4 ? 'text-yellow-700' :
                      'text-red-700'
                    }`}>Sales Prioriteit</span>
                  </div>
                  <div className="flex items-baseline gap-2">
                    <span className={`text-3xl font-bold ${
                      lead.salesPriorityScore >= 7 ? 'text-green-900' :
                      lead.salesPriorityScore >= 4 ? 'text-yellow-900' :
                      'text-red-900'
                    }`}>{lead.salesPriorityScore}</span>
                    <span className={`text-lg ${
                      lead.salesPriorityScore >= 7 ? 'text-green-700' :
                      lead.salesPriorityScore >= 4 ? 'text-yellow-700' :
                      'text-red-700'
                    }`}>/10</span>
                  </div>
                  <p className={`text-xs mt-1 ${
                    lead.salesPriorityScore >= 7 ? 'text-green-600' :
                    lead.salesPriorityScore >= 4 ? 'text-yellow-600' :
                    'text-red-600'
                  }`}>
                    {lead.salesPriorityScore >= 7 ? 'Hoge prioriteit - Zeer waardevol contact' :
                     lead.salesPriorityScore >= 4 ? 'Normale prioriteit - Standaard follow-up' :
                     'Lage prioriteit - Beperkte contactgegevens'}
                  </p>
                </div>
              )}

              {/* Sales Approach with Tabs */}
              {lead.salesApproach && (() => {
                try {
                  const approach = JSON.parse(lead.salesApproach)
                  return (
                    <div className="bg-purple-50 border border-purple-200 rounded-lg overflow-hidden">
                      {/* Header with Regenerate button */}
                      <div className="flex items-center justify-between px-4 py-3 bg-purple-100/50 border-b border-purple-200">
                        <div className="flex items-center gap-1.5">
                          <svg className="w-4 h-4 text-purple-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M17 8h2a2 2 0 012 2v6a2 2 0 01-2 2h-2v4l-4-4H9a1.994 1.994 0 01-1.414-.586m0 0L11 14h4a2 2 0 002-2V6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2v4l.586-.586z" />
                          </svg>
                          <span className="text-xs font-semibold text-purple-700 uppercase tracking-wide">AI Sales Approach</span>
                        </div>
                        <button
                          onClick={handleRegenerateSalesApproach}
                          disabled={isRegenerating}
                          className="text-xs px-2 py-1 bg-purple-600 text-white rounded hover:bg-purple-700 disabled:bg-gray-400 disabled:cursor-not-allowed flex items-center gap-1"
                        >
                          <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                          </svg>
                          {isRegenerating ? 'Bezig...' : 'Regenereer'}
                        </button>
                      </div>

                      {/* Tabs */}
                      <div className="flex border-b border-purple-200 bg-purple-50">
                        <button
                          onClick={() => setActiveTab('linkedin')}
                          className={`flex-1 px-4 py-2 text-xs font-medium transition-colors ${
                            activeTab === 'linkedin'
                              ? 'bg-white text-purple-700 border-b-2 border-purple-600'
                              : 'text-purple-600 hover:bg-purple-100/50'
                          }`}
                        >
                          LinkedIn
                        </button>
                        <button
                          onClick={() => setActiveTab('phone')}
                          className={`flex-1 px-4 py-2 text-xs font-medium transition-colors ${
                            activeTab === 'phone'
                              ? 'bg-white text-purple-700 border-b-2 border-purple-600'
                              : 'text-purple-600 hover:bg-purple-100/50'
                          }`}
                        >
                          Telefoon
                        </button>
                        <button
                          onClick={() => setActiveTab('email')}
                          className={`flex-1 px-4 py-2 text-xs font-medium transition-colors ${
                            activeTab === 'email'
                              ? 'bg-white text-purple-700 border-b-2 border-purple-600'
                              : 'text-purple-600 hover:bg-purple-100/50'
                          }`}
                        >
                          Email
                        </button>
                      </div>

                      {/* Tab Content */}
                      <div className="p-4 bg-white">
                        {activeTab === 'linkedin' && approach.linkedinMessage && (
                          <div>
                            <p className="text-sm text-gray-900 leading-relaxed whitespace-pre-wrap mb-3">{approach.linkedinMessage}</p>
                            <button
                              onClick={() => copyToClipboard(approach.linkedinMessage, 'LinkedIn bericht')}
                              className="text-xs px-3 py-1.5 bg-purple-600 text-white rounded hover:bg-purple-700 flex items-center gap-1"
                            >
                              <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" />
                              </svg>
                              Kopieer
                            </button>
                          </div>
                        )}

                        {activeTab === 'phone' && approach.phoneOpener && (
                          <div>
                            <p className="text-sm text-gray-900 leading-relaxed whitespace-pre-wrap mb-3">{approach.phoneOpener}</p>
                            <button
                              onClick={() => copyToClipboard(approach.phoneOpener, 'Telefoon opener')}
                              className="text-xs px-3 py-1.5 bg-purple-600 text-white rounded hover:bg-purple-700 flex items-center gap-1"
                            >
                              <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" />
                              </svg>
                              Kopieer
                            </button>
                          </div>
                        )}

                        {activeTab === 'email' && approach.emailIntro && (
                          <div>
                            <p className="text-sm text-gray-900 leading-relaxed whitespace-pre-wrap mb-3">{approach.emailIntro}</p>
                            <button
                              onClick={() => copyToClipboard(approach.emailIntro, 'Email intro')}
                              className="text-xs px-3 py-1.5 bg-purple-600 text-white rounded hover:bg-purple-700 flex items-center gap-1"
                            >
                              <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" />
                              </svg>
                              Kopieer
                            </button>
                          </div>
                        )}
                      </div>
                    </div>
                  )
                } catch {
                  return null
                }
              })()}

              {/* Outreach Email Generator */}
              <OutreachEmailPanel lead={lead} />

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

              {/* Assignee (869ck3j4u) */}
              <div>
                <h3 className="text-sm font-semibold text-gray-700 mb-3 pb-1.5 border-b border-gray-100">
                  Toegewezen aan
                </h3>
                <select
                  value={lead.assignedToUserId ?? ''}
                  onChange={(e) => handleAssign(e.target.value || null)}
                  disabled={isAssigning}
                  className="w-full text-sm border border-gray-300 rounded-md px-2 py-1.5 focus:outline-none focus:ring-1 focus:ring-indigo-500 disabled:bg-gray-100"
                >
                  <option value="">— Niet toegewezen —</option>
                  {users.map((u) => (
                    <option key={u.id} value={u.id}>
                      {[u.firstName, u.lastName].filter(Boolean).join(' ') || u.email}
                    </option>
                  ))}
                </select>
              </div>

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

              {/* KvK Data */}
              {(lead.kvkNumber || lead.employeeCount || lead.foundingYear) && (
                <Section title="KvK Gegevens">
                  {lead.kvkNumber && <Field label="KvK nummer" value={lead.kvkNumber} />}
                  {lead.vatNumber && <Field label="BTW nummer" value={lead.vatNumber} />}
                  {lead.street && <Field label="Straat" value={lead.street} />}
                  {lead.zipCode && <Field label="Postcode" value={lead.zipCode} />}
                  {lead.employeeCount && <Field label="Aantal werknemers" value={lead.employeeCount} />}
                  {lead.foundingYear && <Field label="Opgericht" value={lead.foundingYear} />}
                  {lead.legalForm && <Field label="Rechtsvorm" value={lead.legalForm} />}
                </Section>
              )}

              {/* Google Places Data */}
              {(lead.googleRating || lead.googleReviewCount) && (
                <Section title="Google Reviews">
                  {lead.googleRating && (
                    <div>
                      <dt className="text-xs font-medium text-gray-500 uppercase tracking-wide">Beoordeling</dt>
                      <dd className="mt-0.5 text-sm text-gray-900 flex items-center gap-1">
                        <span className="text-yellow-500 font-semibold">{lead.googleRating.toFixed(1)}</span>
                        <span className="text-yellow-500">★</span>
                        {lead.googleReviewCount && (
                          <span className="text-gray-500 text-xs">({lead.googleReviewCount} reviews)</span>
                        )}
                      </dd>
                    </div>
                  )}
                  {lead.googleMapsUrl && <Field label="Google Maps" value={lead.googleMapsUrl} />}
                </Section>
              )}

              {/* Social Media */}
              {(lead.facebookUrl || lead.instagramUrl || lead.twitterUrl) && (
                <Section title="Social Media">
                  {lead.facebookUrl && <Field label="Facebook" value={lead.facebookUrl} />}
                  {lead.instagramUrl && <Field label="Instagram" value={lead.instagramUrl} />}
                  {lead.twitterUrl && <Field label="Twitter/X" value={lead.twitterUrl} />}
                </Section>
              )}

              {/* Reminder */}
              <Section title="Herinnering instellen">
                <div>
                  <dt className="text-xs font-medium text-gray-500 uppercase tracking-wide mb-1">Herinnerdatum</dt>
                  <div className="flex items-center gap-2">
                    <input
                      type="date"
                      value={reminderDate}
                      onChange={(e) => setReminderDate(e.target.value)}
                      className="flex-1 text-sm border border-gray-300 rounded-lg px-3 py-1.5 focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500"
                    />
                    <button
                      onClick={() => handleSetReminder(reminderDate)}
                      disabled={isSavingReminder}
                      className="text-xs px-3 py-1.5 bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 disabled:bg-gray-400 disabled:cursor-not-allowed whitespace-nowrap"
                    >
                      {isSavingReminder ? 'Opslaan...' : 'Sla op'}
                    </button>
                    {reminderDate && (
                      <button
                        onClick={() => { setReminderDate(''); handleSetReminder('') }}
                        disabled={isSavingReminder}
                        className="text-xs px-2 py-1.5 text-gray-500 hover:text-red-600 transition-colors"
                        title="Verwijder herinnering"
                      >
                        ✕
                      </button>
                    )}
                  </div>
                </div>
              </Section>

              {lead.enrichmentVersion === 2 && (
                <Section title="Crawl metadata">
                  <Field label="Gecrawled op" value={lead.crawledAt ? new Date(lead.crawledAt).toLocaleString('nl-NL') : null} />
                  <Field label="Pagina's gecrawled" value={lead.pagesCrawled} />
                  <Field label="Chunks geïndexeerd" value={lead.chunksIndexed} />
                  <Field label="Resolved URL" value={lead.resolvedUrl} />
                  <Field label="Website status" value={lead.websiteStatus} />
                </Section>
              )}

              {/* Activity Timeline (869ck3j4b) */}
              <div className="pb-1 border-t border-gray-100 pt-5">
                <LeadActivityTimeline leadId={lead.id} />
              </div>
            </div>
          </>
        )}
      </div>
    </>
  )
}

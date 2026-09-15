import { useEffect, useState } from 'react'
import { Link, useParams, useNavigate } from 'react-router-dom'
import { getClient, updateClient, type Client } from '../api/clients'
import { useToast } from '../components/Toast'
import {
  ENGAGEMENT_CATEGORIES,
  CATEGORY_DESCRIPTIONS,
  EngagementCategoryBadge,
  ReactiefBanner,
} from '../components/EngagementCategory'
import { intakeApi, type ClientIntake, type ClientBundle, STATUS_LABELS, STATUS_COLORS, PRODUCT_LABELS, BUNDLE_LABELS } from '../api/intake'

function StatusBadge({ status }: { status: string }) {
  const colorMap: Record<string, string> = {
    Active: 'bg-green-50 text-green-700 border-green-200',
    Completed: 'bg-blue-50 text-blue-700 border-blue-200',
    'On Hold': 'bg-amber-50 text-amber-700 border-amber-200',
  }
  const cls = colorMap[status] ?? 'bg-gray-50 text-gray-700 border-gray-200'
  return (
    <span className={`inline-flex items-center text-xs font-medium border rounded-full px-2 py-0.5 ${cls}`}>
      {status}
    </span>
  )
}

export default function ClientDetailPage() {
  const { id } = useParams<{ id: string }>()
  const { showToast } = useToast()
  const navigate = useNavigate()
  const [client, setClient] = useState<Client | null>(null)
  const [loading, setLoading] = useState(true)
  const [intakes, setIntakes] = useState<ClientIntake[]>([])
  const [bundle, setBundle] = useState<ClientBundle | null>(null)

  useEffect(() => {
    if (!id) return
    Promise.all([
      getClient(id),
      intakeApi.list(id).catch(() => ({ data: [] })),
      intakeApi.getBundles(id).catch(() => ({ data: [] })),
    ])
      .then(([clientData, intakesRes, bundlesRes]) => {
        setClient(clientData)
        setIntakes((intakesRes as any).data ?? [])
        const bundles = (bundlesRes as any).data ?? []
        setBundle(bundles.find((b: ClientBundle) => b.isActive) ?? null)
      })
      .catch(() => {
        showToast('Klant niet gevonden', 'error')
        navigate('/clients')
      })
      .finally(() => setLoading(false))
  }, [id])

  if (loading) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600" />
      </div>
    )
  }

  if (!client) return null

  const changeCategory = async (category: string) => {
    // PUT vervangt het hele record, dus altijd de volledige klant meesturen
    const prev = client
    setClient({ ...client, engagementCategory: category || null })
    try {
      const updated = await updateClient(client.id, { ...client, engagementCategory: category || null })
      setClient(updated)
    } catch {
      setClient(prev)
      showToast('Categorie opslaan mislukt', 'error')
    }
  }

  return (
    <div className="max-w-3xl">
      {/* Back navigation */}
      <div className="mb-5 flex items-center gap-3">
        <Link to="/clients" className="text-sm text-gray-500 hover:text-indigo-600 flex items-center gap-1">
          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 19l-7-7 7-7" />
          </svg>
          Klanten
        </Link>
        {client.sourceLeadId && (
          <>
            <span className="text-gray-300">·</span>
            <Link to="/leads" className="text-sm text-gray-500 hover:text-indigo-600">
              Terug naar leads
            </Link>
          </>
        )}
      </div>

      {client.engagementCategory === 'Reactief' && <ReactiefBanner />}

      {/* Client header */}
      <div className="bg-white rounded-xl border border-gray-200 p-6 mb-4">
        <div className="flex items-start justify-between mb-4">
          <div>
            <h1 className="text-2xl font-bold text-gray-900">{client.name}</h1>
            <div className="flex items-center gap-2 mt-1.5">
              <EngagementCategoryBadge category={client.engagementCategory} />
              {client.plan && (
                <span className="text-sm text-gray-600 bg-gray-100 rounded-full px-2.5 py-0.5 font-medium">
                  {client.plan}
                </span>
              )}
              {!client.isActive && (
                <span className="text-xs text-red-600 bg-red-50 border border-red-200 rounded-full px-2 py-0.5">
                  Inactief
                </span>
              )}
            </div>
          </div>
          {client.website && (
            <a
              href={client.website}
              target="_blank"
              rel="noopener noreferrer"
              className="text-sm text-indigo-600 hover:underline flex items-center gap-1"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 12a9 9 0 01-9 9m9-9a9 9 0 00-9-9m9 9H3m9 9a9 9 0 01-9-9m9 9c1.657 0 3-4.03 3-9s-1.343-9-3-9m0 18c-1.657 0-3-4.03-3-9s1.343-9 3-9" />
              </svg>
              Website
            </a>
          )}
        </div>

        <div className="grid grid-cols-2 gap-3 text-sm">
          {client.city && (
            <div>
              <span className="text-xs text-gray-500 uppercase tracking-wide font-medium">Stad</span>
              <p className="text-gray-900 mt-0.5">{client.city}</p>
            </div>
          )}
          {client.sector && (
            <div>
              <span className="text-xs text-gray-500 uppercase tracking-wide font-medium">Sector</span>
              <p className="text-gray-900 mt-0.5">{client.sector}</p>
            </div>
          )}
          <div>
            <span className="text-xs text-gray-500 uppercase tracking-wide font-medium">Klant sinds</span>
            <p className="text-gray-900 mt-0.5">{new Date(client.createdAt).toLocaleDateString('nl-NL')}</p>
          </div>
          <div>
            <span className="text-xs text-gray-500 uppercase tracking-wide font-medium">Omgangscategorie</span>
            <select
              value={client.engagementCategory ?? ''}
              onChange={e => changeCategory(e.target.value)}
              className="block mt-0.5 border border-gray-300 rounded-lg px-2 py-1 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500"
            >
              <option value="">Geen</option>
              {ENGAGEMENT_CATEGORIES.map(c => (
                <option key={c} value={c}>{c}</option>
              ))}
            </select>
            {client.engagementCategory && CATEGORY_DESCRIPTIONS[client.engagementCategory] && (
              <p className="text-xs text-gray-400 mt-1">{CATEGORY_DESCRIPTIONS[client.engagementCategory]}</p>
            )}
          </div>
        </div>

        {client.notes && (
          <div className="mt-4 pt-4 border-t border-gray-100">
            <span className="text-xs text-gray-500 uppercase tracking-wide font-medium">Notities</span>
            <p className="text-sm text-gray-900 mt-1 leading-relaxed">{client.notes}</p>
          </div>
        )}
      </div>

      {/* Primary contact */}
      {(client.primaryContactName || client.primaryContactEmail || client.primaryContactPhone) && (
        <div className="bg-white rounded-xl border border-gray-200 p-6 mb-4">
          <h2 className="text-base font-semibold text-gray-900 mb-4">Primair contact</h2>
          <div className="space-y-2 text-sm">
            {client.primaryContactName && (
              <div className="flex items-center gap-2.5">
                <svg className="w-4 h-4 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M16 7a4 4 0 11-8 0 4 4 0 018 0zM12 14a7 7 0 00-7 7h14a7 7 0 00-7-7z" />
                </svg>
                <span className="text-gray-900 font-medium">{client.primaryContactName}</span>
              </div>
            )}
            {client.primaryContactEmail && (
              <div className="flex items-center gap-2.5">
                <svg className="w-4 h-4 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 8l7.89 5.26a2 2 0 002.22 0L21 8M5 19h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
                </svg>
                <a href={`mailto:${client.primaryContactEmail}`} className="text-indigo-600 hover:underline">
                  {client.primaryContactEmail}
                </a>
              </div>
            )}
            {client.primaryContactPhone && (
              <div className="flex items-center gap-2.5">
                <svg className="w-4 h-4 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 5a2 2 0 012-2h3.28a1 1 0 01.948.684l1.498 4.493a1 1 0 01-.502 1.21l-2.257 1.13a11.042 11.042 0 005.516 5.516l1.13-2.257a1 1 0 011.21-.502l4.493 1.498a1 1 0 01.684.949V19a2 2 0 01-2 2h-1C9.716 21 3 14.284 3 6V5z" />
                </svg>
                <a href={`tel:${client.primaryContactPhone}`} className="text-indigo-600 hover:underline">
                  {client.primaryContactPhone}
                </a>
              </div>
            )}
          </div>
        </div>
      )}

      {/* Bundle status */}
      {bundle && (
        <div className="bg-white rounded-xl border border-gray-200 p-6 mb-4">
          <div className="flex items-center justify-between mb-3">
            <h2 className="text-base font-semibold text-gray-900">Bundel</h2>
            <span className="text-xs text-gray-500">{BUNDLE_LABELS[bundle.bundleType]}</span>
          </div>
          {bundle.bundleType !== 'PayPerHour' && (
            <div>
              <div className="flex justify-between text-xs text-gray-600 mb-1">
                <span>{bundle.hoursUsed.toFixed(1)} uur gebruikt</span>
                <span>{bundle.hoursRemaining.toFixed(1)} uur resterend</span>
              </div>
              <div className="w-full bg-gray-200 rounded-full h-2">
                <div
                  className={`h-2 rounded-full ${bundle.isOverBudget ? 'bg-red-500' : 'bg-green-500'}`}
                  style={{ width: `${Math.min(100, (bundle.hoursUsed / bundle.totalHours) * 100)}%` }}
                />
              </div>
              {bundle.isOverBudget && (
                <p className="text-xs text-red-600 mt-1">Bundel overschreden — losse uren à €{bundle.hourlyRate}/u</p>
              )}
            </div>
          )}
        </div>
      )}

      {/* Intakes */}
      <div className="bg-white rounded-xl border border-gray-200 p-6 mb-4">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-base font-semibold text-gray-900">
            Intakes
            <span className="ml-2 text-sm font-normal text-gray-400">({intakes.length})</span>
          </h2>
          <button
            onClick={() => navigate(`/clients/${id}/intake`)}
            className="px-3 py-1.5 text-xs font-medium text-white bg-blue-600 rounded-lg hover:bg-blue-700"
          >
            + Nieuwe intake
          </button>
        </div>
        {intakes.length === 0 ? (
          <p className="text-sm text-gray-400">Nog geen intakes. Klik op "Nieuwe intake" om te starten.</p>
        ) : (
          <div className="space-y-2">
            {intakes.slice(0, 3).map((intake) => (
              <div
                key={intake.id}
                className="flex items-center justify-between p-3 bg-gray-50 rounded-lg cursor-pointer hover:bg-gray-100"
                onClick={() => navigate(`/clients/${id}/intake`)}
              >
                <div className="min-w-0">
                  <p className="text-sm font-medium text-gray-900 truncate">
                    {intake.firstTask || intake.requirements.slice(0, 50)}
                  </p>
                  <p className="text-xs text-gray-500 mt-0.5">{PRODUCT_LABELS[intake.productType]}</p>
                </div>
                <div className="flex items-center gap-3 ml-4 flex-shrink-0">
                  <span className={`text-xs font-medium px-2 py-0.5 rounded-full ${STATUS_COLORS[intake.status]}`}>
                    {STATUS_LABELS[intake.status]}
                  </span>
                  {intake.estimatedHours && (
                    <span className="text-xs text-gray-500">~{intake.estimatedHours}u</span>
                  )}
                </div>
              </div>
            ))}
            {intakes.length > 3 && (
              <button
                onClick={() => navigate(`/clients/${id}/intake`)}
                className="text-xs text-blue-600 hover:underline mt-1"
              >
                Alle {intakes.length} intakes bekijken →
              </button>
            )}
          </div>
        )}
      </div>

      {/* Projects */}
      <div className="bg-white rounded-xl border border-gray-200 p-6">
        <h2 className="text-base font-semibold text-gray-900 mb-4">
          Projecten
          <span className="ml-2 text-sm font-normal text-gray-400">({client.projects.length})</span>
        </h2>
        {client.projects.length === 0 ? (
          <p className="text-sm text-gray-400">Geen projecten gevonden.</p>
        ) : (
          <div className="space-y-3">
            {client.projects.map((project) => (
              <div key={project.id} className="flex items-center justify-between p-3 bg-gray-50 rounded-lg">
                <div>
                  <p className="text-sm font-medium text-gray-900">{project.name}</p>
                  {project.description && (
                    <p className="text-xs text-gray-500 mt-0.5">{project.description}</p>
                  )}
                </div>
                <div className="flex items-center gap-3 ml-4 flex-shrink-0">
                  <StatusBadge status={project.status} />
                  <span className="text-xs text-gray-400">
                    {new Date(project.createdAt).toLocaleDateString('nl-NL')}
                  </span>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}

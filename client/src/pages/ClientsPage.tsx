import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { getClients, type Client } from '../api/clients'
import { useToast } from '../components/Toast'

function PlanBadge({ plan }: { plan?: string | null }) {
  if (!plan) return null
  const colorMap: Record<string, string> = {
    Starter: 'bg-blue-50 text-blue-700 border-blue-200',
    Pro: 'bg-purple-50 text-purple-700 border-purple-200',
    Enterprise: 'bg-amber-50 text-amber-700 border-amber-200',
  }
  const cls = colorMap[plan] ?? 'bg-gray-50 text-gray-700 border-gray-200'
  return (
    <span className={`inline-flex items-center text-xs font-medium border rounded-full px-2 py-0.5 ${cls}`}>
      {plan}
    </span>
  )
}

export default function ClientsPage() {
  const { showToast } = useToast()
  const [clients, setClients] = useState<Client[]>([])
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    getClients()
      .then(setClients)
      .catch(() => showToast('Klanten laden mislukt', 'error'))
      .finally(() => setLoading(false))
  }, [])

  if (loading) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600" />
      </div>
    )
  }

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Klanten</h1>
          <p className="text-sm text-gray-500 mt-1">{clients.length} klant{clients.length !== 1 ? 'en' : ''}</p>
        </div>
      </div>

      {clients.length === 0 ? (
        <div className="text-center py-16 text-gray-500">
          <svg className="w-12 h-12 mx-auto mb-4 text-gray-300" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M17 20h5v-2a3 3 0 00-5.356-1.857M17 20H7m10 0v-2c0-.656-.126-1.283-.356-1.857M7 20H2v-2a3 3 0 015.356-1.857M7 20v-2c0-.656.126-1.283.356-1.857m0 0a5.002 5.002 0 019.288 0M15 7a3 3 0 11-6 0 3 3 0 016 0z" />
          </svg>
          <p className="text-sm font-medium">Nog geen klanten</p>
          <p className="text-xs mt-1 text-gray-400">Converteer een lead naar klant om hier te beginnen.</p>
          <Link to="/leads" className="mt-4 inline-block text-sm text-indigo-600 hover:underline">
            Naar leads →
          </Link>
        </div>
      ) : (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
          {clients.map((client) => (
            <Link
              key={client.id}
              to={`/clients/${client.id}`}
              className="bg-white rounded-xl border border-gray-200 p-5 hover:shadow-md hover:border-indigo-200 transition-all group"
            >
              <div className="flex items-start justify-between mb-3">
                <h3 className="font-semibold text-gray-900 group-hover:text-indigo-700 transition-colors line-clamp-1">
                  {client.name}
                </h3>
                <PlanBadge plan={client.plan} />
              </div>

              <div className="space-y-1.5 text-sm text-gray-600">
                {client.primaryContactName && (
                  <div className="flex items-center gap-1.5">
                    <svg className="w-3.5 h-3.5 text-gray-400 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M16 7a4 4 0 11-8 0 4 4 0 018 0zM12 14a7 7 0 00-7 7h14a7 7 0 00-7-7z" />
                    </svg>
                    <span className="truncate">{client.primaryContactName}</span>
                  </div>
                )}
                {client.primaryContactEmail && (
                  <div className="flex items-center gap-1.5">
                    <svg className="w-3.5 h-3.5 text-gray-400 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 8l7.89 5.26a2 2 0 002.22 0L21 8M5 19h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
                    </svg>
                    <span className="truncate">{client.primaryContactEmail}</span>
                  </div>
                )}
                {(client.city || client.sector) && (
                  <div className="flex items-center gap-1.5">
                    <svg className="w-3.5 h-3.5 text-gray-400 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M17.657 16.657L13.414 20.9a1.998 1.998 0 01-2.827 0l-4.244-4.243a8 8 0 1111.314 0z" />
                    </svg>
                    <span className="truncate">{[client.city, client.sector].filter(Boolean).join(' · ')}</span>
                  </div>
                )}
                {client.website && (
                  <div className="flex items-center gap-1.5">
                    <svg className="w-3.5 h-3.5 text-gray-400 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 12a9 9 0 01-9 9m9-9a9 9 0 00-9-9m9 9H3m9 9a9 9 0 01-9-9m9 9c1.657 0 3-4.03 3-9s-1.343-9-3-9m0 18c-1.657 0-3-4.03-3-9s1.343-9 3-9" />
                    </svg>
                    <span className="truncate text-indigo-600">{client.website}</span>
                  </div>
                )}
              </div>

              <div className="mt-3 pt-3 border-t border-gray-100 flex items-center justify-between text-xs text-gray-400">
                <span>{client.projects.length} project{client.projects.length !== 1 ? 'en' : ''}</span>
                <span>{new Date(client.createdAt).toLocaleDateString('nl-NL')}</span>
              </div>
            </Link>
          ))}
        </div>
      )}
    </div>
  )
}

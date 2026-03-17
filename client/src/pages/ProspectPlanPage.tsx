import { useEffect, useState } from 'react'
import { useParams, useNavigate, Link } from 'react-router-dom'
import { fetchLeads } from '../api/leads'
import type { Lead } from '../api/leads'
import ProspectPlanPanel from '../components/leads/ProspectPlanPanel'
import { useToast } from '../components/Toast'

export default function ProspectPlanPage() {
  const { leadId } = useParams<{ leadId: string }>()
  const navigate = useNavigate()
  const { showToast } = useToast()
  const [lead, setLead] = useState<Lead | null>(null)
  const [isLoading, setIsLoading] = useState(true)

  useEffect(() => {
    if (!leadId) return

    const loadLead = async () => {
      setIsLoading(true)
      try {
        // Fetch the specific lead - API might need a new endpoint for single lead
        // For now, fetch with filter
        const result = await fetchLeads({ page: 1, pageSize: 1000 })
        const foundLead = result.items.find((l) => l.id === leadId)

        if (!foundLead) {
          showToast('Lead niet gevonden', 'error')
          navigate('/leads')
          return
        }

        setLead(foundLead)
      } catch (err) {
        showToast('Fout bij laden van lead', 'error')
        navigate('/leads')
      } finally {
        setIsLoading(false)
      }
    }

    loadLead()
  }, [leadId, navigate, showToast])

  if (isLoading) {
    return (
      <div className="flex items-center justify-center h-full">
        <div className="text-center">
          <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-purple-600 mb-4"></div>
          <p className="text-gray-600">Lead laden...</p>
        </div>
      </div>
    )
  }

  if (!lead) {
    return (
      <div className="flex items-center justify-center h-full">
        <div className="text-center">
          <p className="text-gray-600 mb-4">Lead niet gevonden</p>
          <Link to="/leads" className="text-purple-600 hover:underline">
            Terug naar leads
          </Link>
        </div>
      </div>
    )
  }

  return (
    <div className="flex flex-col gap-4 h-full">
      {/* Breadcrumb and header */}
      <div className="flex items-center gap-2 text-sm">
        <Link to="/leads" className="text-purple-600 hover:underline">
          Leads
        </Link>
        <span className="text-gray-400">/</span>
        <span className="text-gray-900 font-medium">{lead.name}</span>
        <span className="text-gray-400">/</span>
        <span className="text-gray-600">Prospect Plan</span>
      </div>

      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">{lead.name}</h1>
          {lead.website && (
            <a
              href={lead.resolvedUrl || lead.website}
              target="_blank"
              rel="noopener noreferrer"
              className="text-sm text-purple-600 hover:underline"
            >
              {lead.website}
            </a>
          )}
        </div>
        <button
          onClick={() => navigate('/leads')}
          className="px-4 py-2 text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50"
        >
          Terug
        </button>
      </div>

      {/* Prospect Plan Panel */}
      <div className="flex-1 overflow-y-auto">
        <ProspectPlanPanel
          leadId={lead.id}
          initialPlan={lead.prospectPlan || null}
          isProspect={lead.status === 1}
        />
      </div>
    </div>
  )
}

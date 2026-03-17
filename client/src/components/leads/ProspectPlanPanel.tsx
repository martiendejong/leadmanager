import { useState } from 'react'
import { generateProspectPlan } from '../../api/leads'
import { useToast } from '../Toast'

interface Props {
  leadId: string
  prospectPlan: string | null | undefined
  onPlanGenerated: () => void
}

export default function ProspectPlanPanel({ leadId, prospectPlan, onPlanGenerated }: Props) {
  const { showToast } = useToast()
  const [isGenerating, setIsGenerating] = useState(false)

  const handleGeneratePlan = async () => {
    if (!confirm('Wil je een AI-gegenereerd prospectplan maken op basis van de beschikbare informatie?')) return

    setIsGenerating(true)
    try {
      await generateProspectPlan(leadId)
      showToast('Prospectplan gegenereerd!', 'success')
      onPlanGenerated() // Reload to show the generated plan
    } catch (err: any) {
      showToast(err.response?.data || 'Plan genereren mislukt', 'error')
    } finally {
      setIsGenerating(false)
    }
  }

  if (!prospectPlan) {
    return (
      <div className="bg-purple-50 border border-purple-200 rounded-lg p-6 text-center">
        <div className="mb-4">
          <svg className="w-12 h-12 text-purple-400 mx-auto" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
          </svg>
        </div>
        <h3 className="text-lg font-semibold text-purple-900 mb-2">Nog geen prospectplan</h3>
        <p className="text-sm text-purple-700 mb-4">
          Genereer een gestructureerd actieplan om dit bedrijf als klant binnen te halen.
        </p>
        <button
          onClick={handleGeneratePlan}
          disabled={isGenerating}
          className="px-4 py-2 bg-purple-600 text-white rounded-md hover:bg-purple-700 disabled:bg-gray-400 disabled:cursor-not-allowed"
        >
          {isGenerating ? 'Bezig met genereren...' : 'Genereer AI Prospectplan'}
        </button>
      </div>
    )
  }

  return (
    <div className="bg-white border border-gray-200 rounded-lg">
      {/* Header */}
      <div className="flex items-center justify-between px-4 py-3 bg-purple-50 border-b border-purple-200">
        <div className="flex items-center gap-2">
          <svg className="w-5 h-5 text-purple-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
          </svg>
          <span className="text-sm font-semibold text-purple-900">Prospectplan</span>
        </div>
        <button
          onClick={handleGeneratePlan}
          disabled={isGenerating}
          className="text-xs px-3 py-1 bg-purple-600 text-white rounded hover:bg-purple-700 disabled:bg-gray-400 disabled:cursor-not-allowed flex items-center gap-1"
        >
          <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
          </svg>
          {isGenerating ? 'Bezig...' : 'Regenereer'}
        </button>
      </div>

      {/* Plan Content */}
      <div className="p-4">
        <div className="prose prose-sm max-w-none">
          <pre className="whitespace-pre-wrap text-sm text-gray-900 leading-relaxed font-sans">
            {prospectPlan}
          </pre>
        </div>
      </div>

      {/* Edit note (Task #5) */}
      <div className="px-4 py-3 bg-gray-50 border-t border-gray-200">
        <p className="text-xs text-gray-500 italic">
          💡 WYSIWYG editor voor het bewerken van het plan komt in de volgende update (Taak #5)
        </p>
      </div>
    </div>
  )
}

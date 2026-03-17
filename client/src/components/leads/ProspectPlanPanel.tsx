import { useState } from 'react'
import { generateProspectPlan, updateProspectPlan } from '../../api/leads'
import ProspectPlanEditor from './ProspectPlanEditor'

interface Props {
  leadId: string
  initialPlan: string | null
  isProspect: boolean
}

export default function ProspectPlanPanel({ leadId, initialPlan, isProspect }: Props) {
  const [plan, setPlan] = useState<string | null>(initialPlan)
  const [isGenerating, setIsGenerating] = useState(false)
  const [isEditing, setIsEditing] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const handleGenerate = async () => {
    setError(null)
    setIsGenerating(true)
    try {
      const result = await generateProspectPlan(leadId)
      setPlan(result.plan)
    } catch (err: any) {
      setError(err.response?.data || 'Er is een fout opgetreden bij het genereren van het plan')
    } finally {
      setIsGenerating(false)
    }
  }

  const handleSave = async (newPlan: string) => {
    setError(null)
    try {
      const result = await updateProspectPlan(leadId, newPlan)
      setPlan(result.plan)
      setIsEditing(false)
    } catch (err: any) {
      setError(err.response?.data || 'Er is een fout opgetreden bij het opslaan')
      throw err // Re-throw so editor can handle it
    }
  }

  const handleCancel = () => {
    setIsEditing(false)
    setError(null)
  }

  if (!isProspect) {
    return (
      <div className="bg-white rounded-lg shadow p-6">
        <h2 className="text-xl font-semibold mb-4">Prospect Plan</h2>
        <p className="text-gray-600">
          Deze lead is nog geen prospect. Zet de status naar "Prospect" om een plan te genereren.
        </p>
      </div>
    )
  }

  return (
    <div className="bg-white rounded-lg shadow p-6">
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-xl font-semibold">Prospect Plan</h2>
        {plan && !isEditing && (
          <div className="flex gap-2">
            <button
              onClick={handleGenerate}
              disabled={isGenerating}
              className="px-4 py-2 text-sm text-purple-600 bg-purple-50 rounded-md hover:bg-purple-100 disabled:bg-gray-100 disabled:text-gray-400 disabled:cursor-not-allowed"
            >
              {isGenerating ? 'Opnieuw genereren...' : 'Opnieuw genereren'}
            </button>
            <button
              onClick={() => setIsEditing(true)}
              className="px-4 py-2 text-sm text-white bg-purple-600 rounded-md hover:bg-purple-700"
            >
              Bewerken
            </button>
          </div>
        )}
      </div>

      {error && (
        <div className="mb-4 p-4 bg-red-50 border border-red-200 rounded-md">
          <p className="text-sm text-red-600">{error}</p>
        </div>
      )}

      {!plan && !isGenerating && (
        <div className="text-center py-8">
          <p className="text-gray-600 mb-4">
            Er is nog geen plan gegenereerd voor deze prospect.
          </p>
          <button
            onClick={handleGenerate}
            className="px-6 py-3 text-white bg-purple-600 rounded-md hover:bg-purple-700"
          >
            Genereer Plan
          </button>
        </div>
      )}

      {isGenerating && (
        <div className="text-center py-8">
          <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-purple-600 mb-4"></div>
          <p className="text-gray-600">Plan wordt gegenereerd...</p>
        </div>
      )}

      {plan && !isEditing && (
        <div
          className="prose prose-sm max-w-none"
          dangerouslySetInnerHTML={{ __html: plan }}
        />
      )}

      {plan && isEditing && (
        <ProspectPlanEditor
          initialContent={plan}
          onSave={handleSave}
          onCancel={handleCancel}
        />
      )}
    </div>
  )
}

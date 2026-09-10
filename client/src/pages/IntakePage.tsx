import { useState } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import {
  intakeApi,
  type ClientIntake,
  type IntakeProductType,
  type IntakeBundleType,
  BUNDLE_LABELS,
  PRODUCT_LABELS,
  STATUS_LABELS,
  STATUS_COLORS,
} from '../api/intake'
import { useToast } from '../components/Toast'

const BUNDLE_PRICES: Record<IntakeBundleType, { hours: number; monthly: number; hourly: number }> = {
  Starter: { hours: 50, monthly: 125, hourly: 2.5 },
  Team: { hours: 200, monthly: 500, hourly: 2.5 },
  PayPerHour: { hours: 0, monthly: 0, hourly: 3 },
}

interface IntakeFormProps {
  clientId: string
  clientName: string
  onSuccess: (intake: ClientIntake) => void
  onCancel: () => void
}

function IntakeForm({ clientId, clientName, onSuccess, onCancel }: IntakeFormProps) {
  const { showToast } = useToast()
  const [productType, setProductType] = useState<IntakeProductType>('Website')
  const [bundleType, setBundleType] = useState<IntakeBundleType>('Starter')
  const [requirements, setRequirements] = useState('')
  const [firstTask, setFirstTask] = useState('')
  const [additionalNotes, setAdditionalNotes] = useState('')
  const [submitting, setSubmitting] = useState(false)

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!requirements.trim()) return
    setSubmitting(true)
    try {
      const res = await intakeApi.create({ clientId, productType, bundleType, requirements, firstTask, additionalNotes })
      showToast('Intake aangemaakt', 'success')
      onSuccess(res.data)
    } catch {
      showToast('Aanmaken mislukt', 'error')
    } finally {
      setSubmitting(false)
    }
  }

  const bundle = BUNDLE_PRICES[bundleType]

  return (
    <form onSubmit={handleSubmit} className="space-y-6">
      <div>
        <h2 className="text-lg font-semibold text-gray-900 mb-1">Nieuwe intake — {clientName}</h2>
        <p className="text-sm text-gray-500">
          Beschrijf wat de klant nodig heeft. Jengo schat de uren en prijs, waarna jij akkoord geeft.
        </p>
      </div>

      {/* Product type */}
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-2">Producttype</label>
        <div className="grid grid-cols-2 gap-2">
          {(Object.keys(PRODUCT_LABELS) as IntakeProductType[]).map((t) => (
            <button
              key={t}
              type="button"
              onClick={() => setProductType(t)}
              className={`p-3 rounded-lg border-2 text-left text-sm transition-all ${
                productType === t
                  ? 'border-blue-500 bg-blue-50 text-blue-800'
                  : 'border-gray-200 text-gray-700 hover:border-gray-300'
              }`}
            >
              {PRODUCT_LABELS[t]}
            </button>
          ))}
        </div>
      </div>

      {/* Bundle */}
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-2">Bundel</label>
        <div className="space-y-2">
          {(Object.keys(BUNDLE_LABELS) as IntakeBundleType[]).map((b) => {
            const info = BUNDLE_PRICES[b]
            return (
              <button
                key={b}
                type="button"
                onClick={() => setBundleType(b)}
                className={`w-full p-3 rounded-lg border-2 text-left transition-all flex justify-between items-center ${
                  bundleType === b
                    ? 'border-blue-500 bg-blue-50'
                    : 'border-gray-200 hover:border-gray-300'
                }`}
              >
                <span className="text-sm font-medium text-gray-800">{BUNDLE_LABELS[b]}</span>
                {b !== 'PayPerHour' && (
                  <span className="text-xs text-gray-500">{info.hours}u · €{info.hourly}/u</span>
                )}
              </button>
            )
          })}
        </div>
        {bundleType !== 'PayPerHour' && (
          <p className="mt-2 text-xs text-gray-500">
            Bundel van {bundle.hours} uur voor €{bundle.monthly}/maand · uren bovenop bundel à €3/uur.
          </p>
        )}
      </div>

      {/* Requirements */}
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">
          Requirements <span className="text-red-500">*</span>
        </label>
        <textarea
          value={requirements}
          onChange={(e) => setRequirements(e.target.value)}
          rows={5}
          className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          placeholder="Beschrijf zo concreet mogelijk wat er gebouwd of gedaan moet worden..."
          required
        />
      </div>

      {/* First task */}
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Eerste taak (optioneel)</label>
        <input
          type="text"
          value={firstTask}
          onChange={(e) => setFirstTask(e.target.value)}
          className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          placeholder="Bijv. 'Homepage herontwerpen met nieuwe huisstijl'"
        />
      </div>

      {/* Notes */}
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">Extra notities (optioneel)</label>
        <textarea
          value={additionalNotes}
          onChange={(e) => setAdditionalNotes(e.target.value)}
          rows={2}
          className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          placeholder="Deadlines, technische beperkingen, referenties..."
        />
      </div>

      <div className="flex gap-3 justify-end">
        <button
          type="button"
          onClick={onCancel}
          className="px-4 py-2 text-sm text-gray-700 border border-gray-300 rounded-lg hover:bg-gray-50"
        >
          Annuleren
        </button>
        <button
          type="submit"
          disabled={submitting || !requirements.trim()}
          className="px-5 py-2 text-sm font-medium text-white bg-blue-600 rounded-lg hover:bg-blue-700 disabled:opacity-50"
        >
          {submitting ? 'Aanmaken...' : 'Intake aanmaken'}
        </button>
      </div>
    </form>
  )
}

interface IntakeCardProps {
  intake: ClientIntake
  onRefresh: () => void
}

function IntakeCard({ intake, onRefresh }: IntakeCardProps) {
  const { showToast } = useToast()
  const [loading, setLoading] = useState(false)

  const handleSubmit = async () => {
    setLoading(true)
    try {
      await intakeApi.submit(intake.id)
      showToast('AI schat de uren...', 'success')
      onRefresh()
    } catch {
      showToast('Fout bij indienen', 'error')
    } finally {
      setLoading(false)
    }
  }

  const handleApprove = async (approved: boolean) => {
    setLoading(true)
    try {
      await intakeApi.approve(intake.id, approved)
      showToast(approved ? 'Intake goedgekeurd — taak naar werkwachtrij' : 'Intake teruggestuurd', 'success')
      onRefresh()
    } catch {
      showToast('Fout bij verwerken', 'error')
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="bg-white border border-gray-200 rounded-xl p-5 shadow-sm">
      <div className="flex items-start justify-between mb-3">
        <div>
          <span className="text-xs text-gray-500">{PRODUCT_LABELS[intake.productType]}</span>
          <h3 className="text-sm font-semibold text-gray-900 mt-0.5 line-clamp-1">
            {intake.firstTask || intake.requirements.slice(0, 60)}
          </h3>
        </div>
        <span className={`text-xs font-medium px-2 py-1 rounded-full ${STATUS_COLORS[intake.status]}`}>
          {STATUS_LABELS[intake.status]}
        </span>
      </div>

      <p className="text-xs text-gray-600 mb-3 line-clamp-2">{intake.requirements}</p>

      <div className="flex items-center gap-3 text-xs text-gray-500 mb-4">
        <span>{BUNDLE_LABELS[intake.bundleType]}</span>
        {intake.estimatedHours && (
          <>
            <span>·</span>
            <span className="font-medium text-gray-700">~{intake.estimatedHours}u</span>
            <span>·</span>
            <span className="font-medium text-green-700">€{intake.estimatedPrice?.toFixed(2)}</span>
          </>
        )}
      </div>

      {intake.estimationReasoning && intake.status === 'Estimated' && (
        <div className="bg-yellow-50 border border-yellow-200 rounded-lg p-3 mb-4 text-xs text-yellow-800">
          <strong>Schatting:</strong> {intake.estimationReasoning}
        </div>
      )}

      {intake.workQueueItemId && (
        <div className="text-xs text-gray-400 mb-3">
          Werkwachtrij ID: <code className="bg-gray-100 px-1 rounded">{intake.workQueueItemId}</code>
        </div>
      )}

      {/* Actions */}
      <div className="flex gap-2">
        {intake.status === 'Draft' && (
          <button
            onClick={handleSubmit}
            disabled={loading}
            className="px-3 py-1.5 text-xs font-medium text-white bg-blue-600 rounded-lg hover:bg-blue-700 disabled:opacity-50"
          >
            {loading ? 'Bezig...' : 'Laat schatten'}
          </button>
        )}
        {intake.status === 'Estimated' && (
          <>
            <button
              onClick={() => handleApprove(true)}
              disabled={loading}
              className="px-3 py-1.5 text-xs font-medium text-white bg-green-600 rounded-lg hover:bg-green-700 disabled:opacity-50"
            >
              Akkoord
            </button>
            <button
              onClick={() => handleApprove(false)}
              disabled={loading}
              className="px-3 py-1.5 text-xs font-medium text-gray-700 border border-gray-300 rounded-lg hover:bg-gray-50 disabled:opacity-50"
            >
              Herzien
            </button>
          </>
        )}
      </div>
    </div>
  )
}

export default function IntakePage() {
  const { clientId } = useParams<{ clientId: string }>()
  const navigate = useNavigate()
  const [intakes, setIntakes] = useState<ClientIntake[]>([])
  const [loading, setLoading] = useState(true)
  const [showForm, setShowForm] = useState(false)
  const [clientName, setClientName] = useState('')

  const loadIntakes = async () => {
    if (!clientId) return
    try {
      const res = await intakeApi.list(clientId)
      setIntakes(res.data)
      if (res.data.length > 0) setClientName(res.data[0].clientName)
    } catch {
      // ignore
    } finally {
      setLoading(false)
    }
  }

  useState(() => {
    loadIntakes()
  })

  if (!clientId) return null

  return (
    <div className="max-w-2xl mx-auto py-8 px-4">
      <div className="flex items-center gap-3 mb-6">
        <button
          onClick={() => navigate(`/clients/${clientId}`)}
          className="text-sm text-gray-500 hover:text-gray-700"
        >
          ← Klant
        </button>
        <h1 className="text-xl font-bold text-gray-900">Intakes{clientName ? ` — ${clientName}` : ''}</h1>
        <div className="ml-auto">
          <button
            onClick={() => setShowForm(true)}
            className="px-4 py-2 text-sm font-medium text-white bg-blue-600 rounded-lg hover:bg-blue-700"
          >
            + Nieuwe intake
          </button>
        </div>
      </div>

      {showForm && clientId && (
        <div className="bg-white border border-gray-200 rounded-xl p-6 mb-6 shadow-sm">
          <IntakeForm
            clientId={clientId}
            clientName={clientName}
            onSuccess={(intake) => {
              setIntakes((prev) => [intake, ...prev])
              setShowForm(false)
            }}
            onCancel={() => setShowForm(false)}
          />
        </div>
      )}

      {loading ? (
        <div className="text-center text-gray-400 py-12">Laden...</div>
      ) : intakes.length === 0 && !showForm ? (
        <div className="text-center py-12">
          <p className="text-gray-500 mb-4">Nog geen intakes voor deze klant.</p>
          <button
            onClick={() => setShowForm(true)}
            className="px-5 py-2 text-sm font-medium text-white bg-blue-600 rounded-lg hover:bg-blue-700"
          >
            Eerste intake aanmaken
          </button>
        </div>
      ) : (
        <div className="space-y-4">
          {intakes.map((intake) => (
            <IntakeCard key={intake.id} intake={intake} onRefresh={loadIntakes} />
          ))}
        </div>
      )}
    </div>
  )
}

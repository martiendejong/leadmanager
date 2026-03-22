import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import type { Lead } from '../../api/leads'
import { convertLeadToClient } from '../../api/clients'
import { useToast } from '../Toast'

interface Props {
  lead: Lead
  onClose: () => void
}

const PLAN_SUGGESTIONS = ['Starter', 'Pro', 'Enterprise']

export default function ConvertToClientWizard({ lead, onClose }: Props) {
  const { showToast } = useToast()
  const navigate = useNavigate()
  const [step, setStep] = useState(1)
  const [submitting, setSubmitting] = useState(false)

  // Step 1: company details
  const [name, setName] = useState(lead.name)
  const [plan, setPlan] = useState('')
  const [notes, setNotes] = useState('')

  // Step 2: primary contact
  const [contactName, setContactName] = useState(lead.ownerName || '')
  const [contactEmail, setContactEmail] = useState(lead.personalEmail || lead.companyEmail || '')
  const [contactPhone, setContactPhone] = useState(lead.phone || '')

  const handleSubmit = async () => {
    setSubmitting(true)
    try {
      const client = await convertLeadToClient(lead.id, {
        name,
        plan: plan || null,
        primaryContactName: contactName || null,
        primaryContactEmail: contactEmail || null,
        primaryContactPhone: contactPhone || null,
        notes: notes || null,
      })
      showToast(`${client.name} is succesvol geconverteerd naar klant!`, 'success')
      onClose()
      navigate(`/clients/${client.id}`)
    } catch (err: any) {
      showToast(err.response?.data || 'Conversie mislukt', 'error')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="fixed inset-0 bg-black/40 z-50 flex items-center justify-center p-4">
      <div className="bg-white rounded-2xl shadow-2xl w-full max-w-md">
        {/* Header */}
        <div className="flex items-center justify-between p-6 border-b border-gray-200">
          <div>
            <h2 className="text-lg font-bold text-gray-900">Converteer naar klant</h2>
            <p className="text-sm text-gray-500 mt-0.5">{lead.name}</p>
          </div>
          <button
            onClick={onClose}
            className="p-1.5 rounded-md text-gray-400 hover:text-gray-600 hover:bg-gray-100"
          >
            <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>

        {/* Step indicator */}
        <div className="flex items-center px-6 py-4 border-b border-gray-100">
          {[1, 2, 3].map((s) => (
            <div key={s} className="flex items-center">
              <div className={`w-7 h-7 rounded-full flex items-center justify-center text-xs font-semibold
                ${step === s ? 'bg-indigo-600 text-white' : step > s ? 'bg-green-500 text-white' : 'bg-gray-200 text-gray-500'}`}>
                {step > s ? (
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                  </svg>
                ) : s}
              </div>
              <span className={`ml-2 text-xs font-medium hidden sm:block
                ${step === s ? 'text-indigo-600' : step > s ? 'text-green-600' : 'text-gray-400'}`}>
                {s === 1 ? 'Bedrijfsinfo' : s === 2 ? 'Contactpersoon' : 'Bevestigen'}
              </span>
              {s < 3 && <div className="mx-3 h-px w-8 bg-gray-200 flex-shrink-0" />}
            </div>
          ))}
        </div>

        {/* Body */}
        <div className="p-6 space-y-4">
          {step === 1 && (
            <>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">
                  Bedrijfsnaam <span className="text-red-500">*</span>
                </label>
                <input
                  type="text"
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
                  placeholder="Naam van het bedrijf"
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Plan</label>
                <input
                  type="text"
                  value={plan}
                  onChange={(e) => setPlan(e.target.value)}
                  list="plan-suggestions"
                  className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
                  placeholder="Bijv. Starter, Pro, Enterprise"
                />
                <datalist id="plan-suggestions">
                  {PLAN_SUGGESTIONS.map((p) => <option key={p} value={p} />)}
                </datalist>
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Notities</label>
                <textarea
                  value={notes}
                  onChange={(e) => setNotes(e.target.value)}
                  rows={3}
                  className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:ring-2 focus:ring-indigo-500 focus:border-transparent resize-none"
                  placeholder="Optionele notities..."
                />
              </div>
            </>
          )}

          {step === 2 && (
            <>
              <p className="text-sm text-gray-500">Contactgegevens zijn pre-ingevuld vanuit de lead. Pas aan indien nodig.</p>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Naam contactpersoon</label>
                <input
                  type="text"
                  value={contactName}
                  onChange={(e) => setContactName(e.target.value)}
                  className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
                  placeholder="Naam"
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">E-mailadres</label>
                <input
                  type="email"
                  value={contactEmail}
                  onChange={(e) => setContactEmail(e.target.value)}
                  className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
                  placeholder="email@bedrijf.nl"
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Telefoonnummer</label>
                <input
                  type="tel"
                  value={contactPhone}
                  onChange={(e) => setContactPhone(e.target.value)}
                  className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
                  placeholder="+31 6 12345678"
                />
              </div>
            </>
          )}

          {step === 3 && (
            <div className="space-y-3">
              <p className="text-sm text-gray-600 font-medium">Controleer de gegevens voor conversie:</p>
              <div className="bg-gray-50 rounded-lg p-4 space-y-2 text-sm">
                <div className="flex justify-between">
                  <span className="text-gray-500">Bedrijfsnaam</span>
                  <span className="font-medium text-gray-900">{name}</span>
                </div>
                {plan && (
                  <div className="flex justify-between">
                    <span className="text-gray-500">Plan</span>
                    <span className="font-medium text-gray-900">{plan}</span>
                  </div>
                )}
                {contactName && (
                  <div className="flex justify-between">
                    <span className="text-gray-500">Contactpersoon</span>
                    <span className="font-medium text-gray-900">{contactName}</span>
                  </div>
                )}
                {contactEmail && (
                  <div className="flex justify-between">
                    <span className="text-gray-500">E-mail</span>
                    <span className="font-medium text-gray-900 break-all">{contactEmail}</span>
                  </div>
                )}
                {contactPhone && (
                  <div className="flex justify-between">
                    <span className="text-gray-500">Telefoon</span>
                    <span className="font-medium text-gray-900">{contactPhone}</span>
                  </div>
                )}
                <div className="flex justify-between pt-1 border-t border-gray-200">
                  <span className="text-gray-500">Standaard project</span>
                  <span className="font-medium text-gray-900">Project - {name}</span>
                </div>
              </div>
              <p className="text-xs text-gray-400">
                Er wordt een klantrecord en een standaard project aangemaakt. De lead wordt gemarkeerd als geconverteerd.
              </p>
            </div>
          )}
        </div>

        {/* Footer */}
        <div className="flex items-center justify-between p-6 border-t border-gray-100">
          <button
            onClick={step === 1 ? onClose : () => setStep(step - 1)}
            className="px-4 py-2 text-sm text-gray-600 hover:text-gray-900 hover:bg-gray-100 rounded-lg transition-colors"
          >
            {step === 1 ? 'Annuleren' : 'Terug'}
          </button>
          {step < 3 ? (
            <button
              onClick={() => setStep(step + 1)}
              disabled={step === 1 && !name.trim()}
              className="px-5 py-2 bg-indigo-600 text-white text-sm font-medium rounded-lg hover:bg-indigo-700 disabled:bg-gray-300 disabled:cursor-not-allowed transition-colors"
            >
              Volgende
            </button>
          ) : (
            <button
              onClick={handleSubmit}
              disabled={submitting}
              className="px-5 py-2 bg-green-600 text-white text-sm font-medium rounded-lg hover:bg-green-700 disabled:bg-gray-300 disabled:cursor-not-allowed transition-colors flex items-center gap-2"
            >
              {submitting ? (
                <>
                  <svg className="animate-spin w-4 h-4" fill="none" viewBox="0 0 24 24">
                    <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                    <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
                  </svg>
                  Bezig...
                </>
              ) : 'Converteer naar klant'}
            </button>
          )}
        </div>
      </div>
    </div>
  )
}

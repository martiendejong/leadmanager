import { useState } from 'react'
import type { Lead, OutreachEmailVariant } from '../../api/leads'
import { generateOutreachEmail } from '../../api/leads'
import { useToast } from '../Toast'

interface Props {
  lead: Lead
}

export default function OutreachEmailPanel({ lead }: Props) {
  const { showToast } = useToast()
  const [isGenerating, setIsGenerating] = useState(false)
  const [variants, setVariants] = useState<OutreachEmailVariant[] | null>(null)
  const [activeTab, setActiveTab] = useState(0)
  const [editedBodies, setEditedBodies] = useState<Record<number, string>>({})

  const handleGenerate = async () => {
    setIsGenerating(true)
    try {
      const result = await generateOutreachEmail(lead.id)
      setVariants(result.variants)
      setEditedBodies({})
      setActiveTab(0)
      showToast('Outreach e-mails gegenereerd!', 'success')
    } catch (err: any) {
      showToast(err.response?.data || 'Genereren mislukt', 'error')
    } finally {
      setIsGenerating(false)
    }
  }

  const copyToClipboard = async (variant: OutreachEmailVariant, index: number) => {
    const body = editedBodies[index] ?? variant.body
    const text = `Onderwerp: ${variant.subject}\n\n${body}`
    try {
      await navigator.clipboard.writeText(text)
      showToast('E-mail gekopieerd naar klembord!', 'success')
    } catch {
      showToast('Kopiëren mislukt', 'error')
    }
  }

  const tabColors = [
    { active: 'bg-white text-blue-700 border-b-2 border-blue-600', inactive: 'text-blue-600 hover:bg-blue-50' },
    { active: 'bg-white text-emerald-700 border-b-2 border-emerald-600', inactive: 'text-emerald-600 hover:bg-emerald-50' },
    { active: 'bg-white text-orange-700 border-b-2 border-orange-600', inactive: 'text-orange-600 hover:bg-orange-50' },
  ]

  return (
    <div className="bg-blue-50 border border-blue-200 rounded-lg overflow-hidden">
      {/* Header */}
      <div className="flex items-center justify-between px-4 py-3 bg-blue-100/50 border-b border-blue-200">
        <div className="flex items-center gap-1.5">
          <svg className="w-4 h-4 text-blue-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 8l7.89 5.26a2 2 0 002.22 0L21 8M5 19h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
          </svg>
          <span className="text-xs font-semibold text-blue-700 uppercase tracking-wide">Outreach E-mail Generator</span>
        </div>
        <button
          onClick={handleGenerate}
          disabled={isGenerating}
          className="text-xs px-3 py-1.5 bg-blue-600 text-white rounded hover:bg-blue-700 disabled:bg-gray-400 disabled:cursor-not-allowed flex items-center gap-1"
        >
          {isGenerating ? (
            <>
              <svg className="w-3 h-3 animate-spin" fill="none" viewBox="0 0 24 24">
                <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
              </svg>
              Genereren...
            </>
          ) : (
            <>
              <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
              </svg>
              {variants ? 'Hergenerer' : 'Genereer Outreach Email'}
            </>
          )}
        </button>
      </div>

      {!variants && !isGenerating && (
        <div className="px-4 py-5 text-center">
          <p className="text-sm text-blue-600">
            Klik op "Genereer Outreach Email" om 3 gepersonaliseerde e-mailvarianten te genereren voor <strong>{lead.name}</strong>.
          </p>
        </div>
      )}

      {isGenerating && (
        <div className="px-4 py-5 text-center">
          <div className="flex items-center justify-center gap-2 text-sm text-blue-600">
            <svg className="w-4 h-4 animate-spin" fill="none" viewBox="0 0 24 24">
              <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
              <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
            </svg>
            AI schrijft 3 e-mailvarianten...
          </div>
        </div>
      )}

      {variants && variants.length > 0 && (
        <>
          {/* Tabs */}
          <div className="flex border-b border-blue-200 bg-blue-50">
            {variants.map((v, i) => (
              <button
                key={i}
                onClick={() => setActiveTab(i)}
                className={`flex-1 px-3 py-2 text-xs font-medium transition-colors ${
                  activeTab === i ? tabColors[i]?.active : tabColors[i]?.inactive
                }`}
              >
                {v.style}
              </button>
            ))}
          </div>

          {/* Active variant */}
          {variants[activeTab] && (
            <div className="p-4 bg-white space-y-3">
              {/* Subject */}
              <div>
                <label className="block text-xs font-medium text-gray-500 uppercase tracking-wide mb-1">
                  Onderwerp
                </label>
                <div className="flex items-center gap-2">
                  <input
                    type="text"
                    value={variants[activeTab].subject}
                    readOnly
                    className="flex-1 text-sm border border-gray-200 rounded px-3 py-1.5 bg-gray-50 text-gray-900 focus:outline-none"
                  />
                  <button
                    onClick={() => navigator.clipboard.writeText(variants[activeTab].subject).then(() => showToast('Onderwerp gekopieerd!', 'success'))}
                    className="text-xs px-2 py-1.5 bg-gray-100 text-gray-600 rounded hover:bg-gray-200 flex-shrink-0"
                    title="Kopieer onderwerp"
                  >
                    <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" />
                    </svg>
                  </button>
                </div>
              </div>

              {/* Body */}
              <div>
                <label className="block text-xs font-medium text-gray-500 uppercase tracking-wide mb-1">
                  E-mailtekst <span className="normal-case font-normal">(bewerkbaar)</span>
                </label>
                <textarea
                  rows={8}
                  className="w-full text-sm border border-gray-200 rounded px-3 py-2 text-gray-900 focus:outline-none focus:ring-2 focus:ring-blue-500 resize-y"
                  value={editedBodies[activeTab] ?? variants[activeTab].body}
                  onChange={(e) => setEditedBodies(prev => ({ ...prev, [activeTab]: e.target.value }))}
                />
              </div>

              {/* Copy button */}
              <button
                onClick={() => copyToClipboard(variants[activeTab], activeTab)}
                className="w-full text-xs px-3 py-2 bg-blue-600 text-white rounded hover:bg-blue-700 flex items-center justify-center gap-1.5"
              >
                <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" />
                </svg>
                Kopieer onderwerp + tekst
              </button>
            </div>
          )}
        </>
      )}
    </div>
  )
}

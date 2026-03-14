import { useState, useEffect, useRef } from 'react'
import { getProfile, generateProfile, updateProfile } from '../api/profile'
import type { CompanyProfile } from '../api/profile'
import { useToast } from '../components/Toast'

function TagInput({ label, values, onChange }: { label: string; values: string[]; onChange: (v: string[]) => void }) {
  const [input, setInput] = useState('')

  const add = () => {
    const val = input.trim()
    if (val && !values.includes(val)) onChange([...values, val])
    setInput('')
  }

  return (
    <div>
      <label className="block text-sm font-medium text-gray-700 mb-1">{label}</label>
      <div className="flex flex-wrap gap-2 mb-2">
        {values.map((v) => (
          <span key={v} className="inline-flex items-center gap-1 bg-indigo-100 text-indigo-800 text-xs px-2.5 py-1 rounded-full">
            {v}
            <button onClick={() => onChange(values.filter((x) => x !== v))} className="text-indigo-600 hover:text-indigo-900">×</button>
          </span>
        ))}
      </div>
      <div className="flex gap-2">
        <input
          type="text"
          value={input}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), add())}
          className="flex-1 border border-gray-300 rounded-md px-3 py-1.5 text-sm focus:ring-indigo-500 focus:border-indigo-500"
          placeholder="Typ en druk Enter"
        />
        <button onClick={add} className="px-3 py-1.5 text-sm bg-gray-100 border border-gray-300 rounded-md hover:bg-gray-200">
          Toevoegen
        </button>
      </div>
    </div>
  )
}

function TextArea({ label, value, onChange, rows = 3, placeholder }: {
  label: string; value: string; onChange: (v: string) => void; rows?: number; placeholder?: string
}) {
  return (
    <div>
      <label className="block text-sm font-medium text-gray-700 mb-1">{label}</label>
      <textarea
        value={value}
        onChange={(e) => onChange(e.target.value)}
        rows={rows}
        placeholder={placeholder}
        className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:ring-indigo-500 focus:border-indigo-500"
      />
    </div>
  )
}

const GENERATE_STEPS = [
  'Website crawlen…',
  'Pagina\'s analyseren…',
  'Profiel genereren met AI…',
  'Opslaan…',
]

export default function ProfilePage() {
  const { showToast } = useToast()
  const [profile, setProfile] = useState<CompanyProfile | null>(null)
  const [isLoading, setIsLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [isGenerating, setIsGenerating] = useState(false)
  const [generatingStep, setGeneratingStep] = useState(0)
  const [isSaving, setIsSaving] = useState(false)
  const [websiteUrl, setWebsiteUrl] = useState('')
  const [edited, setEdited] = useState<CompanyProfile | null>(null)
  const stepIntervalRef = useRef<ReturnType<typeof setInterval> | null>(null)

  useEffect(() => {
    getProfile().then((p) => {
      setProfile(p)
      setEdited(p)
      setIsLoading(false)
    }).catch(() => {
      setLoadError(true)
      setIsLoading(false)
    })
  }, [])

  useEffect(() => {
    if (isGenerating) {
      setGeneratingStep(0)
      stepIntervalRef.current = setInterval(() => {
        setGeneratingStep((s) => Math.min(s + 1, GENERATE_STEPS.length - 1))
      }, 10000)
    } else {
      if (stepIntervalRef.current) {
        clearInterval(stepIntervalRef.current)
        stepIntervalRef.current = null
      }
      setGeneratingStep(0)
    }
    return () => {
      if (stepIntervalRef.current) clearInterval(stepIntervalRef.current)
    }
  }, [isGenerating])

  const handleGenerate = async () => {
    if (!websiteUrl.trim()) return
    setIsGenerating(true)
    try {
      const p = await generateProfile(websiteUrl.trim())
      setProfile(p)
      setEdited(p)
      showToast('Profiel gegenereerd!', 'success')
    } catch (err: any) {
      showToast(err.response?.data?.message || 'Genereren mislukt', 'error')
    } finally {
      setIsGenerating(false)
    }
  }

  const handleSave = async () => {
    if (!edited) return
    setIsSaving(true)
    try {
      const p = await updateProfile(edited)
      setProfile(p)
      setEdited(p)
      showToast('Profiel opgeslagen', 'success')
    } catch {
      showToast('Opslaan mislukt', 'error')
    } finally {
      setIsSaving(false)
    }
  }

  const set = (field: keyof CompanyProfile) => (value: any) =>
    setEdited((prev) => prev ? { ...prev, [field]: value } : prev)

  if (isLoading) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="text-gray-400 text-sm">Laden…</div>
      </div>
    )
  }

  if (loadError) {
    return (
      <div className="max-w-3xl mx-auto">
        <div className="bg-red-50 border border-red-200 rounded-lg p-6 text-center">
          <svg className="w-10 h-10 text-red-400 mx-auto mb-3" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M12 9v2m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
          </svg>
          <h3 className="text-sm font-semibold text-red-800 mb-1">Kon profiel niet laden</h3>
          <p className="text-sm text-red-600">Controleer of de API server actief is en herstart indien nodig.</p>
          <button
            onClick={() => { setLoadError(false); setIsLoading(true); getProfile().then((p) => { setProfile(p); setEdited(p); setIsLoading(false) }).catch(() => { setLoadError(true); setIsLoading(false) }) }}
            className="mt-4 px-4 py-2 bg-red-600 text-white rounded-md text-sm font-medium hover:bg-red-700"
          >
            Opnieuw proberen
          </button>
        </div>
      </div>
    )
  }

  return (
    <div className="max-w-3xl mx-auto space-y-8">
      <div>
        <h2 className="text-2xl font-bold text-gray-900">Bedrijfsprofiel</h2>
        <p className="text-sm text-gray-500 mt-1">
          Voer jouw website in. Het systeem crawlt hem en maakt een profiel op basis waarvan slimme leads worden gevonden.
        </p>
      </div>

      {/* Website input + generate */}
      <div className="bg-white border border-gray-200 rounded-lg p-6 space-y-4">
        <h3 className="font-semibold text-gray-800">
          {profile ? 'Profiel hergenereren' : 'Profiel aanmaken'}
        </h3>
        {profile && (
          <p className="text-sm text-gray-500">
            Huidig profiel voor <strong>{profile.websiteUrl}</strong> — versie {profile.profileVersion},
            bijgewerkt {new Date(profile.updatedAt).toLocaleDateString('nl-NL')}
          </p>
        )}
        <div className="flex gap-3">
          <input
            type="url"
            value={websiteUrl}
            onChange={(e) => setWebsiteUrl(e.target.value)}
            placeholder={profile?.websiteUrl || 'https://jouwbedrijf.nl'}
            className="flex-1 border border-gray-300 rounded-md px-3 py-2 text-sm focus:ring-indigo-500 focus:border-indigo-500"
            onKeyDown={(e) => e.key === 'Enter' && handleGenerate()}
          />
          <button
            onClick={handleGenerate}
            disabled={isGenerating || !websiteUrl.trim()}
            className="px-4 py-2 bg-indigo-600 text-white rounded-md text-sm font-medium hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed flex items-center gap-2"
          >
            {isGenerating ? (
              <>
                <svg className="animate-spin h-4 w-4" fill="none" viewBox="0 0 24 24">
                  <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                  <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
                </svg>
                Genereren…
              </>
            ) : (
              'Genereer profiel'
            )}
          </button>
        </div>

        {/* Step indicator during generation */}
        {isGenerating && (
          <div className="space-y-2 pt-1">
            {GENERATE_STEPS.map((step, i) => (
              <div key={step} className="flex items-center gap-2 text-xs">
                {i < generatingStep ? (
                  <svg className="w-4 h-4 text-green-500 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M5 13l4 4L19 7" />
                  </svg>
                ) : i === generatingStep ? (
                  <svg className="animate-spin w-4 h-4 text-indigo-500 flex-shrink-0" fill="none" viewBox="0 0 24 24">
                    <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                    <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
                  </svg>
                ) : (
                  <div className="w-4 h-4 rounded-full border-2 border-gray-200 flex-shrink-0" />
                )}
                <span className={i === generatingStep ? 'text-indigo-700 font-medium' : i < generatingStep ? 'text-green-600' : 'text-gray-400'}>
                  Stap {i + 1}: {step}
                </span>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Editable profile */}
      {edited && (
        <div className="bg-white border border-gray-200 rounded-lg p-6 space-y-6">
          <h3 className="font-semibold text-gray-800">Profiel bewerken</h3>

          <div className="grid grid-cols-1 gap-6">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Bedrijfsnaam</label>
              <input
                type="text"
                value={edited.companyName}
                onChange={(e) => set('companyName')(e.target.value)}
                className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:ring-indigo-500 focus:border-indigo-500"
              />
            </div>

            <TextArea label="Omschrijving (2-3 zinnen)" value={edited.description} onChange={set('description')} />
            <TextArea label="Wat jullie doen (gedetailleerd)" value={edited.whatTheyDo} onChange={set('whatTheyDo')} rows={4} />
            <TextArea
              label="Ideale klantprofiel (ICP)"
              value={edited.idealCustomerProfile}
              onChange={set('idealCustomerProfile')}
              rows={4}
              placeholder="Beschrijf de ideale klant: sector, bedrijfsgrootte, uitdagingen, behoeften…"
            />
            <TextArea label="Tone of voice" value={edited.toneOfVoice} onChange={set('toneOfVoice')} />

            <TagInput label="Doelsectoren" values={edited.targetSectors} onChange={set('targetSectors')} />
            <TagInput label="Doelregio's" values={edited.targetRegions} onChange={set('targetRegions')} />
            <TagInput label="Keywords" values={edited.keywords} onChange={set('keywords')} />
            <TagInput label="USPs" values={edited.usps} onChange={set('usps')} />
          </div>

          <div className="flex justify-end pt-2">
            <button
              onClick={handleSave}
              disabled={isSaving}
              className="px-5 py-2 bg-indigo-600 text-white rounded-md text-sm font-medium hover:bg-indigo-700 disabled:opacity-50"
            >
              {isSaving ? 'Opslaan…' : 'Profiel opslaan'}
            </button>
          </div>
        </div>
      )}
    </div>
  )
}

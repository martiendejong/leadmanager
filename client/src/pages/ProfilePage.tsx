import { useState, useEffect } from 'react'
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

export default function ProfilePage() {
  const { showToast } = useToast()
  const [profile, setProfile] = useState<CompanyProfile | null>(null)
  const [isLoading, setIsLoading] = useState(true)
  const [isGenerating, setIsGenerating] = useState(false)
  const [isSaving, setIsSaving] = useState(false)
  const [websiteUrl, setWebsiteUrl] = useState('')
  const [edited, setEdited] = useState<CompanyProfile | null>(null)

  useEffect(() => {
    getProfile().then((p) => {
      setProfile(p)
      setEdited(p)
      setIsLoading(false)
    }).catch(() => setIsLoading(false))
  }, [])

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
        {isGenerating && (
          <p className="text-xs text-gray-400">Website wordt gecrawld en geanalyseerd door AI — dit kan 30-60 seconden duren.</p>
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

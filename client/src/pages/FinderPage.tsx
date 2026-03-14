import { useState, useRef, useEffect } from 'react'
import { Link } from 'react-router-dom'
import { getProfile, searchLeadsWithProfile, type CompanyProfile, type QualifiedLead } from '../api/profile'
import { importSearchResults } from '../api/leads'
import type { ImportResultDto } from '../api/leads'

export default function FinderPage() {
  const [profile, setProfile] = useState<CompanyProfile | null>(null)
  const [profileLoading, setProfileLoading] = useState(true)
  const [searching, setSearching] = useState(false)
  const [results, setResults] = useState<QualifiedLead[] | null>(null)
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [importing, setImporting] = useState(false)
  const [toast, setToast] = useState<{ msg: string; type: 'success' | 'error' } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const toastTimer = useRef<ReturnType<typeof setTimeout> | null>(null)

  useEffect(() => {
    getProfile()
      .then((p) => { setProfile(p); setProfileLoading(false) })
      .catch(() => setProfileLoading(false))
  }, [])

  function showToast(msg: string, type: 'success' | 'error' = 'success') {
    setToast({ msg, type })
    if (toastTimer.current) clearTimeout(toastTimer.current)
    toastTimer.current = setTimeout(() => setToast(null), 5000)
  }

  async function handleSearch() {
    setSearching(true)
    setError(null)
    setResults(null)
    setSelected(new Set())

    try {
      const data = await searchLeadsWithProfile()
      setResults(data)
      setSelected(new Set(data.map((_, i) => i)))
    } catch (err: any) {
      setError(err.response?.data?.message || 'Zoeken mislukt')
    } finally {
      setSearching(false)
    }
  }

  function toggleAll() {
    if (!results) return
    if (selected.size === results.length) setSelected(new Set())
    else setSelected(new Set(results.map((_, i) => i)))
  }

  function toggleRow(i: number) {
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(i)) next.delete(i)
      else next.add(i)
      return next
    })
  }

  async function handleImport() {
    if (!results || selected.size === 0) return
    setImporting(true)
    try {
      const toImport = results
        .filter((_, i) => selected.has(i))
        .map((r) => ({
          name: r.name,
          website: r.website,
          city: r.city,
          sector: r.sector,
          phone: r.phone,
          email: r.email,
          source: r.source,
        }))
      const result: ImportResultDto = await importSearchResults(toImport as any)
      showToast(`${result.imported} leads geïmporteerd, ${result.skipped} overgeslagen`)
      setResults(null)
      setSelected(new Set())
    } catch {
      showToast('Importeren mislukt', 'error')
    } finally {
      setImporting(false)
    }
  }

  const allSelected = results != null && selected.size === results.length
  const someSelected = selected.size > 0 && !allSelected

  function scoreColor(score: number) {
    if (score >= 80) return 'bg-green-100 text-green-800'
    if (score >= 60) return 'bg-yellow-100 text-yellow-800'
    return 'bg-red-100 text-red-800'
  }

  if (profileLoading) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="text-gray-400 text-sm">Laden…</div>
      </div>
    )
  }

  return (
    <div className="max-w-6xl mx-auto space-y-6">
      {/* Toast */}
      {toast && (
        <div className={`fixed top-4 right-4 z-50 px-5 py-3 rounded-lg shadow-lg text-sm font-medium text-white ${toast.type === 'error' ? 'bg-red-600' : 'bg-green-600'}`}>
          {toast.msg}
        </div>
      )}

      <div>
        <h2 className="text-2xl font-bold text-gray-900">Leads zoeken</h2>
        <p className="text-sm text-gray-500 mt-1">
          Op basis van jouw bedrijfsprofiel zoekt het systeem relevante leads met AI.
        </p>
      </div>

      {/* Profile card */}
      {!profile ? (
        <div className="bg-amber-50 border border-amber-200 rounded-lg p-6 text-center space-y-3">
          <p className="text-amber-800 font-medium">Nog geen bedrijfsprofiel aangemaakt</p>
          <p className="text-amber-700 text-sm">
            Maak eerst een profiel aan zodat het systeem weet voor welk bedrijf leads gezocht moeten worden.
          </p>
          <Link
            to="/profile"
            className="inline-block px-5 py-2 bg-amber-600 text-white rounded-md text-sm font-medium hover:bg-amber-700"
          >
            Profiel aanmaken
          </Link>
        </div>
      ) : (
        <div className="bg-white border border-gray-200 rounded-lg p-6 space-y-4">
          <div className="flex items-start justify-between gap-4">
            <div>
              <h3 className="font-semibold text-gray-900">{profile.companyName}</h3>
              <p className="text-sm text-gray-500 mt-0.5">{profile.websiteUrl}</p>
              <p className="text-sm text-gray-600 mt-2">{profile.description}</p>
            </div>
            <Link to="/profile" className="text-xs text-indigo-600 hover:underline whitespace-nowrap">
              Profiel bewerken
            </Link>
          </div>

          {(profile.targetSectors.length > 0 || profile.targetRegions.length > 0) && (
            <div className="flex flex-wrap gap-2">
              {profile.targetSectors.map((s) => (
                <span key={s} className="text-xs bg-indigo-100 text-indigo-800 px-2 py-0.5 rounded-full">{s}</span>
              ))}
              {profile.targetRegions.map((r) => (
                <span key={r} className="text-xs bg-blue-100 text-blue-800 px-2 py-0.5 rounded-full">{r}</span>
              ))}
            </div>
          )}

          <div className="pt-2">
            <button
              onClick={handleSearch}
              disabled={searching}
              className="px-5 py-2 bg-indigo-600 text-white rounded-md text-sm font-medium hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed flex items-center gap-2"
            >
              {searching ? (
                <>
                  <svg className="animate-spin h-4 w-4" fill="none" viewBox="0 0 24 24">
                    <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                    <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
                  </svg>
                  Leads zoeken… (kan 30-60 sec duren)
                </>
              ) : (
                'Zoek leads met AI'
              )}
            </button>
            {searching && (
              <p className="text-xs text-gray-400 mt-2">
                Queries worden gegenereerd → DuckDuckGo → AI kwalificeert resultaten
              </p>
            )}
          </div>
        </div>
      )}

      {/* Error */}
      {error && (
        <div className="bg-red-50 border border-red-200 text-red-700 text-sm px-4 py-3 rounded-lg">
          {error}
        </div>
      )}

      {/* Results */}
      {results !== null && (
        <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
          <div className="px-6 py-4 border-b border-gray-200 flex items-center justify-between">
            <span className="text-sm text-gray-600 font-medium">
              {results.length} gekwalificeerde leads gevonden
            </span>
            <button
              onClick={handleImport}
              disabled={selected.size === 0 || importing}
              className="inline-flex items-center gap-2 bg-green-600 text-white px-4 py-2 rounded-lg text-sm font-medium hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              {importing ? 'Importeren…' : `Importeer geselecteerde (${selected.size})`}
            </button>
          </div>

          {results.length === 0 ? (
            <div className="px-6 py-12 text-center text-sm text-gray-500">
              Geen resultaten gevonden. Pas je profiel aan en probeer opnieuw.
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead className="bg-gray-50 text-gray-500 text-xs uppercase tracking-wide">
                  <tr>
                    <th className="px-4 py-3 text-left w-10">
                      <input
                        type="checkbox"
                        checked={allSelected}
                        ref={(el) => { if (el) el.indeterminate = someSelected }}
                        onChange={toggleAll}
                        className="rounded border-gray-300 text-indigo-600 focus:ring-indigo-500 cursor-pointer"
                      />
                    </th>
                    <th className="px-4 py-3 text-left">Score</th>
                    <th className="px-4 py-3 text-left">Bedrijf</th>
                    <th className="px-4 py-3 text-left">Website</th>
                    <th className="px-4 py-3 text-left">Sector</th>
                    <th className="px-4 py-3 text-left">Stad</th>
                    <th className="px-4 py-3 text-left">Reden</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100">
                  {results.map((lead, i) => (
                    <tr
                      key={i}
                      className={`hover:bg-gray-50 cursor-pointer ${selected.has(i) ? 'bg-indigo-50' : ''}`}
                      onClick={() => toggleRow(i)}
                    >
                      <td className="px-4 py-3" onClick={(e) => e.stopPropagation()}>
                        <input
                          type="checkbox"
                          checked={selected.has(i)}
                          onChange={() => toggleRow(i)}
                          className="rounded border-gray-300 text-indigo-600 focus:ring-indigo-500 cursor-pointer"
                        />
                      </td>
                      <td className="px-4 py-3">
                        <span className={`inline-block text-xs font-semibold px-2 py-0.5 rounded-full ${scoreColor(lead.confidenceScore)}`}>
                          {lead.confidenceScore}%
                        </span>
                      </td>
                      <td className="px-4 py-3 font-medium text-gray-900 max-w-[180px] truncate">
                        {lead.name || '—'}
                      </td>
                      <td className="px-4 py-3 max-w-[160px]">
                        {lead.website ? (
                          <a
                            href={lead.website}
                            target="_blank"
                            rel="noopener noreferrer"
                            onClick={(e) => e.stopPropagation()}
                            className="text-indigo-600 hover:underline truncate block"
                          >
                            {lead.website.replace(/^https?:\/\/(www\.)?/, '')}
                          </a>
                        ) : '—'}
                      </td>
                      <td className="px-4 py-3 text-gray-600">{lead.sector || '—'}</td>
                      <td className="px-4 py-3 text-gray-600">{lead.city || '—'}</td>
                      <td className="px-4 py-3 text-gray-500 text-xs max-w-[240px] truncate" title={lead.qualificationReason}>
                        {lead.qualificationReason || '—'}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

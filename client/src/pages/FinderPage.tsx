import { useState, useRef } from 'react'
import {
  searchLeads,
  importSearchResults,
  type LeadSearchResult,
  type ImportResultDto,
} from '../api/leads'

export default function FinderPage() {
  const [sector, setSector] = useState('')
  const [location, setLocation] = useState('')
  const [limit, setLimit] = useState(25)
  const [loading, setLoading] = useState(false)
  const [results, setResults] = useState<LeadSearchResult[] | null>(null)
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [importing, setImporting] = useState(false)
  const [toast, setToast] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const toastTimer = useRef<ReturnType<typeof setTimeout> | null>(null)

  function showToast(msg: string) {
    setToast(msg)
    if (toastTimer.current) clearTimeout(toastTimer.current)
    toastTimer.current = setTimeout(() => setToast(null), 5000)
  }

  async function handleSearch(e: React.FormEvent) {
    e.preventDefault()
    if (!sector.trim()) return

    setLoading(true)
    setError(null)
    setResults(null)
    setSelected(new Set())

    try {
      const data = await searchLeads(sector.trim(), location.trim(), limit)
      setResults(data)
      // Pre-select all
      setSelected(new Set(data.map((_, i) => i)))
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : 'Zoeken mislukt'
      setError(msg)
    } finally {
      setLoading(false)
    }
  }

  function toggleAll() {
    if (!results) return
    if (selected.size === results.length) {
      setSelected(new Set())
    } else {
      setSelected(new Set(results.map((_, i) => i)))
    }
  }

  function toggleRow(index: number) {
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(index)) next.delete(index)
      else next.add(index)
      return next
    })
  }

  async function handleImport() {
    if (!results || selected.size === 0) return

    setImporting(true)
    try {
      const toImport = results.filter((_, i) => selected.has(i))
      const result: ImportResultDto = await importSearchResults(toImport)
      showToast(
        `${result.imported} leads geïmporteerd, ${result.skipped} overgeslagen`
      )
      setResults(null)
      setSelected(new Set())
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : 'Importeren mislukt'
      setError(msg)
    } finally {
      setImporting(false)
    }
  }

  const allSelected = results != null && selected.size === results.length
  const someSelected = selected.size > 0 && !allSelected

  return (
    <div className="max-w-6xl mx-auto">
      {/* Toast */}
      {toast && (
        <div className="fixed top-4 right-4 z-50 bg-green-600 text-white px-5 py-3 rounded-lg shadow-lg text-sm font-medium">
          {toast}
        </div>
      )}

      <div className="mb-6">
        <h2 className="text-2xl font-bold text-gray-900">Leads zoeken</h2>
        <p className="text-sm text-gray-500 mt-1">
          Zoek bedrijven via DuckDuckGo en importeer ze direct in de database.
        </p>
      </div>

      {/* Search form */}
      <form
        onSubmit={handleSearch}
        className="bg-white rounded-xl shadow-sm border border-gray-200 p-6 mb-6"
      >
        <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Sector <span className="text-red-500">*</span>
            </label>
            <input
              type="text"
              value={sector}
              onChange={(e) => setSector(e.target.value)}
              placeholder="bijv. loodgieter, accountant, recruiter"
              required
              className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Locatie
            </label>
            <input
              type="text"
              value={location}
              onChange={(e) => setLocation(e.target.value)}
              placeholder="bijv. Amsterdam, Utrecht"
              className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Max resultaten
            </label>
            <select
              value={limit}
              onChange={(e) => setLimit(Number(e.target.value))}
              className="w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            >
              <option value={10}>10</option>
              <option value={25}>25</option>
              <option value={50}>50</option>
            </select>
          </div>
        </div>

        <div className="mt-4 flex items-center gap-3">
          <button
            type="submit"
            disabled={loading}
            className="inline-flex items-center gap-2 bg-blue-600 text-white px-5 py-2 rounded-lg text-sm font-medium hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            {loading && (
              <svg
                className="animate-spin h-4 w-4 text-white"
                fill="none"
                viewBox="0 0 24 24"
              >
                <circle
                  className="opacity-25"
                  cx="12"
                  cy="12"
                  r="10"
                  stroke="currentColor"
                  strokeWidth="4"
                />
                <path
                  className="opacity-75"
                  fill="currentColor"
                  d="M4 12a8 8 0 018-8v8H4z"
                />
              </svg>
            )}
            {loading ? 'Zoeken...' : 'Zoek leads'}
          </button>
        </div>
      </form>

      {/* Error */}
      {error && (
        <div className="bg-red-50 border border-red-200 text-red-700 text-sm px-4 py-3 rounded-lg mb-6">
          {error}
        </div>
      )}

      {/* Results */}
      {results !== null && (
        <div className="bg-white rounded-xl shadow-sm border border-gray-200 overflow-hidden">
          {/* Toolbar */}
          <div className="px-6 py-4 border-b border-gray-200 flex items-center justify-between">
            <span className="text-sm text-gray-600 font-medium">
              {results.length} resultaten gevonden
            </span>
            <button
              onClick={handleImport}
              disabled={selected.size === 0 || importing}
              className="inline-flex items-center gap-2 bg-green-600 text-white px-4 py-2 rounded-lg text-sm font-medium hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
            >
              {importing && (
                <svg
                  className="animate-spin h-4 w-4 text-white"
                  fill="none"
                  viewBox="0 0 24 24"
                >
                  <circle
                    className="opacity-25"
                    cx="12"
                    cy="12"
                    r="10"
                    stroke="currentColor"
                    strokeWidth="4"
                  />
                  <path
                    className="opacity-75"
                    fill="currentColor"
                    d="M4 12a8 8 0 018-8v8H4z"
                  />
                </svg>
              )}
              Importeer geselecteerde ({selected.size})
            </button>
          </div>

          {results.length === 0 ? (
            <div className="px-6 py-12 text-center text-sm text-gray-500">
              Geen resultaten gevonden. Probeer een andere sector of locatie.
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
                        ref={(el) => {
                          if (el) el.indeterminate = someSelected
                        }}
                        onChange={toggleAll}
                        className="rounded border-gray-300 text-blue-600 focus:ring-blue-500 cursor-pointer"
                        title="Alles selecteren"
                      />
                    </th>
                    <th className="px-4 py-3 text-left">Bedrijf</th>
                    <th className="px-4 py-3 text-left">Website</th>
                    <th className="px-4 py-3 text-left">Sector</th>
                    <th className="px-4 py-3 text-left">Stad</th>
                    <th className="px-4 py-3 text-left">Telefoon</th>
                    <th className="px-4 py-3 text-left">Email</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100">
                  {results.map((lead, i) => (
                    <tr
                      key={i}
                      className={`hover:bg-gray-50 cursor-pointer transition-colors ${
                        selected.has(i) ? 'bg-blue-50' : ''
                      }`}
                      onClick={() => toggleRow(i)}
                    >
                      <td
                        className="px-4 py-3"
                        onClick={(e) => e.stopPropagation()}
                      >
                        <input
                          type="checkbox"
                          checked={selected.has(i)}
                          onChange={() => toggleRow(i)}
                          className="rounded border-gray-300 text-blue-600 focus:ring-blue-500 cursor-pointer"
                        />
                      </td>
                      <td className="px-4 py-3 font-medium text-gray-900 max-w-[200px] truncate">
                        {lead.name || '—'}
                      </td>
                      <td className="px-4 py-3 max-w-[180px]">
                        {lead.website ? (
                          <a
                            href={lead.website}
                            target="_blank"
                            rel="noopener noreferrer"
                            onClick={(e) => e.stopPropagation()}
                            className="text-blue-600 hover:underline truncate block"
                          >
                            {lead.website.replace(/^https?:\/\/(www\.)?/, '')}
                          </a>
                        ) : (
                          '—'
                        )}
                      </td>
                      <td className="px-4 py-3 text-gray-600">
                        {lead.sector || '—'}
                      </td>
                      <td className="px-4 py-3 text-gray-600">
                        {lead.city || '—'}
                      </td>
                      <td className="px-4 py-3 text-gray-600">
                        {lead.phone || '—'}
                      </td>
                      <td className="px-4 py-3 text-gray-600">
                        {lead.email || '—'}
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

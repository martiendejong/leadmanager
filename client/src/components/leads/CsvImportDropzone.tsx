import { useCallback, useRef, useState } from 'react'
import { importLeadsFromCsv } from '../../api/leads'
import type { CsvImportResult } from '../../api/leads'

interface CsvImportDropzoneProps {
  onSuccess: (result: CsvImportResult) => void
  onCancel: () => void
}

const COLUMN_ALIASES: Record<string, string[]> = {
  Naam: ['Name', 'Naam', 'Bedrijfsnaam', 'Company', 'company_name'],
  Website: ['Website', 'website', 'url', 'URL'],
  Email: ['Email', 'CompanyEmail', 'Bedrijfsemail', 'email'],
  Sector: ['Sector', 'Industry', 'Industrie', 'sector', 'industry'],
  Stad: ['City', 'Stad', 'Gemeente', 'city', 'plaats'],
  Telefoon: ['Phone', 'Telefoon', 'Tel', 'phone', 'telefoon'],
}

type ImportState = 'idle' | 'preview' | 'importing' | 'done' | 'error'

export default function CsvImportDropzone({ onSuccess, onCancel }: CsvImportDropzoneProps) {
  const [state, setState] = useState<ImportState>('idle')
  const [isDragOver, setIsDragOver] = useState(false)
  const [selectedFile, setSelectedFile] = useState<File | null>(null)
  const [previewHeaders, setPreviewHeaders] = useState<string[]>([])
  const [previewRows, setPreviewRows] = useState<string[][]>([])
  const [result, setResult] = useState<CsvImportResult | null>(null)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)

  const parsePreview = useCallback((file: File) => {
    const reader = new FileReader()
    reader.onload = (e) => {
      const text = e.target?.result as string
      const lines = text.split('\n').filter((l) => l.trim())
      if (lines.length === 0) return

      const parseRow = (line: string): string[] => {
        // Simple CSV parse: handle quoted fields
        const fields: string[] = []
        let current = ''
        let inQuote = false
        for (let i = 0; i < line.length; i++) {
          const ch = line[i]
          if (ch === '"') {
            inQuote = !inQuote
          } else if (ch === ',' && !inQuote) {
            fields.push(current.trim())
            current = ''
          } else {
            current += ch
          }
        }
        fields.push(current.trim())
        return fields
      }

      const headers = parseRow(lines[0])
      const rows = lines.slice(1, 4).map(parseRow) // Show first 3 data rows
      setPreviewHeaders(headers)
      setPreviewRows(rows)
      setState('preview')
    }
    reader.readAsText(file, 'utf-8')
  }, [])

  const handleFile = useCallback(
    (file: File) => {
      if (!file.name.endsWith('.csv')) {
        setErrorMessage('Alleen .csv bestanden worden ondersteund.')
        setState('error')
        return
      }
      setSelectedFile(file)
      setErrorMessage(null)
      parsePreview(file)
    },
    [parsePreview]
  )

  const handleDrop = useCallback(
    (e: React.DragEvent) => {
      e.preventDefault()
      setIsDragOver(false)
      const file = e.dataTransfer.files[0]
      if (file) handleFile(file)
    },
    [handleFile]
  )

  const handleDragOver = useCallback((e: React.DragEvent) => {
    e.preventDefault()
    setIsDragOver(true)
  }, [])

  const handleDragLeave = useCallback(() => {
    setIsDragOver(false)
  }, [])

  const handleFileInput = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const file = e.target.files?.[0]
      if (file) handleFile(file)
      e.target.value = ''
    },
    [handleFile]
  )

  const handleImport = useCallback(async () => {
    if (!selectedFile) return
    setState('importing')
    try {
      const importResult = await importLeadsFromCsv(selectedFile)
      setResult(importResult)
      setState('done')
      if (importResult.created > 0) {
        onSuccess(importResult)
      }
    } catch (err: unknown) {
      const msg =
        err instanceof Error ? err.message : 'Import mislukt. Controleer het CSV bestand.'
      setErrorMessage(msg)
      setState('error')
    }
  }, [selectedFile, onSuccess])

  const handleReset = useCallback(() => {
    setState('idle')
    setSelectedFile(null)
    setPreviewHeaders([])
    setPreviewRows([])
    setResult(null)
    setErrorMessage(null)
  }, [])

  // Detect which columns were recognized
  const recognizedColumns = previewHeaders.filter((h) =>
    Object.values(COLUMN_ALIASES).some((aliases) =>
      aliases.some((a) => a.toLowerCase() === h.toLowerCase())
    )
  )

  return (
    <div className="fixed inset-0 bg-black/50 z-50 flex items-center justify-center p-4">
      <div className="bg-white rounded-xl shadow-2xl w-full max-w-2xl max-h-[90vh] overflow-y-auto">
        {/* Header */}
        <div className="flex items-center justify-between p-5 border-b border-gray-200">
          <div>
            <h2 className="text-lg font-semibold text-gray-900">Leads importeren via CSV</h2>
            <p className="text-sm text-gray-500 mt-0.5">Maximaal 500 rijen per import</p>
          </div>
          <button
            onClick={onCancel}
            className="text-gray-400 hover:text-gray-600 transition-colors"
            aria-label="Sluiten"
          >
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>

        <div className="p-5">
          {/* Idle: drop zone */}
          {state === 'idle' && (
            <>
              <div
                onDrop={handleDrop}
                onDragOver={handleDragOver}
                onDragLeave={handleDragLeave}
                onClick={() => fileInputRef.current?.click()}
                className={`border-2 border-dashed rounded-xl p-10 text-center cursor-pointer transition-colors ${
                  isDragOver
                    ? 'border-indigo-400 bg-indigo-50'
                    : 'border-gray-300 hover:border-indigo-300 hover:bg-gray-50'
                }`}
              >
                <input
                  ref={fileInputRef}
                  type="file"
                  accept=".csv"
                  onChange={handleFileInput}
                  className="hidden"
                />
                <svg
                  className="mx-auto w-12 h-12 text-gray-400 mb-3"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    strokeWidth={1.5}
                    d="M9 13h6m-3-3v6m5 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"
                  />
                </svg>
                <p className="text-gray-700 font-medium">Sleep een CSV bestand hierheen</p>
                <p className="text-gray-400 text-sm mt-1">of klik om te bladeren</p>
              </div>

              {/* Column mapping info */}
              <div className="mt-4 bg-gray-50 rounded-lg p-4">
                <p className="text-xs font-medium text-gray-600 mb-2">Ondersteunde kolomnamen:</p>
                <div className="grid grid-cols-2 gap-1.5 text-xs text-gray-500">
                  {Object.entries(COLUMN_ALIASES).map(([label, aliases]) => (
                    <div key={label}>
                      <span className="font-medium text-gray-700">{label}:</span>{' '}
                      {aliases.slice(0, 3).join(', ')}
                      {aliases.length > 3 && ' ...'}
                    </div>
                  ))}
                </div>
                <p className="text-xs text-gray-400 mt-2">
                  Verplicht: naam-kolom. Optioneel: alle andere kolommen.
                </p>
              </div>
            </>
          )}

          {/* Preview state */}
          {state === 'preview' && (
            <>
              <div className="mb-4">
                <div className="flex items-center gap-2 text-sm text-gray-700 mb-3">
                  <svg className="w-4 h-4 text-green-500" fill="currentColor" viewBox="0 0 20 20">
                    <path
                      fillRule="evenodd"
                      d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.707-9.293a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z"
                      clipRule="evenodd"
                    />
                  </svg>
                  <span className="font-medium">{selectedFile?.name}</span>
                </div>

                {/* Column recognition */}
                <div className="bg-blue-50 border border-blue-200 rounded-lg p-3 text-sm mb-4">
                  <p className="font-medium text-blue-800 mb-1">Herkende kolommen:</p>
                  <div className="flex flex-wrap gap-1.5">
                    {recognizedColumns.map((col) => (
                      <span
                        key={col}
                        className="bg-blue-100 text-blue-700 px-2 py-0.5 rounded text-xs font-medium"
                      >
                        {col}
                      </span>
                    ))}
                    {previewHeaders
                      .filter((h) => !recognizedColumns.includes(h))
                      .map((col) => (
                        <span
                          key={col}
                          className="bg-gray-100 text-gray-500 px-2 py-0.5 rounded text-xs"
                        >
                          {col} (overgeslagen)
                        </span>
                      ))}
                  </div>
                </div>

                {/* Data preview table */}
                <div className="overflow-x-auto rounded-lg border border-gray-200">
                  <table className="text-xs w-full">
                    <thead className="bg-gray-50">
                      <tr>
                        {previewHeaders.map((h) => (
                          <th
                            key={h}
                            className="px-3 py-2 text-left font-medium text-gray-600 whitespace-nowrap"
                          >
                            {h}
                          </th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {previewRows.map((row, i) => (
                        <tr key={i} className="border-t border-gray-100">
                          {row.map((cell, j) => (
                            <td
                              key={j}
                              className="px-3 py-2 text-gray-700 max-w-[150px] truncate"
                              title={cell}
                            >
                              {cell || <span className="text-gray-300">—</span>}
                            </td>
                          ))}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
                <p className="text-xs text-gray-400 mt-1.5">Voorvertoning van de eerste 3 rijen</p>
              </div>

              <div className="flex gap-3 justify-end">
                <button
                  onClick={handleReset}
                  className="px-4 py-2 text-sm text-gray-600 border border-gray-300 rounded-lg hover:bg-gray-50"
                >
                  Ander bestand
                </button>
                <button
                  onClick={handleImport}
                  className="px-4 py-2 text-sm bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 font-medium"
                >
                  Importeren starten
                </button>
              </div>
            </>
          )}

          {/* Importing state */}
          {state === 'importing' && (
            <div className="flex flex-col items-center gap-3 py-10">
              <svg
                className="animate-spin h-8 w-8 text-indigo-500"
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
                  d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
                />
              </svg>
              <p className="text-gray-600 text-sm">Leads worden geïmporteerd…</p>
            </div>
          )}

          {/* Done state */}
          {state === 'done' && result && (
            <div>
              <div className="flex items-center gap-2 mb-4">
                <svg className="w-6 h-6 text-green-500" fill="currentColor" viewBox="0 0 20 20">
                  <path
                    fillRule="evenodd"
                    d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.707-9.293a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z"
                    clipRule="evenodd"
                  />
                </svg>
                <h3 className="font-semibold text-gray-900">Import voltooid</h3>
              </div>

              <div className="grid grid-cols-3 gap-3 mb-4">
                <div className="bg-green-50 border border-green-200 rounded-lg p-3 text-center">
                  <div className="text-2xl font-bold text-green-700">{result.created}</div>
                  <div className="text-xs text-green-600 mt-0.5">Aangemaakt</div>
                </div>
                <div className="bg-yellow-50 border border-yellow-200 rounded-lg p-3 text-center">
                  <div className="text-2xl font-bold text-yellow-700">{result.skipped}</div>
                  <div className="text-xs text-yellow-600 mt-0.5">Overgeslagen</div>
                </div>
                <div className="bg-red-50 border border-red-200 rounded-lg p-3 text-center">
                  <div className="text-2xl font-bold text-red-700">{result.errors.length}</div>
                  <div className="text-xs text-red-600 mt-0.5">Fouten</div>
                </div>
              </div>

              {result.errors.length > 0 && (
                <div className="bg-red-50 border border-red-200 rounded-lg p-3 mb-4 max-h-36 overflow-y-auto">
                  <p className="text-xs font-medium text-red-700 mb-1.5">Fouten:</p>
                  {result.errors.map((e, i) => (
                    <p key={i} className="text-xs text-red-600">
                      Rij {e.row}: {e.message}
                    </p>
                  ))}
                </div>
              )}

              <div className="flex gap-3 justify-end">
                <button
                  onClick={handleReset}
                  className="px-4 py-2 text-sm text-gray-600 border border-gray-300 rounded-lg hover:bg-gray-50"
                >
                  Nog een bestand
                </button>
                <button
                  onClick={onCancel}
                  className="px-4 py-2 text-sm bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 font-medium"
                >
                  Sluiten
                </button>
              </div>
            </div>
          )}

          {/* Error state */}
          {state === 'error' && (
            <div>
              <div className="bg-red-50 border border-red-200 rounded-lg p-4 mb-4">
                <p className="text-sm text-red-700 font-medium">Import mislukt</p>
                {errorMessage && <p className="text-sm text-red-600 mt-1">{errorMessage}</p>}
              </div>
              <div className="flex gap-3 justify-end">
                <button
                  onClick={onCancel}
                  className="px-4 py-2 text-sm text-gray-600 border border-gray-300 rounded-lg hover:bg-gray-50"
                >
                  Annuleren
                </button>
                <button
                  onClick={handleReset}
                  className="px-4 py-2 text-sm bg-indigo-600 text-white rounded-lg hover:bg-indigo-700 font-medium"
                >
                  Opnieuw proberen
                </button>
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  )
}

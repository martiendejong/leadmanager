import { useRef } from 'react'

interface LeadsToolbarProps {
  selectedCount: number
  onEnrich: () => void
  onExport: () => void
  onImport: (file: File) => void
  onClearSelection: () => void
  isEnriching: boolean
}

export default function LeadsToolbar({
  selectedCount,
  onEnrich,
  onExport,
  onImport,
  onClearSelection,
  isEnriching,
}: LeadsToolbarProps) {
  const fileInputRef = useRef<HTMLInputElement>(null)

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (file) {
      onImport(file)
      // Reset so same file can be re-imported
      e.target.value = ''
    }
  }

  return (
    <div className="flex items-center justify-between py-2">
      {/* Left: selection info */}
      <div className="flex items-center gap-3 text-sm text-gray-600">
        {selectedCount > 0 ? (
          <>
            <span className="font-medium text-indigo-700">{selectedCount} lead{selectedCount !== 1 ? 's' : ''} geselecteerd</span>
            <button
              onClick={onClearSelection}
              className="text-gray-400 hover:text-gray-600 underline text-xs"
            >
              Deselecteer alles
            </button>
          </>
        ) : (
          <span className="text-gray-400 text-xs">Selecteer leads om te verrijken</span>
        )}
      </div>

      {/* Right: action buttons */}
      <div className="flex items-center gap-2">
        <input
          ref={fileInputRef}
          type="file"
          accept=".xlsx,.xls"
          onChange={handleFileChange}
          className="hidden"
        />
        <button
          onClick={() => fileInputRef.current?.click()}
          className="px-3 py-1.5 text-sm border border-gray-300 rounded-md text-gray-700 hover:bg-gray-50 font-medium transition-colors"
        >
          Importeer Excel
        </button>
        <button
          onClick={onExport}
          disabled={selectedCount === 0}
          className={`px-3 py-1.5 text-sm rounded-md font-medium transition-colors flex items-center gap-1.5 ${
            selectedCount === 0
              ? 'bg-gray-200 text-gray-400 cursor-not-allowed'
              : 'bg-green-600 text-white hover:bg-green-700 active:bg-green-800'
          }`}
        >
          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 10v6m0 0l-3-3m3 3l3-3m2 8H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
          </svg>
          Exporteer{selectedCount > 0 ? ` (${selectedCount})` : ''}
        </button>
        <button
          onClick={onEnrich}
          disabled={selectedCount === 0 || isEnriching}
          className={`px-3 py-1.5 text-sm rounded-md font-medium transition-colors ${
            selectedCount === 0 || isEnriching
              ? 'bg-indigo-200 text-indigo-400 cursor-not-allowed'
              : 'bg-indigo-600 text-white hover:bg-indigo-700 active:bg-indigo-800'
          }`}
        >
          {isEnriching ? 'Bezig...' : `Verrijk geselecteerde${selectedCount > 0 ? ` (${selectedCount})` : ''}`}
        </button>
      </div>
    </div>
  )
}

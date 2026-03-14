import { useRef } from 'react'

interface LeadsToolbarProps {
  selectedCount: number
  onEnrich: () => void
  onImport: (file: File) => void
  onClearSelection: () => void
  isEnriching: boolean
}

export default function LeadsToolbar({
  selectedCount,
  onEnrich,
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

import type { DuplicateLead } from '../../api/leads'

interface DuplicateModalProps {
  duplicates: DuplicateLead[]
  onMerge: (existingLeadId: string) => void
  onForce: () => void
  onCancel: () => void
}

export default function DuplicateModal({ duplicates, onMerge, onForce, onCancel }: DuplicateModalProps) {
  return (
    <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-[60] p-4">
      <div className="bg-white rounded-lg shadow-xl max-w-lg w-full">
        <div className="px-6 py-4 border-b flex items-center justify-between">
          <div className="flex items-center gap-2">
            <svg className="w-5 h-5 text-yellow-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            <h2 className="text-lg font-semibold">Mogelijke duplicaten gevonden</h2>
          </div>
          <button onClick={onCancel} className="text-gray-400 hover:text-gray-600 text-2xl leading-none">×</button>
        </div>

        <div className="p-6">
          <p className="text-sm text-gray-600 mb-4">
            Deze lead lijkt op bestaande leads in je database. Wil je samenvoegen of toch een nieuwe lead aanmaken?
          </p>

          <div className="space-y-3 mb-6">
            {duplicates.map((dup) => (
              <div
                key={dup.id}
                className="border border-gray-200 rounded-lg p-3 flex items-center justify-between hover:border-indigo-300 transition-colors"
              >
                <div className="flex-1 min-w-0">
                  <div className="flex items-center gap-2">
                    <p className="font-medium text-gray-900 truncate">{dup.name}</p>
                    {dup.score !== null && dup.score !== undefined && (
                      <span className={`text-xs font-semibold px-1.5 py-0.5 rounded ${
                        dup.score >= 7 ? 'bg-green-100 text-green-700' :
                        dup.score >= 4 ? 'bg-yellow-100 text-yellow-700' :
                        'bg-red-100 text-red-700'
                      }`}>
                        {dup.score}/10
                      </span>
                    )}
                  </div>
                  <div className="flex items-center gap-2 mt-0.5 text-xs text-gray-500">
                    {dup.website && <span className="truncate max-w-[180px]">{dup.website}</span>}
                    {dup.city && <span>• {dup.city}</span>}
                    {dup.sector && <span>• {dup.sector}</span>}
                  </div>
                </div>
                <button
                  onClick={() => onMerge(dup.id)}
                  className="ml-3 px-3 py-1.5 text-xs font-medium bg-indigo-600 text-white rounded hover:bg-indigo-700 flex-shrink-0"
                >
                  Samenvoegen
                </button>
              </div>
            ))}
          </div>

          <div className="flex gap-3 justify-end border-t pt-4">
            <button
              onClick={onCancel}
              className="px-4 py-2 text-sm border border-gray-300 rounded-md hover:bg-gray-50"
            >
              Annuleren
            </button>
            <button
              onClick={onForce}
              className="px-4 py-2 text-sm bg-gray-800 text-white rounded-md hover:bg-gray-900"
            >
              Toch aanmaken
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}

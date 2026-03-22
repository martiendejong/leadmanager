import { useEffect, useState } from 'react'
import { getLeadActivities, addLeadActivity } from '../../api/leads'
import type { LeadActivity } from '../../api/leads'
import { useToast } from '../Toast'

const ACTIVITY_TYPES = [
  'NoteAdded',
  'EmailSent',
  'Called',
  'StatusChanged',
  'Enriched',
  'Converted',
]

const ACTIVITY_TYPE_LABELS: Record<string, string> = {
  Created: 'Aangemaakt',
  Enriched: 'Verrijkt',
  StatusChanged: 'Status gewijzigd',
  NoteAdded: 'Notitie toegevoegd',
  EmailSent: 'E-mail verstuurd',
  Called: 'Gebeld',
  Converted: 'Geconverteerd',
}

function ActivityIcon({ type }: { type: string }) {
  const cls = 'w-4 h-4'
  switch (type) {
    case 'Created':
      return (
        <svg className={cls} fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
        </svg>
      )
    case 'Enriched':
      return (
        <svg className={cls} fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
        </svg>
      )
    case 'StatusChanged':
      return (
        <svg className={cls} fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
        </svg>
      )
    case 'NoteAdded':
      return (
        <svg className={cls} fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
        </svg>
      )
    case 'EmailSent':
      return (
        <svg className={cls} fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 8l7.89 5.26a2 2 0 002.22 0L21 8M5 19h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
        </svg>
      )
    case 'Called':
      return (
        <svg className={cls} fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 5a2 2 0 012-2h3.28a1 1 0 01.948.684l1.498 4.493a1 1 0 01-.502 1.21l-2.257 1.13a11.042 11.042 0 005.516 5.516l1.13-2.257a1 1 0 011.21-.502l4.493 1.498a1 1 0 01.684.949V19a2 2 0 01-2 2h-1C9.716 21 3 14.284 3 6V5z" />
        </svg>
      )
    case 'Converted':
      return (
        <svg className={cls} fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4M7.835 4.697a3.42 3.42 0 001.946-.806 3.42 3.42 0 014.438 0 3.42 3.42 0 001.946.806 3.42 3.42 0 013.138 3.138 3.42 3.42 0 00.806 1.946 3.42 3.42 0 010 4.438 3.42 3.42 0 00-.806 1.946 3.42 3.42 0 01-3.138 3.138 3.42 3.42 0 00-1.946.806 3.42 3.42 0 01-4.438 0 3.42 3.42 0 00-1.946-.806 3.42 3.42 0 01-3.138-3.138 3.42 3.42 0 00-.806-1.946 3.42 3.42 0 010-4.438 3.42 3.42 0 00.806-1.946 3.42 3.42 0 013.138-3.138z" />
        </svg>
      )
    default:
      return (
        <svg className={cls} fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
        </svg>
      )
  }
}

function activityColor(type: string): string {
  switch (type) {
    case 'Created': return 'bg-gray-100 text-gray-600'
    case 'Enriched': return 'bg-indigo-100 text-indigo-600'
    case 'StatusChanged': return 'bg-yellow-100 text-yellow-600'
    case 'NoteAdded': return 'bg-blue-100 text-blue-600'
    case 'EmailSent': return 'bg-purple-100 text-purple-600'
    case 'Called': return 'bg-green-100 text-green-600'
    case 'Converted': return 'bg-emerald-100 text-emerald-600'
    default: return 'bg-gray-100 text-gray-600'
  }
}

interface Props {
  leadId: string
}

export default function LeadActivityTimeline({ leadId }: Props) {
  const { showToast } = useToast()
  const [activities, setActivities] = useState<LeadActivity[]>([])
  const [isLoading, setIsLoading] = useState(false)
  const [showAddForm, setShowAddForm] = useState(false)
  const [newType, setNewType] = useState('NoteAdded')
  const [newNote, setNewNote] = useState('')
  const [isSubmitting, setIsSubmitting] = useState(false)

  const load = async () => {
    setIsLoading(true)
    try {
      const data = await getLeadActivities(leadId)
      setActivities(data)
    } catch {
      // Non-critical
    } finally {
      setIsLoading(false)
    }
  }

  useEffect(() => {
    load()
  }, [leadId])

  const handleAdd = async (e: React.FormEvent) => {
    e.preventDefault()
    setIsSubmitting(true)
    try {
      await addLeadActivity(leadId, { activityType: newType, note: newNote || undefined })
      setNewNote('')
      setShowAddForm(false)
      showToast('Activiteit toegevoegd', 'success')
      await load()
    } catch {
      showToast('Activiteit toevoegen mislukt', 'error')
    } finally {
      setIsSubmitting(false)
    }
  }

  return (
    <div>
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-sm font-semibold text-gray-700">Activiteit</h3>
        <button
          onClick={() => setShowAddForm((v) => !v)}
          className="text-xs px-2 py-1 bg-indigo-600 text-white rounded hover:bg-indigo-700"
        >
          + Toevoegen
        </button>
      </div>

      {showAddForm && (
        <form onSubmit={handleAdd} className="mb-4 bg-gray-50 border border-gray-200 rounded-lg p-3 space-y-2">
          <select
            value={newType}
            onChange={(e) => setNewType(e.target.value)}
            className="w-full text-sm border border-gray-300 rounded px-2 py-1.5 focus:outline-none focus:ring-1 focus:ring-indigo-400"
          >
            {ACTIVITY_TYPES.map((t) => (
              <option key={t} value={t}>{ACTIVITY_TYPE_LABELS[t] ?? t}</option>
            ))}
          </select>
          <textarea
            value={newNote}
            onChange={(e) => setNewNote(e.target.value)}
            placeholder="Notitie (optioneel)"
            rows={2}
            className="w-full text-sm border border-gray-300 rounded px-2 py-1.5 focus:outline-none focus:ring-1 focus:ring-indigo-400 resize-none"
          />
          <div className="flex gap-2">
            <button
              type="submit"
              disabled={isSubmitting}
              className="text-xs px-3 py-1 bg-indigo-600 text-white rounded hover:bg-indigo-700 disabled:bg-gray-400"
            >
              {isSubmitting ? 'Opslaan...' : 'Opslaan'}
            </button>
            <button
              type="button"
              onClick={() => setShowAddForm(false)}
              className="text-xs px-3 py-1 border border-gray-300 rounded hover:bg-gray-100"
            >
              Annuleren
            </button>
          </div>
        </form>
      )}

      {isLoading ? (
        <p className="text-xs text-gray-400 py-2">Laden...</p>
      ) : activities.length === 0 ? (
        <p className="text-xs text-gray-400 py-2 italic">Nog geen activiteiten geregistreerd.</p>
      ) : (
        <ol className="relative border-l border-gray-200 ml-2 space-y-4">
          {activities.map((a) => (
            <li key={a.id} className="ml-4">
              <span className={`absolute -left-2 flex items-center justify-center w-4 h-4 rounded-full ring-2 ring-white ${activityColor(a.activityType)}`}>
                <ActivityIcon type={a.activityType} />
              </span>
              <div className="flex items-start justify-between gap-2">
                <div>
                  <p className="text-xs font-medium text-gray-800">
                    {ACTIVITY_TYPE_LABELS[a.activityType] ?? a.activityType}
                  </p>
                  {a.note && (
                    <p className="text-xs text-gray-600 mt-0.5">{a.note}</p>
                  )}
                </div>
                <time className="text-xs text-gray-400 whitespace-nowrap flex-shrink-0">
                  {new Date(a.createdAt).toLocaleDateString('nl-NL', {
                    day: 'numeric',
                    month: 'short',
                    hour: '2-digit',
                    minute: '2-digit',
                  })}
                </time>
              </div>
            </li>
          ))}
        </ol>
      )}
    </div>
  )
}

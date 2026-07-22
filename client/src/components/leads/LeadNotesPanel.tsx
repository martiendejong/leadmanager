import { useState, useEffect } from 'react'
import { fetchLeadNotes, createLeadNote, deleteLeadNote, type LeadNote } from '../../api/leads'
import { useToast } from '../Toast'

interface Props {
  leadId: string
}

export default function LeadNotesPanel({ leadId }: Props) {
  const { showToast } = useToast()
  const [notes, setNotes] = useState<LeadNote[]>([])
  const [isLoading, setIsLoading] = useState(true)
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [newNoteContent, setNewNoteContent] = useState('')

  useEffect(() => {
    loadNotes()
  }, [leadId])

  const loadNotes = async () => {
    setIsLoading(true)
    try {
      const data = await fetchLeadNotes(leadId)
      setNotes(data)
    } catch (err: any) {
      showToast(err.response?.data || 'Notities laden mislukt', 'error')
    } finally {
      setIsLoading(false)
    }
  }

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!newNoteContent.trim()) return

    if (newNoteContent.length > 5000) {
      showToast('Notitie mag maximaal 5000 tekens bevatten', 'error')
      return
    }

    setIsSubmitting(true)
    try {
      const created = await createLeadNote(leadId, { content: newNoteContent.trim() })
      setNotes([created, ...notes])
      setNewNoteContent('')
      showToast('Notitie toegevoegd', 'success')
    } catch (err: any) {
      showToast(err.response?.data || 'Notitie toevoegen mislukt', 'error')
    } finally {
      setIsSubmitting(false)
    }
  }

  const handleDelete = async (noteId: string) => {
    if (!confirm('Weet je zeker dat je deze notitie wilt verwijderen?')) return

    try {
      await deleteLeadNote(leadId, noteId)
      setNotes(notes.filter(n => n.id !== noteId))
      showToast('Notitie verwijderd', 'success')
    } catch (err: any) {
      showToast(err.response?.data || 'Notitie verwijderen mislukt', 'error')
    }
  }

  const formatDate = (dateString: string) => {
    const date = new Date(dateString)
    const now = new Date()
    const diffMs = now.getTime() - date.getTime()
    const diffMins = Math.floor(diffMs / 60000)
    const diffHours = Math.floor(diffMs / 3600000)
    const diffDays = Math.floor(diffMs / 86400000)

    if (diffMins < 1) return 'Zojuist'
    if (diffMins < 60) return `${diffMins} min geleden`
    if (diffHours < 24) return `${diffHours} uur geleden`
    if (diffDays < 7) return `${diffDays} dag${diffDays > 1 ? 'en' : ''} geleden`

    return date.toLocaleDateString('nl-NL', {
      day: 'numeric',
      month: 'short',
      year: date.getFullYear() !== now.getFullYear() ? 'numeric' : undefined
    })
  }

  return (
    <div className="space-y-4">
      {/* Add Note Form */}
      <form onSubmit={handleSubmit} className="bg-gray-50 border rounded-lg p-4">
        <label className="block text-sm font-medium text-gray-700 mb-2">
          Nieuwe notitie
        </label>
        <textarea
          value={newNoteContent}
          onChange={(e) => setNewNoteContent(e.target.value)}
          rows={3}
          maxLength={5000}
          className="w-full px-3 py-2 border rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 mb-2"
          placeholder="Voeg een gesprek of notitie toe..."
          disabled={isSubmitting}
        />
        <div className="flex items-center justify-between">
          <span className="text-xs text-gray-500">
            {newNoteContent.length} / 5000 tekens
          </span>
          <button
            type="submit"
            disabled={isSubmitting || !newNoteContent.trim()}
            className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:bg-gray-400 disabled:cursor-not-allowed text-sm"
          >
            {isSubmitting ? 'Toevoegen...' : 'Toevoegen'}
          </button>
        </div>
      </form>

      {/* Notes Feed */}
      <div>
        <h3 className="text-sm font-semibold text-gray-700 mb-3">
          Notities ({notes.length})
        </h3>

        {isLoading ? (
          <div className="text-center py-8 text-gray-500">Laden...</div>
        ) : notes.length === 0 ? (
          <div className="text-center py-8 text-gray-500 bg-gray-50 rounded-lg border">
            Nog geen notities. Voeg de eerste toe!
          </div>
        ) : (
          <div className="space-y-3">
            {notes.map((note) => (
              <div
                key={note.id}
                className="bg-white border rounded-lg p-4 hover:shadow-sm transition-shadow"
              >
                <div className="flex items-start justify-between mb-2">
                  <div className="flex items-center gap-2 text-xs text-gray-500">
                    <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
                    </svg>
                    <span>{formatDate(note.createdAt)}</span>
                    {note.createdByName && (
                      <>
                        <span>•</span>
                        <span>{note.createdByName}</span>
                      </>
                    )}
                  </div>
                  <button
                    onClick={() => handleDelete(note.id)}
                    className="text-red-600 hover:text-red-800 p-1"
                    title="Verwijder notitie"
                  >
                    <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                    </svg>
                  </button>
                </div>
                <p className="text-sm text-gray-900 whitespace-pre-wrap leading-relaxed">
                  {note.content}
                </p>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}

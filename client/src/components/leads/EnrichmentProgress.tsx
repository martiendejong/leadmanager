import { useEffect, useRef, useState } from 'react'
import * as signalR from '@microsoft/signalr'

interface EnrichmentEvent {
  jobId: string
  leadId: string
  name: string
  processed: number
  total: number
  isSuccess: boolean
}

interface EnrichmentProgressProps {
  jobId: string | null
  onComplete: () => void
}

export default function EnrichmentProgress({ jobId, onComplete }: EnrichmentProgressProps) {
  const [events, setEvents] = useState<EnrichmentEvent[]>([])
  const [isComplete, setIsComplete] = useState(false)
  const [total, setTotal] = useState(0)
  const [processed, setProcessed] = useState(0)
  const connectionRef = useRef<signalR.HubConnection | null>(null)
  const feedRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!jobId) {
      // Reset state when jobId cleared
      setEvents([])
      setIsComplete(false)
      setTotal(0)
      setProcessed(0)
      return
    }

    const token = localStorage.getItem('lm_token')
    const baseUrl = import.meta.env.VITE_API_URL ?? 'http://localhost:5000'

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`${baseUrl}/hubs/enrichment`, {
        accessTokenFactory: () => token ?? '',
      })
      .withAutomaticReconnect()
      .build()

    connection.on('LeadEnriched', (event: EnrichmentEvent) => {
      setEvents((prev) => [...prev, event])
      setProcessed(event.processed)
      setTotal(event.total)

      if (event.processed >= event.total && event.total > 0) {
        setIsComplete(true)
        setTimeout(() => {
          onComplete()
        }, 2000)
      }
    })

    connection
      .start()
      .then(() => {
        return connection.invoke('JoinJob', jobId)
      })
      .catch((err) => {
        console.error('SignalR connection failed:', err)
      })

    connectionRef.current = connection

    return () => {
      connection.stop()
      connectionRef.current = null
    }
  }, [jobId, onComplete])

  // Auto-scroll feed
  useEffect(() => {
    if (feedRef.current) {
      feedRef.current.scrollTop = feedRef.current.scrollHeight
    }
  }, [events])

  if (!jobId) return null

  const percent = total > 0 ? Math.round((processed / total) * 100) : 0
  const successCount = events.filter((e) => e.isSuccess).length
  const failCount = events.filter((e) => !e.isSuccess).length

  return (
    <>
      {/* Backdrop */}
      <div className="fixed inset-0 bg-black/20 z-40" />

      {/* Slide-in panel from right */}
      <div className="fixed right-0 top-0 h-full w-96 bg-white shadow-2xl z-50 flex flex-col animate-in slide-in-from-right duration-300">
        {/* Header */}
        <div className="px-6 py-4 border-b border-gray-200 bg-indigo-600 text-white">
          <h3 className="font-semibold text-lg">Verrijking bezig</h3>
          <p className="text-indigo-200 text-sm mt-0.5">
            {isComplete ? 'Klaar!' : `${processed} van ${total || '?'} verwerkt`}
          </p>
        </div>

        {/* Progress bar */}
        <div className="px-6 py-4 border-b border-gray-100">
          <div className="flex justify-between text-sm text-gray-600 mb-2">
            <span>Voortgang</span>
            <span>{percent}%</span>
          </div>
          <div className="w-full bg-gray-200 rounded-full h-3">
            <div
              className={`h-3 rounded-full transition-all duration-300 ${
                isComplete ? 'bg-green-500' : 'bg-indigo-500'
              }`}
              style={{ width: `${percent}%` }}
            />
          </div>

          {isComplete && (
            <div className="mt-3 flex gap-4 text-sm">
              <span className="text-green-600 font-medium">✓ {successCount} gevonden</span>
              {failCount > 0 && (
                <span className="text-gray-400">✗ {failCount} niet gevonden</span>
              )}
            </div>
          )}
        </div>

        {/* Event feed */}
        <div
          ref={feedRef}
          className="flex-1 overflow-y-auto px-4 py-3 space-y-1"
        >
          {events.length === 0 && (
            <p className="text-sm text-gray-400 text-center mt-8">Wachten op resultaten…</p>
          )}
          {events.map((event, i) => (
            <div
              key={`${event.leadId}-${i}`}
              className={`flex items-start gap-2 text-xs py-1 px-2 rounded ${
                event.isSuccess ? 'text-gray-700' : 'text-gray-400'
              }`}
            >
              <span className={event.isSuccess ? 'text-green-500' : 'text-gray-300'}>
                {event.isSuccess ? '✓' : '✗'}
              </span>
              <span className="font-medium truncate flex-1">{event.name}</span>
              <span className={event.isSuccess ? 'text-green-600' : 'text-gray-400'}>
                {event.isSuccess ? 'Naam gevonden' : 'niet gevonden'}
              </span>
            </div>
          ))}
        </div>

        {isComplete && (
          <div className="px-6 py-4 border-t border-gray-200 bg-green-50">
            <p className="text-sm text-green-700 font-medium">
              Verrijking voltooid — pagina wordt vernieuwd…
            </p>
          </div>
        )}
      </div>
    </>
  )
}

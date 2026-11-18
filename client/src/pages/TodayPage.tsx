import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { useToast } from '../components/Toast'
import { completeNextAction, errorMessage, getTodayActions, type TodayAction } from '../api/crm'

function fmt(iso: string) {
  return new Date(iso).toLocaleString('nl-NL', { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
}

/** The daily call list: everything due today or overdue, with a tap-to-call number. */
export default function TodayPage() {
  const { showToast } = useToast()
  const [rows, setRows] = useState<TodayAction[] | null>(null)
  const [refresh, setRefresh] = useState(0)

  // State is only set after the request resolves, never synchronously in the effect body.
  useEffect(() => {
    let cancelled = false
    getTodayActions()
      .then((data) => {
        if (!cancelled) setRows(data)
      })
      .catch((err) => {
        if (cancelled) return
        showToast(errorMessage(err, 'Lijst laden mislukt'), 'error')
        setRows([])
      })
    return () => {
      cancelled = true
    }
  }, [refresh, showToast])

  const done = async (id: string) => {
    try {
      await completeNextAction(id)
      setRefresh((n) => n + 1)
    } catch (err) {
      showToast(errorMessage(err, 'Afronden mislukt'), 'error')
    }
  }

  return (
    <div className="max-w-3xl">
      <h1 className="text-xl font-semibold text-gray-900 mb-1">Vandaag</h1>
      <p className="text-sm text-gray-500 mb-5">Alles wat vandaag moet of al achterstallig is.</p>

      {rows === null && <p className="text-sm text-gray-400">Laden…</p>}
      {rows?.length === 0 && (
        <div className="bg-white rounded-xl border border-gray-200 p-6 text-sm text-gray-500">
          Niets gepland voor vandaag. Open een lead of klant en plan de volgende actie.
        </div>
      )}

      <ul className="space-y-3">
        {rows?.map((r) => (
          <li key={r.id} className="bg-white rounded-xl border border-gray-200 p-4 flex items-center gap-4">
            <div className="min-w-0 flex-1">
              <Link to={`/${r.parentType}/${r.parentId}`} className="text-sm font-semibold text-gray-900 hover:text-indigo-600">
                {r.parentName}
              </Link>
              <p className="text-sm text-gray-600">{r.title}</p>
              <p className={`text-xs mt-0.5 ${r.overdue ? 'text-red-600 font-medium' : 'text-gray-400'}`}>
                {r.overdue ? 'Achterstallig · ' : ''}{fmt(r.dueAt)}
              </p>
            </div>
            {r.phone && (
              <a href={`tel:${r.phone.replace(/\s/g, '')}`} className="text-sm px-3 py-1.5 rounded-lg border border-indigo-200 text-indigo-700 hover:bg-indigo-50 whitespace-nowrap">
                📞 {r.phone}
              </a>
            )}
            <Link to={`/${r.parentType}/${r.parentId}`} className="text-sm px-3 py-1.5 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700">
              Open
            </Link>
            <button onClick={() => done(r.id)} className="text-sm px-3 py-1.5 rounded-lg border border-gray-300 text-gray-600 hover:bg-gray-50">
              Klaar
            </button>
          </li>
        ))}
      </ul>
    </div>
  )
}

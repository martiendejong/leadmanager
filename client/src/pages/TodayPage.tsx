import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { useToast } from '../components/Toast'
import { completeNextAction, errorMessage, getTodayActions, type TodayAction } from '../api/crm'
import { getDashboard, type Dashboard } from '../api/dashboard'
import { DashboardPanels, DashboardStats } from '../components/dashboard/DashboardPanel'
import NextToContact from '../components/messages/NextToContact'
import OrganizationFilter from '../components/organizations/OrganizationFilter'
import OrganizationBadge from '../components/organizations/OrganizationBadge'
import { useOrganizations } from '../contexts/OrganizationContext'

function fmt(iso: string) {
  return new Date(iso).toLocaleString('en-GB', { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
}

/** The daily call list: everything due today or overdue, with a tap-to-call number. */
export default function TodayPage() {
  const { showToast } = useToast()
  const { filterParam } = useOrganizations()
  const [rows, setRows] = useState<TodayAction[] | null>(null)
  const [refresh, setRefresh] = useState(0)
  const [dash, setDash] = useState<Dashboard | null>(null)

  // State is only set after the request resolves, never synchronously in the effect body.
  useEffect(() => {
    let cancelled = false
    getTodayActions(filterParam)
      .then((data) => {
        if (!cancelled) setRows(data)
      })
      .catch((err) => {
        if (cancelled) return
        showToast(errorMessage(err, 'Could not load the list'), 'error')
        setRows([])
      })
    return () => {
      cancelled = true
    }
  }, [refresh, showToast, filterParam])

  // The dashboard reloads with the list: completing an action changes its figures too.
  useEffect(() => {
    let cancelled = false
    getDashboard(filterParam)
      .then((d) => { if (!cancelled) setDash(d) })
      .catch((err) => { if (!cancelled) showToast(errorMessage(err, 'Could not load the dashboard'), 'error') })
    return () => { cancelled = true }
  }, [refresh, showToast, filterParam])

  const done = async (id: string) => {
    try {
      await completeNextAction(id)
      setRefresh((n) => n + 1)
    } catch (err) {
      showToast(errorMessage(err, 'Could not complete the action'), 'error')
    }
  }

  return (
    <div className="max-w-6xl">
      <h1 className="text-xl font-semibold text-gray-900 mb-1">Today</h1>
      <p className="text-sm text-gray-500 mb-3">What needs doing now, and where things stand.</p>
      <OrganizationFilter className="mb-5" />

      {dash && <DashboardStats data={dash} />}

      <div className="grid grid-cols-1 lg:grid-cols-5 gap-6 items-start">
        <div className="lg:col-span-3">
        <NextToContact />
        <h2 className="text-xs font-semibold text-gray-500 uppercase tracking-wider mb-2">Due today and overdue</h2>

      {rows === null && <p className="text-sm text-gray-400">Loading…</p>}
      {rows?.length === 0 && (
        <div className="bg-white rounded-xl border border-gray-200 p-6 text-sm text-gray-500">
          Nothing scheduled for today. Open a lead or client and plan the next action.
        </div>
      )}

      <ul className="space-y-3">
        {rows?.map((r) => (
          <li key={r.id} className="bg-white rounded-xl border border-gray-200 p-4 flex items-center gap-4">
            <div className="min-w-0 flex-1">
              <Link to={`/${r.parentType}/${r.parentId}`} className="text-sm font-semibold text-gray-900 hover:text-indigo-600">
                {r.parentName}
              </Link>
              {r.organizationId && <OrganizationBadge organizationId={r.organizationId} className="ml-2" />}
              <p className={`text-sm ${(r.title.startsWith('Call now:') || r.title.startsWith('Bel nu:')) ? 'text-amber-700 font-medium' : 'text-gray-600'}`}>{r.title}</p>
              <p className={`text-xs mt-0.5 ${r.overdue ? 'text-red-600 font-medium' : 'text-gray-400'}`}>
                {r.overdue ? 'Overdue · ' : ''}{fmt(r.dueAt)}
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
              Done
            </button>
          </li>
        ))}
      </ul>
        </div>
        <div className="lg:col-span-2">{dash ? <DashboardPanels data={dash} /> : <p className="text-sm text-gray-400">Loading…</p>}</div>
      </div>
    </div>
  )
}

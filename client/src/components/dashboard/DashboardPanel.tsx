import { Link } from 'react-router-dom'
import { INTERACTION_LABELS, type InteractionKind } from '../../api/crm'
import { KIND_LABELS, type DocumentKind } from '../../api/documents'
import type { Dashboard, DashboardClientRow, DashboardDocumentRow } from '../../api/dashboard'
import { ScoreBadge } from '../crm/PriorityCard'

const card = 'bg-white rounded-xl border border-gray-200 p-4'
const heading = 'text-xs font-semibold text-gray-500 uppercase tracking-wider mb-2'

const PROJECT_LABELS: Record<string, string> = {
  Concept: 'Internal concept',
  Pilot: 'Pilot',
  Active: 'Active',
  Maintenance: 'Maintenance',
  'On Hold': 'On hold',
  Completed: 'Completed',
}
const LEAD_STAGES: [string, string][] = [
  ['New', 'New'],
  ['Contacted', 'Contacted'],
  ['Qualified', 'Qualified'],
  ['ProposalSent', 'Proposal sent'],
  ['Won', 'Won'],
  ['Lost', 'Lost'],
]

function day(iso?: string | null) {
  return iso ? new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' }) : null
}

function money(n: number) {
  return n.toLocaleString('en-GB', { style: 'currency', currency: 'EUR', maximumFractionDigits: 0 })
}

function ago(iso: string) {
  const days = Math.floor((Date.now() - new Date(iso).getTime()) / 86_400_000)
  if (days <= 0) return 'today'
  if (days === 1) return 'yesterday'
  return `${days} days ago`
}

function Tile({ label, value, sub, tone, to }: { label: string; value: string | number; sub?: string; tone?: 'alert'; to?: string }) {
  const body = (
    <div className={`${card} h-full ${to ? 'hover:border-indigo-300 transition-colors' : ''}`}>
      <p className="text-xs text-gray-500">{label}</p>
      <p className={`text-2xl font-semibold mt-1 ${tone === 'alert' ? 'text-red-600' : 'text-gray-900'}`}>{value}</p>
      {sub && <p className="text-xs text-gray-400 mt-0.5">{sub}</p>}
    </div>
  )
  return to ? <Link to={to} className="block">{body}</Link> : body
}

function Empty({ children }: { children: React.ReactNode }) {
  return <p className="text-sm text-gray-400">{children}</p>
}

function ClientList({ rows, show }: { rows: DashboardClientRow[]; show: 'next' | 'contact' }) {
  return (
    <ul className="divide-y divide-gray-100">
      {rows.map((c) => (
        <li key={c.id} className="py-1.5 flex items-center gap-2 text-sm">
          <Link to={`/clients/${c.id}`} className="text-indigo-600 hover:underline truncate">{c.name}</Link>
          <span className="ml-auto flex items-center gap-2 flex-shrink-0">
            {show === 'next' && <span className="text-xs text-gray-400">{c.nextActionAt ? `next: ${day(c.nextActionAt)}` : 'nothing planned'}</span>}
            {show === 'contact' && <span className="text-xs text-gray-400">{c.lastContactAt ? `last contact ${ago(c.lastContactAt)}` : 'never contacted'}</span>}
            <ScoreBadge score={c.score} />
          </span>
        </li>
      ))}
    </ul>
  )
}

function DocumentList({ rows, show }: { rows: DashboardDocumentRow[]; show: 'sent' | 'valid' }) {
  return (
    <ul className="divide-y divide-gray-100">
      {rows.map((d) => (
        <li key={d.id} className="py-1.5 text-sm">
          <div className="flex items-center gap-2">
            <Link to={`/clients/${d.clientId}`} className="text-indigo-600 hover:underline truncate">{d.clientName}</Link>
            <span className="ml-auto text-xs text-gray-400 flex-shrink-0">
              {show === 'sent' ? `sent ${ago(d.documentDate)}` : `valid until ${day(d.validUntil)}`}
            </span>
          </div>
          <p className="text-xs text-gray-500 truncate">
            {KIND_LABELS[d.kind as DocumentKind] ?? d.kind}: {d.title}
            {d.amount != null && ` · ${money(d.amount)}`}
          </p>
        </li>
      ))}
    </ul>
  )
}

/** The figures row: what needs doing and where the business stands. */
export function DashboardStats({ data }: { data: Dashboard }) {
  const c = data.counts
  return (
    <div className="grid grid-cols-2 md:grid-cols-3 xl:grid-cols-6 gap-3 mb-6">
      <Tile label="Due today" value={c.dueToday} sub={c.overdue > 0 ? `${c.overdue} overdue` : 'nothing overdue'} tone={c.overdue > 0 ? 'alert' : undefined} to="/actions" />
      <Tile label="Open actions" value={c.openActions} to="/actions" />
      <Tile label="Clients" value={c.clients} sub={`${c.activeProjects} running projects`} to="/clients" />
      <Tile label="Open quotes" value={c.openQuotes} sub={c.openQuotes > 0 ? money(c.openQuotesAmount) : undefined} />
      <Tile label="Awaiting signature" value={c.awaitingSignature} />
      <Tile label="Leads" value={c.leads} to="/leads" />
    </div>
  )
}

/** The panels below the call list: priorities, attention, documents, projects, leads and recent activity. */
export function DashboardPanels({ data }: { data: Dashboard }) {
  const projects = Object.entries(data.projectsByStatus).sort((a, b) => b[1] - a[1])
  const totalProjects = projects.reduce((n, [, v]) => n + v, 0)
  const stages = LEAD_STAGES.filter(([k]) => (data.leadsByStage[k] ?? 0) > 0)
  const totalLeads = Object.values(data.leadsByStage).reduce((n, v) => n + v, 0)

  return (
    <div className="space-y-4">
      <section className={card}>
        <h3 className={heading}>Top priority clients</h3>
        {data.topClients.length === 0 ? <Empty>Score your clients (value, urgency, effort) to see them here.</Empty> : <ClientList rows={data.topClients} show="next" />}
      </section>

      <section className={card}>
        <h3 className={heading}>No next action planned</h3>
        {data.noNextAction.length === 0 ? <Empty>Every client has a next step.</Empty> : <ClientList rows={data.noNextAction} show="contact" />}
      </section>

      <section className={card}>
        <h3 className={heading}>No contact for 30+ days</h3>
        {data.silent.length === 0 ? <Empty>You are in touch with everyone.</Empty> : <ClientList rows={data.silent} show="contact" />}
      </section>

      <section className={card}>
        <h3 className={heading}>Awaiting signature</h3>
        {data.awaitingSignature.length === 0 ? <Empty>Nothing waiting to be signed.</Empty> : <DocumentList rows={data.awaitingSignature} show="sent" />}
      </section>

      <section className={card}>
        <h3 className={heading}>Expiring within 45 days</h3>
        {data.expiring.length === 0 ? <Empty>No agreements or quotes about to expire.</Empty> : <DocumentList rows={data.expiring} show="valid" />}
      </section>

      <section className={card}>
        <h3 className={heading}>Projects</h3>
        {totalProjects === 0 ? <Empty>No projects yet.</Empty> : (
          <ul className="space-y-1.5">
            {projects.map(([status, n]) => (
              <li key={status} className="text-sm flex items-center gap-2">
                <span className="w-28 text-gray-600 flex-shrink-0">{PROJECT_LABELS[status] ?? status}</span>
                <span className="flex-1 h-2 bg-gray-100 rounded-full overflow-hidden" aria-hidden>
                  <span className="block h-full bg-indigo-400" style={{ width: `${Math.round((n / totalProjects) * 100)}%` }} />
                </span>
                <span className="w-6 text-right text-gray-700">{n}</span>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className={card}>
        <h3 className={heading}>Lead pipeline</h3>
        {totalLeads === 0 ? <Empty>No leads yet.</Empty> : (
          <ul className="flex flex-wrap gap-2">
            {stages.map(([key, label]) => (
              <li key={key} className="text-xs px-2.5 py-1 rounded-full bg-gray-50 border border-gray-200 text-gray-700">
                {label} <span className="font-semibold">{data.leadsByStage[key]}</span>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className={card}>
        <h3 className={heading}>Recent activity</h3>
        {data.recentActivity.length === 0 ? <Empty>Nothing logged yet.</Empty> : (
          <ul className="divide-y divide-gray-100">
            {data.recentActivity.map((a) => (
              <li key={a.id} className="py-1.5 text-sm">
                <div className="flex items-center gap-2">
                  <Link to={`/${a.parentType}/${a.parentId}`} className="text-indigo-600 hover:underline truncate">{a.parentName}</Link>
                  <span className="text-xs text-gray-400 flex-shrink-0">{INTERACTION_LABELS[a.kind as InteractionKind] ?? a.kind}</span>
                  <span className="ml-auto text-xs text-gray-400 flex-shrink-0">{ago(a.at)}</span>
                </div>
                <p className="text-xs text-gray-500 truncate">{a.summary}</p>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  )
}

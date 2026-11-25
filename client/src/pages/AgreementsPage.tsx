import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { useToast } from '../components/Toast'
import { errorMessage } from '../api/crm'
import { DueRow } from '../components/crm/InvoicesSection'
import { termsLine } from '../components/crm/ServicesSection'
import {
  SERVICE_LABELS,
  getAgreements,
  getInvoicingDue,
  getFedhaSyncStatus,
  runFedhaSync,
  linkFedhaContact,
  eur,
  type FedhaSyncStatus,
  type ClientDue,
  type AgreementDoc,
  type ClientAgreements,
  type ServiceKind,
} from '../api/agreements'

function fmtDate(iso?: string | null) {
  return iso ? new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' }) : ''
}

function DocList({ title, docs, empty }: { title: string; docs: AgreementDoc[]; empty: string }) {
  return (
    <div>
      <h3 className="text-xs font-semibold uppercase tracking-wide text-gray-500 mb-1">{title}</h3>
      {docs.length === 0 ? (
        <p className="text-xs text-gray-400">{empty}</p>
      ) : (
        <ul className="space-y-1">
          {docs.map((d) => (
            <li key={d.id} className="text-sm text-gray-800">
              {d.title}
              {d.version > 1 && <span className="text-xs text-gray-500"> v{d.version}</span>}
              <span className="text-xs text-gray-500"> · {d.status} · {fmtDate(d.documentDate)}{d.fileCount > 0 ? ` · ${d.fileCount} file${d.fileCount > 1 ? 's' : ''}` : ''}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

/** Last Fedha sync, a button to run it, and the Fedha contacts that have invoices but no client yet. */
function FedhaSyncPanel({ clients, onSynced }: { clients: { id: string; name: string }[]; onSynced: () => void }) {
  const { showToast } = useToast()
  const [st, setSt] = useState<FedhaSyncStatus | null>(null)
  const [busy, setBusy] = useState(false)
  const load = () => getFedhaSyncStatus().then(setSt).catch(() => setSt(null))
  useEffect(() => { load() }, [])

  async function run() {
    setBusy(true)
    try {
      const r = await runFedhaSync()
      showToast(`Fedha sync: ${r.created} new, ${r.updated} updated, ${r.unchanged} unchanged`, 'success')
      await load()
      onSynced()
    } catch (e) {
      showToast(errorMessage(e, 'Fedha sync failed'), 'error')
    } finally { setBusy(false) }
  }

  async function link(contactId: number, name: string, clientId: string) {
    if (!clientId) return
    try { await linkFedhaContact(contactId, name, clientId); showToast(`${name} linked. Run the sync to bring in its invoices.`, 'success'); await load() }
    catch (e) { showToast(errorMessage(e, 'Could not link'), 'error') }
  }

  if (!st) return null
  const last = st.lastRun
  return (
    <section className="bg-white border border-gray-200 rounded-xl p-4 mb-4" aria-label="Fedha sync">
      <div className="flex flex-wrap items-center gap-2">
        <h2 className="text-sm font-semibold text-gray-800 mr-auto">Invoices from Fedha</h2>
        {st.configured ? (
          <button type="button" disabled={busy} onClick={run} className="text-sm px-3 py-1 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50">{busy ? 'Syncing...' : 'Sync now'}</button>
        ) : <span className="text-xs text-gray-500">Not connected to Fedha on this environment</span>}
      </div>
      <p className="text-xs text-gray-500 mt-1">
        Read-only copy of the Fedha invoices (concept, sent, open, paid), refreshed daily. {st.lastSuccessAt ? `Last successful sync ${fmtDate(st.lastSuccessAt)}.` : 'No successful sync yet.'}
        {last?.error ? ` Last run failed: ${last.error}` : ''}
      </p>
      {st.unmatched.length > 0 && (
        <div className="mt-3">
          <h3 className="text-xs font-semibold uppercase tracking-wide text-gray-500 mb-1">Fedha contacts without a client</h3>
          <ul className="divide-y divide-gray-100">
            {st.unmatched.map((u) => (
              <li key={u.fedhaContactId} className="py-1.5 flex flex-wrap items-center gap-2 text-sm">
                <span className="font-medium text-gray-800">{u.name}</span>
                <span className="text-xs text-gray-500">{u.invoiceCount} invoice{u.invoiceCount > 1 ? 's' : ''}{u.openAmount > 0 ? ` · ${eur(u.openAmount)} open` : ''}</span>
                <select className="ml-auto text-xs border border-gray-300 rounded-md px-1 py-1" defaultValue="" onChange={(e) => link(u.fedhaContactId, u.name, e.target.value)} aria-label={`Link ${u.name} to a client`}>
                  <option value="">Link to client...</option>
                  {clients.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
                </select>
              </li>
            ))}
          </ul>
        </div>
      )}
    </section>
  )
}

function BillingLine({ r }: { r: ClientAgreements }) {
  const b = r.billing
  if (!b || (b.concepts + b.sent + b.paid + b.cancelled) === 0) return <p className="text-xs text-gray-400 mt-3">No invoices recorded</p>
  return (
    <div className="mt-3 border-t border-gray-100 pt-2">
      <div className="flex flex-wrap items-center gap-2 text-xs">
        <span className="font-semibold uppercase tracking-wide text-gray-500">Invoices</span>
        {b.concepts > 0 && <span className="px-2 py-0.5 rounded-full border bg-gray-50 text-gray-700 border-gray-200">{b.concepts} concept</span>}
        {b.sent > 0 && <span className="px-2 py-0.5 rounded-full border bg-amber-50 text-amber-800 border-amber-200">{b.sent} sent, {eur(b.openAmount)} open</span>}
        {b.paid > 0 && <span className="px-2 py-0.5 rounded-full border bg-green-50 text-green-700 border-green-200">{b.paid} paid</span>}
        {b.sendUnknown > 0 && <span className="px-2 py-0.5 rounded-full border bg-gray-50 text-gray-600 border-gray-300" title="Marked sent in the books but no send date recorded">{b.sendUnknown} send unknown</span>}
        {b.lastInvoiceDate && <span className="text-gray-400">last invoice {fmtDate(b.lastInvoiceDate)}</span>}
      </div>
      <ul className="mt-1 text-xs text-gray-600">
        {b.recent.map((i) => (
          <li key={i.id}>{i.number} · {i.status}{i.status === 'Sent' && i.outstandingAmount ? ` · ${eur(i.outstandingAmount)} open` : ''}{i.sendUnknown ? ' · send unknown' : ''} · {fmtDate(i.invoiceDate)} · {eur(i.amountInclVat)} incl.</li>
        ))}
      </ul>
    </div>
  )
}

/** One view over all clients: services (such as website management), contracts sent or signed, and concepts. */
export default function AgreementsPage() {
  const { showToast } = useToast()
  const [rows, setRows] = useState<ClientAgreements[] | null>(null)
  const [service, setService] = useState<ServiceKind | ''>('')
  const [due, setDue] = useState<ClientDue[]>([])
  const [reload, setReload] = useState(0)

  useEffect(() => {
    getInvoicingDue().then(setDue).catch(() => setDue([]))
  }, [reload])

  useEffect(() => {
    getAgreements(service)
      .then(setRows)
      .catch((e) => { setRows([]); showToast(errorMessage(e, 'Something went wrong'), 'error') })
  }, [service, showToast, reload])

  return (
    <div className="p-4 sm:p-6 max-w-5xl">
      <div className="flex flex-wrap items-center gap-3 mb-4">
        <h1 className="text-lg font-semibold text-gray-900 mr-auto">Agreements</h1>
        <label className="text-sm text-gray-600 flex items-center gap-2">
          Service
          <select className="text-sm border border-gray-300 rounded-lg px-2 py-1.5" value={service} onChange={(e) => setService(e.target.value as ServiceKind | '')}>
            <option value="">All clients</option>
            {(Object.keys(SERVICE_LABELS) as ServiceKind[]).map((k) => <option key={k} value={k}>{SERVICE_LABELS[k]}</option>)}
          </select>
        </label>
      </div>
      <FedhaSyncPanel clients={(rows ?? []).map((r) => ({ id: r.clientId, name: r.clientName }))} onSynced={() => setReload((n) => n + 1)} />
      {due.some((c) => c.items.some((i) => i.state === 'Due' || i.state === 'Upcoming' || i.state === 'NeedsInput')) && (
        <section className="bg-white border border-gray-200 rounded-xl p-4 mb-4" aria-label="Invoicing due">
          <h2 className="text-sm font-semibold text-gray-800 mb-1">Invoicing due</h2>
          <p className="text-xs text-gray-500 mb-2">Derived from the agreed terms and the invoices recorded. Nothing is sent from here.</p>
          {due.map((c) => {
            const items = c.items.filter((i) => i.state === 'Due' || i.state === 'Upcoming' || i.state === 'NeedsInput')
            if (items.length === 0) return null
            return (
              <div key={c.clientId} className="mb-2">
                <Link to={`/clients/${c.clientId}`} className="text-sm font-semibold text-indigo-700 hover:underline">{c.clientName}</Link>
                <ul className="divide-y divide-gray-100">{items.map((i) => <DueRow key={i.serviceId} d={i} />)}</ul>
              </div>
            )
          })}
        </section>
      )}
      {rows == null ? (
        <p className="text-sm text-gray-500">Loading...</p>
      ) : rows.length === 0 ? (
        <p className="text-sm text-gray-500">No clients match.</p>
      ) : (
        <div className="space-y-4">
          {rows.map((r) => (
            <section key={r.clientId} className="bg-white border border-gray-200 rounded-xl p-4">
              <div className="flex flex-wrap items-center gap-2 mb-3">
                <Link to={`/clients/${r.clientId}`} className="text-sm font-semibold text-indigo-700 hover:underline">{r.clientName}</Link>
                {r.services.length === 0 && <span className="text-xs text-gray-400">no services recorded</span>}
                {r.services.map((s) => (
                  <span key={s.id} className={`text-xs px-2 py-0.5 rounded-full border ${s.status === 'Active' ? 'bg-green-50 text-green-700 border-green-200' : 'bg-gray-50 text-gray-600 border-gray-200'}`}>
                    {s.name} · {s.status}{termsLine(s) ? ` · ${termsLine(s)}` : ''}
                  </span>
                ))}
              </div>
              <div className="grid gap-4 sm:grid-cols-3">
                <DocList title="Signed" docs={r.contracts.filter((d) => d.status === 'Signed')} empty="Nothing signed yet" />
                <DocList title="Sent, no answer yet" docs={r.contracts.filter((d) => d.status === 'Sent')} empty="Nothing waiting for an answer" />
                <DocList title="Concepts" docs={r.concepts} empty="No concepts" />
              </div>
              <BillingLine r={r} />
              {r.contracts.some((d) => !['Signed', 'Sent'].includes(d.status)) && (
                <div className="mt-3">
                  <DocList title="Rejected, expired or cancelled" docs={r.contracts.filter((d) => !['Signed', 'Sent'].includes(d.status))} empty="" />
                </div>
              )}
            </section>
          ))}
        </div>
      )}
    </div>
  )
}

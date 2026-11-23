import { useCallback, useEffect, useState } from 'react'
import { useToast } from '../Toast'
import { errorMessage } from '../../api/crm'
import {
  createInvoice,
  deleteInvoice,
  eur,
  getInvoices,
  getInvoicingDue,
  getServices,
  updateInvoice,
  type ClientInvoice,
  type ClientService,
  type DueItem,
  type InvoiceStatus,
} from '../../api/agreements'

const small = 'text-sm border border-gray-300 rounded-md px-2 py-1 w-full'
const STATES: InvoiceStatus[] = ['Draft', 'Sent', 'Paid', 'Cancelled']
const fmt = (iso?: string | null) => (iso ? new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' }) : '')
const STATE_CLS: Record<string, string> = {
  Due: 'bg-red-50 text-red-700 border-red-200',
  Upcoming: 'bg-amber-50 text-amber-800 border-amber-200',
  Covered: 'bg-green-50 text-green-700 border-green-200',
  NeedsInput: 'bg-gray-50 text-gray-700 border-gray-300',
  Nothing: 'bg-gray-50 text-gray-500 border-gray-200',
}

export function DueRow({ d, onUse }: { d: DueItem; onUse?: (d: DueItem) => void }) {
  return (
    <li className="py-2 text-sm">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-medium text-gray-800">{d.serviceName}</span>
        <span className={`text-xs px-2 py-0.5 rounded-full border ${STATE_CLS[d.state] ?? STATE_CLS.Nothing}`}>{d.state === 'NeedsInput' ? 'Needs input' : d.state}</span>
        {d.amountExclVat != null && <span className="text-gray-700">{eur(d.amountExclVat)} excl. · {eur(d.amountInclVat)} incl. VAT</span>}
        {d.periodStart && d.periodEnd && <span className="text-xs text-gray-500">{fmt(d.periodStart)} to {fmt(d.periodEnd)}</span>}
        {onUse && (d.state === 'Due' || d.state === 'Upcoming') && (
          <button type="button" onClick={() => onUse(d)} className="ml-auto text-xs px-2 py-1 rounded-md text-indigo-700 hover:bg-indigo-50">Prefill invoice</button>
        )}
      </div>
      <p className="text-xs text-gray-500 mt-0.5">{d.explanation}</p>
    </li>
  )
}

/** Invoices sent to this client, tied to their service, plus what is due next according to the agreed terms. */
export default function InvoicesSection({ clientId, refreshKey = 0 }: { clientId: string; refreshKey?: number }) {
  const { showToast } = useToast()
  const [items, setItems] = useState<ClientInvoice[]>([])
  const [services, setServices] = useState<ClientService[]>([])
  const [due, setDue] = useState<DueItem[]>([])
  const [f, setF] = useState({ number: '', amount: '', serviceId: '', start: '', end: '', status: 'Sent' as InvoiceStatus, sentTo: '' })
  const set = (k: keyof typeof f, v: string) => setF((p) => ({ ...p, [k]: v }))

  const load = useCallback(() => {
    getInvoices(clientId).then(setItems).catch((e) => showToast(errorMessage(e, 'Something went wrong'), 'error'))
    getServices(clientId).then(setServices).catch(() => undefined)
    getInvoicingDue(clientId).then((r) => setDue(r[0]?.items ?? [])).catch(() => undefined)
  }, [clientId, showToast])

  useEffect(() => { load() }, [load, refreshKey])

  function prefill(d: DueItem) {
    setF((p) => ({
      ...p, serviceId: d.serviceId, amount: String(d.amountExclVat ?? ''),
      start: d.periodStart?.slice(0, 10) ?? '', end: d.periodEnd?.slice(0, 10) ?? '',
    }))
  }

  async function add() {
    const amount = Number(f.amount.replace(',', '.'))
    if (!f.number.trim() || Number.isNaN(amount)) { showToast('Invoice number and amount are required', 'error'); return }
    try {
      await createInvoice(clientId, {
        number: f.number.trim(), amountExclVat: amount, serviceId: f.serviceId || null,
        periodStart: f.start || null, periodEnd: f.end || null, status: f.status, sentTo: f.sentTo || null,
      })
      setF({ number: '', amount: '', serviceId: '', start: '', end: '', status: 'Sent', sentTo: '' })
      load()
    } catch (e) {
      showToast(errorMessage(e, 'Something went wrong'), 'error')
    }
  }

  async function setStatus(i: ClientInvoice, status: InvoiceStatus) {
    try {
      await updateInvoice(i.id, {
        number: i.number, amountExclVat: i.amountExclVat, invoiceDate: i.invoiceDate, periodStart: i.periodStart, periodEnd: i.periodEnd,
        vatRate: i.vatRate, status, sentTo: i.sentTo, notes: i.notes, serviceId: i.serviceId,
      })
      load()
    } catch (e) {
      showToast(errorMessage(e, 'Something went wrong'), 'error')
    }
  }

  async function remove(i: ClientInvoice) {
    if (!window.confirm(`Remove invoice ${i.number}? Cancel it instead if it was sent.`)) return
    try { await deleteInvoice(i.id); load() } catch (e) { showToast(errorMessage(e, 'Something went wrong'), 'error') }
  }

  const svcName = (id?: string | null) => services.find((s) => s.id === id)?.name ?? ''

  return (
    <section className="bg-white border border-gray-200 rounded-xl p-4 max-w-5xl" aria-label="Invoices">
      <h2 className="text-sm font-semibold text-gray-800 mb-2">Invoices</h2>
      {due.length > 0 && (
        <div className="mb-3">
          <h3 className="text-xs font-semibold uppercase tracking-wide text-gray-500">To invoice next</h3>
          <ul className="divide-y divide-gray-100">{due.map((d) => <DueRow key={d.serviceId} d={d} onUse={prefill} />)}</ul>
        </div>
      )}
      <div className="grid gap-2 sm:grid-cols-4 mb-3 p-3 bg-gray-50 rounded-lg">
        <input className={small} placeholder="Number, e.g. 2026-15" value={f.number} onChange={(e) => set('number', e.target.value)} aria-label="Invoice number" />
        <input className={small} placeholder="Amount excl. VAT" inputMode="decimal" value={f.amount} onChange={(e) => set('amount', e.target.value)} aria-label="Amount excl. VAT" />
        <select className={small} value={f.serviceId} onChange={(e) => set('serviceId', e.target.value)} aria-label="Service">
          <option value="">No service</option>
          {services.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
        </select>
        <select className={small} value={f.status} onChange={(e) => set('status', e.target.value)} aria-label="Status">
          {STATES.map((s) => <option key={s} value={s}>{s}</option>)}
        </select>
        <input type="date" className={small} value={f.start} onChange={(e) => set('start', e.target.value)} aria-label="Period start" />
        <input type="date" className={small} value={f.end} onChange={(e) => set('end', e.target.value)} aria-label="Period end" />
        <input className={small} placeholder="Sent to" value={f.sentTo} onChange={(e) => set('sentTo', e.target.value)} aria-label="Sent to" />
        <button type="button" onClick={add} className="text-sm px-3 py-1 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700">Record invoice</button>
      </div>
      {items.length === 0 ? (
        <p className="text-sm text-gray-500">No invoices recorded yet.</p>
      ) : (
        <ul className="divide-y divide-gray-100">
          {items.map((i) => (
            <li key={i.id} className="py-2 flex flex-wrap items-center gap-2 text-sm">
              <span className="font-medium text-gray-800">{i.number}</span>
              <span className="text-gray-700">{eur(i.amountExclVat)} excl. · {eur(i.amountInclVat)} incl.</span>
              {i.periodStart && i.periodEnd && <span className="text-xs text-gray-500">{fmt(i.periodStart)} to {fmt(i.periodEnd)}</span>}
              {i.serviceId && <span className="text-xs text-gray-500">{svcName(i.serviceId)}</span>}
              <span className="ml-auto flex items-center gap-1">
                <select className="text-xs border border-gray-200 rounded-md px-1 py-0.5" value={i.status} onChange={(e) => setStatus(i, e.target.value as InvoiceStatus)} aria-label={`Status of ${i.number}`}>
                  {STATES.map((s) => <option key={s} value={s}>{s}</option>)}
                </select>
                <button type="button" onClick={() => remove(i)} className="text-xs px-2 py-1 rounded-md text-gray-500 hover:bg-gray-100">Remove</button>
              </span>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

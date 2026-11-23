import { useCallback, useEffect, useState } from 'react'
import { useToast } from '../Toast'
import { errorMessage } from '../../api/crm'
import {
  INTERVAL_LABELS,
  SERVICE_LABELS,
  SERVICE_STATUS_LABELS,
  createService,
  deleteService,
  eur,
  getServices,
  updateService,
  type BillingInterval,
  type ClientService,
  type ServiceKind,
  type ServiceStatus,
} from '../../api/agreements'

const input = 'text-sm border border-gray-300 rounded-lg px-3 py-1.5 focus:outline-none focus:ring-2 focus:ring-indigo-300'
const small = 'text-sm border border-gray-300 rounded-md px-2 py-1 w-full'
const STATUS_CLS: Record<ServiceStatus, string> = {
  Proposed: 'bg-gray-50 text-gray-600 border-gray-200',
  Active: 'bg-green-50 text-green-700 border-green-200',
  Paused: 'bg-amber-50 text-amber-800 border-amber-200',
  Ended: 'bg-gray-50 text-gray-500 border-gray-200',
}

const day = (iso?: string | null) => (iso ? iso.slice(0, 10) : '')
const fmt = (iso?: string | null) => (iso ? new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' }) : '')

/** One line with the agreed terms, so the agreement is readable without opening the form. */
export function termsLine(s: ClientService): string {
  const parts: string[] = []
  if (s.billingInterval === 'Hourly') {
    parts.push(s.hourlyRate != null ? `${eur(s.hourlyRate)} / hour` : 'hourly, no rate yet')
  } else {
    const amount = s.billingAmount ?? (s.billingInterval === 'Monthly' ? s.monthlyAmount : null)
    if (amount != null) parts.push(`${eur(amount)} ${s.billingInterval === 'OneOff' ? 'one-off' : INTERVAL_LABELS[s.billingInterval].toLowerCase()} excl. VAT`)
  }
  if (s.freeUntil) parts.push(`free until ${fmt(s.freeUntil)}${s.freeReason ? ` (${s.freeReason})` : ''}`)
  if (s.paidUntil) parts.push(`paid until ${fmt(s.paidUntil)}`)
  return parts.join(' · ')
}

function BillingForm({ s, onSaved }: { s: ClientService; onSaved: () => void }) {
  const { showToast } = useToast()
  const [f, setF] = useState({
    billingInterval: s.billingInterval ?? 'Monthly',
    billingAmount: String(s.billingAmount ?? (s.billingInterval === 'Monthly' ? s.monthlyAmount ?? '' : '')),
    hourlyRate: String(s.hourlyRate ?? ''),
    vatRate: String(s.vatRate ?? ''),
    startDate: day(s.startDate),
    endDate: day(s.endDate),
    paidUntil: day(s.paidUntil),
    freeUntil: day(s.freeUntil),
    freeReason: s.freeReason ?? '',
    invoiceNote: s.invoiceNote ?? '',
  })
  const set = (k: keyof typeof f, v: string) => setF((p) => ({ ...p, [k]: v }))
  const num = (v: string) => (v.trim() === '' ? null : Number(v.replace(',', '.')))

  async function save() {
    try {
      await updateService(s.id, {
        kind: s.kind, name: s.name, status: s.status, projectId: s.projectId, notes: s.notes, monthlyAmount: s.monthlyAmount,
        billingInterval: f.billingInterval as BillingInterval,
        billingAmount: num(f.billingAmount), hourlyRate: num(f.hourlyRate), vatRate: num(f.vatRate),
        startDate: f.startDate || null, endDate: f.endDate || null,
        paidUntil: f.paidUntil || null, freeUntil: f.freeUntil || null,
        freeReason: f.freeReason || null, invoiceNote: f.invoiceNote || null,
      })
      showToast('Agreement saved', 'success')
      onSaved()
    } catch (e) {
      showToast(errorMessage(e, 'Something went wrong'), 'error')
    }
  }

  const field = (label: string, el: React.ReactNode) => (
    <label className="text-xs text-gray-600 flex flex-col gap-0.5">{label}{el}</label>
  )
  return (
    <div className="mt-2 mb-1 p-3 bg-gray-50 rounded-lg grid gap-3 sm:grid-cols-3">
      {field('Invoiced', (
        <select className={small} value={f.billingInterval} onChange={(e) => set('billingInterval', e.target.value)}>
          {(Object.keys(INTERVAL_LABELS) as BillingInterval[]).map((k) => <option key={k} value={k}>{INTERVAL_LABELS[k]}</option>)}
        </select>
      ))}
      {field('Amount per period excl. VAT (EUR)', <input className={small} inputMode="decimal" value={f.billingAmount} onChange={(e) => set('billingAmount', e.target.value)} />)}
      {field('Hourly rate excl. VAT (EUR)', <input className={small} inputMode="decimal" value={f.hourlyRate} onChange={(e) => set('hourlyRate', e.target.value)} />)}
      {field('VAT % (empty = 21)', <input className={small} inputMode="decimal" value={f.vatRate} onChange={(e) => set('vatRate', e.target.value)} />)}
      {field('Service starts', <input type="date" className={small} value={f.startDate} onChange={(e) => set('startDate', e.target.value)} />)}
      {field('Service ends', <input type="date" className={small} value={f.endDate} onChange={(e) => set('endDate', e.target.value)} />)}
      {field('Paid until (e.g. a year ahead)', <input type="date" className={small} value={f.paidUntil} onChange={(e) => set('paidUntil', e.target.value)} />)}
      {field('Free until (part of a deal)', <input type="date" className={small} value={f.freeUntil} onChange={(e) => set('freeUntil', e.target.value)} />)}
      {field('Why free', <input className={small} value={f.freeReason} onChange={(e) => set('freeReason', e.target.value)} />)}
      <div className="sm:col-span-3">{field('Invoice remark', <input className={small} value={f.invoiceNote} onChange={(e) => set('invoiceNote', e.target.value)} />)}</div>
      <div className="sm:col-span-3">
        <button type="button" onClick={save} className="text-sm px-3 py-1.5 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700">Save agreement</button>
      </div>
    </div>
  )
}

/** The services we deliver to a client (website management, hosting, ...) and what was agreed about them. */
export default function ServicesSection({ clientId, onChanged }: { clientId: string; onChanged?: () => void }) {
  const { showToast } = useToast()
  const [items, setItems] = useState<ClientService[]>([])
  const [kind, setKind] = useState<ServiceKind>('WebsiteManagement')
  const [busy, setBusy] = useState(false)
  const [open, setOpen] = useState<string | null>(null)

  const load = useCallback(() => {
    getServices(clientId).then(setItems).catch((e) => showToast(errorMessage(e, 'Something went wrong'), 'error'))
    onChanged?.()
  }, [clientId, showToast, onChanged])

  useEffect(() => { load() }, [load])

  async function add() {
    setBusy(true)
    try {
      const created = await createService(clientId, { kind, status: 'Active' })
      setOpen(created.id)
      load()
    } catch (e) {
      showToast(errorMessage(e, 'Something went wrong'), 'error')
    } finally {
      setBusy(false)
    }
  }

  async function setStatus(s: ClientService, status: ServiceStatus) {
    try {
      await updateService(s.id, { kind: s.kind, name: s.name, status, startDate: s.startDate, endDate: s.endDate, monthlyAmount: s.monthlyAmount, notes: s.notes, projectId: s.projectId })
      load()
    } catch (e) {
      showToast(errorMessage(e, 'Something went wrong'), 'error')
    }
  }

  async function remove(s: ClientService) {
    if (!window.confirm(`Remove "${s.name}"? Documents stay, without the link.`)) return
    try {
      await deleteService(s.id)
      load()
    } catch (e) {
      showToast(errorMessage(e, 'Something went wrong'), 'error')
    }
  }

  return (
    <section className="bg-white border border-gray-200 rounded-xl p-4 max-w-5xl" aria-label="Services">
      <div className="flex flex-wrap items-center gap-2 mb-3">
        <h2 className="text-sm font-semibold text-gray-800 mr-auto">Services and agreements</h2>
        <select className={input} value={kind} onChange={(e) => setKind(e.target.value as ServiceKind)} aria-label="Service kind">
          {(Object.keys(SERVICE_LABELS) as ServiceKind[]).map((k) => <option key={k} value={k}>{SERVICE_LABELS[k]}</option>)}
        </select>
        <button type="button" disabled={busy} onClick={add} className="text-sm px-3 py-1.5 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50">Add service</button>
      </div>
      {items.length === 0 ? (
        <p className="text-sm text-gray-500">No services yet.</p>
      ) : (
        <ul className="divide-y divide-gray-100">
          {items.map((s) => (
            <li key={s.id} className="py-2">
              <div className="flex flex-wrap items-center gap-2">
                <span className="text-sm font-medium text-gray-800">{s.name}</span>
                <span className={`text-xs px-2 py-0.5 rounded-full border ${STATUS_CLS[s.status]}`}>{SERVICE_STATUS_LABELS[s.status]}</span>
                <span className="text-xs text-gray-500">{termsLine(s) || 'No terms agreed yet'}</span>
                <span className="ml-auto flex items-center gap-1">
                  <button type="button" onClick={() => setOpen(open === s.id ? null : s.id)} className="text-xs px-2 py-1 rounded-md text-indigo-700 hover:bg-indigo-50">{open === s.id ? 'Close' : 'Edit agreement'}</button>
                  <select className="text-xs border border-gray-200 rounded-md px-1 py-0.5" value={s.status} onChange={(e) => setStatus(s, e.target.value as ServiceStatus)} aria-label={`Status of ${s.name}`}>
                    {(Object.keys(SERVICE_STATUS_LABELS) as ServiceStatus[]).map((k) => <option key={k} value={k}>{SERVICE_STATUS_LABELS[k]}</option>)}
                  </select>
                  <button type="button" onClick={() => remove(s)} className="text-xs px-2 py-1 rounded-md text-gray-500 hover:bg-gray-100">Remove</button>
                </span>
              </div>
              {open === s.id && <BillingForm key={s.id + (s.billingAmount ?? '')} s={s} onSaved={load} />}
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

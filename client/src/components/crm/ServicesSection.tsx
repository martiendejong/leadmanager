import { useCallback, useEffect, useState } from 'react'
import { useToast } from '../Toast'
import { errorMessage } from '../../api/crm'
import {
  SERVICE_LABELS,
  SERVICE_STATUS_LABELS,
  createService,
  deleteService,
  getServices,
  updateService,
  type ClientService,
  type ServiceKind,
  type ServiceStatus,
} from '../../api/agreements'

const input = 'text-sm border border-gray-300 rounded-lg px-3 py-1.5 focus:outline-none focus:ring-2 focus:ring-indigo-300'
const STATUS_CLS: Record<ServiceStatus, string> = {
  Proposed: 'bg-gray-50 text-gray-600 border-gray-200',
  Active: 'bg-green-50 text-green-700 border-green-200',
  Paused: 'bg-amber-50 text-amber-800 border-amber-200',
  Ended: 'bg-gray-50 text-gray-500 border-gray-200',
}

/** The services we deliver to a client (website management, hosting, ...). Contracts link to them. */
export default function ServicesSection({ clientId }: { clientId: string }) {
  const { showToast } = useToast()
  const [items, setItems] = useState<ClientService[]>([])
  const [kind, setKind] = useState<ServiceKind>('WebsiteManagement')
  const [busy, setBusy] = useState(false)

  const load = useCallback(() => {
    getServices(clientId).then(setItems).catch((e) => showToast(errorMessage(e, 'Something went wrong'), 'error'))
  }, [clientId, showToast])

  useEffect(() => { load() }, [load])

  async function add() {
    setBusy(true)
    try {
      await createService(clientId, { kind, status: 'Active' })
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
        <h2 className="text-sm font-semibold text-gray-800 mr-auto">Services</h2>
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
            <li key={s.id} className="py-2 flex flex-wrap items-center gap-2">
              <span className="text-sm font-medium text-gray-800">{s.name}</span>
              <span className={`text-xs px-2 py-0.5 rounded-full border ${STATUS_CLS[s.status]}`}>{SERVICE_STATUS_LABELS[s.status]}</span>
              {s.monthlyAmount != null && <span className="text-xs text-gray-500">{s.monthlyAmount.toLocaleString('en-GB', { style: 'currency', currency: 'EUR' })} / month</span>}
              <span className="ml-auto flex items-center gap-1">
                <select className="text-xs border border-gray-200 rounded-md px-1 py-0.5" value={s.status} onChange={(e) => setStatus(s, e.target.value as ServiceStatus)} aria-label={`Status of ${s.name}`}>
                  {(Object.keys(SERVICE_STATUS_LABELS) as ServiceStatus[]).map((k) => <option key={k} value={k}>{SERVICE_STATUS_LABELS[k]}</option>)}
                </select>
                <button type="button" onClick={() => remove(s)} className="text-xs px-2 py-1 rounded-md text-gray-500 hover:bg-gray-100">Remove</button>
              </span>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { useToast } from '../components/Toast'
import { errorMessage } from '../api/crm'
import {
  SERVICE_LABELS,
  getAgreements,
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

/** One view over all clients: services (such as website management), contracts sent or signed, and concepts. */
export default function AgreementsPage() {
  const { showToast } = useToast()
  const [rows, setRows] = useState<ClientAgreements[] | null>(null)
  const [service, setService] = useState<ServiceKind | ''>('')

  useEffect(() => {
    getAgreements(service)
      .then(setRows)
      .catch((e) => { setRows([]); showToast(errorMessage(e, 'Something went wrong'), 'error') })
  }, [service, showToast])

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
                    {s.name} · {s.status}
                  </span>
                ))}
              </div>
              <div className="grid gap-4 sm:grid-cols-2">
                <DocList title="Contracts" docs={r.contracts} empty="None sent or signed yet" />
                <DocList title="Concepts" docs={r.concepts} empty="No concepts" />
              </div>
            </section>
          ))}
        </div>
      )}
    </div>
  )
}

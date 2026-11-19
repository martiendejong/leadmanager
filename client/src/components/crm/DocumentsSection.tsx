import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useToast } from '../Toast'
import { errorMessage } from '../../api/crm'
import type { Project } from '../../api/clients'
import {
  KIND_LABELS,
  NEXT_KIND,
  STATUS_LABELS,
  createDocument,
  deleteDocument,
  deleteDocumentFile,
  downloadDocumentFile,
  formatBytes,
  getDocuments,
  updateDocument,
  uploadDocumentFile,
  type ClientDocument,
  type DocumentFile,
  type DocumentKind,
  type DocumentStatus,
  type UpsertDocument,
} from '../../api/documents'

const input = 'w-full text-sm border border-gray-300 rounded-lg px-3 py-1.5 focus:outline-none focus:ring-2 focus:ring-indigo-300'
const label = 'block text-xs font-medium text-gray-600 mb-1'
const primaryBtn = 'text-sm px-3 py-1.5 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50'
const ghostBtn = 'text-xs px-2 py-1 rounded-md text-gray-500 hover:bg-gray-100'

const STATUS_CLS: Record<DocumentStatus, string> = {
  Draft: 'bg-gray-50 text-gray-600 border-gray-200',
  Sent: 'bg-blue-50 text-blue-700 border-blue-200',
  Signed: 'bg-green-50 text-green-700 border-green-200',
  Rejected: 'bg-red-50 text-red-700 border-red-200',
  Expired: 'bg-amber-50 text-amber-800 border-amber-200',
  Cancelled: 'bg-gray-50 text-gray-500 border-gray-200',
}

const QUICK: { kind: DocumentKind; text: string }[] = [
  { kind: 'Quote', text: 'Quote' },
  { kind: 'Order', text: 'Order' },
  { kind: 'Dpa', text: 'Data processing agreement' },
  { kind: 'Nda', text: 'NDA' },
  { kind: 'Agreement', text: 'Agreement' },
]

function fmtDate(iso?: string | null) {
  return iso ? new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' }) : null
}

function fmtMoney(n?: number | null) {
  return n == null ? null : n.toLocaleString('en-GB', { style: 'currency', currency: 'EUR' })
}

function toDateInput(iso?: string | null) {
  return iso ? iso.slice(0, 10) : ''
}

function fromDateInput(v: string): string | null {
  return v ? `${v}T12:00:00Z` : null
}

interface FormState {
  id?: string
  kind: DocumentKind
  title: string
  status: DocumentStatus
  reference: string
  documentDate: string
  signedAt: string
  validUntil: string
  amount: string
  projectId: string
  predecessorId: string
  url: string
  notes: string
}

function emptyForm(kind: DocumentKind, predecessorId = ''): FormState {
  return {
    kind, title: KIND_LABELS[kind], status: 'Draft', reference: '', documentDate: new Date().toISOString().slice(0, 10),
    signedAt: '', validUntil: '', amount: '', projectId: '', predecessorId, url: '', notes: '',
  }
}

function formFrom(d: ClientDocument): FormState {
  return {
    id: d.id, kind: d.kind, title: d.title, status: d.status, reference: d.reference ?? '',
    documentDate: toDateInput(d.documentDate), signedAt: toDateInput(d.signedAt), validUntil: toDateInput(d.validUntil),
    amount: d.amount == null ? '' : String(d.amount), projectId: d.projectId ?? '', predecessorId: d.predecessorId ?? '',
    url: d.url ?? '', notes: d.notes ?? '',
  }
}

function DocumentForm({
  initial, documents, projects, onSave, onCancel,
}: {
  initial: FormState
  documents: ClientDocument[]
  projects: Project[]
  onSave: (f: FormState) => Promise<void>
  onCancel: () => void
}) {
  const [f, setF] = useState<FormState>(initial)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const set = (k: keyof FormState) => (e: { target: { value: string } }) => setF((s) => ({ ...s, [k]: e.target.value }))

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (busy) return
    if (!f.title.trim()) { setError('Title is required.'); return }
    if (f.amount && Number.isNaN(Number(f.amount.replace(',', '.')))) { setError('Amount is not a number.'); return }
    setBusy(true)
    setError(null)
    try {
      await onSave(f)
    } catch (err) {
      setError(errorMessage(err, 'Could not save'))
    } finally {
      setBusy(false)
    }
  }

  const candidates = documents.filter((d) => d.id !== f.id)

  return (
    <form onSubmit={submit} className="mb-4 space-y-3 p-4 bg-gray-50 rounded-lg" aria-label="Document">
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <div>
          <label className={label} htmlFor="doc-kind">Type</label>
          <select id="doc-kind" className={input} value={f.kind} onChange={set('kind')}>
            {(Object.keys(KIND_LABELS) as DocumentKind[]).map((k) => <option key={k} value={k}>{KIND_LABELS[k]}</option>)}
          </select>
        </div>
        <div>
          <label className={label} htmlFor="doc-status">Status</label>
          <select id="doc-status" className={input} value={f.status} onChange={set('status')}>
            {(Object.keys(STATUS_LABELS) as DocumentStatus[]).map((s) => <option key={s} value={s}>{STATUS_LABELS[s]}</option>)}
          </select>
        </div>
      </div>
      <div>
        <label className={label} htmlFor="doc-title">Title *</label>
        <input id="doc-title" className={input} value={f.title} onChange={set('title')} maxLength={300} autoFocus />
      </div>
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
        <div>
          <label className={label} htmlFor="doc-ref">Reference / number</label>
          <input id="doc-ref" className={input} value={f.reference} onChange={set('reference')} maxLength={100} />
        </div>
        <div>
          <label className={label} htmlFor="doc-amount">Amount excl. VAT (€)</label>
          <input id="doc-amount" className={input} inputMode="decimal" value={f.amount} onChange={set('amount')} />
        </div>
        <div>
          <label className={label} htmlFor="doc-date">Date</label>
          <input id="doc-date" type="date" className={input} value={f.documentDate} onChange={set('documentDate')} />
        </div>
      </div>
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <div>
          <label className={label} htmlFor="doc-signed">Signed on</label>
          <input id="doc-signed" type="date" className={input} value={f.signedAt} onChange={set('signedAt')} />
        </div>
        <div>
          <label className={label} htmlFor="doc-valid">Valid until</label>
          <input id="doc-valid" type="date" className={input} value={f.validUntil} onChange={set('validUntil')} />
        </div>
      </div>
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
        <div>
          <label className={label} htmlFor="doc-pred">Follows</label>
          <select id="doc-pred" className={input} value={f.predecessorId} onChange={set('predecessorId')}>
            <option value="">None (standalone)</option>
            {candidates.map((d) => <option key={d.id} value={d.id}>{KIND_LABELS[d.kind]}: {d.title}</option>)}
          </select>
        </div>
        <div>
          <label className={label} htmlFor="doc-project">Project</label>
          <select id="doc-project" className={input} value={f.projectId} onChange={set('projectId')}>
            <option value="">None</option>
            {projects.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
          </select>
        </div>
      </div>
      <div>
        <label className={label} htmlFor="doc-url">Link (e.g. where the document is stored)</label>
        <input id="doc-url" className={input} value={f.url} onChange={set('url')} placeholder="https://..." />
      </div>
      <div>
        <label className={label} htmlFor="doc-notes">Notes</label>
        <textarea id="doc-notes" rows={3} className={input} value={f.notes} onChange={set('notes')} maxLength={4000} />
      </div>
      {error && <p className="text-sm text-red-600" role="alert">{error}</p>}
      <div className="flex justify-end gap-2">
        <button type="button" onClick={onCancel} disabled={busy} className="px-3 py-1.5 text-sm rounded-lg border border-gray-300 text-gray-700 hover:bg-gray-100">Cancel</button>
        <button type="submit" disabled={busy} className={primaryBtn}>{f.id ? 'Save' : 'Add'}</button>
      </div>
    </form>
  )
}

function FileRow({ file, onChanged }: { file: DocumentFile; onChanged: () => void }) {
  const { showToast } = useToast()
  const [busy, setBusy] = useState(false)

  const download = async () => {
    setBusy(true)
    try {
      await downloadDocumentFile(file)
    } catch (err) {
      showToast(errorMessage(err, 'Could not download'), 'error')
    } finally {
      setBusy(false)
    }
  }

  const remove = async () => {
    if (!window.confirm(`Delete file "${file.fileName}"?`)) return
    try {
      await deleteDocumentFile(file.id)
      onChanged()
    } catch (err) {
      showToast(errorMessage(err, 'Could not delete'), 'error')
    }
  }

  return (
    <li className="flex items-center gap-2 text-sm">
      <span aria-hidden>📎</span>
      <button onClick={download} disabled={busy} className="text-indigo-600 hover:underline truncate text-left">{file.fileName}</button>
      <span className="text-xs text-gray-400 flex-shrink-0">{formatBytes(file.sizeBytes)}</span>
      <button onClick={remove} className={`${ghostBtn} ml-auto`} aria-label={`Delete ${file.fileName}`}>✕</button>
    </li>
  )
}

function DocumentCard({
  doc, byId, onEdit, onFollow, onChanged,
}: {
  doc: ClientDocument
  byId: Map<string, ClientDocument>
  onEdit: (d: ClientDocument) => void
  onFollow: (d: ClientDocument) => void
  onChanged: () => void
}) {
  const { showToast } = useToast()
  const fileInput = useRef<HTMLInputElement>(null)
  const [uploading, setUploading] = useState(false)
  const predecessor = doc.predecessorId ? byId.get(doc.predecessorId) : undefined
  const expired = doc.validUntil && doc.status !== 'Signed' && new Date(doc.validUntil) < new Date()

  const upload = async (files: FileList | null) => {
    if (!files || files.length === 0) return
    setUploading(true)
    try {
      for (const file of Array.from(files)) {
        try {
          await uploadDocumentFile(doc.id, file)
        } catch (err) {
          showToast(`${file.name}: ${errorMessage(err, 'could not upload')}`, 'error')
        }
      }
      onChanged()
    } finally {
      setUploading(false)
      if (fileInput.current) fileInput.current.value = ''
    }
  }

  const remove = async () => {
    if (!window.confirm(`Delete document "${doc.title}" and its ${doc.files.length} file(s)? This cannot be undone.`)) return
    try {
      await deleteDocument(doc.id)
      onChanged()
    } catch (err) {
      showToast(errorMessage(err, 'Could not delete'), 'error')
    }
  }

  return (
    <li className="p-3 bg-gray-50 rounded-lg">
      <div className="flex items-start gap-3 flex-wrap">
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2 flex-wrap">
            <span className="text-xs px-2 py-0.5 rounded-full bg-white border border-gray-200 text-gray-600">{KIND_LABELS[doc.kind]}</span>
            <span className={`text-xs font-medium border rounded-full px-2 py-0.5 ${STATUS_CLS[doc.status]}`}>{STATUS_LABELS[doc.status]}</span>
            {expired && <span className="text-xs font-medium text-amber-700">expired on {fmtDate(doc.validUntil)}</span>}
            <p className="text-sm font-medium text-gray-900 break-words">{doc.title}</p>
          </div>
          <p className="text-xs text-gray-500 mt-1">
            {[
              doc.reference && `reference ${doc.reference}`,
              fmtMoney(doc.amount),
              fmtDate(doc.documentDate),
              doc.signedAt && `signed ${fmtDate(doc.signedAt)}`,
              doc.validUntil && !expired && `valid until ${fmtDate(doc.validUntil)}`,
              doc.projectName && `project ${doc.projectName}`,
            ].filter(Boolean).join(' · ')}
          </p>
          {predecessor && (
            <p className="text-xs text-gray-500 mt-0.5">↳ follows {KIND_LABELS[predecessor.kind].toLowerCase()} &quot;{predecessor.title}&quot;</p>
          )}
          {doc.notes && <p className="text-xs text-gray-600 mt-1 whitespace-pre-wrap break-words">{doc.notes}</p>}
          {doc.url && /^https?:\/\//i.test(doc.url) && (
            <a href={doc.url} target="_blank" rel="noopener noreferrer" className="text-xs text-indigo-600 hover:underline break-all">{doc.url}</a>
          )}
        </div>
        <div className="flex items-center gap-1 flex-shrink-0">
          <button onClick={() => onEdit(doc)} className={ghostBtn}>Edit</button>
          <button onClick={() => onFollow(doc)} className={ghostBtn}>Next document</button>
          <button onClick={remove} className={ghostBtn} aria-label={`Delete ${doc.title}`}>✕</button>
        </div>
      </div>

      <ul className="mt-2 space-y-1">
        {doc.files.map((f) => <FileRow key={f.id} file={f} onChanged={onChanged} />)}
      </ul>
      <div className="mt-2">
        <input ref={fileInput} type="file" multiple className="hidden" id={`up-${doc.id}`} tabIndex={-1} aria-label={`Choose file for ${doc.title}`} onChange={(e) => upload(e.target.files)}
          accept=".pdf,.doc,.docx,.xls,.xlsx,.odt,.ods,.txt,.png,.jpg,.jpeg,.eml,.msg" />
        <button
          type="button"
          onClick={() => fileInput.current?.click()}
          disabled={uploading}
          className={`${ghostBtn} border border-dashed border-gray-300`}
        >
          {uploading ? 'Uploading...' : '+ Add file (max 25 MB)'}
        </button>
      </div>
    </li>
  )
}

/** The file of a client: quotes, orders and signed agreements, each with its files. */
export default function DocumentsSection({ clientId, projects }: { clientId: string; projects: Project[] }) {
  const { showToast } = useToast()
  const [docs, setDocs] = useState<ClientDocument[] | null>(null)
  const [form, setForm] = useState<FormState | null>(null)

  const latest = useRef(0)
  const load = useCallback(() => {
    const ticket = ++latest.current
    getDocuments(clientId)
      .then((d) => {
        if (ticket !== latest.current) return
        setDocs(d)
        // A form for a document that no longer exists must not stay open.
        setForm((f) => (f?.id && !d.some((x) => x.id === f.id) ? null : f))
      })
      .catch((err) => {
        if (ticket !== latest.current) return
        showToast(errorMessage(err, 'Could not load client file'), 'error')
        setDocs([])
      })
  }, [clientId, showToast])

  useEffect(load, [load])

  const byId = useMemo(() => new Map((docs ?? []).map((d) => [d.id, d])), [docs])

  const toBody = (f: FormState): UpsertDocument => ({
    kind: f.kind,
    title: f.title.trim(),
    status: f.status,
    reference: f.reference.trim() || null,
    documentDate: fromDateInput(f.documentDate),
    signedAt: fromDateInput(f.signedAt),
    validUntil: fromDateInput(f.validUntil),
    amount: f.amount.trim() ? Number(f.amount.replace(',', '.')) : null,
    notes: f.notes.trim() || null,
    url: f.url.trim() || null,
    projectId: f.projectId || null,
    predecessorId: f.predecessorId || null,
  })

  const save = async (f: FormState) => {
    if (f.id) await updateDocument(f.id, toBody(f))
    else await createDocument(clientId, toBody(f))
    setForm(null)
    load()
  }

  const follow = (d: ClientDocument) => {
    const next = NEXT_KIND[d.kind] ?? 'Other'
    const base = emptyForm(next, d.id)
    setForm({ ...base, projectId: d.projectId ?? '', amount: d.amount == null ? '' : String(d.amount) })
  }

  return (
    <div className="bg-white rounded-xl border border-gray-200 p-6 mb-6">
      <div className="flex items-center justify-between mb-3 flex-wrap gap-2">
        <h2 className="text-base font-semibold text-gray-900">
          File
          <span className="ml-2 text-sm font-normal text-gray-400">({docs?.length ?? 0})</span>
        </h2>
        <div className="flex gap-1 flex-wrap">
          {QUICK.map((q) => (
            <button key={q.kind} onClick={() => setForm(emptyForm(q.kind))} className={`${ghostBtn} border border-gray-200`}>+ {q.text}</button>
          ))}
        </div>
      </div>
      <p className="text-xs text-gray-400 mb-3">Quotes, orders and signed agreements (such as a data processing agreement) with their files.</p>

      {form && (
        <DocumentForm key={form.id ?? `new-${form.kind}-${form.predecessorId}`} initial={form} documents={docs ?? []} projects={projects}
          onSave={save} onCancel={() => setForm(null)} />
      )}

      {docs === null ? (
        <p className="text-sm text-gray-400">Loading...</p>
      ) : docs.length === 0 ? (
        <p className="text-sm text-gray-400">Nothing recorded yet. Start with a quote or an agreement.</p>
      ) : (
        <ul className="space-y-3">
          {docs.map((d) => (
            <DocumentCard key={d.id} doc={d} byId={byId} onEdit={(x) => setForm(formFrom(x))} onFollow={follow} onChanged={load} />
          ))}
        </ul>
      )}
    </div>
  )
}

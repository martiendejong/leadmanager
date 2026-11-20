import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useToast } from '../Toast'
import { errorMessage } from '../../api/crm'
import { CHANNELS, CHANNEL_LABELS, bulkDrafts, type BulkResult, type Channel } from '../../api/messages'
import { getProducts, type Product } from '../../api/hunt'
import { useEffect } from 'react'

/** Create drafts for all selected leads. Drafts only: nothing is sent and no lead changes status. */
export default function BulkDraftDialog({ leadIds, onClose }: { leadIds: string[]; onClose: () => void }) {
  const { showToast } = useToast()
  const [channel, setChannel] = useState<Channel>('Email')
  const [product, setProduct] = useState('')
  const [products, setProducts] = useState<Product[]>([])
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<BulkResult | null>(null)

  useEffect(() => { getProducts().then(setProducts).catch(() => {}) }, [])

  const run = async () => {
    setBusy(true)
    try { setResult(await bulkDrafts(leadIds, channel, product)) }
    catch (err) { showToast(errorMessage(err, 'Could not create the drafts'), 'error') }
    finally { setBusy(false) }
  }

  const input = 'w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500'
  return (
    <div className="fixed inset-0 bg-black/40 z-50 flex items-center justify-center p-4" role="dialog" aria-label="Draft messages">
      <div className="bg-white rounded-xl p-5 w-full max-w-md space-y-3">
        <h2 className="text-base font-semibold text-gray-900">Draft messages for {leadIds.length} lead{leadIds.length !== 1 ? 's' : ''}</h2>
        {!result ? (
          <>
            <p className="text-sm text-gray-500">This only creates drafts from your templates. Nothing is sent and no lead changes status. You review and send each message yourself.</p>
            <label className="block text-xs text-gray-600">Channel
              <select aria-label="Channel" className={input} value={channel} onChange={(e) => setChannel(e.target.value as Channel)}>
                {CHANNELS.map((c) => <option key={c} value={c}>{CHANNEL_LABELS[c]}</option>)}
              </select>
            </label>
            <label className="block text-xs text-gray-600">Product template
              <select aria-label="Product template" className={input} value={product} onChange={(e) => setProduct(e.target.value)}>
                <option value="">Best fit per lead / default</option>
                {products.map((p) => <option key={p.key} value={p.key}>{p.name}</option>)}
              </select>
            </label>
            <div className="flex gap-2 justify-end">
              <button onClick={onClose} className="px-3 py-1.5 rounded-lg border border-gray-300 text-sm text-gray-700">Cancel</button>
              <button disabled={busy} onClick={run} className="px-3 py-1.5 rounded-lg bg-indigo-600 text-white text-sm disabled:opacity-50">Create drafts</button>
            </div>
          </>
        ) : (
          <>
            <p className="text-sm text-gray-800">{result.created} draft{result.created !== 1 ? 's' : ''} created, {result.skipped} skipped. Nothing was sent.</p>
            {result.skippedItems.length > 0 && (
              <ul className="text-xs text-gray-500 space-y-0.5 max-h-32 overflow-y-auto">
                {result.skippedItems.map((s) => <li key={s.leadId}>{s.name ?? s.leadId}: {s.reason}</li>)}
              </ul>
            )}
            <div className="flex gap-2 justify-end">
              <Link to="/messages" className="px-3 py-1.5 rounded-lg bg-indigo-600 text-white text-sm">Review drafts</Link>
              <button onClick={onClose} className="px-3 py-1.5 rounded-lg border border-gray-300 text-sm text-gray-700">Close</button>
            </div>
          </>
        )}
      </div>
    </div>
  )
}

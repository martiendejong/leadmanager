import { useEffect, useState } from 'react'
import { getProducts, type Product } from '../../api/hunt'
import type { ProductFit } from '../../api/leads'

let cache: Record<string, Product> | null = null
let pending: Promise<Record<string, Product>> | null = null

function loadProducts(): Promise<Record<string, Product>> {
  if (cache) return Promise.resolve(cache)
  pending ??= getProducts()
    .then((list) => {
      cache = Object.fromEntries(list.map((p) => [p.key, p]))
      return cache
    })
    .catch(() => {
      pending = null
      return {} as Record<string, Product>
    })
  return pending
}

/** The catalog of our products (key to name), fetched once. Until it is there a chip shows the key. */
export function useProducts(): Record<string, Product> {
  const [products, setProducts] = useState<Record<string, Product>>(cache ?? {})
  useEffect(() => {
    let alive = true
    if (!cache) loadProducts().then((p) => { if (alive) setProducts(p) })
    return () => { alive = false }
  }, [])
  return products
}

const isHttp = (u?: string | null): u is string => !!u && /^https?:\/\//i.test(u)

/**
 * The products that can help a lead. Compact: only the names, with the reason as a tooltip (for the list). Full: every fit with its reason and
 * the quote from the business's own pages that shows the trigger, linked to the page.
 */
export function ProductChips({ fit, compact = false }: { fit: ProductFit[]; compact?: boolean }) {
  const products = useProducts()
  const name = (key: string) => products[key]?.name ?? key
  if (fit.length === 0) return null

  const chips = (
    <div className="flex flex-wrap gap-1">
      {fit.map((f) => (
        <span key={f.product} title={f.reason}
          className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-indigo-50 text-indigo-700 border border-indigo-200">
          {name(f.product)}
        </span>
      ))}
    </div>
  )
  if (compact) return chips

  return (
    <div className="space-y-2">
      {chips}
      <ul className="space-y-2">
        {fit.map((f) => (
          <li key={f.product} className="border-l-2 border-indigo-300 pl-3 text-sm">
            <div className="font-medium text-gray-800">{name(f.product)}</div>
            <div className="text-gray-700">{f.reason}</div>
            {f.evidenceQuote && (
              <blockquote className="text-xs text-gray-600 italic">
                "{f.evidenceQuote}"{' '}
                {isHttp(f.sourceUrl) && (
                  <a href={f.sourceUrl} target="_blank" rel="noopener noreferrer" className="not-italic text-indigo-600 hover:underline">source</a>
                )}
              </blockquote>
            )}
          </li>
        ))}
      </ul>
    </div>
  )
}

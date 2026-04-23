import { useEffect, useRef, useState } from 'react'
import type { LeadsAnalytics } from '../api/leads'
import { getLeadsAnalytics } from '../api/leads'

function StatCard({ label, value, sub }: { label: string; value: string | number; sub?: string }) {
  return (
    <div className="bg-white rounded-xl border border-gray-200 p-5 shadow-sm">
      <p className="text-xs font-medium text-gray-500 uppercase tracking-wide mb-1">{label}</p>
      <p className="text-2xl font-bold text-gray-900">{value}</p>
      {sub && <p className="text-xs text-gray-500 mt-0.5">{sub}</p>}
    </div>
  )
}

function ProportionalBar({ label, count, max, colorClass }: { label: string; count: number; max: number; colorClass: string }) {
  const pct = max > 0 ? Math.max(4, Math.round((count / max) * 100)) : 4
  return (
    <div className="flex items-center gap-3">
      <span className="text-sm text-gray-700 w-32 truncate flex-shrink-0" title={label}>{label}</span>
      <div className="flex-1 bg-gray-100 rounded-full h-2.5">
        <div className={`${colorClass} h-2.5 rounded-full transition-all`} style={{ width: `${pct}%` }} />
      </div>
      <span className="text-sm font-medium text-gray-900 w-8 text-right flex-shrink-0">{count}</span>
    </div>
  )
}

function Sparkline({ data }: { data: { date: string; count: number }[] }) {
  if (!data || data.length === 0) return <p className="text-sm text-gray-400">Geen data</p>

  const maxVal = Math.max(...data.map(d => d.count), 1)
  const width = 600
  const height = 80
  const padX = 8
  const padY = 8

  const points = data.map((d, i) => {
    const x = padX + (i / Math.max(data.length - 1, 1)) * (width - padX * 2)
    const y = padY + ((maxVal - d.count) / maxVal) * (height - padY * 2)
    return `${x},${y}`
  })

  return (
    <div className="overflow-x-auto">
      <svg viewBox={`0 0 ${width} ${height}`} className="w-full" style={{ minWidth: 200, maxHeight: 80 }}>
        <polyline
          points={points.join(' ')}
          fill="none"
          stroke="#6366f1"
          strokeWidth="2"
          strokeLinejoin="round"
          strokeLinecap="round"
        />
        {data.map((d, i) => {
          const [x, y] = points[i].split(',').map(Number)
          return (
            <circle key={i} cx={x} cy={y} r="3" fill="#6366f1">
              <title>{d.date}: {d.count}</title>
            </circle>
          )
        })}
      </svg>
      <div className="flex justify-between text-xs text-gray-400 mt-1 px-2">
        <span>{data[0]?.date}</span>
        <span>{data[data.length - 1]?.date}</span>
      </div>
    </div>
  )
}

async function exportChartAsPng(element: HTMLElement, filename: string) {
  // Use html2canvas-style approach with SVG foreignObject
  const clone = element.cloneNode(true) as HTMLElement

  const svgNs = 'http://www.w3.org/2000/svg'
  const { width, height } = element.getBoundingClientRect()

  const svg = document.createElementNS(svgNs, 'svg')
  svg.setAttribute('width', String(width))
  svg.setAttribute('height', String(height))

  const fo = document.createElementNS(svgNs, 'foreignObject')
  fo.setAttribute('width', '100%')
  fo.setAttribute('height', '100%')

  const wrapper = document.createElement('div')
  wrapper.setAttribute('xmlns', 'http://www.w3.org/1999/xhtml')

  // Inline all computed styles on each element
  const inlineStyles = (source: Element, target: Element) => {
    const computed = window.getComputedStyle(source)
    const el = target as HTMLElement
    if (el.style) {
      el.style.cssText = computed.cssText
    }
    const sourceChildren = source.children
    const targetChildren = target.children
    for (let i = 0; i < sourceChildren.length && i < targetChildren.length; i++) {
      inlineStyles(sourceChildren[i], targetChildren[i])
    }
  }
  inlineStyles(element, clone)

  wrapper.appendChild(clone)
  fo.appendChild(wrapper)
  svg.appendChild(fo)

  const serialized = new XMLSerializer().serializeToString(svg)
  const svgBlob = new Blob([serialized], { type: 'image/svg+xml;charset=utf-8' })
  const url = URL.createObjectURL(svgBlob)

  const img = new Image()
  img.onload = () => {
    const canvas = document.createElement('canvas')
    const scale = 2 // retina
    canvas.width = width * scale
    canvas.height = height * scale
    const ctx = canvas.getContext('2d')!
    ctx.scale(scale, scale)
    ctx.fillStyle = '#f9fafb'
    ctx.fillRect(0, 0, width, height)
    ctx.drawImage(img, 0, 0, width, height)
    URL.revokeObjectURL(url)

    canvas.toBlob((blob) => {
      if (!blob) return
      const a = document.createElement('a')
      a.href = URL.createObjectURL(blob)
      a.download = filename
      document.body.appendChild(a)
      a.click()
      document.body.removeChild(a)
      setTimeout(() => URL.revokeObjectURL(a.href), 100)
    }, 'image/png')
  }
  img.src = url
}

export default function AnalyticsPage() {
  const [analytics, setAnalytics] = useState<LeadsAnalytics | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const chartsRef = useRef<HTMLDivElement>(null)

  const load = async (fromVal?: string, toVal?: string) => {
    setLoading(true)
    setError(null)
    try {
      const data = await getLeadsAnalytics(fromVal || undefined, toVal || undefined)
      setAnalytics(data)
    } catch (err: any) {
      setError('Analyse laden mislukt')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    load()
  }, [])

  const handleFilter = (e: React.FormEvent) => {
    e.preventDefault()
    load(from, to)
  }

  const handleReset = () => {
    setFrom('')
    setTo('')
    load()
  }

  const enrichedPct = analytics && analytics.totalLeads > 0
    ? Math.round((analytics.enrichedLeads / analytics.totalLeads) * 100)
    : 0

  const maxIndustryCount = analytics ? Math.max(...analytics.leadsByIndustry.map(x => x.count), 1) : 1
  const maxStatusCount = analytics ? Math.max(...analytics.leadsByStatus.map(x => x.count), 1) : 1
  const totalSourceCount = analytics ? analytics.topSources.reduce((s, x) => s + x.count, 0) : 0

  const industryColors = ['bg-indigo-500', 'bg-blue-500', 'bg-purple-500', 'bg-pink-500', 'bg-emerald-500', 'bg-teal-500', 'bg-amber-500', 'bg-orange-500']
  const statusColors = ['bg-green-500', 'bg-yellow-500', 'bg-blue-500', 'bg-red-400']

  return (
    <div className="space-y-6">
      {/* Page header */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Analyse</h1>
          <p className="text-sm text-gray-500 mt-0.5">Overzicht van je leadpipeline en prestaties</p>
        </div>

        <div className="flex items-center gap-3">
          {/* Export PNG button */}
          {analytics && !loading && (
            <button
              type="button"
              onClick={() => {
                if (chartsRef.current) {
                  const dateStr = new Date().toISOString().slice(0, 10)
                  exportChartAsPng(chartsRef.current, `lead-analytics-${dateStr}.png`)
                }
              }}
              className="text-sm px-3 py-1.5 bg-white border border-gray-200 text-gray-700 rounded-lg hover:bg-gray-50 flex items-center gap-1.5"
            >
              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" strokeWidth="1.5" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" d="M3 16.5v2.25A2.25 2.25 0 005.25 21h13.5A2.25 2.25 0 0021 18.75V16.5M16.5 12L12 16.5m0 0L7.5 12m4.5 4.5V3" />
              </svg>
              Export PNG
            </button>
          )}

          {/* Date range filter */}
          <form onSubmit={handleFilter} className="flex items-center gap-2">
            <input
              type="date"
              value={from}
              onChange={e => setFrom(e.target.value)}
              className="text-sm border border-gray-200 rounded-lg px-3 py-1.5 focus:outline-none focus:ring-2 focus:ring-indigo-500"
              placeholder="Van"
            />
            <span className="text-gray-400 text-sm">—</span>
            <input
              type="date"
              value={to}
              onChange={e => setTo(e.target.value)}
              className="text-sm border border-gray-200 rounded-lg px-3 py-1.5 focus:outline-none focus:ring-2 focus:ring-indigo-500"
              placeholder="Tot"
            />
            <button
              type="submit"
              className="text-sm px-4 py-1.5 bg-indigo-600 text-white rounded-lg hover:bg-indigo-700"
            >
              Filter
            </button>
            {(from || to) && (
              <button
                type="button"
                onClick={handleReset}
                className="text-sm px-3 py-1.5 bg-gray-100 text-gray-600 rounded-lg hover:bg-gray-200"
              >
                Reset
              </button>
            )}
          </form>
        </div>
      </div>

      {loading && (
        <div className="flex items-center justify-center py-16">
          <svg className="w-6 h-6 animate-spin text-indigo-600" fill="none" viewBox="0 0 24 24">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
          </svg>
          <span className="ml-2 text-sm text-gray-500">Analyse laden...</span>
        </div>
      )}

      {error && (
        <div className="bg-red-50 border border-red-200 rounded-lg p-4 text-sm text-red-700">{error}</div>
      )}

      {analytics && !loading && (
        <div ref={chartsRef}>
          {/* Stat cards */}
          <div className="grid grid-cols-2 lg:grid-cols-5 gap-4">
            <StatCard label="Totaal leads" value={analytics.totalLeads} />
            <StatCard
              label="Verrijkt"
              value={`${enrichedPct}%`}
              sub={`${analytics.enrichedLeads} van ${analytics.totalLeads}`}
            />
            <StatCard
              label="Conversieratio"
              value={`${Math.round(analytics.conversionRate * 100)}%`}
              sub="Verrijkt / totaal"
            />
            <StatCard
              label="Gemiddelde score"
              value={analytics.avgSalesScore > 0 ? analytics.avgSalesScore.toFixed(1) : '—'}
              sub="Sales prioriteit /10"
            />
            <StatCard
              label="Deze maand"
              value={analytics.leadsThisMonth}
              sub="Leads aangemaakt (30d)"
            />
          </div>

          {/* Charts row */}
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
            {/* Leads by industry */}
            <div className="bg-white rounded-xl border border-gray-200 p-5 shadow-sm">
              <h2 className="text-sm font-semibold text-gray-700 mb-4">Leads per sector (top 8)</h2>
              {analytics.leadsByIndustry.length === 0 ? (
                <p className="text-sm text-gray-400">Geen sectordata beschikbaar</p>
              ) : (
                <div className="space-y-3">
                  {analytics.leadsByIndustry.map((item, i) => (
                    <ProportionalBar
                      key={item.industry}
                      label={item.industry}
                      count={item.count}
                      max={maxIndustryCount}
                      colorClass={industryColors[i % industryColors.length]}
                    />
                  ))}
                </div>
              )}
            </div>

            {/* Leads by pipeline status */}
            <div className="bg-white rounded-xl border border-gray-200 p-5 shadow-sm">
              <h2 className="text-sm font-semibold text-gray-700 mb-4">Leads per status</h2>
              {analytics.leadsByStatus.length === 0 ? (
                <p className="text-sm text-gray-400">Geen statusdata beschikbaar</p>
              ) : (
                <div className="space-y-3">
                  {analytics.leadsByStatus.map((item, i) => (
                    <ProportionalBar
                      key={item.status}
                      label={item.status}
                      count={item.count}
                      max={maxStatusCount}
                      colorClass={statusColors[i % statusColors.length]}
                    />
                  ))}
                </div>
              )}

              {/* Donut-style summary */}
              {analytics.leadsByStatus.length > 0 && (
                <div className="mt-4 pt-4 border-t border-gray-100 flex gap-3 flex-wrap">
                  {analytics.leadsByStatus.map((item, i) => {
                    const pct = analytics.totalLeads > 0 ? Math.round((item.count / analytics.totalLeads) * 100) : 0
                    return (
                      <div key={item.status} className="flex items-center gap-1.5 text-xs text-gray-600">
                        <span className={`w-2.5 h-2.5 rounded-full ${statusColors[i % statusColors.length]}`} />
                        {item.status}: {pct}%
                      </div>
                    )
                  })}
                </div>
              )}
            </div>
          </div>

          {/* Leads over time */}
          <div className="bg-white rounded-xl border border-gray-200 p-5 shadow-sm">
            <h2 className="text-sm font-semibold text-gray-700 mb-4">Leads aangemaakt over tijd (laatste 30 dagen)</h2>
            <Sparkline data={analytics.leadsOverTime} />
          </div>

          {/* Bottom row: top sources + avg score by industry */}
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
            {/* Top sources */}
            <div className="bg-white rounded-xl border border-gray-200 p-5 shadow-sm">
              <h2 className="text-sm font-semibold text-gray-700 mb-4">Top bronnen</h2>
              {analytics.topSources.length === 0 ? (
                <p className="text-sm text-gray-400">Geen brondata beschikbaar</p>
              ) : (
                <table className="w-full text-sm">
                  <thead>
                    <tr className="text-xs text-gray-500 uppercase tracking-wide border-b border-gray-100">
                      <th className="text-left pb-2 font-medium">Bron</th>
                      <th className="text-right pb-2 font-medium">Aantal</th>
                      <th className="text-right pb-2 font-medium">%</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-gray-50">
                    {analytics.topSources.map(s => {
                      const pct = totalSourceCount > 0 ? Math.round((s.count / totalSourceCount) * 100) : 0
                      return (
                        <tr key={s.source} className="hover:bg-gray-50">
                          <td className="py-2 text-gray-700">{s.source || '(onbekend)'}</td>
                          <td className="py-2 text-right font-medium text-gray-900">{s.count}</td>
                          <td className="py-2 text-right text-gray-500">{pct}%</td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              )}
            </div>

            {/* Avg score by industry */}
            <div className="bg-white rounded-xl border border-gray-200 p-5 shadow-sm">
              <h2 className="text-sm font-semibold text-gray-700 mb-4">Gem. score per sector</h2>
              {analytics.avgScoreByIndustry.length === 0 ? (
                <p className="text-sm text-gray-400">Geen scoredata beschikbaar (scores nog niet gegenereerd)</p>
              ) : (
                <div className="space-y-3">
                  {analytics.avgScoreByIndustry.map((item, i) => (
                    <div key={item.industry} className="flex items-center gap-3">
                      <span className="text-sm text-gray-700 w-32 truncate flex-shrink-0" title={item.industry}>{item.industry}</span>
                      <div className="flex-1 bg-gray-100 rounded-full h-2.5">
                        <div
                          className={`${industryColors[i % industryColors.length]} h-2.5 rounded-full transition-all`}
                          style={{ width: `${Math.max(4, (item.avgScore / 10) * 100)}%` }}
                        />
                      </div>
                      <span className="text-sm font-medium text-gray-900 w-8 text-right flex-shrink-0">{item.avgScore}</span>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>

          {/* Team performance */}
          <div className="bg-white rounded-xl border border-gray-200 p-5 shadow-sm">
            <h2 className="text-sm font-semibold text-gray-700 mb-4">Teamprestaties</h2>
            {analytics.teamPerformance.length === 0 ? (
              <p className="text-sm text-gray-400">Geen teamdata beschikbaar (leads nog niet toegewezen)</p>
            ) : (
              <table className="w-full text-sm">
                <thead>
                  <tr className="text-xs text-gray-500 uppercase tracking-wide border-b border-gray-100">
                    <th className="text-left pb-2 font-medium">Medewerker</th>
                    <th className="text-right pb-2 font-medium">Leads</th>
                    <th className="text-right pb-2 font-medium">Verrijkt</th>
                    <th className="text-right pb-2 font-medium">Gem. score</th>
                    <th className="text-right pb-2 font-medium">Gewonnen</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-50">
                  {analytics.teamPerformance.map(tp => (
                    <tr key={tp.userName} className="hover:bg-gray-50">
                      <td className="py-2 text-gray-700 font-medium">{tp.userName}</td>
                      <td className="py-2 text-right text-gray-900">{tp.totalLeads}</td>
                      <td className="py-2 text-right text-gray-900">{tp.enrichedLeads}</td>
                      <td className="py-2 text-right text-gray-900">{tp.avgScore > 0 ? tp.avgScore.toFixed(1) : '—'}</td>
                      <td className="py-2 text-right">
                        <span className={tp.wonLeads > 0 ? 'text-green-600 font-semibold' : 'text-gray-400'}>{tp.wonLeads}</span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        </div>
      )}
    </div>
  )
}

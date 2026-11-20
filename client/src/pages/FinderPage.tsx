import { Link } from 'react-router-dom'
import HuntPanel from '../components/leads/HuntPanel'

/** Find new leads: a real web search, then every score bearing fact is verified on the company's own website. */
export default function FinderPage() {
  return (
    <div className="max-w-3xl mx-auto space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-2xl font-bold text-gray-900">Find new leads</h2>
          <p className="text-sm text-gray-500 mt-0.5">Real companies from a web search, scored from facts that are proven on their own websites.</p>
        </div>
        <Link to="/leads" className="text-sm text-indigo-600 hover:underline">Back to leads</Link>
      </div>
      <div className="bg-white border border-gray-200 rounded-xl p-5">
        <HuntPanel />
      </div>
    </div>
  )
}

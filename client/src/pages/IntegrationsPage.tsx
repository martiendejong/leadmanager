import { useCallback, useEffect, useState } from 'react'
import { useToast } from '../components/Toast'
import { errorMessage } from '../api/crm'
import { createApiKey, getApiKeys, revokeApiKey, type ApiKeyInfo } from '../api/apiKeys'

const MCP_TOOLS: [string, string][] = [
  ['list_clients', 'All clients by priority, with projects, next action and last contact'],
  ['create_client', 'Add a client'],
  ['set_client_priority', "Set a client's value, urgency and effort"],
  ['create_project', 'Add a project to a client'],
  ['log_interaction', 'Log a conversation or note, optionally on a project'],
  ['add_next_action / complete_next_action', 'Schedule or complete a next step'],
  ['list_actions / next_actions_today', "All open actions, or today's call list"],
  ['search_leads / get_lead / create_lead', 'Search, read and create leads and clients'],
  ['add_contact / link_item', 'Add a contact, link to JengoWork, JengoKnowledge, mail or Meet'],
]

const card = 'bg-white rounded-xl border border-gray-200 p-5 mb-5'
const input = 'text-sm border border-gray-300 rounded-lg px-3 py-1.5 focus:outline-none focus:ring-2 focus:ring-indigo-300'

function fmt(iso?: string | null) {
  return iso ? new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' }) : '–'
}

function CopyBlock({ text, label }: { text: string; label: string }) {
  const { showToast } = useToast()
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text)
      showToast('Copied', 'success')
    } catch {
      showToast('Could not copy: select the text and copy it manually', 'error')
    }
  }
  return (
    <div className="relative">
      <pre aria-label={label} className="text-xs font-mono bg-gray-50 border border-gray-200 rounded-lg p-3 pr-20 overflow-x-auto whitespace-pre-wrap break-all">{text}</pre>
      <button onClick={copy} className="absolute top-2 right-2 text-xs px-2 py-1 rounded-md bg-white border border-gray-300 hover:bg-gray-50">Copy</button>
    </div>
  )
}

/** API keys (also the MCP token) and ready-made connection settings for agents and other tools. */
export default function IntegrationsPage() {
  const { showToast } = useToast()
  const [keys, setKeys] = useState<ApiKeyInfo[] | null>(null)
  const [name, setName] = useState('')
  const [busy, setBusy] = useState(false)
  const [fresh, setFresh] = useState<{ name: string; key: string } | null>(null)

  const origin = window.location.origin
  const mcpUrl = `${origin}/mcp`
  const shownKey = fresh?.key ?? 'lm_YOUR_KEY'

  const load = useCallback(() => {
    getApiKeys()
      .then(setKeys)
      .catch((err) => { showToast(errorMessage(err, 'Could not load keys'), 'error'); setKeys([]) })
  }, [showToast])

  useEffect(load, [load])

  const create = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!name.trim() || busy) return
    setBusy(true)
    try {
      const k = await createApiKey(name.trim())
      setFresh({ name: k.name, key: k.key })
      setName('')
      load()
    } catch (err) {
      showToast(errorMessage(err, 'Could not create key'), 'error')
    } finally {
      setBusy(false)
    }
  }

  const revoke = async (k: ApiKeyInfo) => {
    if (!window.confirm(`Revoke key "${k.name}"? Anything using it stops working immediately.`)) return
    try {
      await revokeApiKey(k.id)
      load()
    } catch (err) {
      showToast(errorMessage(err, 'Could not revoke key'), 'error')
    }
  }

  const claudeCmd = `claude mcp add --transport http jengo-clients ${mcpUrl} --header "Authorization: Bearer ${shownKey}"`
  const jsonConfig = JSON.stringify(
    { mcpServers: { 'jengo-clients': { type: 'http', url: mcpUrl, headers: { Authorization: `Bearer ${shownKey}` } } } },
    null,
    2,
  )
  const curlExample = `curl -H "Authorization: Bearer ${shownKey}" ${origin}/api/clients`

  return (
    <div className="max-w-3xl">
      <h1 className="text-xl font-semibold text-gray-900 mb-1">Integrations</h1>
      <p className="text-sm text-gray-500 mb-5">
        Connect agents and other tools to your clients, projects and actions with an API key. The same key works for the
        REST API and the MCP server.
      </p>

      <section className={card}>
        <h2 className="text-base font-semibold text-gray-900 mb-3">API keys</h2>
        <form onSubmit={create} className="flex gap-2 mb-4">
          <input
            className={`${input} flex-1`}
            placeholder="What is the key for? e.g. Claude Code laptop"
            aria-label="Key name"
            value={name}
            onChange={(e) => setName(e.target.value)}
            maxLength={100}
          />
          <button type="submit" disabled={busy || !name.trim()} className="text-sm px-3 py-1.5 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50">
            Create key
          </button>
        </form>

        {fresh && (
          <div className="mb-4 rounded-lg border border-amber-300 bg-amber-50 p-3" role="status">
            <p className="text-sm font-medium text-amber-900 mb-2">
              Key &quot;{fresh.name}&quot; was created. Copy it now: it will not be shown again.
            </p>
            <CopyBlock text={fresh.key} label="New API key" />
            <button onClick={() => setFresh(null)} className="mt-2 text-xs text-amber-900 underline">I have saved it</button>
          </div>
        )}

        {keys === null ? (
          <p className="text-sm text-gray-400">Loading...</p>
        ) : keys.length === 0 ? (
          <p className="text-sm text-gray-400">No keys yet.</p>
        ) : (
          <ul className="divide-y divide-gray-100">
            {keys.map((k) => (
              <li key={k.id} className="py-2 flex items-center gap-3 text-sm">
                <div className="min-w-0 flex-1">
                  <p className={`font-medium ${k.revokedAt ? 'text-gray-400 line-through' : 'text-gray-900'}`}>{k.name}</p>
                  <p className="text-xs text-gray-500">
                    <span className="font-mono">{k.prefix}…</span> · created {fmt(k.createdAt)} · last used {fmt(k.lastUsedAt)}
                    {k.revokedAt && ` · revoked ${fmt(k.revokedAt)}`}
                  </p>
                </div>
                {!k.revokedAt && (
                  <button onClick={() => revoke(k)} className="text-xs px-2 py-1 rounded-md text-red-600 hover:bg-red-50">Revoke</button>
                )}
              </li>
            ))}
          </ul>
        )}
        <p className="text-xs text-gray-400 mt-3">
          A key works on clients, projects, contacts, conversations and actions, and nothing else; deleting can only be done here in the app. It does not expire: revoke it if it leaks.
        </p>
      </section>

      <section className={card}>
        <h2 className="text-base font-semibold text-gray-900 mb-3">MCP server</h2>
        <p className="text-sm text-gray-600 mb-3">
          Address: <span className="font-mono text-xs">{mcpUrl}</span>. {fresh ? 'The commands below contain your new key.' : 'Create a key first, then replace YOUR_KEY.'}
        </p>
        <p className="text-xs font-semibold text-gray-500 uppercase tracking-wider mb-1">Claude Code</p>
        <CopyBlock text={claudeCmd} label="Claude Code command" />
        <p className="text-xs font-semibold text-gray-500 uppercase tracking-wider mt-4 mb-1">Other MCP clients (JSON)</p>
        <CopyBlock text={jsonConfig} label="MCP JSON configuration" />
        <p className="text-xs text-gray-400 mt-3">
          ChatGPT and the Claude.ai connectors require OAuth login, which is not available yet. Claude Code, agents and other tools work with the key. Note: the command puts the key in your shell history and in the Claude configuration.
        </p>
        <h3 className="text-xs font-semibold text-gray-500 uppercase tracking-wider mt-5 mb-2">What an agent can do</h3>
        <ul className="space-y-1">
          {MCP_TOOLS.map(([tool, what]) => (
            <li key={tool} className="text-sm text-gray-600">
              <span className="font-mono text-xs text-gray-800">{tool}</span> · {what}
            </li>
          ))}
        </ul>
      </section>

      <section className={card}>
        <h2 className="text-base font-semibold text-gray-900 mb-3">REST API</h2>
        <p className="text-sm text-gray-600 mb-3">
          The same key as <span className="font-mono text-xs">Authorization: Bearer</span> on clients, projects, leads (CRM part only), conversations and actions.
        </p>
        <CopyBlock text={curlExample} label="curl example" />
      </section>
    </div>
  )
}

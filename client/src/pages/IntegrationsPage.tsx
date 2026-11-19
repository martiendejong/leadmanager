import { useCallback, useEffect, useState } from 'react'
import { useToast } from '../components/Toast'
import { errorMessage } from '../api/crm'
import { createApiKey, getApiKeys, revokeApiKey, type ApiKeyInfo } from '../api/apiKeys'

const MCP_TOOLS: [string, string][] = [
  ['list_clients', 'Alle klanten op prioriteit, met projecten, volgende actie en laatste contact'],
  ['create_client', 'Klant toevoegen'],
  ['set_client_priority', 'Waarde, urgentie en inspanning van een klant zetten'],
  ['create_project', 'Project toevoegen aan een klant'],
  ['log_interaction', 'Gesprek of notitie vastleggen, optioneel bij een project'],
  ['add_next_action / complete_next_action', 'Volgende stap plannen of afvinken'],
  ['list_actions / next_actions_today', 'Alle open acties, of de belijst van vandaag'],
  ['search_leads / get_lead / create_lead', 'Leads en klanten zoeken, lezen en aanmaken'],
  ['add_contact / link_item', 'Contactpersoon toevoegen, koppelen aan JengoWork, JengoKnowledge, mail of Meet'],
]

const card = 'bg-white rounded-xl border border-gray-200 p-5 mb-5'
const input = 'text-sm border border-gray-300 rounded-lg px-3 py-1.5 focus:outline-none focus:ring-2 focus:ring-indigo-300'

function fmt(iso?: string | null) {
  return iso ? new Date(iso).toLocaleDateString('nl-NL', { day: 'numeric', month: 'short', year: 'numeric' }) : '–'
}

function CopyBlock({ text, label }: { text: string; label: string }) {
  const { showToast } = useToast()
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text)
      showToast('Gekopieerd', 'success')
    } catch {
      showToast('Kopiëren mislukt: selecteer de tekst en kopieer handmatig', 'error')
    }
  }
  return (
    <div className="relative">
      <pre aria-label={label} className="text-xs font-mono bg-gray-50 border border-gray-200 rounded-lg p-3 pr-20 overflow-x-auto whitespace-pre-wrap break-all">{text}</pre>
      <button onClick={copy} className="absolute top-2 right-2 text-xs px-2 py-1 rounded-md bg-white border border-gray-300 hover:bg-gray-50">Kopieer</button>
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
  const shownKey = fresh?.key ?? 'lm_JOUW_SLEUTEL'

  const load = useCallback(() => {
    getApiKeys()
      .then(setKeys)
      .catch((err) => { showToast(errorMessage(err, 'Sleutels laden mislukt'), 'error'); setKeys([]) })
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
      showToast(errorMessage(err, 'Aanmaken mislukt'), 'error')
    } finally {
      setBusy(false)
    }
  }

  const revoke = async (k: ApiKeyInfo) => {
    if (!window.confirm(`Sleutel "${k.name}" intrekken? Alles wat hem gebruikt stopt direct met werken.`)) return
    try {
      await revokeApiKey(k.id)
      load()
    } catch (err) {
      showToast(errorMessage(err, 'Intrekken mislukt'), 'error')
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
      <h1 className="text-xl font-semibold text-gray-900 mb-1">Koppelingen</h1>
      <p className="text-sm text-gray-500 mb-5">
        Verbind agents en andere tools met je klanten, projecten en acties via een API-sleutel. Dezelfde sleutel werkt voor de
        REST-API en voor de MCP-server.
      </p>

      <section className={card}>
        <h2 className="text-base font-semibold text-gray-900 mb-3">API-sleutels</h2>
        <form onSubmit={create} className="flex gap-2 mb-4">
          <input
            className={`${input} flex-1`}
            placeholder="Waarvoor is de sleutel? bijv. Claude Code laptop"
            aria-label="Naam van de sleutel"
            value={name}
            onChange={(e) => setName(e.target.value)}
            maxLength={100}
          />
          <button type="submit" disabled={busy || !name.trim()} className="text-sm px-3 py-1.5 rounded-lg bg-indigo-600 text-white hover:bg-indigo-700 disabled:opacity-50">
            Sleutel maken
          </button>
        </form>

        {fresh && (
          <div className="mb-4 rounded-lg border border-amber-300 bg-amber-50 p-3" role="status">
            <p className="text-sm font-medium text-amber-900 mb-2">
              Sleutel &quot;{fresh.name}&quot; is gemaakt. Kopieer hem nu: hij wordt niet opnieuw getoond.
            </p>
            <CopyBlock text={fresh.key} label="Nieuwe API-sleutel" />
            <button onClick={() => setFresh(null)} className="mt-2 text-xs text-amber-900 underline">Ik heb hem bewaard</button>
          </div>
        )}

        {keys === null ? (
          <p className="text-sm text-gray-400">Laden...</p>
        ) : keys.length === 0 ? (
          <p className="text-sm text-gray-400">Nog geen sleutels.</p>
        ) : (
          <ul className="divide-y divide-gray-100">
            {keys.map((k) => (
              <li key={k.id} className="py-2 flex items-center gap-3 text-sm">
                <div className="min-w-0 flex-1">
                  <p className={`font-medium ${k.revokedAt ? 'text-gray-400 line-through' : 'text-gray-900'}`}>{k.name}</p>
                  <p className="text-xs text-gray-500">
                    <span className="font-mono">{k.prefix}…</span> · gemaakt {fmt(k.createdAt)} · laatst gebruikt {fmt(k.lastUsedAt)}
                    {k.revokedAt && ` · ingetrokken ${fmt(k.revokedAt)}`}
                  </p>
                </div>
                {!k.revokedAt && (
                  <button onClick={() => revoke(k)} className="text-xs px-2 py-1 rounded-md text-red-600 hover:bg-red-50">Intrekken</button>
                )}
              </li>
            ))}
          </ul>
        )}
        <p className="text-xs text-gray-400 mt-3">
          Een sleutel doet alles wat jij kunt in klanten, projecten, contacten en acties, en niets daarbuiten. Hij verloopt niet: trek hem in als hij lekt.
        </p>
      </section>

      <section className={card}>
        <h2 className="text-base font-semibold text-gray-900 mb-3">MCP-server</h2>
        <p className="text-sm text-gray-600 mb-3">
          Adres: <span className="font-mono text-xs">{mcpUrl}</span>. {fresh ? 'De opdrachten hieronder bevatten je nieuwe sleutel.' : 'Maak eerst een sleutel en vervang daarna JOUW_SLEUTEL.'}
        </p>
        <p className="text-xs font-semibold text-gray-500 uppercase tracking-wider mb-1">Claude Code</p>
        <CopyBlock text={claudeCmd} label="Claude Code opdracht" />
        <p className="text-xs font-semibold text-gray-500 uppercase tracking-wider mt-4 mb-1">Andere MCP-clients (JSON)</p>
        <CopyBlock text={jsonConfig} label="MCP JSON-configuratie" />
        <p className="text-xs text-gray-400 mt-3">
          ChatGPT en de Claude.ai-connectors vragen inloggen via OAuth; dat is er nog niet. Claude Code, agents en andere tools werken met de sleutel.
        </p>
        <h3 className="text-xs font-semibold text-gray-500 uppercase tracking-wider mt-5 mb-2">Wat een agent kan</h3>
        <ul className="space-y-1">
          {MCP_TOOLS.map(([tool, what]) => (
            <li key={tool} className="text-sm text-gray-600">
              <span className="font-mono text-xs text-gray-800">{tool}</span> · {what}
            </li>
          ))}
        </ul>
      </section>

      <section className={card}>
        <h2 className="text-base font-semibold text-gray-900 mb-3">REST-API</h2>
        <p className="text-sm text-gray-600 mb-3">
          Dezelfde sleutel als <span className="font-mono text-xs">Authorization: Bearer</span> op klanten, projecten, leads (alleen het CRM-deel), gesprekken en acties.
        </p>
        <CopyBlock text={curlExample} label="curl voorbeeld" />
      </section>
    </div>
  )
}

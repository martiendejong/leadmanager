// The connector consent page (/oauth/authorize, served by the API) sends a signed-out user to /login and
// remembers where it came from. After login the app goes back there instead of to /today, so ChatGPT and
// Claude.ai can finish the connection. Only a fresh, same-origin /oauth/authorize URL is honoured.
const KEY = 'lm_oauth_return'
const MAX_AGE_MS = 15 * 60 * 1000

/** The path to resume (and clears it), or null when there is none. */
export function takeOAuthReturn(): string | null {
  try {
    const raw = localStorage.getItem(KEY)
    if (!raw) return null
    localStorage.removeItem(KEY)
    const { url, ts } = JSON.parse(raw) as { url?: unknown; ts?: unknown }
    if (typeof url !== 'string' || typeof ts !== 'number') return null
    if (Date.now() - ts > MAX_AGE_MS) return null
    return url.startsWith('/oauth/authorize?') ? url : null
  } catch {
    return null
  }
}

/** Where to go after a successful login. */
export function afterLoginTarget(fallback = '/today'): string {
  return takeOAuthReturn() ?? fallback
}

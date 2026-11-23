# Jengo Clients · MCP server

`POST /mcp` lets agents and other Jengo tools read and write leads and clients. The endpoint is the shared
connector from `jengo-components` (`dotnet/mcp-connector`, copied unchanged into `src/LeadManager.Api/Mcp/Connector/`)
with a token source backed by the app's API key table.

## Connecting

1. Create an API key in the app under **Integrations** (or `POST /api/api-keys` with `{"name": "..."}`; the key
   starts with `lm_` and is shown only once). That screen also gives the ready-made command for Claude Code
   and a JSON configuration for other MCP clients.
2. Use that key as a Bearer token:

```
claude mcp add --transport http jengo-clients https://clients.prospergenics.com/mcp --header "Authorization: Bearer lm_..."
```

The same key also works on the REST API for clients, projects, leads (CRM part), interactions and actions; deleting
(a client or project) is only possible from the app. Note: `claude mcp add --header` puts the key in your shell history and
in the Claude configuration.

The key acts as the user who created it: the same leads and clients, the same rules. A revoked key or a
deactivated user is rejected. A JWT from the login page is not a valid MCP token.

## Tools

| Tool | Does |
|---|---|
| `list_organizations` | The organisations you are a member of (id, name, slug, role). Leads belong to one organisation each and you only see those of your own; see [ORGANISATIONS.md](ORGANISATIONS.md) |
| `search_leads` | Search leads and clients by name, email address or domain; optional `organization` (id, slug or name) narrows it to one organisation's leads |
| `find_by_email_or_domain` | Who is this? Email address or domain to lead or client (also finds contacts) |
| `get_lead` | Everything about one lead or client: details, contacts, open actions, links, recent history. A lead also carries `whyLead` (why it is a lead), `needs` (what the business needs, each with a quote from its own pages) and `productFit` (which of our products can help: product key, reason, evidence quote, source URL; an empty list means no clear fit, null means not assessed) |
| `create_lead` | Create a lead, or return the existing one if the website domain is already known within the organisation (safe to repeat). `organization` is required when you are a member of several |
| `log_interaction` | Record an interaction (call, email, WhatsApp, meeting, note); `source_ref` makes it idempotent |
| `add_next_action` | Plan a next step; appears on the Today list when it is due |
| `complete_next_action` | Tick off an action |
| `next_actions_today` | The call list: due today (Dutch time) or overdue, with name and phone number |
| `list_clients` | All clients, highest priority first, with score, projects, next action and last contact |
| `create_client` | Add a client manually (refuses the same name twice) |
| `set_client_priority` | Set value, urgency and effort: score = value × urgency ÷ effort² |
| `create_project` | Add a project to a client; `log_interaction` then accepts `project_id` |
| `list_documents` | A client's file: quotes, assignments, agreements, data processing agreements, NDAs with status, amount, dates, previous document and file names |
| `add_document` | Record a document in the file (details and optionally a link); uploading files is done in the app. Chain with `predecessor_id`: quote, assignment, agreement |
| `list_actions` | All open actions across leads and clients in a period, with name |
| `add_contact` | Add a person to a lead or client |
| `get_message_prefill` | A suggested message for a lead from the templates (deterministic, no AI), built only from what is stored on the lead, with warnings (do-not-contact note, missing address). Saves and sends nothing |
| `list_messages` | Drafts and sent messages of your leads, filter by lead, status or channel |
| `create_message_draft` / `update_message_draft` | Save or edit a draft. A draft changes nothing about the lead. **Nothing is ever sent by the app or by these tools** |
| `mark_message_sent` | Record that the user sent a message themselves (`confirmed: true` is required): the lead moves from New to Contacted (never back from a later status) and an activity is written |
| `link_item` | Link to something in another tool: JengoWork board or task, JengoKnowledge project, mail thread, Meet session |
| `get_lead_hunt` | Progress and result of a hunt: status, counters and one row per company with its verified score and why it stopped |

For `get_lead`, `log_interaction`, `add_next_action`, `add_contact` and `link_item`, `parent_type` is
`leads` (default) or `clients`, with the matching `parent_id`. Times are ISO 8601; without a zone, UTC applies.

## One set of rules

Every tool calls the same controller as the REST API, as the user behind the key. Ownership,
validation, limits, `SourceRef` idempotency and UTC times are therefore the same everywhere, and a tool can never do more
than the REST endpoint it wraps. Errors come back as a tool error (`isError: true`) with the reason, not as a
server error.

## ChatGPT and Claude.ai (custom connector, OAuth)

Add the connector with only the URL `https://clients.prospergenics.com/mcp`: ChatGPT (Settings, Connectors, Developer
mode, Create) or Claude.ai (Settings, Connectors, Add custom connector). Leave the OAuth client fields empty: both
register themselves (dynamic client registration). You then land on the Clients consent page:

1. If you are not signed in to Clients in that browser, the page offers **Sign in to Clients** (the normal login, SSO
   included) and brings you back to the consent page afterwards.
2. **Allow access** creates a personal `lm_` API key named "ChatGPT connector" (or "Claude connector"), hands it to the
   connector as its access token and replaces the previous key of that connector. The key appears on **Integrations**
   and can be revoked there at any time; the connector then stops working until you connect again.

Because the token is your own API key, the connector sees exactly your leads, clients and organisations, and has the
same limits as any key (no deleting, no file downloads). Existing keys and `claude mcp add` setups keep working.

How it works: `Mcp/LeadManagerOAuthController.cs` on top of the shared connector (`OAuthControllerBase`, `OAuthService`).
Endpoints: `/.well-known/oauth-protected-resource` (RFC 9728) and `/.well-known/oauth-authorization-server` (RFC 8414),
both also under the `/mcp` aliases, `/oauth/register` (RFC 7591), `/oauth/authorize` (consent page), `/oauth/consent`
(JWT bearer only, mints the key), `/oauth/token` (authorization code + PKCE S256; PKCE is mandatory). Redirects are
limited to https `chatgpt.com` (including `/connector/oauth/<id>`), `chat.openai.com`, `platform.openai.com`, `claude.ai` and `claude.com`, plus http loopback (`localhost`/`127.0.0.1`, any port) for Claude Code (the allowlist is enforced at
registration and at authorize, whatever was registered). Registered clients and authorization codes live in memory
(10 minutes), so a restart only costs a reconnect in the middle of a consent; issued keys survive restarts.
ChatGPT caches the tool list per connector: after tool changes use "Reconnect" in the connector menu.

## Not yet

- Each tool works for one owner at a time; sharing leads between users does not exist in the app yet.

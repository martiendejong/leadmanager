# Jengo Clients on Docker · test, acc and prod

One image (`jengo-clients:<environment>`) contains the API, SignalR and the React frontend. The stack follows
the CoachOS example: one folder per environment on the server, its own `.env`, no published ports.
The server's shared Caddy gateway reaches the container through a Docker network alias.

| Environment | URL | Server | Stack | Gateway | Alias |
|---|---|---|---|---|---|
| test | https://clients-test.martiendejong.nl | 46.224.114.149 | `/opt/clients-test` | `/opt/jengo-gateway` (`proxy_net`) | `test-clients` |
| acc | https://clients-acc.martiendejong.nl | 46.224.114.149 | `/opt/clients-acc` | same | `acc-clients` |
| prod | https://clients.prospergenics.com (`leads.prospergenics.com` redirects, `api.leads...` serves the same app) | 46.62.219.14 | `/opt/clients-prod` | `/opt/maaride-gateway` (`maaride_net`) | `prod-clients` |

Test and acc start empty, without AI keys and without integrations. Prod has run on the data of the old Windows
installation since 19-11-2026 (see "Migration" below).

## First time per environment

From a machine with the SSH key and the Jengo vault tools:

```
python deploy/upload_and_deploy.py init-env test    # .env with new secrets; secrets also go in the vault (project 12)
python deploy/upload_and_deploy.py dns test         # A record in Cloudflare (DNS only)
python deploy/upload_and_deploy.py gateway test     # add Caddy block (with backup and validation)
python deploy/upload_and_deploy.py deploy test      # upload source, build image, start
python deploy/upload_and_deploy.py smoke test       # 31 checks against the public URL
```

The tool only connects to servers that are already in `~/.ssh/known_hosts` (strict host key checking): an
unknown or changed key is a hard stop. Verify the fingerprint once with `ssh root@<ip>`.
`init-env --force` creates new secrets (everyone is logged out) and sets `SEED_RESET_PASSWORDS=true` for the next
start, so the administrator password in the database changes along with it; `deploy.sh` removes that
switch from `.env` again afterwards.

Same steps for `acc`. The first administrator is `info@martiendejong.nl` with a generated password
(vault 12, "Jengo Clients <env> admin login"). Without `ADMIN_EMAIL` and `ADMIN_PASSWORD` no account is
created: there is no default password.

## Every subsequent deploy

Always from `develop` (see the working agreement): merge feature work first.

```
python deploy/upload_and_deploy.py deploy test       # or acc; --ref to test another branch
python deploy/upload_and_deploy.py smoke test
```

`deploy.sh` first makes a WAL-safe backup (`backups/<timestamp>/`, the last 14 are kept), builds, starts
and waits until the container is healthy. It is not guaranteed that a failed deploy leaves the old container
running until the new one is healthy: restore from the backup if needed (below).

## Production: promote, do not build

The production server is small and also runs MaaRide, IAM and CoachOS. So the image is built and tested on
acc, and then transferred:

```
python deploy/upload_and_deploy.py init-env prod     # once
python deploy/upload_and_deploy.py dns prod
python deploy/upload_and_deploy.py gateway prod
python deploy/upload_and_deploy.py promote --confirm-prod
python deploy/upload_and_deploy.py smoke prod
```

`promote` streams `jengo-clients:acc` to prod (`docker save | gunzip | docker load`), checks that the
image id is identical on both sides (otherwise nothing starts), tags it as `jengo-clients:prod`, puts exactly
the same source revisions as acc in place (leadmanager and websearch, from `REVISION` on acc) and starts with
`--no-build`.

## Configuration

Everything is in `.env` (see `.env.example`); `docker-compose.yml` translates it to the app's settings.

| Setting | Meaning |
|---|---|
| `JWT_KEY` | Signs logins. At least 32 characters, unique per environment. Changing it = everyone logs in again. |
| `ADMIN_EMAIL`, `ADMIN_PASSWORD` | First administrator (`Seed:Admins`). Existing accounts keep their password. |
| `PUBLIC_URL` | CORS and SSO callback (`<PUBLIC_URL>/api/auth/sso/callback`). |
| `SSO_ISSUER`, `SSO_CLIENT_ID` | Jengo IAM, see "Signing in with Jengo IAM". |
| `SEED_RESET_PASSWORDS` | Recovery switch, only via `init-env --force`; applies to one start. |
| `OPENAI_API_KEY` etc. | Optional; empty = that feature is off. Deliberately empty on test. |
| `GOOGLE_PLACES_API_KEY` | Optional. Without it no Google rating is ever used, so no lead can reach a score of 9. |
| `JENGOWORK_BASE_URL`, `JENGOWORK_API_KEY` | Needed for **Find new leads** and **Ask agent to review**: the app creates a JengoWork task on board 105 "Jengo Clients" (`POST {base}/api/v2/list/105/task`, Bearer key). Board 105 is not monitored by jengo-agi yet, so a person starts the task (move it to todo); `HUNT_DISPATCH_AGENT_PICKS_UP=true` switches the screen texts once a lane starts the agent by itself. The key is a JengoWork agent key (a `tm_` key). Empty = both answer 503 "Agent hunting is not configured on this server". No OpenAI key is involved. |
| `HUNT_DISPATCH_BOARD_ID` | Optional. The JengoWork board (list id) that receives the tasks; empty = 105. New tasks land in the board's first lane ("prioritization backlog"); a human or jengo-agi monitoring starts them. See `docs/agent/LEAD_HUNT.md`. |
| `HUNT_DISPATCH_STATUS` | Optional lane the task is created in, for example `todo`. Empty = the board default. |
| (per hunt token) | Nothing to configure: every hunt mints a random token (stored hashed, valid 3 hours, revoked when the hunt completes, fails or is cancelled). It is in the task text of that hunt only. |
| `HUNT_ALLOWED_EMAILS` | Who may start a hunt, comma separated. Empty = administrators only. |
| `HUNT_MAX_PER_USER_PER_DAY` | Hunts per user per day (default 5). `HUNT_MAX_VERIFY_PER_USER_PER_DAY` is the same for verify runs (default 20). |
| `HUNT_API_BASE_URL` | Optional. The URL the agent calls (default: `PUBLIC_URL`). |
| `MEM_LIMIT` | Hard memory limit of the container (default 768 MB). |

The container does not start if `Jwt:Key` is empty, shorter than 32 characters or the public key from `appsettings.json`.
The Hangfire dashboard is off in the container.

The app runs as user `app` (uid 1654); `deploy.sh` fixes the owner of `data/`. The data volume
(`./data`) contains `leadmanager.db` and `dp-keys/` (Data Protection keys). The image contains no secrets.

## Health and checks

- `GET /api/health` (open): status and version. The container has a healthcheck on it.
- `python deploy/upload_and_deploy.py status <env>`: revision, container status, memory and health.
- `python deploy/smoke_test.py --base <url> --admin-email <email>` (password in `LM_ADMIN_PASSWORD`):
  frontend, routing, login, a complete CRM walkthrough and cleanup.

## Restoring

A backup contains the database and `dp-keys`:

```
cd /opt/clients-<env>/deploy
docker compose down
cp backups/<timestamp>/db/leadmanager.db data/leadmanager.db
docker compose up -d
```

## Organisations and who sees which leads

Leads belong to an organisation and are visible to its members only: see `docs/ORGANISATIONS.md`. The people of each organisation are
seeded from `deploy/org_members.json` (embedded in the image: edit, commit, deploy) and managed in the app under Settings > Organisations. A
person who logs in and is in no organisation sees nothing, so a new user needs a line in that file or an organisation admin to add them.

## Signing in with Jengo IAM

The login page has "Sign in with IAM". The app redirects to the IAM (authorization code with PKCE), gets the
user back on `<PUBLIC_URL>/api/auth/sso/callback`, fetches the email address and logs in as the local
user with that address. If that user does not exist yet, it is created with the role `User`; an existing
administrator (such as the first administrator from `ADMIN_EMAIL`) therefore logs in as administrator.

| Environment | IAM (`SSO_ISSUER`) |
|---|---|
| prod | https://maendeleo.martiendejong.nl |
| acc | https://iam-acc.martiendejong.nl |
| test | https://iam-acc.martiendejong.nl (the separate test IAM has only 3 users, not the owner) |

The IAM client `leadmanager` must know the environment's callback address. This is handled, repeatably and
add-only (the old row is first kept on the IAM server), by:

```
python deploy/upload_and_deploy.py sso <env>
```

`init-env` sets `SSO_ISSUER` correctly per environment. The old installation (`leads.prospergenics.com`) stays
in the client. **Note:** everyone with an account in the chosen IAM can sign in and then gets their own, empty
environment (each user only sees their own leads). If you do not want that, remove the button or restrict the IAM client.

## MCP

`POST https://<environment>/mcp` is the MCP server (see `docs/MCP.md`). A user's API key is the
Bearer token. The smoke test checks initialize, the tool list, a `create_lead` and the batch limit.

## Migration from the Windows installation (done 19-11-2026)

`leads.prospergenics.com` was LeadManager as a Windows service behind IIS on 85.215.217.154. Since 19-11-2026 it is
the same app as a Docker stack on the prod server.

- **Data:** the database (101 leads, 1 client, 2 users, indexed pages and notifications) was copied after a clean
  stop of the old service (integrity checked, checksum identical) and brought through the new migrations at
  startup. Everything belonged to `info@martiendejong.nl`; so that user sees their own leads, also via IAM login.
- **Passwords:** the old admin passwords were in git. Both admin accounts were set to new, random passwords during
  the migration with the one-time reset (vault project 12). Other users did not exist.
- **DNS:** `leads.prospergenics.com` and `api.leads.prospergenics.com` point to 46.62.219.14 (DNS only);
  `clients.prospergenics.com` keeps working. Since the host switch (19-11-2026, see below) the main name (`PUBLIC_URL`,
  SSO callback) is `clients.prospergenics.com`.
- **Old installation:** archived, not deleted, in `C:\archive\leadmanager-20261119` on 85.215.217.154 (878 MB):
  the whole folder, the database as it was, the watchdog, and the exported IIS sites, app pools, scheduled task and
  service (`config\`), with a README with restore steps. IIS sites `leads` and `LeadManagerAPI`, their app pools, the
  service `LeadManagerApi` and the task `LeadManagerMemWatch` have been removed. The other IIS sites are untouched.
- **Rolling back** is only possible together with DNS back to 85.215.217.154; see the README in the archive.
- Sessions: the JWT key is new, so everyone logs in once more (with IAM or with their password).
- Note: the win-acme certificate renewal on the old server for `leads` and `api.leads` no longer has a purpose.

## Host switch: clients.prospergenics.com is the main name (19-11-2026)

`leads.prospergenics.com` was the name of the old installation; the app is called Jengo Clients. The main name is now
`clients.prospergenics.com`:

```
python deploy/upload_and_deploy.py sso prod       # extra IAM callback for the new name (the old one stays)
python deploy/upload_and_deploy.py rehost prod    # PUBLIC_URL in .env, restart without build, adjust gateway
python deploy/upload_and_deploy.py smoke prod
```

- `leads.prospergenics.com` gives a permanent redirect (308, keeps the method and path) to the new
  name. `api.leads.prospergenics.com` still serves the app directly, for anyone still using that name.
- Browsers store the login per address: everyone logs in once more on the new name (IAM or password).
- API keys, the MCP key and the calendar feed work on both names; new feed links use the new name
  (existing links keep working via the redirect, but Google Calendar does not always follow a redirect: reconnect
  if a calendar stops refreshing).
- `rehost` makes a backup of `.env` and the Caddyfile, validates Caddy and on any error puts the old file back
  over a fresh connection.

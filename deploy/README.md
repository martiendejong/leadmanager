# Jengo Clients op Docker · test, acc en prod

Eén image (`jengo-clients:<omgeving>`) bevat de API, SignalR en de React-frontend. De stack volgt het
voorbeeld van CoachOS: een map per omgeving op de server, een eigen `.env`, geen gepubliceerde poorten.
De gedeelde Caddy-gateway van de server bereikt de container via een Docker-netwerk-alias.

| Omgeving | URL | Server | Stack | Gateway | Alias |
|---|---|---|---|---|---|
| test | https://clients-test.martiendejong.nl | 46.224.114.149 | `/opt/clients-test` | `/opt/jengo-gateway` (`proxy_net`) | `test-clients` |
| acc | https://clients-acc.martiendejong.nl | 46.224.114.149 | `/opt/clients-acc` | idem | `acc-clients` |
| prod | https://clients.prospergenics.com | 46.62.219.14 | `/opt/clients-prod` | `/opt/maaride-gateway` (`maaride_net`) | `prod-clients` |

Test en acc starten leeg, zonder AI-sleutels en zonder koppelingen. Prod is een nieuwe, aparte installatie;
`leads.prospergenics.com` (Windows/IIS) blijft ongemoeid tot er bewust wordt overgeschakeld (zie onderaan).

## Eerste keer per omgeving

Vanaf een machine met de SSH-sleutel en de Jengo-vaulttools:

```
python deploy/upload_and_deploy.py init-env test    # .env met nieuwe geheimen; geheimen ook in de vault (project 12)
python deploy/upload_and_deploy.py dns test         # A-record in Cloudflare (alleen DNS)
python deploy/upload_and_deploy.py gateway test     # Caddy-blok toevoegen (met backup en validatie)
python deploy/upload_and_deploy.py deploy test      # broncode uploaden, image bouwen, starten
python deploy/upload_and_deploy.py smoke test       # 31 controles tegen de publieke URL
```

Het tool verbindt alleen met servers die al in `~/.ssh/known_hosts` staan (strikte hostsleutelcontrole): een
onbekende of gewijzigde sleutel is een harde stop. Controleer de vingerafdruk eenmalig met `ssh root@<ip>`.
`init-env --force` maakt nieuwe geheimen aan (iedereen wordt uitgelogd) en zet voor de eerstvolgende start
`SEED_RESET_PASSWORDS=true`, zodat het beheerderswachtwoord in de database meeverandert; `deploy.sh` haalt die
schakelaar daarna zelf weer uit `.env`.

Dezelfde stappen voor `acc`. De eerste beheerder is `info@martiendejong.nl` met een gegenereerd wachtwoord
(vault 12, "Jengo Clients <env> admin login"). Zonder `ADMIN_EMAIL` en `ADMIN_PASSWORD` wordt er geen account
aangemaakt: er is geen standaardwachtwoord.

## Elke volgende deploy

Altijd vanaf `develop` (zie de werkafspraak): feature-werk eerst mergen.

```
python deploy/upload_and_deploy.py deploy test       # of acc; --ref om een andere branch te testen
python deploy/upload_and_deploy.py smoke test
```

`deploy.sh` maakt eerst een WAL-veilige backup (`backups/<tijdstempel>/`, de laatste 14 blijven), bouwt, start
en wacht tot de container gezond is. Een mislukte deploy laat de oude container staan tot de nieuwe gezond is
niet gegarandeerd: herstel desnoods uit de backup (hieronder).

## Productie: promoveren, niet bouwen

De productieserver is klein en draait ook MaaRide, IAM en CoachOS. Daarom wordt het image op acc gebouwd en
getest, en daarna overgezet:

```
python deploy/upload_and_deploy.py init-env prod     # eenmalig
python deploy/upload_and_deploy.py dns prod
python deploy/upload_and_deploy.py gateway prod
python deploy/upload_and_deploy.py promote --confirm-prod
python deploy/upload_and_deploy.py smoke prod
```

`promote` streamt `jengo-clients:acc` naar prod (`docker save | gunzip | docker load`), controleert dat het
image-id aan beide kanten identiek is (anders start er niets), tagt het als `jengo-clients:prod`, zet exact
dezelfde broncode-revisies als acc neer (leadmanager én websearch, uit `REVISION` op acc) en start met
`--no-build`.

## Configuratie

Alles staat in `.env` (zie `.env.example`); `docker-compose.yml` vertaalt het naar de instellingen van de app.

| Instelling | Betekenis |
|---|---|
| `JWT_KEY` | Ondertekent logins. Minimaal 32 tekens, per omgeving uniek. Wijzigen = iedereen opnieuw inloggen. |
| `ADMIN_EMAIL`, `ADMIN_PASSWORD` | Eerste beheerder (`Seed:Admins`). Bestaande accounts behouden hun wachtwoord. |
| `PUBLIC_URL` | CORS en SSO-callback (`<PUBLIC_URL>/api/auth/sso/callback`). |
| `SSO_ISSUER`, `SSO_CLIENT_ID` | Jengo IAM, zie "Inloggen met Jengo-IAM". |
| `SEED_RESET_PASSWORDS` | Herstelschakelaar, alleen via `init-env --force`; geldt voor één start. |

De container start niet als `Jwt:Key` leeg, korter dan 32 tekens of de openbare sleutel uit `appsettings.json` is.
Het Hangfire-dashboard staat in de container uit.

## Inloggen met Jengo-IAM

Op de inlogpagina staat "Inloggen met IAM". De app stuurt naar de IAM (autorisatiecode met PKCE), krijgt de
gebruiker terug op `<PUBLIC_URL>/api/auth/sso/callback`, haalt het e-mailadres op en logt in als de lokale
gebruiker met dat adres. Bestaat die nog niet, dan wordt hij aangemaakt met de rol `User`; een bestaande
beheerder (zoals de eerste beheerder uit `ADMIN_EMAIL`) logt dus als beheerder in.

| Omgeving | IAM (`SSO_ISSUER`) |
|---|---|
| prod | https://maendeleo.martiendejong.nl |
| acc | https://iam-acc.martiendejong.nl |
| test | https://iam-acc.martiendejong.nl (de aparte test-IAM heeft maar 3 gebruikers, niet de eigenaar) |

De IAM-client `leadmanager` moet het callback-adres van de omgeving kennen. Dat regelt, herhaalbaar en
alleen toevoegend (de oude rij wordt eerst op de IAM-server bewaard):

```
python deploy/upload_and_deploy.py sso <env>
```

`init-env` zet `SSO_ISSUER` per omgeving goed. De oude installatie (`leads.prospergenics.com`) blijft in de client
staan. **Let op:** iedereen met een account in de gekozen IAM kan inloggen en krijgt dan een eigen, lege
omgeving (elke gebruiker ziet alleen zijn eigen leads). Wie dat niet wil, haalt de knop weg of beperkt de IAM-client.

## MCP

`POST https://<omgeving>/mcp` is de MCP-server (zie `docs/MCP.md`). Een API-key van de gebruiker is het
Bearer-token. De smoke-test controleert initialize, de toollijst, een `create_lead` en de batchgrens.
| `OPENAI_API_KEY` e.a. | Optioneel; leeg = die functie staat uit. Op test bewust leeg. |
| `MEM_LIMIT` | Harde geheugengrens van de container (standaard 768 MB). |

De app draait als gebruiker `app` (uid 1654); `deploy.sh` zet de eigenaar van `data/` goed. Het datavolume
(`./data`) bevat `leadmanager.db` en `dp-keys/` (Data Protection-sleutels). Het image bevat geen geheimen.

## Gezondheid en controle

- `GET /api/health` (open): status en versie. De container heeft hier een healthcheck op.
- `python deploy/upload_and_deploy.py status <env>`: revisie, containerstatus, geheugen en health.
- `python deploy/smoke_test.py --base <url> --admin-email <e-mail>` (wachtwoord in `LM_ADMIN_PASSWORD`):
  frontend, routing, login, een complete CRM-doorloop en opruimen.

## Terugzetten

Een backup bevat de database en `dp-keys`:

```
cd /opt/clients-<env>/deploy
docker compose down
cp backups/<tijdstempel>/db/leadmanager.db data/leadmanager.db
docker compose up -d
```

## Overschakelen van de Windows-installatie (nog te doen, bewust niet automatisch)

De bestaande installatie (`leads.prospergenics.com`, 85.215.217.154, SQLite in
`C:\stores\leadmanager\data\leadmanager.db`) is een aparte omgeving met echte data. Overschakelen betekent:
database kopiëren naar `/opt/clients-prod/deploy/data/`, `leads.prospergenics.com` omzetten of laten
doorverwijzen, en de IAM-client `leadmanager` bijwerken. Dat gebeurt alleen op uitdrukkelijke opdracht.
Na de overstap zijn de oude admin-wachtwoorden uit de git-historie niet meer relevant, maar rotatie op de
oude installatie blijft nodig zolang die draait.

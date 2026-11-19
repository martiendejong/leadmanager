# Jengo Clients · MCP-server

`POST /mcp` laat agents en andere Jengo-tools leads en klanten lezen en schrijven. De endpoint is de
gedeelde connector uit `jengo-components` (`dotnet/mcp-connector`, in `src/LeadManager.Api/Mcp/Connector/`
gekopieerd, ongewijzigd) met een tokenbron op de API-key-tabel van de app.

## Aansluiten

1. Maak een API-key aan in de app onder **Koppelingen** (of `POST /api/api-keys` met `{"name": "..."}`; de sleutel
   begint met `lm_` en wordt maar één keer getoond). Dat scherm geeft ook de kant-en-klare opdracht voor Claude Code
   en een JSON-configuratie voor andere MCP-clients.
2. Gebruik die sleutel als Bearer-token:

```
claude mcp add --transport http jengo-clients https://clients.prospergenics.com/mcp --header "Authorization: Bearer lm_..."
```

Dezelfde sleutel werkt ook op de REST-API voor klanten, projecten, leads (CRM-deel), gesprekken en acties; verwijderen
(klant of project) kan alleen vanuit de app. Let op: `claude mcp add --header` zet de sleutel in je shell-geschiedenis en
in de Claude-configuratie.

De sleutel handelt als de gebruiker die hem maakte: dezelfde leads en klanten, dezelfde regels. Een
ingetrokken sleutel of een gedeactiveerde gebruiker wordt geweigerd. Een JWT van de inlogpagina is geen
geldig MCP-token.

## Tools

| Tool | Doet |
|---|---|
| `search_leads` | Zoek leads en klanten op naam, e-mailadres of domein |
| `find_by_email_or_domain` | Wie is dit? E-mailadres of domein naar lead of klant (vindt ook contactpersonen) |
| `get_lead` | Alles over één lead of klant: gegevens, contacten, open acties, links, laatste geschiedenis |
| `create_lead` | Lead aanmaken, of de bestaande teruggeven als het websitedomein al bekend is (veilig te herhalen) |
| `log_interaction` | Gesprek vastleggen (bellen, mail, WhatsApp, meeting, notitie); `source_ref` maakt het idempotent |
| `add_next_action` | Volgende stap plannen; komt op de Vandaag-lijst als hij aan de beurt is |
| `complete_next_action` | Actie afvinken |
| `next_actions_today` | De belijst: vandaag (Nederlandse tijd) of achterstallig, met naam en telefoonnummer |
| `list_clients` | Alle klanten, hoogste prioriteit eerst, met score, projecten, volgende actie en laatste contact |
| `create_client` | Klant handmatig toevoegen (weigert dezelfde naam twee keer) |
| `set_client_priority` | Waarde, urgentie en inspanning zetten: score = waarde × urgentie ÷ inspanning² |
| `create_project` | Project toevoegen aan een klant; `log_interaction` accepteert daarbij `project_id` |
| `list_actions` | Alle open acties over leads en klanten in een periode, met naam |
| `add_contact` | Persoon toevoegen aan een lead of klant |
| `link_item` | Koppelen aan iets in een andere tool: JengoWork-bord of -taak, JengoKnowledge-project, mailthread, Meet-sessie |

Voor `get_lead`, `log_interaction`, `add_next_action`, `add_contact` en `link_item` is `parent_type`
`leads` (standaard) of `clients`, met het bijbehorende `parent_id`. Tijden zijn ISO 8601; zonder zone geldt UTC.

## Eén set regels

Elke tool roept dezelfde controller aan als de REST-API, als de gebruiker achter de sleutel. Eigenaarschap,
validatie, limieten, `SourceRef`-idempotentie en UTC-tijden zijn dus overal gelijk, en een tool kan nooit meer
dan het REST-endpoint dat hij omhult. Fouten komen terug als toolfout (`isError: true`) met de reden, niet als
serverfout.

## Nog niet

- **Geen OAuth.** ChatGPT- en Claude.ai-connectors vragen OAuth (autorisatiecode met PKCE). De connector heeft
  daar een basis voor (`OAuthControllerBase`), maar LeadManager heeft nog geen toestemmingsscherm.
  Claude Code, agents en andere tools werken nu al met de sleutel. De 401 wijst al naar
  `/.well-known/oauth-protected-resource`, dat nu nog niet bestaat.
- Elke tool werkt voor één eigenaar tegelijk; delen van leads tussen gebruikers bestaat in de app nog niet.

# Lead hunt (Find new leads) and Verify and rescore

The **Find new leads** button on the Leads screen starts a background **Jengo agent**. The agent searches the web for companies and
submits them one by one; the server never trusts what it submits. **Verify and rescore** re-checks leads that already exist, by rules
only. Neither needs an OpenAI key.

## Why this design

The first version let a language model invent companies without internet access and gave each one a fixed confidence. The score of a lead
is a *contactability* score (how directly the owner can be reached, maximum 9, see `Services/Enrichment/SalesScoreService.cs`), so it
must rest on facts that were seen, with their source:

- the agent has real web search and fetch (the Claude Code harness), so the research is real;
- the server fetches the candidate's own pages itself and accepts a claim only when its quote occurs on those pages and the rule for its
  field holds (a landline is not a mobile, a footer range is not a founding year, a role mailbox is not personal, a LinkedIn handle must
  carry the owner's name, a B.V. contradicts "small");
- the score is always computed by `SalesScoreService` from the verified fields, never taken from the agent (`claimedScore` is only
  compared and recorded: "The agent claimed 9; the server verified 6");
- a Google rating is only used when Google Places produced it, never from a website widget or a review site.

## Flow

```
Leads screen  ->  POST /api/leads/hunt            (checks, creates HuntJob, creates a JengoWork task)
                  jengo-agi picks up the task      (spawns an agent with the task text = the versioned prompt)
Agent         ->  GET  /api/agent/hunts/{id}               brief + known domains
              ->  POST /api/agent/hunts/{id}/candidates    one researched company, with claims and quotes
              <-  outcome, verified score, bestScoreSoFar, stop
              ->  POST /api/agent/hunts/{id}/complete      (or /fail)
Leads screen  <-  progress via SignalR (HuntProgress) and a 4 s poll; found companies appear in the list as they arrive
```

The prompt is `src/LeadManager.Api/Agent/LEAD_HUNT_PROMPT.md` (embedded resource, `prompt-version` on the first line). The criteria are
rendered into a delimited data block, sanitized (letters, digits, spaces and `-,.&/()'` only, segment at most 120 characters, region at most
80). The task text never contains a secret.

## What the server decides

| Rule | Where |
|---|---|
| One active job per user; at most `Hunt:MaxPerUserPerDay` hunts per user per day (default 5) | `HuntController.Start` |
| Only administrators, or the users in `Hunt:AllowedEmails`, may start a hunt | `HuntAccess` |
| `minScore` 4 to 9 (default 8), `targetCount` 1 to 10 (default 3), `maxCandidates` 5 to 40 (default 30) | `HuntController.Start` |
| Stop (`stop: true` to the agent) when the target is reached, the candidate limit is reached, or the user cancelled | `HuntService.ShouldStop` |
| A company that is already a lead of the user (same domain) is reported back and not added | `HuntService.SubmitCandidateAsync` |
| An unreachable website is recorded but not added as a lead | idem |
| Every researched candidate is stored as a lead (source `Lead hunt <date>`) with an evidence note: accepted claims with source and quote, rejected claims with the reason | idem |
| A hunt whose agent has been silent for 45 minutes is marked failed | `HuntSweepService` |

The fetching is SSRF-guarded (`UrlGuard`, `SafePageFetcher`): public http(s) addresses only, every connection and redirect hop is checked
against private, loopback and link-local ranges.

## Configuration

| Setting (env) | Meaning |
|---|---|
| `JENGOWORK_BASE_URL`, `JENGOWORK_API_KEY` | JengoWork API and an agent key (`tm_...`) that may create tasks |
| `HUNT_DISPATCH_BOARD_ID` | The board (list id) that receives the tasks: `POST {base}/api/v2/list/{board}/task` with `{name, description}` |
| `HUNT_DISPATCH_STATUS` | Optional lane for the new task so that jengo-agi picks it up at once (for example `todo`) |
| `HUNT_AGENT_TOKEN` | Shared secret of the agent. Header `X-Hunt-Agent-Token`, compared in constant time. Empty = agent endpoints answer 503 |
| `HUNT_ALLOWED_EMAILS` | Comma separated; empty = administrators only |
| `HUNT_MAX_PER_USER_PER_DAY` | default 5 |
| `HUNT_API_BASE_URL` | The URL the agent calls; default the public URL |
| `GOOGLE_PLACES_API_KEY` | Optional; without it no lead can reach 9 |

Without the dispatch settings or the token, **Find new leads** answers `503` ("The Jengo agent dispatch is not configured on this server" or
"The Jengo agent token is not configured on this server") and the screen says so.

## Setting it up on an environment

1. Pick or create a JengoWork board for the hunts and make sure **jengo-agi monitors it** (a board that is not monitored never starts an
   agent). Put its id in `HUNT_DISPATCH_BOARD_ID`, and a lane name that jengo-agi polls in `HUNT_DISPATCH_STATUS`.
2. Create the vault credential **`Clients hunt agent token`** in project 12 (a long random value), put the same value in `HUNT_AGENT_TOKEN` in
   the environment's `.env`, and restart the stack. The agent reads it from the vault by that name.
3. Set `JENGOWORK_BASE_URL` and `JENGOWORK_API_KEY`.
4. Run the smoke test: it checks that the agent endpoints refuse a call without the token and that the capabilities answer.

## Endpoints

User (JWT or API key): `GET /api/leads/hunt/capabilities`, `POST /api/leads/hunt`, `GET /api/leads/hunt`, `GET /api/leads/hunt/{id}`,
`POST /api/leads/hunt/{id}/cancel`, `POST /api/leads/verify`, `POST /api/leads/{id}/verify`, `GET /api/leads/{id}/evidence`.
MCP: `start_lead_hunt`, `get_lead_hunt`.

Agent (agent token only; no login or API key opens them): `GET /api/agent/hunts/{id}`, `POST /api/agent/hunts/{id}/candidates`,
`POST /api/agent/hunts/{id}/complete`, `POST /api/agent/hunts/{id}/fail`. They only work while the hunt is Pending or Running, and
whatever the agent creates belongs to the user who started the hunt.

## Privacy

Only information that the business itself publishes is used. A mobile number or a personal e-mail address of a sole proprietor is
personal data: check the GDPR rules and the Do-Not-Call register before you call or mail. The agent prompt forbids sending anything to
anyone.

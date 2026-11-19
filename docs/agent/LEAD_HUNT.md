# Lead hunt (Find new leads) and Verify and rescore

The **Find new leads** button on the Leads screen starts a background **Jengo agent**. The agent searches the web for companies and
submits them one by one; the server never trusts what it submits. **Verify and rescore** re-checks leads that already exist, by rules
only. Neither needs an OpenAI key.

## Why this design

The first version let a language model invent companies without internet access and gave each one a fixed confidence. The score of a lead
is a *contactability* score (how directly the owner can be reached, maximum 9, see `Services/Enrichment/SalesScoreService.cs`), so it
must rest on facts that were seen, with their source:

- the agent has real web search and fetch (the Claude Code harness), so the research is real;
- the server fetches the candidate's own pages itself and finds the facts with its own rules (`LeadEvidenceService`): a person named with an
  owner cue (owner, founder, director, "my name is", JSON-LD founder, an about-page heading), a first-person or labelled-legal statement
  that the business is a one-person business, mobile numbers, LinkedIn links, mailboxes, years, KvK numbers;
- an agent claim counts only when its quote is in the **visible** text of a fetched page (scripts, styles, the document head and hidden
  elements are not visible text), the extractor found the same fact, and the rule for the field holds (a landline is not a mobile, a
  footer range is not a founding year, a role mailbox or a free-provider address is not the business's personal mailbox, a LinkedIn
  handle must carry the owner's name, a B.V. contradicts "small", a number next to a review or a web designer's credit does not count
  when the site shows several numbers);
- **what is stored is the value the server found, never the string the agent sent**: the canonical LinkedIn address built from the
  handle, the mobile number normalised, the mailbox as published, the year and the KvK number from the matching fact, the name as the
  site writes it;
- the score is always computed by `SalesScoreService` from the verified fields (`claimedScore` is only compared and recorded: "The agent
  claimed 9; the server verified 6");
- a Google rating is only used when Google Places produced it, never from a website widget or a review site;
- free text from the agent or a website (description, services, ...) is untrusted: control characters, markup and instruction-like
  phrases are removed and it is clamped. The sales-approach prompt wraps it in `<bedrijfsdata>` delimiters and says it is data.

## Flow

```
Leads screen  ->  POST /api/leads/hunt            (checks, creates HuntJob, creates a JengoWork task)
                  jengo-agi picks up the task      (spawns an agent with the task text = the versioned prompt, incl. the token of this hunt)
Agent         ->  GET  /api/agent/hunts/{id}               brief (criteria, stop)
              ->  POST /api/agent/hunts/{id}/check         which of these websites are already leads (yes/no per site)
              ->  POST /api/agent/hunts/{id}/candidates    one researched company, with claims and quotes (one at a time)
              <-  outcome, verified score, bestScoreSoFar, stop
              ->  POST /api/agent/hunts/{id}/complete      (or /fail)
Leads screen  <-  progress via SignalR (HuntProgress) and a 4 s poll; found companies appear in the list as they arrive
```

The prompt is `src/LeadManager.Api/Agent/LEAD_HUNT_PROMPT.md` (embedded resource, `prompt-version` on the first line). The criteria are
rendered into a delimited data block. A segment or region with a line break, markup characters or an instruction-like phrase
("ignore previous instructions", "system:", ...) is **refused with 400**; the accepted text is reduced to letters, digits, spaces and
`-,.&/()'`; segment at most 120 characters, region at most 80.

## The token of a hunt

The agent proves itself with `X-Hunt-Agent-Token`. That token is `HMAC-SHA256(Hunt:AgentSecret, "hunt-agent:" + huntId)`, derived per
hunt and compared in constant time. It opens **only that hunt** and **only while it is Pending or Running**, so it is useless after
the hunt ends and for every other hunt. For that reason the dispatcher puts it **in the task text** (the least-leaky design: the agent
needs no vault access, and a copy of the task text is worthless once the hunt is over). The server secret itself (`HUNT_AGENT_SECRET`) is
never put anywhere but the server's environment (and the vault, for ops). Without the secret the agent endpoints answer 503.

## What the server decides

| Rule | Where |
|---|---|
| One active job per user (a unique index in the database) and a per-user lock around check-and-insert, so two requests at the same moment cannot both start | `HuntController.CreateJobAsync`, `IX_HuntJobs_OneActivePerUser` |
| At most `Hunt:MaxPerUserPerDay` hunts (default 5) and `Hunt:MaxVerifyPerUserPerDay` verify runs (default 20) per user per day | `HuntController` |
| Only administrators, or the users in `Hunt:AllowedEmails`, may start a hunt; verifying several leads at once needs the same access, verifying one lead does not | `HuntAccess` |
| `minScore` 4 to 9 (default 8), `targetCount` 1 to 10 (default 3), `maxCandidates` 5 to 40 (default 30) | `HuntController.Start` |
| One candidate of a hunt is checked at a time (a second submission gets 429); the stop conditions are checked again after the slow part, before anything is saved | `HuntLocks`, `HuntService.SubmitCandidateAsync` |
| A candidate may take 120 s and one site 60 s in total; one page fetch has a hard 20 s limit for the whole body, so a site that drips its answer cannot hold a worker | `SafePageFetcher`, `SitePages.Budget`, `HuntService.CandidateBudget` |
| A company that is already a lead of the user is reported back and not added; the agent can only ask yes/no per website, never read the user's lead list | `HuntAgentController.Check` |
| An unreachable website is recorded but not added as a lead | `HuntService` |
| Every researched candidate is stored as a lead (source `Lead hunt <date>`) with an evidence note: accepted claims with source and quote, rejected claims with the reason | `HuntService` |
| A hunt whose agent has been silent for 45 minutes is marked failed | `HuntSweepService` |

Fetching is SSRF-guarded (`UrlGuard`, `SafePageFetcher`): public http(s) addresses only; every connection and redirect hop is checked
against private, loopback, link-local, IPv4-compatible, IPv4-translated, 6to4, NAT64, Teredo and documentation ranges; no proxy is used; a
redirect to another registrable domain is not followed, so a site's facts are never attributed to another business; the body is capped
and decoded with the response charset.

## Hostile pages

A page of a few hundred thousand nested tags fits in the 1.5 MB cap, and the HTML parser walks a page recursively: it overflowed the stack and
ended the process (a stack overflow cannot be caught; reproduced in a test at 20,000 levels). Every page is therefore parsed through
`SafeHtml` only:

- a linear, non-recursive pre-scan measures the element nesting (comments, scripts and attribute values are skipped; elements whose end tag may
  be left out do not count); a page deeper than 120 levels is **not parsed**: it gives no text and no facts;
- the parser itself has the same limit (`OptionMaxNestedChildNodes`) as a second line, and every walk over a page has a depth guard;
- the visible text is built in ONE linear pass (no per-node insertions); phone numbers use the match position; the HTML parser version is pinned;
- the older enrichment code parses pages through the same loader.

Tests: 250,000 nested `div`, `span`, `b`, `a`, `font`, `section`, `table` and `ul`, 100,000 unclosed `p`, `li`, `td` and `option`, and large flat
pages must finish in a few seconds without a crash (they run alone, after the other tests, so a busy machine cannot make them flaky).

## Which values may be removed: provenance

Every value the server writes (a hunt candidate, a verify, the old AI enrichment) is recorded per field as a short hash in
`Lead.HuntWrittenJson` (`HuntProvenance`). A verify may only **remove or overwrite a value whose hash still matches**, that is a value the
server wrote and nobody changed since. A value a person typed, or edited after a hunt wrote it, is kept and listed in the note
("Not removed, because they were entered by a person (confirm to remove them)"); `confirm=true` lifts that for the run. The old AI
enrichment audit follows the same rule: it records what the enrichment run itself changed (a snapshot before and after) and only audits
those values; hand-entered mobile numbers, LinkedIn links, e-mail addresses and ratings are never wiped. Existing leads have no provenance,
so a verify never clears anything on them without `confirm`.

## What counts as the business speaking

Names and statements are read from the page **without** quotations: `blockquote`, `cite`, `q` and elements whose class or id says testimonial,
review, quote, reference or feedback are left out, a block that follows a quotation is its signature, and a role followed by another
organisation than the site's own ("directeur Bakkerij Boer") is somebody else's. A name that only stands next to a role word (no structured
data, no about heading, no "my name is") may confirm a name the lead already has, but is not enough to give a lead its first owner. A year counts
only in a statement of the business about itself ("opgericht in", "in 2010 ben ik ... gestart", "sinds 2009 werk ik ...", "actief sinds 2009"); not
in "Sinds 2008 is er veel veranderd in de praktijk", "sinds 2004 werken wij samen met bureau X", a membership, a newsletter or "vanaf". The year
comes from the best ranked statement, not from the earliest year on the site. A company LinkedIn link counts when it stands in the header, footer
or a social block of the site, or when its handle resembles the business or its domain; a link in a text about clients does not.

## Google rating

Google Places is asked for the company name, but a listing counts only when its **website is on the business's own registrable domain**
or its **phone number is one the business's pages publish**. A similar name proves nothing. Without such a match there is no Google point.
Hosts that many businesses share by path (`linktr.ee/name`, `medium.com/@name`, `bio.link/name`) are told apart by their first path segment, which is kept in the stored address. Site builder and hosting domains (`wixsite.com`, `github.io`, `vercel.app`, ...) are treated as public suffixes, so `a.wixsite.com` and
`b.wixsite.com` are different businesses.

## Verify and rescore (existing leads)

Rules only, no model. The server fetches the lead's own website and applies the same fact rules.

- Values that the server wrote itself and nobody changed since (provenance, see above) may be removed when they cannot be proven.
- A value a person typed is **never** cleared or overwritten by default. What would be removed, and what differs
  from the website, is listed in the evidence note ("Not removed, because they were entered by a person (confirm to remove them)").
- `POST /api/leads/verify` with `{"ids": [...], "confirm": true}` (or `?confirm=true` for one lead) confirms the removal.
- `POST /api/leads/{id}/verify?dryRun=true[&confirm=true]` returns the proposed changes (score before and after, what would be removed,
  what is kept because a person entered it) and changes nothing.

## Configuration

| Setting (env) | Meaning |
|---|---|
| `JENGOWORK_BASE_URL`, `JENGOWORK_API_KEY` | JengoWork API and an agent key (`tm_...`) that may create tasks |
| `HUNT_DISPATCH_BOARD_ID` | The board (list id) that receives the tasks: `POST {base}/api/v2/list/{board}/task` with `{name, description}` |
| `HUNT_DISPATCH_STATUS` | Optional lane for the new task so that jengo-agi picks it up at once (for example `todo`) |
| `HUNT_AGENT_SECRET` | Server secret from which the token of each hunt is derived. Empty = Find new leads and the agent endpoints answer 503 |
| `HUNT_ALLOWED_EMAILS` | Comma separated; empty = administrators only |
| `HUNT_MAX_PER_USER_PER_DAY`, `HUNT_MAX_VERIFY_PER_USER_PER_DAY` | default 5 and 20 |
| `HUNT_API_BASE_URL` | The URL the agent calls; default the public URL |
| `GOOGLE_PLACES_API_KEY` | Optional; without it no lead can reach 9 |

Without the dispatch settings or the secret, **Find new leads** answers `503` ("The Jengo agent dispatch is not configured on this server" or
"The Jengo agent token secret is not configured on this server") and the screen says so.

## Setting it up on an environment

1. Pick or create a JengoWork board for the hunts and make sure **jengo-agi monitors it** (a board that is not monitored never starts an
   agent). Put its id in `HUNT_DISPATCH_BOARD_ID`, and a lane name that jengo-agi polls in `HUNT_DISPATCH_STATUS`.
2. Generate a long random value for `HUNT_AGENT_SECRET`, put it in the environment's `.env` (keep a copy in the vault, project 12, credential
   `Clients hunt agent secret`) and restart the stack.
3. Set `JENGOWORK_BASE_URL` and `JENGOWORK_API_KEY`.
4. Run the smoke test: it checks that the agent endpoints refuse a call without the token and that the capabilities answer.

## Endpoints

User (JWT or API key): `GET /api/leads/hunt/capabilities`, `POST /api/leads/hunt`, `GET /api/leads/hunt`, `GET /api/leads/hunt/{id}`,
`POST /api/leads/hunt/{id}/cancel`, `POST /api/leads/verify`, `POST /api/leads/{id}/verify[?dryRun=true&confirm=true]`,
`GET /api/leads/{id}/evidence`. MCP: `start_lead_hunt`, `get_lead_hunt`.

Agent (the token of that hunt only; no login or API key opens them): `GET /api/agent/hunts/{id}`, `POST /api/agent/hunts/{id}/check`,
`POST /api/agent/hunts/{id}/candidates`, `POST /api/agent/hunts/{id}/complete`, `POST /api/agent/hunts/{id}/fail`. They only work while
the hunt is Pending or Running, and whatever the agent creates belongs to the user who started the hunt.

## Privacy

Only information that the business itself publishes is used. A mobile number or a personal e-mail address of a sole proprietor is
personal data: check the GDPR rules and the Do-Not-Call register before you call or mail. The agent prompt forbids sending anything to
anyone.

## Database migrations and the environments

| Migration | What |
|---|---|
| `20261119155650_AddLeadHunt` | `HuntJobs` and `HuntCandidates` (the first version; applied on test and acc) |
| `20261119164155_HuntActiveJobIndexAndConfirm` | `ConfirmClear` column and the unique index `IX_HuntJobs_OneActivePerUser`. Before the index is created, a SQL step marks all but the newest Pending or Running job of each user as Failed ("Superseded"), so a database that ran under the old race still migrates. Tested on a data copy with such rows. |
| `20261119170216_AddLeadHuntProvenance` | `Leads.HuntWrittenJson` (nullable; existing leads stay without provenance) |

How test and acc were handled: they applied `AddLeadHunt` (155650) and then `HuntActiveJobIndexAndConfirm`; their history was checked (no other hunt
migration row, no active jobs), and the clean-up SQL was added to the migration afterwards, which changes nothing for a database that already ran it.
An intermediate version of `AddLeadHunt` (id 20261119162503) was generated once and deployed to test, where it failed and rolled back before it was
recorded; it exists on no database, and its files are gone. Prod has none of these migrations yet and runs all three in order.

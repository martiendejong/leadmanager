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
Agent         ->  GET  /api/hunt-agent/{id}/brief          criteria, rules, knownDomains (the user's own), best score so far, remaining budget, stop
              ->  POST /api/hunt-agent/{id}/progress       what it is working on (display text only)
              ->  POST /api/hunt-agent/{id}/candidates     one researched company, with claims and quotes (one at a time)
              <-  outcome, verified score, bestScoreSoFar, stop
              ->  POST /api/hunt-agent/{id}/complete      (failed:true when it cannot work)
Leads screen  <-  progress via SignalR (HuntProgress) and a 4 s poll; found companies appear in the list as they arrive
```

The prompt is `docs/hunt/AGENT_PROMPT.md` (embedded resource, `prompt-version` on the first line). The criteria are
rendered into a delimited data block. A segment or region with a line break, markup characters or an instruction-like phrase
("ignore previous instructions", "system:", ...) is **refused with 400**; the accepted text is reduced to letters, digits, spaces and
`-,.&/()'`; segment at most 120 characters, region at most 80.

## The token of a hunt

Every hunt (and every agent review) mints its own token: 32 random bytes, sent to JengoWork once as part of the task text and stored on the job
only as a SHA-256 hash with an expiry of **3 hours**. The agent sends it as `X-Hunt-Token` on `/api/hunt-agent/{id}/...`. It opens **only that
hunt**; it is separate from the user login and from API keys (neither opens these endpoints, and the token opens nothing else); it is
**revoked** (hash removed) when the hunt completes, fails, is cancelled, goes silent for 45 minutes or its task could not be created. A wrong,
expired, revoked, missing or foreign token is always the same 401, whether or not the hunt exists. The agent endpoints are rate limited
(60 calls per minute per hunt). The token is the only credential in the task text: it holds no JengoWork key and no server secret, so a copy
of the task text is worthless once the hunt is over. The criteria text is quoted data in the task (`<hunt-data>`) and the prompt tells the
agent never to follow instructions inside it.

## Agent review of existing leads

**Ask agent to review** (Leads screen, up to 5 selected leads, `POST /api/leads/hunt/review`) creates the same kind of task for leads the user
already has. The agent researches each lead on the web and submits claims with `leadId`; the server fetches the lead's own stored website,
verifies the claims the same way and applies only verified facts. It never overwrites a value a person entered (it is listed in the lead's
note instead) and never clears anything. **Verify and rescore** stays deterministic and server-side, with no agent and no model.

## What the server decides

| Rule | Where |
|---|---|
| One active job per user (a unique index in the database) and a per-user lock around check-and-insert, so two requests at the same moment cannot both start | `HuntController.CreateJobAsync`, `IX_HuntJobs_OneActivePerUser` |
| At most `Hunt:MaxPerUserPerDay` hunts (default 5) and `Hunt:MaxVerifyPerUserPerDay` verify runs (default 20) per user per day | `HuntController` |
| Only administrators, or the users in `Hunt:AllowedEmails`, may start a hunt; verifying several leads at once needs the same access, verifying one lead does not | `HuntAccess` |
| `minScore` 4 to 9 (default 8), `targetCount` 1 to 10 (default 3), `maxCandidates` 5 to 40 (default 30) | `HuntController.Start` |
| One candidate of a hunt is checked at a time (a second submission gets 429); the stop conditions are checked again after the slow part, before anything is saved | `HuntLocks`, `HuntService.SubmitCandidateAsync` |
| A candidate may take 120 s and one site 60 s in total; one page fetch has a hard 20 s limit for the whole body, so a site that drips its answer cannot hold a worker | `SafePageFetcher`, `SitePages.Budget`, `HuntService.CandidateBudget` |
| A company that is already a lead of the user is reported back and not added; the brief lists the hunt owner's own domains for the dedupe, never another user's | `HuntAgentController.Brief`, `HuntService` |
| The ratchet: a candidate whose best reachable score cannot beat the best so far is marked `StoppedEarly` (still saved as a lead); the hunt stops at `targetCount` leads at the minimum score or at `maxCandidates` | `HuntService.SubmitCandidateAsync`, `ShouldStop` |
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

Regular expressions run over text from the web, so every one has a match timeout (`HuntRegex`, 1 s; a rule that times out finds nothing and the
other rules still run), the name pattern fails in linear time on one very long capitalised word (it was quadratic: 25,000 letters took 4 s), lines
longer than 2,000 characters are scanned in overlapping windows, sentence lookups search a bounded window, and a rule looks at its first 2,000
matches only. An XML sitemap is read through `SafeXml`: no DOCTYPE (a billion laughs document is refused unexpanded), no resolver, a size cap, a
depth cap and a node cap, checked before the document is loaded.

Tests: 250,000 nested `div`, `span`, `b`, `a`, `font`, `section`, `table` and `ul`, 100,000 unclosed `p`, `li`, `td` and `option`, and large flat
pages must finish in a few seconds without a crash (they run alone, after the other tests, so a busy machine cannot make them flaky).

## Why this lead, what it needs, how we can help

Every lead shows three things at the top of its panel (and a one-line why plus product chips in the list): **why it is a lead**, **what the business
needs** and **which of OUR products can help**. They live in `Lead.WhyLead` (max 1000 characters), `Lead.Needs` (max 1000) and
`Lead.ProductFitJson` (a list of `{product, reason, evidenceQuote, sourceUrl}`), and the lead DTO returns them as `whyLead`, `needs` and `productFit`.

**Decision: STRICT, nothing is stored unverified.** A need or a product fit is stored only when its quote occurs in the visible text of a
fetched page of the business itself (not in a testimonial, a script or a hidden element, at least 25 letters). An entry whose quote the server cannot find is
dropped, listed in the evidence note and the evidence viewer as "not stored", and never stored with an "unverified" flag. Product keys outside the catalog (including the keys of the first catalog) are refused for new input; a fit that an earlier version stored under an old key is still shown, labelled as a retired product;
each product once; at most 5 needs and 4 fits. The agent's `need` and `reason` texts are short interpretations that are only kept next to a verified quote, and are cleaned
and clamped like all agent text.

- **The product catalog** is one file: `src/LeadManager.Api/Agent/PRODUCTS.md` (an embedded resource that is also pasted into the agent prompt). The catalog is the one of Jengo Products (v2); the keys are `coachos`, `siteaudit`, `visit`,
  `werkteam`, `cloudws`, `custom`, `mail` and `meet` (`seogod` and `calendar` are in build and may not be proposed). Internal prices never appear: amounts are stripped from what the API and the agent prompt show; `ProductCatalog.Keys` lists them for validation and a test keeps the list and the file identical. Edit the file to change the catalog
  (and the list of keys with it). `GET /api/leads/products` returns the catalog to the app.
- **"No clear fit" is a valid answer**: `productFit: []` with `noClearFit: true` is stored as `[]` and shown as "No clear fit with our products". Not saying anything stores nothing and
  shows "Not assessed yet". A verified fit wins over the flag.
- **Why this lead is written by the server**, never by the agent: the score and its reasons (who can be reached), the sector and place, the one-person statement and the start year
  in the business's own words (verified quotes), and one verified `observation` quote. Nothing in it comes from the agent unverified; a `whyLead` field in the submission is ignored.
- **The agent submits** under `claims`: `observation` (quote), `needs` (need, quote), `productFit` (product, reason, quote) and `noClearFit`; see the prompt.
- **PATCH `/api/leads/{id}/profile`** accepts `whyLead`, `needs` and `productFit` (only catalog keys, a reason of 3 to 300 characters, an http(s) source) and `clearFields` may name
  `whyLead`, `needs` and `productFit`. A hunt records them as written by the server (`HuntWrittenJson`), like the other fields.
- **Verify and rescore never touches them**: the verifier has no code path for these three fields, with or without `confirm`.

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
| `JENGOWORK_BASE_URL`, `JENGOWORK_API_KEY` | JengoWork API and an agent key (`tm_...`) that may create tasks. Empty = Find new leads and Ask agent to review answer 503 "Agent hunting is not configured on this server" |
| `HUNT_DISPATCH_BOARD_ID` | Optional, default `105` (board "Jengo Clients", publicId zKaEpzhJGG): `POST {base}/api/v2/list/105/task` with `{name, description}`. The task lands in the first lane, "prioritization backlog" |
| `HUNT_DISPATCH_STATUS` | Optional lane for the new task |
| `HUNT_ALLOWED_EMAILS` | Comma separated; empty = administrators only |
| `HUNT_MAX_PER_USER_PER_DAY`, `HUNT_MAX_VERIFY_PER_USER_PER_DAY` | default 5 and 20 |
| `HUNT_API_BASE_URL` | The URL the agent calls; default the public URL |
| `GOOGLE_PLACES_API_KEY` | Optional; without it no lead can reach 9 |

There is no hunt secret to set: tokens are random per hunt. No OpenAI key is used by the hunt.

## Setting it up on an environment

1. Set `JENGOWORK_BASE_URL` and `JENGOWORK_API_KEY` in the environment's `.env` and restart the stack.
2. jengo-agi must monitor board 105 for the tasks to start an agent (not done by this app; until then a person can start the task by hand).
3. Run the smoke test: it checks that the agent endpoints refuse a call without the token and that the capabilities answer.

## Endpoints

User (JWT or API key): `GET /api/leads/hunt/capabilities`, `POST /api/leads/hunt`, `POST /api/leads/hunt/review`, `GET /api/leads/hunt`,
`GET /api/leads/hunt/{id}`, `POST /api/leads/hunt/{id}/cancel`, `GET /api/leads/products`, `POST /api/leads/verify`,
`POST /api/leads/{id}/verify[?dryRun=true&confirm=true]`, `GET /api/leads/{id}/evidence`. MCP: `start_lead_hunt`, `get_lead_hunt`.

Agent (the token of that hunt only, header `X-Hunt-Token`; no login or API key opens them): `GET /api/hunt-agent/{id}/brief`,
`POST /api/hunt-agent/{id}/progress`, `POST /api/hunt-agent/{id}/candidates`, `POST /api/hunt-agent/{id}/complete`. They only work while
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

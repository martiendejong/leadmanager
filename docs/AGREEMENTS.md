# Agreements: services, contracts and concepts per client

One view of what we have agreed with each client. Part of the client agreements programme
(`jengo-knowledge-private/proposals/2026-11-klantafspraken-sync/PROPOSAL.md`).

## Model

- **Service** (`ClientService`): what we deliver, for example website management, hosting, maintenance.
  Status Proposed, Active, Paused, Ended. Invoicing stays in Fedha; the monthly amount is informative.
- **Document** (`ClientDocument`): quote, order, agreement, DPA, NDA. A **concept** is a document with status
  Draft. A **contract** is a Sent, Signed, Rejected, Expired or Cancelled agreement-like document.
  A document can point to a service (`serviceId`), has a `version`, and a `sourceRef`.
- **SourceRef**: stable id from the system that reported the item (for example `mail:Sent/476`). Posting the
  same sourceRef again updates the existing service or document instead of creating a duplicate, so imports
  are safe to repeat.
- **Duplicate files**: the same file (SHA-256) uploaded again to the same document is linked once and logged.
  It is not a conflict and needs no approval.

## API

| Call | What |
|---|---|
| `GET /api/agreements?service=WebsiteManagement` | per client: services, contracts, concepts; optional filter on a running service |
| `GET/POST /api/clients/{id}/services` | list and add services (POST with a known sourceRef updates) |
| `PUT/DELETE /api/services/{id}` | change or remove a service (delete is closed to API keys) |
| `POST /api/clients/{id}/documents` | documents, now with `serviceId`, `version`, `sourceRef` |

All of these are available to API keys and MCP clients like the rest of the CRM, under the same ownership rules.

## Rules for sources

Mail, Jengo Meet, JengoWork and Fedha never change this data silently. Changes proposed from another source go
through the proposal and approval flow of Jengo Knowledge (tasks 8193 to 8196); Clients only stores the
accepted outcome. Conflicting sources are resolved there, in company mode: every conflict ends in a decision or a
recorded reconstruction.

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

## Billing terms and invoices

The agreement per service now says how it is invoiced, so Jengo can answer "make an invoice for X" from this data.

- **Terms on the service:** `billingInterval` (Monthly, Quarterly, Yearly, OneOff, Hourly), `billingAmount` (per period, excl. VAT;
  when empty `monthlyAmount` is used), `hourlyRate`, `vatRate` (default 21), `paidUntil` (for example a year paid ahead), `freeUntil` with
  `freeReason` (for example a free year as part of a deal), `invoiceNote`. A PUT or POST that does not mention these leaves them as they are.
- **Invoices (`ClientInvoice`):** number, date, inclusive period, amount excl. VAT, VAT rate, status (Draft, Sent, Paid, Cancelled), sent to,
  service. `GET/POST /api/clients/{id}/invoices`, `PUT/DELETE /api/invoices/{id}` (delete is closed to API keys; cancel instead). The same
  number or `sourceRef` (for example `fedha:2026-15`) updates the existing invoice. The books stay in Fedha; this only ties what was
  agreed to what was invoiced.
- **What is due:** `GET /api/invoicing/due?clientId=&state=&asOf=` per active service: the covered-through day is the latest of
  `paidUntil`, `freeUntil` and the period end of Sent/Paid invoices; the next period starts the day after (or at the start date), is
  invoiced in advance, and its amount comes from the terms. States: Due (period has started, with the number of open periods), Upcoming
  (starts within 30 days), Covered (prepaid or free beyond that), NeedsInput (hourly, no amount or no start date), Nothing (not active,
  ended, one-off already invoiced). A last period that is cut short by the end date is prorated by days. Draft and Cancelled invoices do not
  cover a period.
- **UI:** per client "Services and agreements" (Edit agreement) and "Invoices" (to invoice next, record invoice); the Agreements page shows
  "Invoicing due" across all clients.
- Nothing here sends an invoice. Creating the invoice in Fedha and sending it stays a deliberate step.

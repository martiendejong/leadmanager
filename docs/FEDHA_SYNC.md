# Fedha invoice sync (task 8360)

Clients keeps a read-only copy of the Fedha invoices so `/agreements` and the client page show, per client, what is a
concept, what was sent and is still open, and what is paid. Fedha stays the owner of invoicing: nothing is written back.

## What it does

- `POST /api/fedha-sync/run` (or the "Sync now" button on `/agreements`) and a daily job at 05:30 UTC read `GET {Fedha}/invoices`
  with a read-only `X-API-Key` and upsert a `ClientInvoice` per Fedha invoice (`SourceRef = fedha:<number>`). Reruns and the
  earlier one-off loader (`load_billing_terms.py`) do not create duplicates.
- Status mapping: Fedha Draft is Draft (never shown as sent), Sent and Overdue are Sent (with the open amount), Paid is Paid,
  Cancelled is Cancelled. A Sent or Overdue invoice with nothing outstanding becomes Paid (rounding residue).
- **Send unknown:** Fedha only stamps `lastSentAt` when an invoice is sent through its send action. A Sent, Overdue or Paid invoice
  without that date is flagged `sendUnknown`: the books say sent, but the sending is not proven.
- Client match: manual link (`POST /api/fedha-sync/links`) first, then exact name, then a built-in alias
  (Persch Art International = Persch Art, Giethoorn Hospitality = Port of Giethoorn). Contacts without a client are
  listed under "Fedha contacts without a client" and can be linked from the page. **Clients are never created automatically.**
- A monthly invoice is tied to its service and period only when exactly one active monthly service name appears in the
  description and the description holds one Dutch month and year ("Hosting en beheer weerzienmara.nl · november 2026").
  Links made by hand are never overwritten. Only Sent and Paid invoices count as covering a period in `/api/invoicing/due`.

## Configuration (environment, never in git)

| Variable | Meaning |
|---|---|
| `FEDHA_API_KEY` | read-only Fedha key (vault project 9). Empty = sync off |
| `FEDHA_BASE_URL` | default `https://fedha.martiendejong.nl/api` |
| `FEDHA_SYNC_USER_EMAIL` | Clients user whose clients receive the invoices (daily job) |

## Tables

`FedhaContactLinks` (manual links), `FedhaSyncRuns` (run log with the unmatched list), and four columns on `ClientInvoices`
(`SentAt`, `SendUnknown`, `OutstandingAmount`, `SyncedAt`). Migration `FedhaBillingSync`, additive.

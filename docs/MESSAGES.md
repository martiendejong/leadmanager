# Messages to leads

A lead can have messages: a text for an e-mail, LinkedIn, WhatsApp or a phone call note. **The app never sends anything.** A message is text. You copy it or open your mail app, send it yourself, and then tell the app so with "Mark as sent".

## The rules

| Action | What changes |
|---|---|
| Save draft, edit draft, delete draft, bulk drafts, prefill | Nothing about the lead. No status change, no activity. |
| Mark as sent | Needs a confirmation (`confirmed: true`, in the UI "I sent this message myself"). Sets `sentAt`, writes a lead activity (`EmailSent`, `Called` or a note), sets `Lead.LastContactedAt`. A lead that is **New** becomes **Contacted** (a second activity records it). A lead in any later status (Contacted, Qualified, ProposalSent, Won, Lost) keeps its status: it is never moved back. |
| A sent message | Is history: it cannot be edited, deleted or marked a second time. |

Everything is scoped to the owner of the lead. Another user gets 404 on every endpoint (list, prefill, edit, delete, mark as sent, mailto, bulk).

## Where it is in the app

* **Lead detail** (side panel and lead page): the **Message** section. Channel, product template, subject (e-mail only), text, **Save draft**, **Copy**, **Open in mail app** (only for a valid published address), **Mark as sent** (with confirmation) and the history of that lead. Warnings show above the form: a note that says not to contact the lead, a missing address or phone number, a lead that is already past New.
* **Messages** (nav item): all drafts and sent messages, filter by status, channel and lead name; edit or delete a draft, mark as sent, open the lead.
* **Today, "Next to contact"**: the best leads that are still New and never contacted (score 6 or higher) with why, needs and product chips. **Write message** opens the lead at the Message section.
* **Settings, Message templates**: a template per channel, and per product of the catalog (default per channel when a product has none). Dutch text, placeholders below. Built-in templates exist in Dutch and English and are used until you save your own.
* **Leads list**: a Pipeline column and a Last contact column (sortable), and the filters **New** and **Contacted**. **Draft messages** on a selection creates one draft per lead (drafts only, max 50, a lead that already has a draft for that channel is skipped).

## The prefill

Deterministic, no AI and no key. The text is built from what is stored on the lead:

| Placeholder | Value |
|---|---|
| `{name}` | the business name |
| `{owner}` | the owner's first name; an unknown owner turns "Hoi {owner}," into "Goedendag," (English: "Hello,") |
| `{why}` | the verified quote from the observation (the business's own words, never the internal score text) |
| `{need}` | the quote of the first need |
| `{product}` | the name of the chosen product (the best verified fit, or the product chosen in the section) |
| `{reason}` | the reason stored with that fit |

A line whose placeholder has no value is **left out**. Nothing is invented: a lead with nothing stored gets a short neutral message. Language: Dutch for a `.nl` website or a Dutch phone number, otherwise English. Order of templates: your template for the channel and product, your default for the channel, the built-in one. A template with an unknown placeholder (a typo such as `{nam}`) is refused when it is saved.

## Safety

* Subject: one line only (control characters and line breaks become spaces), at most 200 characters. Body at most 5000 characters, control characters removed.
* `mailto:` links are built by the server: strict address check (a comma, `?`, space or line break in the address gives no link), subject and body percent-encoded, body line breaks as CRLF, at most about 1900 characters (longer: use Copy). An `&` or `=` in the text cannot start a new parameter.
* "Do not contact": there is no field for it, so the notes of the lead are read for phrases such as "niet bellen", "do not contact", "geen acquisitie", "opt out". It is a warning only.

## API

All under `/api`, signed in or with an API key (not the templates).

| Endpoint | |
|---|---|
| `GET /leads/{id}/messages` | history of one lead |
| `GET /leads/{id}/messages/prefill?channel=&product=` | suggested subject and body, warnings, address |
| `POST /leads/{id}/messages` | save a draft `{channel, subject, body}` |
| `GET /messages?status=&channel=&leadId=&q=&page=` | overview |
| `PUT /messages/{id}` / `DELETE /messages/{id}` | edit or delete a draft |
| `POST /messages/{id}/sent` `{confirmed:true}` | mark as sent |
| `GET /messages/{id}/mailto` | the mail link (e-mail only, valid address) |
| `POST /messages/bulk-drafts` `{leadIds, channel, product?}` | drafts only |
| `GET /messages/queue?take=&minScore=` | next to contact |
| `GET/PUT /message-templates`, `DELETE /message-templates/{id}` | templates (signed in only) |
| `GET /leads?pipelineStatus=New\|Contacted&sortBy=lastContactedAt` | lead list filter and sort |

MCP tools: `get_message_prefill`, `list_messages`, `create_message_draft`, `update_message_draft`, `mark_message_sent` (see MCP.md). `mark_message_sent` needs the boolean `true`; a string or a missing flag is refused.

## Database

Migration `AddLeadMessages`: table `LeadMessages`, table `MessageTemplates` (unique per user, channel and product key) and column `Leads.LastContactedAt`. It is a follow-up to the hunt migrations: an environment needs them in order. Prod has none of them yet.

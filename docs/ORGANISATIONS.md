# Organisations

Leads are divided per organisation of ours. A person sees and edits the leads of the organisations they are a member of, and
nothing else. This replaces the old rule "a lead belongs to the person who imported it".

## The organisations

Created by the migration (fixed ids, see `Data/OrganizationSeed.cs`): Prospergenics, Martien de Jong, Port of Giethoorn, Karibu Mara,
Rena Farm system, MaaLink Ride, Weerzien Mara. More are created in the app by an application admin (Settings > Organisations) or with
`POST /api/organizations`.

## What belongs to an organisation

* A lead has exactly one organisation (`Lead.OrganizationId`, never empty).
* Everything that hangs off the lead follows it: notes, activities, messages and drafts, demo ideas, contacts, interactions, next
  actions, links, documents and the hunt history. A lead of an organisation you are not in does not exist for you: a lookup by id is a 404,
  a list is empty, a write is a 404.
* A hunt (`HuntJob`) has an organisation too. The leads it finds are created in that organisation, and "already known" is checked within it.
  The same business can be a lead of two organisations (the duplicate rule is per organisation: name plus website plus organisation).
* Clients, projects and documents of clients are not part of this: they keep the per-person rule they had.
* API keys act as their user and follow the same membership rules. A key can read `GET /api/organizations` but cannot change members.

## Members

`OrganizationMember` (organisation, user, e-mail, role). The role is `admin` (manages the members) or `member`.

* An organisation admin adds a member by e-mail address, changes a role and removes a member (Settings > Organisations, or
  `POST/PUT/DELETE /api/organizations/{id}/members`). An organisation needs at least one admin.
* A person without an account yet gets a **pending** membership by e-mail. It is attached to the user at their first login (password
  login, `/api/auth/me` or the Jengo IAM single sign-on) and gives no access before that.
* A person who is in no organisation sees nothing and is told so (a banner in the app, an explicit error when they try to create a lead).

## Seeding people

`deploy/org_members.json` is embedded in the app and applied at start:

```json
{ "members": [ { "name": "Martien", "email": "info@martiendejong.nl", "role": "admin", "orgs": ["Prospergenics", "Karibu Mara"] } ] }
```

* Organisations by name or slug. `role` is `admin` or `member` (default `member`).
* Each (organisation, e-mail) pair is applied **once** and remembered (`OrganizationSeedLog`): a member removed in the app is not put
  back by the next start. An existing membership keeps its role. A pair that names an unknown organisation is skipped with a warning.
* To add someone: add them to the file, deploy. `Organizations:MembersFile` (environment `Organizations__MembersFile`) points at another
  file instead of the embedded one.

## Migration of existing data

`AddOrganizations` creates the tables and inserts the seven organisations. Every existing lead and hunt first gets Prospergenics (the column is
NOT NULL), then leads whose city is Giethoorn (case and spaces ignored) are moved to Port of Giethoorn; hunts whose region or segment mention
Giethoorn likewise. Adding the foreign keys makes SQLite rebuild the `Leads` and `HuntJobs` tables, so the migration was rehearsed on a copy of the
production backup (counts before and after in the pull request). The unique index on leads changes from (name, website, importer) to
(name, website, organisation).

## Where the organisation shows up

* **Create**: a new lead, a CSV import, a hunt, `POST /api/leads/search/import` and the CRM import take `organizationId`. Without it the only
  organisation of the person is used; with several the request is refused (400) and the app asks (the picker defaults to the filter, then the last
  organisation used). A person must be a member of the target.
* **Move**: `PUT /api/leads/{id}/organization` (or `organizationId` in the lead update). The person must be a member of both. A merge of leads of
  two organisations is refused.
* **Assign**: a lead can only be assigned to a member of its organisation.
* **Filter**: `organizationId` (one or more ids or slugs, comma separated) on `GET /api/leads`, the export, stats, analytics, messages, the
  queue, Today, Actions and the dashboard. An organisation the person is not in matches nothing (it never widens to "all"). In the app the filter is
  a row of chips above the lists ("All my organisations" by default), remembered per user in the browser.
* **Badge**: every lead row, pipeline card, queue item, message, action, slide-over and lead page shows the organisation.
* **MCP**: `list_organizations`, and an optional `organization` (id, slug or name) on `search_leads`, `create_lead`, `list_messages` and
  `next_actions_today`. `get_lead` returns the organisation.

## Not covered

Clients have no organisation, so with an organisation filter the Today list, Actions and the dashboard show only lead figures and lead actions.
Stale-lead reminders still go to the person who imported the lead.

"""Smoke test for a running Jengo Clients instance (local, test, acc or prod).

Checks that the frontend and the API are served correctly and that the CRM works end to end, then
removes what it created. Run it after every deploy:

    set LM_ADMIN_PASSWORD=...
    python deploy/smoke_test.py --base https://clients-test.martiendejong.nl --admin-email admin@example.nl

The password is read from LM_ADMIN_PASSWORD and never printed. Exit code 0 = everything passed.
"""
import argparse
import os
import re
import sys
import datetime as dt
import time
import uuid
from urllib.parse import quote

import requests

results = []


def check(ok: bool, message: str) -> None:
    results.append(ok)
    print(("PASS " if ok else "FAIL ") + message)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base", required=True, help="e.g. https://clients-test.martiendejong.nl")
    ap.add_argument("--admin-email", required=True)
    args = ap.parse_args()

    password = os.environ.get("LM_ADMIN_PASSWORD")
    if not password:
        print("Set LM_ADMIN_PASSWORD first.", file=sys.stderr)
        return 2

    base = args.base.rstrip("/")
    s = requests.Session()
    s.headers["User-Agent"] = "jengo-clients-smoke-test"
    get = lambda path, **kw: s.get(base + path, timeout=30, **kw)

    # --- frontend and routing ---------------------------------------------------------------
    r = get("/")
    check(r.status_code == 200 and "text/html" in r.headers.get("content-type", ""), "/ serves the app")
    check("no-store" in r.headers.get("cache-control", "") or "no-cache" in r.headers.get("cache-control", ""),
          "index.html is not cached")
    r = get("/leads/00000000-0000-0000-0000-000000000000")
    check(r.status_code == 200 and "text/html" in r.headers.get("content-type", ""), "client-side route falls back to the app")

    # The built bundle must be served as JavaScript, not swallowed by the SPA fallback.
    html = get("/").text
    asset = next((p for p in html.split('"') if p.startswith("/assets/") and p.endswith(".js")), None)
    if asset:
        ar = get(asset)
        check(ar.status_code == 200 and "javascript" in ar.headers.get("content-type", ""), f"{asset} is served as JavaScript")
        check("localhost:5000" not in ar.text, "bundle calls the same host (no localhost API URL baked in)")
        check("max-age" in ar.headers.get("cache-control", "") or "immutable" in ar.headers.get("cache-control", "")
              or "no-store" not in ar.headers.get("cache-control", ""), "hashed asset is cacheable")
    else:
        check(False, "index.html references a JavaScript bundle")

    # --- API basics -------------------------------------------------------------------------
    r = get("/api/health")
    check(r.status_code == 200 and r.json().get("status") == "ok", f"/api/health ok (version {r.json().get('version') if r.ok else '?'})")
    check(get("/api/does-not-exist").status_code == 404, "unknown API path is a 404, not the app")
    check(get("/api/leads").status_code == 401, "API needs authentication")

    # Log in with Jengo IAM: the app builds the authorize URL for ITS callback, the IAM accepts that callback
    # (302 to its login page) and refuses one that is not registered (400). Only meaningful for a public
    # environment: a local run has a localhost callback that no IAM knows.
    r = get("/api/auth/sso/start")
    start = r.json().get("authorizeUrl") if r.status_code == 200 else None
    mine = quote(base + "/api/auth/sso/callback", safe="")
    check(bool(start) and f"redirect_uri={mine}" in start, "SSO start builds the authorize URL with this host's callback")
    if start and not re.match(r"https?://(localhost|127\.0\.0\.1)", base):
        r = s.get(start, allow_redirects=False, timeout=30)
        check(r.status_code == 302 and "login" in r.headers.get("location", "").lower(),
              f"the IAM ({start.split('/connect')[0]}) accepts the callback and sends to its login page")
        r = s.get(re.sub(r"redirect_uri=[^&]+", "redirect_uri=" + quote("https://evil.example/cb", safe=""), start),
                  allow_redirects=False, timeout=30)
        check(r.status_code == 400, "the IAM refuses a callback that is not registered")
    elif start:
        print("SKIP the IAM checks for a local run")

    # The SPA fallback must never answer for the MCP endpoint or OAuth discovery with the app's HTML page.
    r = get("/mcp")
    check(r.status_code == 401 and "text/html" not in r.headers.get("content-type", ""), "GET /mcp without a token is a 401, not the app")
    r = get("/.well-known/oauth-protected-resource")
    check(r.status_code == 404 and "text/html" not in r.headers.get("content-type", ""), "OAuth discovery path is a 404, not the app")
    r = s.post(base + "/mcp", json={"jsonrpc": "2.0", "id": 1, "method": "initialize"}, timeout=30)
    check(r.status_code == 401, "POST /mcp without a token is a 401")

    # --- login ------------------------------------------------------------------------------
    r = s.post(base + "/api/auth/login", json={"email": args.admin_email, "password": password}, timeout=30)
    check(r.status_code == 200, "admin can log in")
    if r.status_code != 200:
        print("Cannot continue without a login.")
        return 1
    token = r.json().get("token")
    jwt = {"Authorization": f"Bearer {token}"}
    check(s.post(base + "/api/auth/login", json={"email": args.admin_email, "password": "definitely-wrong"}, timeout=30).status_code in (400, 401),
          "wrong password is refused")

    # --- CRM end to end, then clean up ------------------------------------------------------
    stamp = str(int(time.time()))
    lead_id = key_id = client_id = None
    try:
        r = s.post(base + "/api/leads", headers=jwt, timeout=30, json={
            "name": f"Smoke test {stamp}", "website": f"https://smoke-{stamp}.example", "sector": "test",
            "city": "test", "phone": "0000", "companyEmail": f"smoke@smoke-{stamp}.example", "source": "smoke-test"})
        check(r.status_code == 201, "create a lead")
        # The id comes from the create response: the list is paged (50), so on a real database the new lead
        # is not necessarily on the first page.
        lead_id = r.json().get("id") if r.status_code == 201 else None
        check(lead_id is not None and s.get(f"{base}/api/leads/{lead_id}", headers=jwt, timeout=30).status_code == 200,
              "the new lead can be read back")

        r = s.post(base + "/api/api-keys", headers=jwt, json={"name": f"smoke {stamp}"}, timeout=30)
        check(r.status_code == 200 and r.json().get("key", "").startswith("lm_"), "mint an API key")
        key = r.json()["key"]
        key_id = r.json()["id"]
        k = {"X-Api-Key": key}

        if lead_id:
            r = s.post(f"{base}/api/leads/{lead_id}/interactions", headers=k, timeout=30,
                       json={"kind": "Call", "summary": "smoke call", "outcome": "no answer"})
            check(r.status_code == 201, "API key logs a call")
            r = s.get(f"{base}/api/leads/{lead_id}/timeline", headers=k, timeout=30)
            check(r.status_code == 200 and any(i["summary"] == "smoke call" for i in r.json()), "the call is in the timeline")
            r = s.post(f"{base}/api/leads/{lead_id}/next-actions", headers=k, timeout=30,
                       json={"title": "smoke follow-up", "dueAt": "2020-01-01T09:00:00Z"})
            check(r.status_code == 201, "plan a next action")
            r = s.get(base + "/api/next-actions/today", headers=k, timeout=30)
            check(r.status_code == 200 and any(i["title"] == "smoke follow-up" and i["overdue"] for i in r.json()), "overdue action is on the Vandaag list")
            r = s.get(f"{base}/api/leads/{lead_id}/next-actions", headers=k, timeout=30)
            check("2020-01-01T09:00:00Z" in r.text, "timestamps carry a zone (Z)")
            r = s.get(base + "/api/crm/find", headers=k, params={"domain": f"smoke-{stamp}.example"}, timeout=30)
            check(r.status_code == 200 and len(r.json()) == 1, "find the lead by domain")

        # MCP: the same key as Bearer token. Initialize, list the tools, make (and later remove) a lead.
        mcp = {"Authorization": f"Bearer {key}", "Content-Type": "application/json"}
        rpc = lambda method, params=None, rid=1: s.post(base + "/mcp", headers=mcp, timeout=30,
                                                         json={"jsonrpc": "2.0", "id": rid, "method": method, "params": params})
        r = rpc("initialize", {"protocolVersion": "2025-03-26"})
        check(r.status_code == 200 and r.json().get("result", {}).get("serverInfo", {}).get("name") == "jengo-clients", "MCP initialize")
        r = rpc("tools/list")
        names = [t["name"] for t in r.json().get("result", {}).get("tools", [])] if r.status_code == 200 else []
        check({"search_leads", "get_lead", "create_lead", "log_interaction", "add_next_action", "next_actions_today",
               "link_item", "find_by_email_or_domain", "list_clients", "create_client", "set_client_priority",
               "create_project", "list_actions", "list_documents", "add_document"} <= set(names), f"MCP lists the planned tools ({len(names)})")
        r = rpc("tools/call", {"name": "create_lead", "arguments": {"name": f"Smoke MCP {stamp}", "website": f"https://smoke-mcp-{stamp}.example"}}, 2)
        mcp_text = r.json()["result"]["content"][0]["text"] if r.status_code == 200 else ""
        mcp_lead = None
        try:
            import json as _json
            mcp_lead = _json.loads(mcp_text).get("id")
        except Exception:
            pass
        check(bool(mcp_lead), "MCP create_lead")
        check(s.post(base + "/mcp", headers=mcp, timeout=30,
                     json=[{"jsonrpc": "2.0", "id": i, "method": "ping"} for i in range(21)]).status_code == 400, "MCP refuses a batch of 21")
        if mcp_lead:
            s.delete(f"{base}/api/leads/{mcp_lead}", headers=jwt, timeout=30)

        # Clients with projects and history, priority score, actions overview and the calendar feed.
        # The id comes from the create response (lists are paged), and the client is deleted in the finally block.
        r = s.post(base + "/api/clients", headers=jwt, timeout=30, json={
            "name": f"Smoke client {stamp}", "website": f"smoke-client-{stamp}.example",
            "projects": [{"name": "Smoke project"}]})
        check(r.status_code == 201, "create a client with a project")
        client = r.json() if r.status_code == 201 else {}
        client_id = client.get("id")
        if client_id:
            project_id = (client.get("projects") or [{}])[0].get("id")
            check(s.post(base + "/api/clients", headers=jwt, timeout=30, json={"name": f"smoke client {stamp}"}).status_code == 409,
                  "the same client name twice is refused")
            r = s.put(f"{base}/api/clients/{client_id}/priority", headers=jwt, timeout=30, json={"value": 8, "urgency": 5, "effort": 2})
            check(r.status_code == 200 and r.json().get("score") == 10, "priority score = value x urgency / effort squared")
            r = s.post(f"{base}/api/clients/{client_id}/interactions", headers=jwt, timeout=30,
                       json={"kind": "Note", "summary": "smoke project note", "projectId": project_id})
            check(r.status_code == 201, "log a note on a project")
            r = s.get(f"{base}/api/clients/{client_id}/timeline", headers=jwt, params={"projectId": project_id}, timeout=30)
            check(r.status_code == 200 and len(r.json()) == 1 and r.json()[0].get("projectName") == "Smoke project",
                  "project history shows its own note")
            due = (dt.datetime.now(dt.timezone.utc) + dt.timedelta(days=2)).strftime("%Y-%m-%dT%H:%M:%SZ")
            # Dossier: quote -> agreement chain with a file, downloaded again, removed with the client.
            r = s.post(f"{base}/api/clients/{client_id}/documents", headers=jwt, timeout=30,
                       json={"kind": "Quote", "title": "Smoke offerte", "amount": 100})
            check(r.status_code == 201, "add a quote to the dossier")
            quote_id = r.json().get("id") if r.status_code == 201 else None
            if quote_id:
                r = s.post(f"{base}/api/clients/{client_id}/documents", headers=jwt, timeout=30,
                           json={"kind": "Dpa", "title": "Smoke DPA", "status": "Signed", "predecessorId": quote_id})
                check(r.status_code == 201 and r.json().get("predecessorId") == quote_id and r.json().get("signedAt"), "chain a signed agreement to the quote")
                r = s.post(f"{base}/api/documents/{quote_id}/files", headers={"Authorization": jwt["Authorization"]}, timeout=60,
                           files={"file": ("smoke.txt", b"smoke dossier file", "text/plain")})
                check(r.status_code == 201, "upload a file to a document")
                file_id = r.json().get("id") if r.status_code == 201 else None
                if file_id:
                    dr = s.get(f"{base}/api/documents/files/{file_id}", headers=jwt, timeout=30)
                    check(dr.status_code == 200 and dr.content == b"smoke dossier file" and dr.headers.get("x-content-type-options") == "nosniff",
                          "download returns the same bytes as a nosniff attachment")
                check(s.post(f"{base}/api/documents/{quote_id}/files", headers={"Authorization": jwt["Authorization"]}, timeout=60,
                             files={"file": ("x.exe", b"MZ", "application/octet-stream")}).status_code == 400, "an .exe upload is refused")
            r = s.post(f"{base}/api/clients/{client_id}/next-actions", headers=jwt, timeout=30, json={"title": "smoke actie", "dueAt": due})
            check(r.status_code == 201, "plan a next action on the client")
            r = s.get(base + "/api/next-actions", headers=jwt, timeout=30)
            check(r.status_code == 200 and any(a["title"] == "smoke actie" for a in r.json()), "the action shows in the Acties overview")
            r = s.get(base + "/api/calendar/feed", headers=jwt, timeout=30)
            feed_url = r.json().get("url") if r.status_code == 200 else None
            check(bool(feed_url) and feed_url.startswith(base), "calendar feed link is on the public host")
            if feed_url:
                fr = requests.get(feed_url, timeout=30)  # anonymous: the token is the credential
                check(fr.status_code == 200 and "text/calendar" in fr.headers.get("content-type", "") and "smoke actie" in fr.text,
                      "calendar feed serves the action without login")
                check(requests.get(feed_url.replace(".ics", "x.ics"), timeout=30).status_code == 404, "a wrong feed token gives 404")

        # Lead hunt and verify: a hunt is run by a Jengo agent. Without the dispatch configuration the start must say so (503), never fail
        # silently. Only harmless calls are made here, so a smoke test never starts an agent or a paid job.
        r = s.get(base + "/api/leads/hunt/capabilities", headers=jwt, timeout=30)
        caps = r.json() if r.status_code == 200 else {}
        check(r.status_code == 200 and "huntAvailable" in caps, "lead hunt capabilities answer")
        check(s.get(base + "/api/leads/hunt", headers=jwt, timeout=30).status_code == 200, "recent hunts list answers")
        if caps.get("huntAvailable") is False:
            r = s.post(base + "/api/leads/hunt", headers=jwt, timeout=30, json={"segment": "smoke test segment"})
            check(r.status_code == 503 and "Agent hunting is not configured on this server" in r.text, "starting a hunt without the JengoWork connection says so (503)")
            r = s.post(base + "/api/leads/hunt/review", headers=jwt, timeout=30, json={"ids": []})
            check(r.status_code == 503, "asking an agent to review without the JengoWork connection says so (503)")
        else:
            check(s.post(base + "/api/leads/hunt", headers=jwt, timeout=30, json={"segment": ""}).status_code == 400, "a hunt without a segment is refused")
        check(s.post(base + "/api/leads/verify", headers=jwt, timeout=30, json={"ids": []}).status_code == 400, "verify without leads is refused")

        # Why this lead, what it needs, which of our products can help: the catalog, the patch, the lead dto and the clear
        r = s.get(base + "/api/leads/products", headers=jwt, timeout=30)
        keys = sorted(p.get("key") for p in r.json()) if r.status_code == 200 else []
        check(keys == sorted(["coachos", "siteaudit", "visit", "werkteam", "cloudws", "custom", "mail", "meet", "karibumara", "farmmgmt", "maaride"]), "the product catalog lists our 11 products (Jengo Products + 3 Kenya products)")
        if lead_id:
            patch = f"{base}/api/leads/{lead_id}/profile"
            fit = [{"product": "SiteAudit", "reason": "Booking only by phone, a new site with online booking helps", "evidenceQuote": "Afspraken alleen per telefoon", "sourceUrl": "https://example.nl/contact"}]
            r = s.patch(patch, headers=jwt, timeout=30, json={"whyLead": "Smoke test why", "needs": "Smoke need", "productFit": fit})
            body = r.json() if r.status_code == 200 else {}
            check(r.status_code == 200 and body.get("whyLead") == "Smoke test why" and body.get("productFit", [{}])[0].get("product") == "siteaudit",
                  "a lead takes why, needs and product fit (the product key is canonical)")
            check(s.patch(patch, headers=jwt, timeout=30, json={"productFit": [{"product": "TRIP", "reason": "a product of the first catalog"}]}).status_code == 400,
                  "a product that is not in the catalog is refused")
            r = s.get(f"{base}/api/leads/{lead_id}", headers=jwt, timeout=30)
            check(r.status_code == 200 and r.json().get("needs") == "Smoke need" and len(r.json().get("productFit") or []) == 1, "the lead dto returns needs and product fit")
            r = s.patch(patch, headers=jwt, timeout=30, json={"clearFields": ["whyLead", "needs", "productFit"]})
            check(r.status_code == 200 and r.json().get("whyLead") is None and r.json().get("productFit") is None, "clearFields resets why, needs and product fit")
        # Messages: the app never sends; a draft changes nothing; "Mark as sent" needs a confirmation and moves New to Contacted only
        r = s.post(base + "/api/leads", headers=jwt, timeout=30, json={"name": f"Smoke msg {stamp}", "website": f"https://smoke-msg-{stamp}.example", "sector": "test", "city": "test", "phone": "0000", "companyEmail": f"smoke@smoke-msg-{stamp}.example", "source": "smoke-test"})
        msg_lead = r.json().get("id") if r.status_code == 201 else None
        if msg_lead:
            r = s.get(f"{base}/api/leads/{msg_lead}/messages/prefill?channel=Email", headers=jwt, timeout=30)
            check(r.status_code == 200 and "body" in r.json() and "{" not in r.json().get("body", "{"), "message prefill answers with a filled in text")
            check(s.get(f"{base}/api/leads/{msg_lead}/messages/prefill?channel=Fax", headers=jwt, timeout=30).status_code == 400, "prefill with an unknown channel is refused")
            r = s.post(f"{base}/api/leads/{msg_lead}/messages", headers=jwt, timeout=30, json={"channel": "Email", "subject": "Smoke\r\nBcc: x@y.nl", "body": "Smoke test message"})
            msg = r.json() if r.status_code == 201 else {}
            check(r.status_code == 201 and "\n" not in msg.get("subject", "x\n") and msg.get("status") == "Draft", "a draft is saved (subject is one line)")
            lead_now = s.get(f"{base}/api/leads/{msg_lead}", headers=jwt, timeout=30).json()
            check(lead_now.get("pipelineStatus") == "New", f"saving a draft does not change the lead status")
            mid = msg.get("id")
            if mid:
                r = s.post(f"{base}/api/messages/{mid}/sent", headers=jwt, timeout=30, json={})
                check(r.status_code == 400 and "confirmation_required" in r.text, "mark as sent without a confirmation is refused")
                check(s.get(f"{base}/api/leads/{msg_lead}", headers=jwt, timeout=30).json().get("pipelineStatus") == "New", "the status is still New after a refused mark as sent")
                r = s.post(f"{base}/api/messages/{mid}/sent", headers=jwt, timeout=30, json={"confirmed": True})
                check(r.status_code == 200 and r.json().get("pipelineStatus") == "Contacted", "mark as sent with a confirmation moves New to Contacted")
                check(s.post(f"{base}/api/messages/{mid}/sent", headers=jwt, timeout=30, json={"confirmed": True}).status_code == 409, "a message cannot be marked as sent twice")
                check(s.put(f"{base}/api/messages/{mid}", headers=jwt, timeout=30, json={"body": "changed"}).status_code == 409, "a sent message cannot be edited")
            r = s.get(base + "/api/messages?status=Sent", headers=jwt, timeout=30)
            check(r.status_code == 200 and r.json().get("total", 0) >= 1, "the messages overview lists the sent message")
            r = s.get(base + "/api/leads?pipelineStatus=Contacted", headers=jwt, timeout=30)
            check(r.status_code == 200 and any(i.get("id") == msg_lead for i in r.json().get("items", [])), "the lead list filters on Contacted and shows the lead")
            r = s.post(base + "/api/messages/bulk-drafts", headers=jwt, timeout=30, json={"leadIds": [msg_lead], "channel": "WhatsApp"})
            check(r.status_code == 200 and r.json().get("created") == 1, "bulk drafts create a draft")
            check(s.get(base + "/api/messages/queue", headers=jwt, timeout=30).status_code == 200, "the next to contact queue answers")
            # AI compose (regenerate with an instruction, demo ideas). No key is needed here: without one the endpoints must answer a clean 503, and a real call is never made.
            r = s.get(base + "/api/ai/status", headers=jwt, timeout=30)
            ai_on = r.status_code == 200 and r.json().get("configured") is True
            check(r.status_code == 200 and "configured" in r.json(), "the AI status answers")
            ai_msg = f"{base}/api/leads/{msg_lead}/ai/message"
            ai_demo = f"{base}/api/leads/{msg_lead}/ai/demos"
            if not ai_on:
                r = s.post(ai_msg, headers=jwt, timeout=30, json={"channel": "Email", "instruction": "shorter"})
                check(r.status_code == 503 and r.json().get("code") == "ai_not_configured" and "AI is not configured" in r.json().get("error", ""), "AI message without a key: 503 ai_not_configured")
                r = s.post(ai_demo, headers=jwt, timeout=30, json={"instruction": "x"})
                check(r.status_code == 503 and r.json().get("code") == "ai_not_configured", "AI demo ideas without a key: 503 ai_not_configured")
            else:
                r = s.post(ai_msg, headers=jwt, timeout=30, json={"channel": "Email", "instruction": "x" * 501})
                check(r.status_code == 400 and r.json().get("code") == "instruction_too_long", "AI message refuses an instruction over 500 characters (no model call)")
                check(s.post(ai_msg, headers=jwt, timeout=30, json={"channel": "Fax"}).status_code == 400, "AI message refuses an unknown channel (no model call)")
            r = s.get(ai_demo, headers=jwt, timeout=30)
            check(r.status_code == 200 and r.json().get("items") == [], "saved demo ideas of a new lead are empty")
            check(s.post(ai_msg, headers=k, json={"channel": "Email"}, timeout=30).status_code == 403, "an API key cannot generate an AI message")
            check(s.post(ai_demo, headers=k, json={}, timeout=30).status_code == 403, "an API key cannot generate demo ideas")
            check(s.get(ai_demo, headers=k, timeout=30).status_code == 403, "an API key does not reach the demo ideas")
            check(requests.post(ai_msg, json={"channel": "Email"}, timeout=30).status_code == 401, "AI message needs a login")
            check(s.get(base + "/api/leads/" + str(uuid.uuid4()) + "/ai/demos", headers=jwt, timeout=30).status_code == 404, "demo ideas of an unknown lead: 404")
        if msg_lead:
            s.delete(f"{base}/api/leads/{msg_lead}", headers=jwt, timeout=30)
        r = s.put(base + "/api/message-templates", headers=jwt, timeout=30, json={"channel": "LinkedIn", "productKey": "werkteam", "body": "Smoke {name} {unknown}"})
        check(r.status_code == 400, "a template with an unknown placeholder is refused")
        r = s.put(base + "/api/message-templates", headers=jwt, timeout=30, json={"channel": "LinkedIn", "productKey": "werkteam", "body": "Smoke template voor {name}"})
        check(r.status_code == 200, "a message template is saved")
        if r.status_code == 200:
            check(s.delete(f"{base}/api/message-templates/{r.json().get('id')}", headers=jwt, timeout=30).status_code == 204, "clean up: delete the test template")
        check(s.get(base + "/api/messages", headers=k, timeout=30).status_code == 200, "an API key reaches the messages")
        check(s.get(base + "/api/message-templates", headers=k, timeout=30).status_code in (401, 403), "an API key does not reach the template settings")
        # The agent endpoints are closed to everyone without the agent token (a login or an API key does not open them).
        some_id = str(uuid.uuid4())   # a fresh id per run: wrong tokens are counted per hunt
        check(requests.get(f"{base}/api/hunt-agent/{some_id}/brief", timeout=30).status_code == 401, "agent endpoint refuses a call without the hunt token")
        check(requests.get(f"{base}/api/hunt-agent/{some_id}/brief", headers={"X-Hunt-Token": "wrong"}, timeout=30).status_code == 401, "agent endpoint refuses a wrong hunt token")
        check(s.get(f"{base}/api/hunt-agent/{some_id}/brief", headers=jwt, timeout=30).status_code == 401, "a normal login does not open the agent endpoint")
        check(s.get(f"{base}/api/hunt-agent/{some_id}/brief", headers=k, timeout=30).status_code == 401, "an API key does not open the agent endpoint")
        check(requests.post(f"{base}/api/hunt-agent/{some_id}/candidates", headers={"X-Hunt-Token": "wrong"}, json={"claims": {}}, timeout=30).status_code == 401, "submitting a candidate needs the hunt token")
        check(requests.post(f"{base}/api/hunt-agent/{some_id}/complete", headers={"X-Hunt-Token": "wrong"}, json={"summary": "x"}, timeout=30).status_code == 401, "completing a hunt needs the hunt token")
        check(requests.post(f"{base}/api/hunt-agent/{some_id}/known", headers={"X-Hunt-Token": "wrong"}, json={"domains": ["example.nl"]}, timeout=30).status_code == 401, "the known-domains check needs the hunt token")
        # a key may read the hunt status but never start, cancel or verify (the guard is per action)
        check(s.post(f"{base}/api/leads/hunt", headers=k, json={"segment": "smoke test segment"}, timeout=30).status_code == 403, "an API key cannot start a lead hunt")
        check(s.post(f"{base}/api/leads/hunt/{some_id}/cancel", headers=k, timeout=30).status_code == 403, "an API key cannot cancel a lead hunt")
        check(s.get(f"{base}/api/leads/hunt/capabilities", headers=k, timeout=30).status_code == 200, "an API key may read the hunt capabilities")
        check(requests.get(f"{base}/api/agent/hunts/{some_id}", timeout=30).status_code in (401, 404), "the old agent route is gone")

        # The key reaches the CRM and nothing else.
        check(s.get(base + "/api/leads", headers=k, timeout=30).status_code == 403, "API key is refused outside the CRM")
        check(s.post(base + "/api/api-keys", headers=k, json={"name": "x"}, timeout=30).status_code == 401, "API key cannot create keys")
    finally:
        if lead_id:
            check(s.delete(f"{base}/api/leads/{lead_id}", headers=jwt, timeout=30).status_code in (200, 204), "clean up: delete the test lead")
        if client_id:
            check(s.delete(f"{base}/api/clients/{client_id}", headers=jwt, timeout=30).status_code in (200, 204), "clean up: delete the test client")
        if key_id:
            check(s.delete(f"{base}/api/api-keys/{key_id}", headers=jwt, timeout=30).status_code == 204, "clean up: revoke the test key")

    failed = results.count(False)
    print(f"\n{len(results) - failed}/{len(results)} checks passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())

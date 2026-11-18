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
import time
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
    lead_id = key_id = None
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
                       json={"kind": "Call", "summary": "smoke call", "outcome": "geen gehoor"})
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
               "link_item", "find_by_email_or_domain"} <= set(names), f"MCP lists the planned tools ({len(names)})")
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

        # The key reaches the CRM and nothing else.
        check(s.get(base + "/api/leads", headers=k, timeout=30).status_code == 403, "API key is refused outside the CRM")
        check(s.post(base + "/api/api-keys", headers=k, json={"name": "x"}, timeout=30).status_code == 401, "API key cannot create keys")
    finally:
        if lead_id:
            check(s.delete(f"{base}/api/leads/{lead_id}", headers=jwt, timeout=30).status_code in (200, 204), "clean up: delete the test lead")
        if key_id:
            check(s.delete(f"{base}/api/api-keys/{key_id}", headers=jwt, timeout=30).status_code == 204, "clean up: revoke the test key")

    failed = results.count(False)
    print(f"\n{len(results) - failed}/{len(results)} checks passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())

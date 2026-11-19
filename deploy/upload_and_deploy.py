#!/usr/bin/env python3
"""Roll Jengo Clients out to the Hetzner servers: test, acc and (promoted from acc) prod.

Run from a machine that has the SSH key (C:/Users/HP/.ssh/id_ed25519) and, for DNS and secrets, the
Jengo vault tools. The servers have no GitHub access, so the source is uploaded as `git archive`.

    python deploy/upload_and_deploy.py init-env test      # once per environment: .env + secrets in the vault
    python deploy/upload_and_deploy.py dns test           # A record in Cloudflare (DNS only)
    python deploy/upload_and_deploy.py gateway test       # Caddy block on the gateway (backup + validate)
    python deploy/upload_and_deploy.py deploy test        # upload source, build image on the server, start
    python deploy/upload_and_deploy.py sso test           # register the SSO callback at the Jengo IAM (additive, with backup)
    python deploy/upload_and_deploy.py smoke test         # end-to-end check against the public URL
    python deploy/upload_and_deploy.py status test

    # prod is never built on its own server (small machine, shared with MaaRide, IAM and CoachOS):
    python deploy/upload_and_deploy.py promote --confirm-prod   # image from acc -> prod, then start

Secrets are generated on the fly, written to the server .env (chmod 600) and to the vault (project 12).
They are never printed and never committed.
"""
import argparse
import os
import re
import secrets
import string
import subprocess
import sys
import tempfile
import time
import importlib.util

import paramiko
import requests

# Docker prints UTF-8 (check marks, progress bars); the Windows console is cp1252 and would crash on them.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

KEY_FILE = os.path.expanduser("~/.ssh/id_ed25519")
LEADMANAGER_REPO = os.environ.get("LM_REPO", r"F:\projects\leadmanager")
WEBSEARCH_REPO = os.environ.get("WS_REPO", r"F:\projects\websearch")
VAULT_TOOLS = os.environ.get("JENGO_VAULT_TOOLS", r"F:\projects\jengo\jengo-system-private\tools")
VAULT_PROJECT = 12  # "Lead Generator"

TEST_ACC_IP = "46.224.114.149"
PROD_IP = "46.62.219.14"

ENVS = {
    "test": dict(ip=TEST_ACC_IP, url="https://clients-test.martiendejong.nl", host="clients-test.martiendejong.nl", aliases=[],
                 zone="martiendejong.nl", network="proxy_net", gateway_dir="/opt/jengo-gateway",
                 caddy="jengo-gateway-caddy-1"),
    "acc": dict(ip=TEST_ACC_IP, url="https://clients-acc.martiendejong.nl", host="clients-acc.martiendejong.nl", aliases=[],
                zone="martiendejong.nl", network="proxy_net", gateway_dir="/opt/jengo-gateway",
                caddy="jengo-gateway-caddy-1"),
    # Since 2026-11-19 prod replaced the old Windows installation of leads.prospergenics.com, and since the same
    # day the canonical address is clients.prospergenics.com (PUBLIC_URL, SSO callback). api.leads... keeps
    # serving the same container (old API address); leads... redirects (308, keeps the method) to the canonical host.
    "prod": dict(ip=PROD_IP, url="https://clients.prospergenics.com", host="clients.prospergenics.com",
                 aliases=["api.leads.prospergenics.com"], redirects=["leads.prospergenics.com"],
                 zone="prospergenics.com", network="maaride_net", gateway_dir="/opt/maaride-gateway",
                 caddy="maaride-gateway-caddy-1"),
}


# Which Jengo IAM an environment signs in with, and how to reach its database. Test uses the acc IAM: the
# separate test IAM has only a handful of users (not the owner), so nobody could log in there.
IAMS = {
    "prod": dict(ip=PROD_IP, db="iam-prod-db-1", issuer="https://maendeleo.martiendejong.nl"),
    "acc": dict(ip=TEST_ACC_IP, db="iam-db-1", issuer="https://iam-acc.martiendejong.nl"),
}
SSO_IAM = {"test": "acc", "acc": "acc", "prod": "prod"}
SSO_CLIENT_ID = "leadmanager"
SSO_PERMISSIONS = ('["ept:authorization","ept:token","ept:userinfo","gt:authorization_code","gt:refresh_token",'
                   '"rst:code","scp:openid","scp:profile","scp:email","scp:roles"]')


def stack_dir(env: str) -> str:
    return f"/opt/clients-{env}"


# --- ssh helpers ---------------------------------------------------------------------------------

KNOWN_HOSTS = os.path.expanduser("~/.ssh/known_hosts")


def connect(ip: str) -> paramiko.SSHClient:
    """Connects only to a server whose host key is already in known_hosts. Everything that follows (the
    .env secrets over SFTP, shell commands, the image stream to prod) would be exposed to or injectable by a
    machine that impersonates the server, so an unknown or changed key is a hard stop."""
    c = paramiko.SSHClient()
    try:
        c.load_host_keys(KNOWN_HOSTS)
    except OSError:
        raise SystemExit(f"{KNOWN_HOSTS} not found: connect once with `ssh root@{ip}` and verify the fingerprint first.")
    c.set_missing_host_key_policy(paramiko.RejectPolicy())
    try:
        c.connect(ip, username="root", key_filename=KEY_FILE, timeout=20)
    except paramiko.SSHException as err:
        raise SystemExit(f"SSH to {ip} refused: {err}. If the host key is new or changed, verify it with `ssh root@{ip}` "
                         f"before trusting it.")
    return c


def run(ssh: paramiko.SSHClient, cmd: str, show: bool = True, check: bool = True, timeout: int = 1800) -> tuple[int, str]:
    """Run a command and stream its output. Returns (exit code, combined output)."""
    chan = ssh.get_transport().open_session()
    chan.set_combine_stderr(True)
    chan.settimeout(timeout)
    chan.exec_command(cmd)
    out = []
    while True:
        data = chan.recv(65536)
        if not data:
            break
        text = data.decode("utf-8", "replace")
        out.append(text)
        if show:
            sys.stdout.write(text)
            sys.stdout.flush()
    rc = chan.recv_exit_status()
    if check and rc != 0:
        raise SystemExit(f"command failed ({rc}): {cmd[:120]}")
    return rc, "".join(out)


def put_bytes(ssh: paramiko.SSHClient, data: bytes, path: str, mode: int | None = None) -> None:
    sftp = ssh.open_sftp()
    with sftp.open(path, "wb") as fh:
        # Permissions first, content second: a secret file is never readable by others, not even briefly.
        if mode is not None:
            fh.chmod(mode)
        fh.write(data)
    sftp.close()


def read_text(ssh: paramiko.SSHClient, path: str) -> str:
    sftp = ssh.open_sftp()
    try:
        with sftp.open(path, "rb") as fh:
            return fh.read().decode("utf-8")
    finally:
        sftp.close()


# --- git helpers ---------------------------------------------------------------------------------

def git(repo: str, *args: str) -> bytes:
    return subprocess.run(["git", "-C", repo, "-c", "core.autocrlf=false", *args], check=True, capture_output=True).stdout


def archive(repo: str, ref: str) -> tuple[bytes, str]:
    subprocess.run(["git", "-C", repo, "fetch", "-q", "origin"], check=True)
    revision = git(repo, "rev-parse", "--short", ref).decode().strip()
    return git(repo, "archive", "--format=tar.gz", ref), revision


# --- vault and Cloudflare ---------------------------------------------------------------------------

def vault():
    spec = importlib.util.spec_from_file_location("vault_api", os.path.join(VAULT_TOOLS, "vault-api.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def cloudflare_token() -> str:
    cred = vault()._fetch_credential(9, 204, "Jengo Clients DNS record")
    if not cred or not cred.get("password"):
        raise SystemExit("Cloudflare token not available from the vault (9/204)")
    return cred["password"]


def random_password(length: int = 24) -> str:
    """Meets the Identity policy (upper, lower, digit, symbol) and survives a .env file (no $ # quotes)."""
    symbols = "!@%^*-_"
    alphabet = string.ascii_letters + string.digits + symbols
    while True:
        pw = "".join(secrets.choice(alphabet) for _ in range(length))
        if (any(c.isupper() for c in pw) and any(c.islower() for c in pw)
                and any(c.isdigit() for c in pw) and any(c in symbols for c in pw)):
            return pw


# --- commands ---------------------------------------------------------------------------------------

def cmd_init_env(args) -> None:
    cfg = ENVS[args.env]
    ssh = connect(cfg["ip"])
    stack = stack_dir(args.env)
    env_path = f"{stack}/deploy/.env"
    rc, _ = run(ssh, f"test -f {env_path}", show=False, check=False)
    if rc == 0 and not args.force:
        raise SystemExit(f"{env_path} exists already; use --force to replace it (this changes the secrets).")

    if rc == 0 and args.force:
        print("[init-env] WARNING: --force makes new secrets. The JWT key changes (everybody is logged out) and the "
              "admin password in the database is NOT changed by a normal start, so SEED_RESET_PASSWORDS=true is set "
              "for the next start only (deploy.sh removes it again).")

    jwt_key = secrets.token_urlsafe(48)
    admin_password = random_password()
    lines = [
        f"ENV_PREFIX={args.env}",
        f"PUBLIC_URL={cfg['url']}",
        f"GATEWAY_NETWORK={cfg['network']}",
        "MEM_LIMIT=768m",
        f"JWT_KEY={jwt_key}",
        f"ADMIN_EMAIL={args.admin_email}",
        f"ADMIN_PASSWORD={admin_password}",
        "ADMIN_FIRST_NAME=Admin",
        *(["SEED_RESET_PASSWORDS=true"] if (rc == 0 and args.force) else []),
        f"SSO_ISSUER={IAMS[SSO_IAM[args.env]]['issuer']}",
        f"SSO_CLIENT_ID={SSO_CLIENT_ID}",
        "OPENAI_API_KEY=", "GOOGLE_PLACES_API_KEY=", "JENGOWORK_BASE_URL=", "JENGOWORK_API_KEY=", "MINOX_POC_BASE_URL=",
    ]
    # The vault comes first: the server .env is a copy, the vault is where people look. If the vault
    # refuses, nothing is written to the server, so no secret exists only on the server.
    v = vault()
    a = v.create_credential(VAULT_PROJECT, f"Jengo Clients {args.env} admin login", username=args.admin_email,
                            password=admin_password, url=cfg["url"],
                            notes=f"Local admin of Jengo Clients {args.env}. Server: {cfg['ip']} {stack}/deploy/.env")
    j = v.create_credential(VAULT_PROJECT, f"Jengo Clients {args.env} JWT key", username="jwt-signing-key",
                            password=jwt_key, url=cfg["url"],
                            notes=f"Signing key of Jengo Clients {args.env}. Changing it logs everybody out.")
    if not a or not j:
        raise SystemExit("[init-env] the vault did not accept the credentials; nothing was written to the server.")

    run(ssh, f"mkdir -p {stack}/deploy", show=False)
    put_bytes(ssh, ("\n".join(lines) + "\n").encode(), env_path, mode=0o600)
    print(f"\n[init-env] {env_path} written (chmod 600). Vault project {VAULT_PROJECT}: "
          f"admin login id {a.get('id')}, JWT key id {j.get('id')}.")
    ssh.close()


def cmd_dns(args) -> None:
    cfg = ENVS[args.env]
    headers = {"Authorization": f"Bearer {cloudflare_token()}", "Content-Type": "application/json"}
    api = "https://api.cloudflare.com/client/v4"
    zones = requests.get(f"{api}/zones", params={"name": cfg["zone"]}, headers=headers, timeout=30).json()
    if not zones.get("success") or not zones["result"]:
        raise SystemExit(f"zone {cfg['zone']} not found / no access: {zones.get('errors')}")
    zone_id = zones["result"][0]["id"]
    for host in [cfg["host"], *cfg["aliases"]]:
        record = {"type": "A", "name": host, "content": cfg["ip"], "ttl": 300, "proxied": False}
        existing = requests.get(f"{api}/zones/{zone_id}/dns_records", params={"type": "A", "name": host},
                                headers=headers, timeout=30).json()["result"]
        if existing:
            r = requests.put(f"{api}/zones/{zone_id}/dns_records/{existing[0]['id']}", json=record, headers=headers, timeout=30)
            action = "updated"
        else:
            r = requests.post(f"{api}/zones/{zone_id}/dns_records", json=record, headers=headers, timeout=30)
            action = "created"
        if not r.json().get("success"):
            raise SystemExit(f"Cloudflare refused: {r.json().get('errors')}")
        print(f"[dns] {action}: {host} A {cfg['ip']} (DNS only)")


def caddy_block(env: str) -> str:
    cfg = ENVS[env]
    return (f"\n{', '.join([cfg['host'], *cfg['aliases']])} {{\n"
            f"    encode zstd gzip\n"
            f"    reverse_proxy {env}-clients:8080 {{\n"
            f"        header_up X-Forwarded-For {{remote_host}}\n"
            f"        header_up X-Forwarded-Proto {{scheme}}\n"
            f"        header_up X-Forwarded-Host {{host}}\n"
            f"    }}\n"
            f"}}\n")


def has_site(caddyfile: str, host: str) -> bool:
    """True when `host` is one of the addresses of a real site block (also in `a.com, b.com {`), not when it is
    only mentioned in a comment or is a longer host name."""
    for line in caddyfile.splitlines():
        stripped = line.strip()
        if stripped.startswith("#") or not stripped.endswith("{"):
            continue
        if host in [part.strip() for part in stripped[:-1].split(",")]:
            return True
    return False


def cmd_gateway(args) -> None:
    cfg = ENVS[args.env]
    ssh = connect(cfg["ip"])
    path = f"{cfg['gateway_dir']}/Caddyfile"
    current = read_text(ssh, path)
    # A real site block, not a substring: a comment or a longer host name must not count as "already there".
    if has_site(current, cfg["host"]):
        print(f"[gateway] {cfg['host']} is already in {path}; nothing to do.")
        ssh.close()
        return
    stamp = time.strftime("%Y%m%d-%H%M%S")
    backup = f"{path}.bak-{stamp}-clients"
    run(ssh, f"cp -p {path} {backup}", show=False)
    validate = f"docker exec {cfg['caddy']} caddy validate --config /etc/caddy/Caddyfile"
    staged = f"{path}.new-{stamp}-clients"
    try:
        # The new content goes to a separate file first: a transfer that breaks halfway leaves a harmless
        # temp file, never a half-written Caddyfile. The swap itself is one `cat` on the server, in place
        # (same inode, the Caddyfile is a single-file bind mount).
        put_bytes(ssh, (current.rstrip("\n") + "\n" + caddy_block(args.env)).encode(), staged)
        run(ssh, f"cat {staged} > {path}", show=False)
        rc, _ = run(ssh, validate, show=False, check=False)
        if rc != 0:
            raise RuntimeError("Caddy rejected the new config")
        run(ssh, f"docker exec {cfg['caddy']} caddy reload --config /etc/caddy/Caddyfile", show=False)
        run(ssh, f"rm -f {staged}", show=False, check=False)
    except BaseException as err:
        # ANY failure puts the old file back, over a FRESH connection: the one that just failed may be the
        # broken part. On prod this file is shared with MaaRide, so a bad Caddyfile breaks every site.
        try:
            ssh.close()
        except Exception:
            pass
        fresh = connect(cfg["ip"])
        run(fresh, f"cat {backup} > {path}", show=False)
        rc, _ = run(fresh, validate, show=False, check=False)
        run(fresh, f"rm -f {staged}", show=False, check=False)
        fresh.close()
        raise SystemExit(f"[gateway] {err}; the old Caddyfile is restored "
                         f"({'valid' if rc == 0 else 'STILL INVALID: restore ' + backup + ' by hand'}).")
    print(f"[gateway] {cfg['host']} added to {path} and Caddy reloaded (backup: {backup})")
    ssh.close()


def rehost_caddy(current: str, cfg: dict) -> str:
    """The Caddyfile with the canonical host first, its aliases next to it, and a permanent redirect block for
    every old host. Idempotent: running it on an already converted file changes nothing."""
    want = [cfg["host"], *cfg["aliases"]]
    out = []
    for line in current.split("\n"):
        stripped = line.strip()
        if not stripped.startswith("#") and stripped.endswith("{"):
            hosts = [p.strip() for p in stripped[:-1].split(",")]
            if cfg["host"] in hosts and hosts != want:
                line = line[: len(line) - len(line.lstrip())] + ", ".join(want) + " {"
        out.append(line)
    text = "\n".join(out)
    for old in cfg.get("redirects", []):
        if not has_site(text, old):
            text = text.rstrip("\n") + (f"\n\n{old} {{\n    redir https://{cfg['host']}{{uri}} 308\n}}\n")
    return text


def cmd_rehost(args) -> None:
    """Makes the canonical host of an environment the one in ENVS: PUBLIC_URL in .env (CORS, SSO callback,
    calendar feed links), a container restart without a rebuild, and the gateway: the old host redirects.
    Run `sso <env>` first so the new SSO callback is registered, and `dns <env>` if the host is new."""
    cfg = ENVS[args.env]
    ssh = connect(cfg["ip"])
    stack = stack_dir(args.env)
    stamp = time.strftime("%Y%m%d-%H%M%S")

    env_path = f"{stack}/deploy/.env"
    env_text = read_text(ssh, env_path)
    if f"PUBLIC_URL={cfg['url']}" not in env_text.splitlines():
        run(ssh, f"cp -p {env_path} {env_path}.bak-{stamp}-rehost", show=False)
        new_env = "\n".join(f"PUBLIC_URL={cfg['url']}" if l.startswith("PUBLIC_URL=") else l for l in env_text.split("\n"))
        put_bytes(ssh, new_env.encode(), f"{env_path}.new", mode=0o600)
        run(ssh, f"cat {env_path}.new > {env_path} && rm -f {env_path}.new", show=False)
        run(ssh, f"cd {stack}/deploy && NO_BUILD=1 ./deploy.sh")
    else:
        print(f"[rehost] PUBLIC_URL is already {cfg['url']}")

    path = f"{cfg['gateway_dir']}/Caddyfile"
    current = read_text(ssh, path)
    updated = rehost_caddy(current, cfg)
    if updated == current:
        print("[rehost] the gateway already has this layout; nothing to do.")
        ssh.close()
        return
    backup = f"{path}.bak-{stamp}-rehost"
    staged = f"{path}.new-{stamp}-rehost"
    validate = f"docker exec {cfg['caddy']} caddy validate --config /etc/caddy/Caddyfile"
    run(ssh, f"cp -p {path} {backup}", show=False)
    try:
        put_bytes(ssh, updated.encode(), staged)
        run(ssh, f"cat {staged} > {path}", show=False)
        rc, _ = run(ssh, validate, show=False, check=False)
        if rc != 0:
            raise RuntimeError("Caddy rejected the new config")
        run(ssh, f"docker exec {cfg['caddy']} caddy reload --config /etc/caddy/Caddyfile", show=False)
        run(ssh, f"rm -f {staged}", show=False, check=False)
    except BaseException as err:
        try:
            ssh.close()
        except Exception:
            pass
        fresh = connect(cfg["ip"])
        run(fresh, f"cat {backup} > {path}", show=False)
        rc, _ = run(fresh, validate, show=False, check=False)
        run(fresh, f"rm -f {staged}", show=False, check=False)
        fresh.close()
        raise SystemExit(f"[rehost] {err}; the old Caddyfile is restored "
                         f"({'valid' if rc == 0 else 'STILL INVALID: restore ' + backup + ' by hand'}).")
    print(f"[rehost] {cfg['host']} is canonical, redirects {cfg.get('redirects', [])} (backup: {backup})")
    ssh.close()


def upload_source(ssh, env: str, ref: str, ws_ref: str) -> str:
    lm_tar, revision = archive(LEADMANAGER_REPO, ref)
    ws_tar, ws_revision = archive(WEBSEARCH_REPO, ws_ref)
    stack = stack_dir(env)
    run(ssh, f"mkdir -p {stack}", show=False)
    put_bytes(ssh, lm_tar, f"{stack}/_lm.tar.gz")
    put_bytes(ssh, ws_tar, f"{stack}/_ws.tar.gz")
    # Replace the source, keep .env, data and backups. Files that no longer exist in git disappear too.
    run(ssh, (f"cd {stack} && rm -rf src client _websearch Dockerfile LeadManager.sln && mkdir -p _websearch deploy "
              f"&& tar -xzf _lm.tar.gz && tar -xzf _ws.tar.gz -C _websearch && rm -f _lm.tar.gz _ws.tar.gz "
              f"&& chmod +x deploy/*.sh && echo '{revision} (websearch {ws_revision})' > REVISION"), show=False)
    print(f"[upload] leadmanager {revision} + websearch {ws_revision} -> {stack}")
    return revision


def cmd_deploy(args) -> None:
    if args.env == "prod":
        raise SystemExit("prod is not built on its server. Use: promote --confirm-prod")
    ssh = connect(ENVS[args.env]["ip"])
    stack = stack_dir(args.env)
    rc, _ = run(ssh, f"test -f {stack}/deploy/.env", show=False, check=False)
    if rc != 0:
        raise SystemExit(f"no {stack}/deploy/.env yet; run: init-env {args.env}")
    upload_source(ssh, args.env, args.ref, args.websearch_ref)
    run(ssh, f"cd {stack}/deploy && ./deploy.sh")
    ssh.close()


def cmd_promote(args) -> None:
    if not args.confirm_prod:
        raise SystemExit("Promoting to production needs --confirm-prod.")
    acc, prod = connect(ENVS["acc"]["ip"]), connect(ENVS["prod"]["ip"])
    stack = stack_dir("prod")
    rc, _ = run(prod, f"test -f {stack}/deploy/.env", show=False, check=False)
    if rc != 0:
        raise SystemExit(f"no {stack}/deploy/.env yet; run: init-env prod")
    _, acc_rev = run(acc, f"cat {stack_dir('acc')}/REVISION", show=False)
    print(f"[promote] image jengo-clients:acc (source {acc_rev.strip()}) -> prod")
    # REVISION reads "<leadmanager commit> (websearch <commit>)": prod gets exactly those two, not whatever
    # origin/main is by now.
    match = re.match(r"\s*(\w+) \(websearch (\w+)\)", acc_rev)
    if not match:
        raise SystemExit(f"cannot read the revision of acc: {acc_rev!r}")
    acc_lm, acc_ws = match.groups()
    # Layers, not the image id: with the containerd image store the id is a manifest digest that may differ
    # after save/load while the content is identical.
    inspect = "docker image inspect --format '{{json .RootFS.Layers}}' jengo-clients:acc"
    _, acc_image = run(acc, inspect, show=False)

    # Stream the image: docker save | gzip on acc  ->  gunzip | docker load on prod.
    src = acc.get_transport().open_session()
    src.exec_command("docker save jengo-clients:acc | gzip -1")
    dst = prod.get_transport().open_session()
    dst.exec_command("gunzip | docker load")
    total = 0
    while True:
        chunk = src.recv(1 << 20)
        if not chunk:
            break
        dst.sendall(chunk)
        total += len(chunk)
    dst.shutdown_write()
    out = b""
    while True:
        part = dst.recv(65536)
        if not part:
            break
        out += part
    if dst.recv_exit_status() != 0 or src.recv_exit_status() != 0:
        raise SystemExit(f"image transfer failed: {out.decode(errors='replace')}")
    print(f"[promote] transferred {total / 1e6:.0f} MB: {out.decode(errors='replace').strip()}")
    # The image that passed the tests on acc must be the image that arrives: compare content ids.
    _, prod_image = run(prod, inspect, show=False)
    if not acc_image.strip() or acc_image.strip() != prod_image.strip():
        raise SystemExit("image layers differ after transfer; nothing was started.")
    run(prod, "docker tag jengo-clients:acc jengo-clients:prod", show=False)
    print(f"[promote] verified: all {acc_image.count(',') + 1} layers are identical on both servers")

    # Same source revision as acc (compose file, scripts), then start without building.
    upload_source(prod, "prod", args.ref or acc_lm, acc_ws)
    run(prod, f"cd {stack}/deploy && NO_BUILD=1 ./deploy.sh")


def cmd_sso(args) -> None:
    """Registers this environment's callback at the Jengo IAM so people can log in with their IAM account.
    Additive and idempotent: existing redirect URIs stay, the client row is saved to /root first, and an IAM
    that has no `leadmanager` client yet gets one (public client, PKCE, same permissions as production)."""
    cfg = ENVS[args.env]
    iam = IAMS[SSO_IAM[args.env]]
    callback, home = f"{cfg['url']}/api/auth/sso/callback", f"{cfg['url']}/"
    # These land in SQL: only plain https URLs of our own environments are acceptable.
    for value in (callback, home):
        if not re.fullmatch(r"https://[a-z0-9.-]+(/[a-z0-9/_.-]*)?", value):
            raise SystemExit(f"refusing to put {value!r} in SQL")

    ssh = connect(iam["ip"])
    psql = (f"docker exec -i {iam['db']} psql -U iam -d iam_db")

    def sql(text: str, extra: str = "") -> str:
        _, out = run(ssh, f"cat <<'SQLEOF' | {psql} {extra} 2>&1\n{text}\nSQLEOF", show=False)
        return out

    stamp = time.strftime("%Y%m%d-%H%M%S")
    backup = f"/root/iam-leadmanager-client-before-{stamp}.txt"
    exists = sql(f"select count(*) from \"OpenIddictApplications\" where \"ClientId\"='{SSO_CLIENT_ID}';", "-t -A").strip() == "1"
    sql(f"select * from \"OpenIddictApplications\" where \"ClientId\"='{SSO_CLIENT_ID}';", f"-x > {backup}")
    if exists:
        sql(f"""
update "OpenIddictApplications"
   set "RedirectUris" = ("RedirectUris"::jsonb || to_jsonb('{callback}'::text))::text
 where "ClientId"='{SSO_CLIENT_ID}' and not ("RedirectUris"::jsonb ? '{callback}');
update "OpenIddictApplications"
   set "PostLogoutRedirectUris" = ("PostLogoutRedirectUris"::jsonb || to_jsonb('{home}'::text))::text
 where "ClientId"='{SSO_CLIENT_ID}' and not ("PostLogoutRedirectUris"::jsonb ? '{home}');
""")
    else:
        print(f"[sso] no '{SSO_CLIENT_ID}' client at {iam['issuer']} yet: creating it")
        sql(f"""
insert into "OpenIddictApplications"
  ("Id","ClientId","ClientType","ConsentType","DisplayName","Permissions","PostLogoutRedirectUris","RedirectUris","Requirements","ConcurrencyToken")
select gen_random_uuid()::text, '{SSO_CLIENT_ID}', 'public', 'implicit', 'LeadManager (Jengo Clients)',
       '{SSO_PERMISSIONS}', '["{home}"]', '["{callback}"]', '["ft:pkce"]', gen_random_uuid()::text
 where not exists (select 1 from "OpenIddictApplications" where "ClientId"='{SSO_CLIENT_ID}');
""")
    uris = sql(f"select \"RedirectUris\" from \"OpenIddictApplications\" where \"ClientId\"='{SSO_CLIENT_ID}';", "-t -A")
    ssh.close()
    if callback not in uris:
        raise SystemExit(f"[sso] {callback} is not registered after the update: {uris.strip()[:200]}")
    print(f"[sso] {callback} is registered at {iam['issuer']} (previous row saved on the IAM server: {backup})")

    # The environment must point at that IAM (init-env does this for new environments).
    app = connect(cfg["ip"])
    env_path = f"{stack_dir(args.env)}/deploy/.env"
    current = [l for l in read_text(app, env_path).splitlines() if l.startswith("SSO_ISSUER=")]
    if current != [f"SSO_ISSUER={iam['issuer']}"]:
        print(f"[sso] {args.env} pointed at {current}; switching to {iam['issuer']} and restarting")
        run(app, f"cd {stack_dir(args.env)}/deploy && sed -i 's#^SSO_ISSUER=.*#SSO_ISSUER={iam['issuer']}#' .env "
                 f"&& docker compose up -d --no-build", show=False)
        for _ in range(40):
            _, status = run(app, f"docker inspect -f '{{{{.State.Health.Status}}}}' clients-{args.env}-api-1", show=False, check=False)
            if status.strip() == "healthy":
                break
            time.sleep(3)
    app.close()


def cmd_smoke(args) -> None:
    cfg = ENVS[args.env]
    ssh = connect(cfg["ip"])
    env_text = read_text(ssh, f"{stack_dir(args.env)}/deploy/.env")
    ssh.close()
    values = dict(line.split("=", 1) for line in env_text.splitlines() if "=" in line)
    script = os.path.join(os.path.dirname(os.path.abspath(__file__)), "smoke_test.py")
    result = subprocess.run([sys.executable, script, "--base", cfg["url"], "--admin-email", values["ADMIN_EMAIL"]],
                            env={**os.environ, "LM_ADMIN_PASSWORD": values["ADMIN_PASSWORD"]})
    sys.exit(result.returncode)


def cmd_status(args) -> None:
    ssh = connect(ENVS[args.env]["ip"])
    run(ssh, f"cat {stack_dir(args.env)}/REVISION 2>/dev/null; docker ps --filter name=clients-{args.env} "
             f"--format '{{{{.Names}}}}\\t{{{{.Status}}}}\\t{{{{.Image}}}}'; "
             f"docker stats --no-stream --format 'memory: {{{{.MemUsage}}}}' clients-{args.env}-api-1 2>/dev/null; "
             f"docker exec clients-{args.env}-api-1 curl -fsS http://localhost:8080/api/health 2>/dev/null; echo",
        check=False)
    ssh.close()


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="command", required=True)
    for name, fn in [("init-env", cmd_init_env), ("dns", cmd_dns), ("gateway", cmd_gateway),
                     ("deploy", cmd_deploy), ("smoke", cmd_smoke), ("status", cmd_status), ("sso", cmd_sso),
                     ("rehost", cmd_rehost)]:
        p = sub.add_parser(name)
        p.add_argument("env", choices=sorted(ENVS))
        p.set_defaults(fn=fn)
        if name == "init-env":
            p.add_argument("--admin-email", default="info@martiendejong.nl")
            p.add_argument("--force", action="store_true")
        if name == "deploy":
            p.add_argument("--ref", default="origin/develop", help="leadmanager git ref (default origin/develop)")
            p.add_argument("--websearch-ref", default="origin/main")
    p = sub.add_parser("promote")
    p.add_argument("--confirm-prod", action="store_true")
    p.add_argument("--ref", default=None, help="default: the revision that runs on acc")
    p.set_defaults(fn=cmd_promote)

    args = ap.parse_args()
    args.fn(args)


if __name__ == "__main__":
    main()

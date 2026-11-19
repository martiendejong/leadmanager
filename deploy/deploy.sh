#!/bin/sh
# Deploy of one Jengo Clients environment. Run this on the server, in the stack's deploy folder:
#
#   /opt/clients-test/deploy/deploy.sh        (or clients-acc)
#
# The environment follows from the folder the script is in and the .env there; the script has no
# "deploy to production" flag. Order: check -> backup -> build -> start -> verify.
# Production is not built on the server but promoted from acc (see upload_and_deploy.py).
set -eu
cd "$(dirname "$0")"

[ -f .env ] || { echo "[deploy] .env is missing in $(pwd)"; exit 1; }
# .env is a Docker env file (values may contain spaces), not a shell script: read selectively.
waarde() { grep -E "^$1=" .env | head -1 | cut -d= -f2- | tr -d '\r'; }
ENV_PREFIX=$(waarde ENV_PREFIX)
PUBLIC_URL=$(waarde PUBLIC_URL)
[ -n "$ENV_PREFIX" ] || { echo "[deploy] ENV_PREFIX is missing in .env"; exit 1; }
for v in JWT_KEY ADMIN_EMAIL ADMIN_PASSWORD GATEWAY_NETWORK; do
    [ -n "$(waarde $v)" ] || { echo "[deploy] $v is empty in .env"; exit 1; }
done
[ "$(waarde JWT_KEY | wc -c)" -ge 33 ] || { echo "[deploy] JWT_KEY is shorter than 32 characters"; exit 1; }
echo "[deploy] environment: $ENV_PREFIX  url: $PUBLIC_URL"

# The container runs as user 'app' (uid 1654) and must be able to write to the data volume.
mkdir -p data backups
chown -R 1654:1654 data
# The database and the keys are readable by the app (uid 1654) and root only.
chmod 700 data

./backup.sh

if [ "${NO_BUILD:-}" = "1" ]; then
    docker compose up -d --no-build
else
    docker compose up -d --build
fi

wacht_gezond() {
    echo "[deploy] waiting for the container to become healthy"
    i=0
    until [ "$(docker inspect -f '{{.State.Health.Status}}' "clients-$ENV_PREFIX-api-1" 2>/dev/null)" = "healthy" ]; do
        i=$((i + 1))
        if [ "$i" -gt 40 ]; then
            echo "[deploy] FAILED: container does not become healthy"
            docker compose logs --tail 60 api
            exit 1
        fi
        sleep 3
    done
}
wacht_gezond

docker compose exec -T api curl -fsS http://localhost:8080/api/health

# The recovery switch (init-env --force) applies to exactly one start: remove it so a later
# restart does not overwrite the administrator password again.
# Compose bakes the variable into the container: removing it from .env is not enough, because every restart of
# THIS container (reboot, OOM, restart policy) would overwrite the password again. So recreate the container,
# now without the switch.
if grep -q "^SEED_RESET_PASSWORDS=true" .env; then
    sed -i "/^SEED_RESET_PASSWORDS=/d" .env
    docker compose up -d --no-build --force-recreate
    wacht_gezond
    echo "[deploy] SEED_RESET_PASSWORDS was one-time: .env cleaned and container recreated without"
fi
echo
echo "[deploy] done: $ENV_PREFIX is running $(cat ../REVISION 2>/dev/null || echo unknown)"

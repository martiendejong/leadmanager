#!/bin/sh
# Deploy van één Jengo Clients-omgeving. Draai dit op de server, in de deploy-map van de stack:
#
#   /opt/clients-test/deploy/deploy.sh        (of clients-acc)
#
# De omgeving volgt uit de map waarin het script staat en de .env daar; het script kent geen
# "deploy naar productie"-vlag. Volgorde: controle -> backup -> bouwen -> starten -> controleren.
# Productie wordt niet op de server gebouwd maar gepromoveerd vanaf acc (zie upload_and_deploy.py).
set -eu
cd "$(dirname "$0")"

[ -f .env ] || { echo "[deploy] .env ontbreekt in $(pwd)"; exit 1; }
# .env is een Docker-env-bestand (waarden mogen spaties bevatten), geen shellscript: lees gericht.
waarde() { grep -E "^$1=" .env | head -1 | cut -d= -f2- | tr -d '\r'; }
ENV_PREFIX=$(waarde ENV_PREFIX)
PUBLIC_URL=$(waarde PUBLIC_URL)
[ -n "$ENV_PREFIX" ] || { echo "[deploy] ENV_PREFIX ontbreekt in .env"; exit 1; }
for v in JWT_KEY ADMIN_EMAIL ADMIN_PASSWORD GATEWAY_NETWORK; do
    [ -n "$(waarde $v)" ] || { echo "[deploy] $v is leeg in .env"; exit 1; }
done
[ "$(waarde JWT_KEY | wc -c)" -ge 33 ] || { echo "[deploy] JWT_KEY is korter dan 32 tekens"; exit 1; }
echo "[deploy] omgeving: $ENV_PREFIX  url: $PUBLIC_URL"

# De container draait als gebruiker 'app' (uid 1654) en moet in het datavolume kunnen schrijven.
mkdir -p data backups
chown -R 1654:1654 data

./backup.sh

if [ "${NO_BUILD:-}" = "1" ]; then
    docker compose up -d --no-build
else
    docker compose up -d --build
fi

echo "[deploy] wachten tot de container gezond is"
i=0
until [ "$(docker inspect -f '{{.State.Health.Status}}' "clients-$ENV_PREFIX-api-1" 2>/dev/null)" = "healthy" ]; do
    i=$((i + 1))
    if [ "$i" -gt 40 ]; then
        echo "[deploy] MISLUKT: container wordt niet gezond"
        docker compose logs --tail 60 api
        exit 1
    fi
    sleep 3
done

docker compose exec -T api curl -fsS http://localhost:8080/api/health

# Herstelschakelaar (init-env --force) geldt voor precies één start: haal hem weg zodat een volgende
# herstart het beheerderswachtwoord niet opnieuw overschrijft.
if grep -q "^SEED_RESET_PASSWORDS=true" .env; then
    sed -i "/^SEED_RESET_PASSWORDS=/d" .env
    echo "[deploy] SEED_RESET_PASSWORDS uit .env gehaald (eenmalig)"
fi
echo
echo "[deploy] klaar: $ENV_PREFIX draait $(cat ../REVISION 2>/dev/null || echo onbekend)"

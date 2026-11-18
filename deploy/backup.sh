#!/bin/sh
# WAL-veilige backup van de datamap van deze stack. Draait vóór elke deploy (deploy.sh) en kan los
# gestart worden. Databases gaan via `sqlite3 .backup` (een gewone kopie mist data die nog in de WAL
# staat); alle andere bestanden (dp-keys) gaan in een tar. De laatste 14 backups blijven bewaard.
#
#   ./backup.sh            -> backups/<tijdstempel>/
set -eu
cd "$(dirname "$0")"
# Backups bevatten de database en de Data Protection-sleutels: alleen voor de eigenaar leesbaar.
umask 077

DATA=./data
[ -d "$DATA" ] || { echo "[backup] geen datamap, niets te doen"; exit 0; }
command -v sqlite3 >/dev/null || { echo "[backup] sqlite3 ontbreekt op de host (apt install sqlite3)"; exit 1; }

STAMP=$(date -u +%Y%m%d-%H%M%S)
DOEL=./backups/$STAMP
mkdir -p "$DOEL/db"
chmod 700 ./backups

find "$DATA" -name '*.db' -not -path '*/backups/*' | while read -r db; do
    rel=${db#"$DATA"/}
    mkdir -p "$DOEL/db/$(dirname "$rel")"
    sqlite3 "$db" ".backup '$DOEL/db/$rel'"
    # Controle op inhoud, niet alleen op bestaan: de kopie moet een leesbare database zijn.
    sqlite3 "$DOEL/db/$rel" 'PRAGMA integrity_check;' | grep -qx ok || { echo "[backup] KAPOT: $rel"; exit 1; }
    echo "[backup] ok  $rel"
done

tar -czf "$DOEL/bestanden.tar.gz" \
    --exclude='*.db' --exclude='*.db-shm' --exclude='*.db-wal' --exclude='./backups' \
    -C "$DATA" .

# Opruimen: bewaar de 14 nieuwste.
ls -1d ./backups/*/ 2>/dev/null | sort -r | tail -n +15 | while read -r oud; do rm -rf "$oud"; done

echo "[backup] klaar: $DOEL ($(du -sh "$DOEL" | cut -f1))"

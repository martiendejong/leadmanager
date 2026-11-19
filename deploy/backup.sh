#!/bin/sh
# WAL-safe backup of this stack's data folder. Runs before every deploy (deploy.sh) and can be started
# on its own. Databases go through `sqlite3 .backup` (a plain copy misses data still in the WAL);
# all other files (dp-keys) go into a tar. The last 14 backups are kept.
#
#   ./backup.sh            -> backups/<timestamp>/
set -eu
cd "$(dirname "$0")"
# Backups contain the database and the Data Protection keys: readable by the owner only.
umask 077

DATA=./data
[ -d "$DATA" ] || { echo "[backup] no data folder, nothing to do"; exit 0; }
command -v sqlite3 >/dev/null || { echo "[backup] sqlite3 is missing on the host (apt install sqlite3)"; exit 1; }

STAMP=$(date -u +%Y%m%d-%H%M%S)
DOEL=./backups/$STAMP
mkdir -p "$DOEL/db"
chmod 700 ./backups

find "$DATA" -name '*.db' -not -path '*/backups/*' | while read -r db; do
    rel=${db#"$DATA"/}
    mkdir -p "$DOEL/db/$(dirname "$rel")"
    sqlite3 "$db" ".backup '$DOEL/db/$rel'"
    # Check the content, not just existence: the copy must be a readable database.
    sqlite3 "$DOEL/db/$rel" 'PRAGMA integrity_check;' | grep -qx ok || { echo "[backup] CORRUPT: $rel"; exit 1; }
    echo "[backup] ok  $rel"
done

tar -czf "$DOEL/files.tar.gz" \
    --exclude='*.db' --exclude='*.db-shm' --exclude='*.db-wal' --exclude='./backups' \
    -C "$DATA" .

# Cleanup: keep the 14 newest.
ls -1d ./backups/*/ 2>/dev/null | sort -r | tail -n +15 | while read -r oud; do rm -rf "$oud"; done

echo "[backup] done: $DOEL ($(du -sh "$DOEL" | cut -f1))"

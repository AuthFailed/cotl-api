#!/usr/bin/env bash
# Capture the game's data on the game PC, pack it into site/, and (after asking) commit and push.
# Needs mod/CotlDataDump.dll (mod/build.sh) and a .env with COTL_SSH, COTL_KEY, COTL_GAME_DIR, COTL_SAVES_DIR.
set -euo pipefail
cd "$(dirname "$0")"
source .env
: "${COTL_SSH:?}" "${COTL_KEY:?}" "${COTL_GAME_DIR:?}" "${COTL_SAVES_DIR:?}"
[ -f mod/CotlDataDump.dll ] || { echo "build the mod first: mod/build.sh <Managed dir> <BepInEx core dir>"; exit 1; }

pc() { ssh -i "$COTL_KEY" -o ConnectTimeout=15 "$COTL_SSH" "$@"; }
q() { printf '%q' "$1"; }
GAME=$(q "$COTL_GAME_DIR") SAVES=$(q "$COTL_SAVES_DIR")
PLUGIN="$GAME/BepInEx/plugins/CotlDataDump.dll" CFG="$GAME/BepInEx/config/chrsnv.cotldata.dump.cfg" OUT="$GAME/BepInEx/plugins/CotlDataDump"
SUMS=/tmp/cotl-saves-before2.sha256
DUMP=dump  # kept after the run (gitignored) to re-pack or inspect

if pc 'pgrep -f "[C]ult Of The Lamb.exe" >/dev/null'; then echo "The game is running on the PC; not touching it."; exit 1; fi

# Saves: verify against the last list; if it's missing or differs, back them up and make a new one.
pc "cd $SAVES && if ! sha256sum -c --quiet $SUMS 2>/dev/null; then
      b=~/cotl-saves-backup-\$(date +%Y%m%d-%H%M%S); cp -a . \$b && echo \"saves backed up to \$b\"
      sha256sum * > $SUMS; fi"

cleanup() {
  echo "cleaning up on the PC"
  pc "pkill -f '[C]ult Of The Lamb.exe'; rm -rf $PLUGIN $CFG $OUT" || echo "WARNING: cleanup failed; remove the plugin, cfg and output by hand"
  if pc "cd $SAVES && sha256sum -c --quiet $SUMS"; then echo "saves unchanged"
  else echo "WARNING: SAVES CHANGED. Backups are in ~/cotl-saves-backup-* on the PC."; fi
}
trap cleanup EXIT

pc "rm -rf $OUT; printf '[General]\nSlot = 0\n\n[Jobs]\nItems = true\nStructures = true\nSkins = true\n' > $CFG"
pc "cat > $PLUGIN" < mod/CotlDataDump.dll
pc 'DISPLAY=:0 XDG_RUNTIME_DIR=/run/user/1000 DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus nohup steam -applaunch 1313140 >/dev/null 2>&1 &'
echo "game launching; waiting for the dump (up to 10 minutes)"
seen=0
for _ in $(seq 60); do
  sleep 10
  if pc "test -f $OUT/done"; then break; fi
  if pc 'pgrep -f "[C]ult Of The Lamb.exe" >/dev/null'; then seen=1
  elif [ $seen = 1 ]; then echo "the game exited without finishing; see BepInEx/LogOutput.log on the PC"; exit 1; fi
done
pc "test -f $OUT/done" || { echo "timed out after 10 minutes"; exit 1; }

rm -rf "$DUMP"
rsync -a --partial -e "ssh -i $COTL_KEY" "$COTL_SSH:$OUT/" "$DUMP/"
python3 pack.py "$DUMP" site
git status --short site | tail -20
read -r -p "Commit and push this data? [y/N] " ok
if [ "$ok" = y ]; then
  git add site
  git commit -m "Data from game version $(python3 -c 'import json;print(json.load(open("site/v1/index.json"))["gameVersion"])')"
  git push
fi

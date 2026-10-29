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
GAME=$(q "$COTL_GAME_DIR") SAVES=$(q "$COTL_SAVES_DIR") SUMS=$(q "${COTL_SUMS:-/tmp/cotl-saves-before2.sha256}")
PLUGIN="$GAME/BepInEx/plugins/CotlDataDump.dll" CFG="$GAME/BepInEx/config/chrsnv.cotldata.dump.cfg" OUT="$GAME/BepInEx/plugins/CotlDataDump"
DUMP=dump  # kept after the run (gitignored) to re-pack or inspect
GAME_UP='pgrep -f "[C]ult Of The Lamb.exe" >/dev/null'

# pgrep exits 1 when nothing matches; ssh exits 255 when the PC can't be reached, which says nothing.
rc=0; pc "$GAME_UP" || rc=$?
[ $rc = 1 ] || { [ $rc = 0 ] && echo "The game is running on the PC; not touching it." || echo "Can't reach the PC (ssh exit $rc)."; exit 1; }

# Saves: compare the whole listing (new files too) with the last one; if it differs, back up, then re-list.
pc "cd $SAVES && { sha256sum * | cmp -s - $SUMS || {
      b=~/cotl-saves-backup-\$(date +%Y%m%d-%H%M%S); cp -a . \"\$b\" && sha256sum * > $SUMS && echo \"saves backed up to \$b\"; }; }" \
  || { echo "Couldn't check or back up the saves; not starting."; exit 1; }

cleanup() {
  trap '' INT
  echo "cleaning up on the PC"
  local tries=0
  until pc "pkill -f '[C]ult Of The Lamb.exe'; rm -rf $PLUGIN $CFG $OUT"; do
    tries=$((tries + 1))
    [ $tries -lt 10 ] || { echo "WARNING: cleanup failed; remove the plugin, cfg and output by hand"; break; }
    sleep 30
  done
  if pc "cd $SAVES && sha256sum * | cmp -s - $SUMS"; then echo "saves unchanged"; return 0; fi
  echo "WARNING: SAVES CHANGED (or couldn't be checked). Backups are in ~/cotl-saves-backup-* on the PC."
  return 1
}
trap cleanup EXIT

pc "rm -rf $OUT; printf '[General]\nArmed = true\nSlot = 0\n\n[Jobs]\nItems = true\nStructures = true\nSkins = true\n' > $CFG"
pc "cat > $PLUGIN" < mod/CotlDataDump.dll
pc 'DISPLAY=:0 XDG_RUNTIME_DIR=/run/user/1000 DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus nohup steam -applaunch 1313140 >/dev/null 2>&1 &'
echo "game launching; waiting for the dump (up to 10 minutes)"
seen=0
for _ in $(seq 60); do
  sleep 10
  rc=0; pc "test -f $OUT/done" || rc=$?
  [ $rc = 0 ] && break
  rc=0; pc "$GAME_UP" || rc=$?
  if [ $rc = 0 ]; then seen=1
  elif [ $rc = 1 ] && [ $seen = 1 ]; then echo "the game exited without finishing; see BepInEx/LogOutput.log on the PC"; exit 1
  fi  # 255: the PC dropped off the network for a moment; keep waiting
done
pc "test -f $OUT/done" || { echo "timed out after 10 minutes"; exit 1; }

rm -rf "$DUMP"
rsync -a --partial -e "ssh -i $COTL_KEY" "$COTL_SSH:$OUT/" "$DUMP/"
# Clean the PC and check the saves now, before anything is packed or published.
trap - EXIT
cleanup || exit 1

python3 pack.py "$DUMP" site
git status --short site | tail -20
read -r -p "Commit and push this data? [y/N] " ok
if [ "$ok" = y ]; then
  git add site
  git commit -m "Data from game version $(python3 -c 'import json;print(json.load(open("site/v1/index.json"))["gameVersion"])')"
  git push
fi

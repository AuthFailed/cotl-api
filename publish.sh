#!/usr/bin/env bash
# Capture the game's data on the game PC, pack it into site/, and (after asking) commit and push.
# Needs mod/CotlDataDump.dll (mod/build.sh) and a .env with COTL_SSH, COTL_KEY, COTL_GAME_DIR, COTL_SAVES_DIR.
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=/dev/null  # local, not in the repo
source .env
: "${COTL_SSH:?}" "${COTL_KEY:?}" "${COTL_GAME_DIR:?}" "${COTL_SAVES_DIR:?}"
[ -f mod/CotlDataDump.dll ] || { echo "build the mod first: mod/build.sh <Managed dir> <BepInEx core dir>"; exit 1; }
[ "$(git rev-parse --abbrev-ref HEAD)" = main ] || { echo "Publish from main (the site deploys from it)."; exit 1; }

# -n: ssh would otherwise read (and use up) this script's stdin, answers to the prompt included.
pc() { ssh -n -i "$COTL_KEY" -o ConnectTimeout=15 "$COTL_SSH" "$@"; }
pc_stdin() { ssh -i "$COTL_KEY" -o ConnectTimeout=15 "$COTL_SSH" "$@"; }
q() { printf '%q' "$1"; }
GAME=$(q "$COTL_GAME_DIR") SAVES=$(q "$COTL_SAVES_DIR") SUMS=$(q "${COTL_SUMS:-/tmp/cotl-saves-before2.sha256}") LOCK=$(q "${COTL_LOCK:-/tmp/cotl-api.lock}")
PLUGIN="$GAME/BepInEx/plugins/CotlDataDump.dll" CFG="$GAME/BepInEx/config/chrsnv.cotldata.dump.cfg" OUT="$GAME/BepInEx/plugins/CotlDataDump"
DUMP=dump  # kept after the run (gitignored) to re-pack or inspect
GAME_UP='pgrep -f "[C]ult Of The Lamb.exe" >/dev/null'
SESSION='XDG_RUNTIME_DIR=/run/user/1000 DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1000/bus'

# pgrep exits 1 when nothing matches; ssh exits 255 when the PC can't be reached, which says nothing.
rc=0; pc "$GAME_UP" || rc=$?
[ $rc = 1 ] || { [ $rc = 0 ] && echo "The game is running on the PC; not touching it." || echo "Can't reach the PC (ssh exit $rc)."; exit 1; }

# One capture at a time: mkdir is atomic, and a held lock is never ours to remove.
pc "mkdir $LOCK" || { echo "Another capture holds the PC ($LOCK there); not starting."; exit 1; }
trap 'pc "rmdir $LOCK"' EXIT

# Saves: compare the whole listing (new files too) with the last one; if it differs, back up, then re-list.
# Keeps this script's newest 5 backups (names sort by time); other backups are left alone.
pc "cd $SAVES && { sha256sum * | cmp -s - $SUMS || {
      b=~/cotl-api-saves-backup-\$(date +%Y%m%d-%H%M%S); cp -a . \"\$b\" && sha256sum * > $SUMS && echo \"saves backed up to \$b\" &&
      ls -d ~/cotl-api-saves-backup-* | sort -r | tail -n +6 | while read -r old; do rm -rf \"\$old\"; done; }; }" \
  || { echo "Couldn't check or back up the saves; not starting."; exit 1; }

cleanup() {
  trap '' INT
  echo "cleaning up on the PC"
  local tries=0
  until pc "pkill -f '[C]ult Of The Lamb.exe'; rm -rf $PLUGIN $CFG $OUT; $SESSION systemctl --user stop cotl-api-awake 2>/dev/null; rmdir $LOCK; true"; do
    tries=$((tries + 1))
    [ $tries -lt 10 ] || { echo "WARNING: cleanup failed; remove the plugin, cfg and output by hand"; break; }
    sleep 30
  done
  if pc "cd $SAVES && sha256sum * | cmp -s - $SUMS"; then echo "saves unchanged"; return 0; fi
  echo "WARNING: SAVES CHANGED (or couldn't be checked). Backups are in ~/cotl-api-saves-backup-* on the PC."
  return 1
}
trap cleanup EXIT

# Steam's Game Mode suspends the PC after an hour without controller input, and it may be close to that already.
# A user unit counts as the local session (a plain systemd-inhibit over SSH is refused); it ends on its own in 30 min.
pc "$SESSION systemd-run --user --collect --unit=cotl-api-awake systemd-inhibit --what=sleep:idle --who=cotl-api \
    --why='Data capture over SSH' --mode=block sleep 1800"

# The plugin acts only within 30 minutes of ArmedAt, so a cfg left behind by a failed cleanup does nothing later.
pc "rm -rf $OUT; printf '[General]\nArmedAt = %s\nSlot = 0\n\n[Jobs]\nItems = true\nStructures = true\nSkins = true\nRituals = true\n' \"\$(date +%s)\" > $CFG"
pc_stdin "cat > $PLUGIN" < mod/CotlDataDump.dll
# As a user unit, so the launcher isn't killed with the SSH session before it reaches Steam (a nohup'd one sometimes was).
launch() {
  pc "$SESSION systemd-run --user --collect --unit=cotl-api-launch-\$(date +%s) --setenv=DISPLAY=:0 \
      steam -applaunch 1313140 >>/tmp/cotl-launch.log 2>&1"
}
launch
echo "game launching; waiting for the dump (up to 10 minutes)"
seen=0
for i in $(seq 60); do
  sleep 10
  if [ "$i" = 12 ] && [ $seen = 0 ]; then echo "no game after 2 minutes; asking Steam again"; launch; fi
  rc=0; pc "test -f $OUT/done" || rc=$?
  [ $rc = 0 ] && break
  rc=0; pc "$GAME_UP" || rc=$?
  if [ $rc = 0 ]; then seen=1
  elif [ $rc = 1 ] && [ $seen = 1 ]; then echo "the game exited without finishing; see BepInEx/LogOutput.log on the PC"; exit 1
  fi  # 255: the PC dropped off the network for a moment; keep waiting
done
pc "test -f $OUT/done" || { echo "timed out after 10 minutes"; exit 1; }

# tar over ssh: no remote-path quoting to get wrong (rsync 3.2.4+ reads it differently), and the dump is small.
rm -rf "$DUMP" && mkdir "$DUMP"
pc "tar -C $OUT -cf - ." | tar -C "$DUMP" -xf -
# Clean the PC and check the saves now, before anything is packed or published.
trap - EXIT
cleanup || exit 1

python3 pack.py "$DUMP" site
git status --short site | tail -20
read -r -p "Commit and push this data? [y/N] " ok
if [ "$ok" = y ]; then
  git add site
  git commit -m "Data from game version $(python3 -c 'import json;print(json.load(open("site/v1/index.json"))["gameVersion"])')" -- site
  git push origin main
fi

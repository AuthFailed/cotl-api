"""publish.sh against a fake PC: ssh runs each remote command locally, steam drops the fixture dump."""
import os
import shutil
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).parent.parent
FAKES = {
    # ssh <opts> host 'cmd': fail with 255 when the command contains $FAKE_SSH_DOWN, else run it here
    "ssh": 'cmd="${@: -1}"; echo "$cmd" >> "$FAKE_LOG"\n'
           'if [ -n "${FAKE_SSH_DOWN:-}" ] && [[ "$cmd" == *"$FAKE_SSH_DOWN"* ]]; then exit 255; fi\n'
           # like real ssh, a command that doesn't read stdin still forwards (and so uses up) the caller's
           'if [ "$1" = -n ]; then exec bash -c "$cmd" < /dev/null; fi\n'
           'if [[ "$cmd" == "cat >"* ]]; then exec bash -c "$cmd"; fi\n'
           'cat > /dev/null; exec bash -c "$cmd" < /dev/null',
    "pgrep": 'exit "${FAKE_PGREP:-1}"',
    # systemd-run <--options> cmd...: log it, then run cmd here (with its --setenv values)
    "systemd-run": 'echo "systemd-run $*" >> "$FAKE_LOG"\n'
                   'while [[ "$1" == --* ]]; do [[ "$1" == --setenv=* ]] && export "${1#--setenv=}"; shift; done\n'
                   'exec "$@"',
    "systemd-inhibit": "exit 0",
    "systemctl": 'echo "systemctl $*" >> "$FAKE_LOG"',
    "date": 'if [ "$1" = +%s ]; then echo 1800000000; else /bin/date "$@"; fi',
    "pkill": "exit 0",
    "sleep": "exit 0",
    "steam": 'if [ -n "${FAKE_STEAM_IGNORES:-}" ]; then exit 0; fi; cat "$FAKE_CFG" >> "$FAKE_LOG"; mkdir -p "$FAKE_OUT" && cp -R "$FAKE_DUMP/." "$FAKE_OUT/"\n'
             'if [ -n "${FAKE_TOUCH_SAVES:-}" ]; then echo new > "$FAKE_SAVES/slot_9.json"; fi',
    # rsync -a --partial -e ... host:<escaped path>/ dest/
    "rsync": 'src="${@: -2:1}"; eval "p=${src#*:}"; mkdir -p "${@: -1}"; cp -R "$p/." "${@: -1}"',
}


class PublishTest(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(lambda: (os.chmod(self.tmp / "home", 0o755), shutil.rmtree(self.tmp)))
        self.repo = self.tmp / "repo"
        self.repo.mkdir()
        for f in ("publish.sh", "pack.py"):
            shutil.copy(ROOT / f, self.repo / f)
        shutil.copytree(ROOT / "schema", self.repo / "schema")
        (self.repo / "mod").mkdir()
        (self.repo / "mod" / "CotlDataDump.dll").write_bytes(b"dll")
        self.game = self.tmp / "Cult of the Lamb"
        for d in ("BepInEx/plugins", "BepInEx/config"):
            (self.game / d).mkdir(parents=True)
        self.saves = self.tmp / "saves dir"
        self.saves.mkdir()
        (self.saves / "slot_0.json").write_text("save")
        (self.tmp / "home").mkdir()
        (self.repo / ".env").write_text(
            f'COTL_SSH=pc\nCOTL_KEY=key\nCOTL_GAME_DIR="{self.game}"\nCOTL_SAVES_DIR="{self.saves}"\n'
            f'COTL_SUMS="{self.tmp / "sums"}"\n')
        bin = self.tmp / "bin"
        bin.mkdir()
        for name, body in FAKES.items():
            (bin / name).write_text("#!/usr/bin/env bash\n" + body + "\n")
            (bin / name).chmod(0o755)
        subprocess.run(["git", "init", "-q"], cwd=self.repo, check=True)
        self.env = {**os.environ, "PATH": f"{bin}:{os.environ['PATH']}", "HOME": str(self.tmp / "home"),
                    "FAKE_LOG": str(self.tmp / "ssh.log"), "FAKE_DUMP": str(ROOT / "tests" / "fixture-dump"),
                    "FAKE_OUT": str(self.game / "BepInEx/plugins/CotlDataDump"), "FAKE_SAVES": str(self.saves),
                    "FAKE_CFG": str(self.game / "BepInEx/config/chrsnv.cotldata.dump.cfg")}

    def run_publish(self, **env):
        r = subprocess.run(["bash", "publish.sh"], cwd=self.repo, input="n\n", capture_output=True, text=True,
                           env={**self.env, **env}, timeout=60)
        self.log = (self.tmp / "ssh.log").read_text() if (self.tmp / "ssh.log").exists() else ""
        return r

    def installed(self):
        return "cat >" in self.log

    def test_happy_path_cleans_up_before_packing(self):
        r = self.run_publish()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertTrue((self.repo / "site" / "v1" / "index.json").exists())
        self.assertFalse((self.game / "BepInEx/plugins/CotlDataDump.dll").exists())
        self.assertFalse((self.game / "BepInEx/config/chrsnv.cotldata.dump.cfg").exists())
        self.assertLess(r.stdout.index("saves unchanged"), r.stdout.index("items: 4"))

    def test_pc_kept_awake_from_launch_to_cleanup(self):
        self.run_publish()
        awake, launch, stop = (self.log.index(x) for x in ("systemd-inhibit", "steam -applaunch", "systemctl --user stop"))
        self.assertLess(awake, launch)
        self.assertLess(launch, stop)
        self.assertIn("--what=sleep:idle", self.log)

    def test_cfg_is_armed_with_the_pc_time(self):
        self.run_publish()  # the fake steam copies the cfg it finds into the log
        self.assertIn("ArmedAt = 1800000000\n", self.log)

    def test_game_is_launched_outside_the_ssh_session(self):
        self.run_publish()
        self.assertRegex(self.log, r"systemd-run --user --collect --unit=cotl-api-launch-\d+ .*steam -applaunch 1313140")

    def test_launch_is_retried_when_the_game_never_starts(self):
        r = self.run_publish(FAKE_STEAM_IGNORES="1")
        self.assertEqual(sum(l.startswith("systemd-run") and "applaunch" in l for l in self.log.splitlines()), 2)
        self.assertNotEqual(r.returncode, 0)

    def test_unreachable_pc_is_not_taken_as_game_stopped(self):
        r = self.run_publish(FAKE_SSH_DOWN="pgrep")
        self.assertNotEqual(r.returncode, 0)
        self.assertFalse(self.installed())

    def test_new_save_file_during_run_stops_before_packing(self):
        r = self.run_publish(FAKE_TOUCH_SAVES="1")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("SAVES CHANGED", r.stdout)
        self.assertFalse((self.repo / "site").exists())

    def test_failed_backup_stops_the_run(self):
        os.chmod(self.tmp / "home", 0o555)  # cp -a into ~/cotl-saves-backup-* fails
        r = self.run_publish()
        self.assertNotEqual(r.returncode, 0)
        self.assertFalse(self.installed())


if __name__ == "__main__":
    unittest.main()

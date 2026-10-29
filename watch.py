"""Open an issue when the game's public Steam build moves past the one the API was captured from.

python3 watch.py <site dir>   (needs the gh CLI and GH_TOKEN; run daily by .github/workflows/watch.yml)
"""
import json
import subprocess
import sys
import urllib.request
from pathlib import Path

APP = "1313140"
INFO = f"https://api.steamcmd.net/v1/info/{APP}"


def issue_title(captured, public, existing):
    """The issue to open, or None: nothing new, the captured build is unknown, or it's already been opened."""
    if captured is None or public == captured:
        return None
    title = f"Game updated: Steam build {public}"
    return None if title in existing else title


def main(site_dir: Path) -> int:
    index = json.loads((site_dir / "v1" / "index.json").read_text(encoding="utf-8"))
    with urllib.request.urlopen(INFO, timeout=30) as r:
        public = int(json.load(r)["data"][APP]["depots"]["branches"]["public"]["buildid"])
    existing = json.loads(subprocess.run(["gh", "issue", "list", "--state", "all", "--search", "Game updated in:title",
                                          "--json", "title", "--limit", "100"], check=True, capture_output=True, text=True).stdout)
    title = issue_title(index.get("steamBuildId"), public, [i["title"] for i in existing])
    print(f"captured build {index.get('steamBuildId')}, public build {public}: {'opening ' + repr(title) if title else 'nothing to do'}")
    if title:
        subprocess.run(["gh", "issue", "create", "--title", title, "--label", "data", "--body",
                        f"Steam's public build of Cult of the Lamb is now {public}; the API was captured from build "
                        f"{index['steamBuildId']} (game version {index['gameVersion']}, {index['capturedAt']}).\n\n"
                        "Run `./publish.sh` on the game PC to capture the new version. Close this once it's published."],
                       check=True)
    return 0


if __name__ == "__main__":
    sys.exit(main(Path(sys.argv[1])))

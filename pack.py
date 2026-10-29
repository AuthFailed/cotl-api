"""Turn a raw CotlDataDump folder into the public v1 API: python3 pack.py <dump dir> <site dir>"""
import json
import re
import shutil
import sys
from pathlib import Path

KINDS = ("items", "structures", "skins")
SAFE_ID = re.compile(r"^[A-Za-z0-9_-]+$")
# The game's localisation sheet also has non-language columns ("Chara Count", "Comments", ...).
LANGUAGE = re.compile(r"^[a-z]{2}(-[A-Za-z]{2})?$")


def load_dump(dump_dir: Path) -> dict:
    return {name: json.loads((dump_dir / f"{name}.json").read_text(encoding="utf-8"))
            for name in (*KINDS, "i18n", "meta")}


def write_json(path: Path, obj) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(obj, indent=2, sort_keys=True, ensure_ascii=False) + "\n", encoding="utf-8")


def texts(i18n: dict, term) -> dict:
    return {lang: text for lang, text in i18n.get(term, {}).items() if text and LANGUAGE.match(lang)} if term else {}


def build_site(dump: dict, dump_dir: Path, site_dir: Path) -> dict:
    tmp = site_dir / "v1.tmp"
    shutil.rmtree(tmp, ignore_errors=True)
    for kind in KINDS:
        entities = []
        for raw in sorted(dump[kind], key=lambda e: e["id"]):
            e = {k: v for k, v in raw.items() if k not in ("nameTerm", "descTerm")}
            e["name"] = texts(dump["i18n"], raw.get("nameTerm"))
            e["description"] = texts(dump["i18n"], raw.get("descTerm"))
            icon = dump_dir / "icons" / kind / f"{raw['id']}.png"
            e["icon"] = None
            if icon.exists():
                e["icon"] = f"icons/{kind}/{raw['id']}.png"
                (tmp / "icons" / kind).mkdir(parents=True, exist_ok=True)
                shutil.copyfile(icon, tmp / e["icon"])
            write_json(tmp / kind / f"{e['id']}.json", e)
            entities.append(e)
        write_json(tmp / f"{kind}.json", entities)
    index = {
        "gameVersion": dump["meta"]["gameVersion"],
        "capturedAt": dump["meta"]["capturedAt"],
        "languages": sorted({lang for t in dump["i18n"].values() for lang, text in t.items() if text and LANGUAGE.match(lang)}),
        "counts": {kind: len(dump[kind]) for kind in KINDS},
    }
    write_json(tmp / "index.json", index)
    shutil.copytree(Path(__file__).parent / "schema", tmp / "schema")
    # Swap in whole so ids the game dropped don't linger as stale files.
    shutil.rmtree(site_dir / "v1", ignore_errors=True)
    tmp.rename(site_dir / "v1")
    return index


def problems(dump: dict, previous_index) -> list:
    found = [f"job {e['job']} failed: {e['message']}" for e in dump["meta"].get("errors", [])]
    for kind in KINDS:
        ids = [e["id"] for e in dump[kind]]
        found += [f"{kind}: duplicate id {id}" for id in sorted({i for i in ids if ids.count(i) > 1})]
        found += [f"{kind}: id {id!r} is not safe as a file name" for id in ids if not SAFE_ID.match(id)]
        found += [f"{kind}: {e['id']} has no English name" for e in dump[kind]
                  if not texts(dump["i18n"], e.get("nameTerm")).get("en")]
        before = (previous_index or {}).get("counts", {}).get(kind, 0)
        if len(ids) < before * 0.9:
            found.append(f"{kind}: {len(ids)} now, {before} before (more than 10% fewer)")
    return found


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8")) if path.exists() else None


def main(argv: list) -> int:
    dump_dir, site_dir = Path(argv[1]), Path(argv[2])
    dump = load_dump(dump_dir)
    found = problems(dump, read_json(site_dir / "v1" / "index.json"))
    if found:
        print("Not packing:", *found, sep="\n  ")
        return 1
    before = {kind: {e["id"] for e in read_json(site_dir / "v1" / f"{kind}.json") or []} for kind in KINDS}
    build_site(dump, dump_dir, site_dir)
    for kind in KINDS:
        now = {e["id"] for e in dump[kind]}
        print(f"{kind}: {len(now)}, added {sorted(now - before[kind]) or 'none'}, removed {sorted(before[kind] - now) or 'none'}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

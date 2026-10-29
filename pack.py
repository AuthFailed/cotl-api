"""Turn a raw CotlDataDump folder into the public v1 API: python3 pack.py <dump dir> <site dir>"""
import json
import shutil
import sys
from pathlib import Path

KINDS = ("items", "structures", "skins")


def load_dump(dump_dir: Path) -> dict:
    return {name: json.loads((dump_dir / f"{name}.json").read_text(encoding="utf-8"))
            for name in (*KINDS, "i18n", "meta")}


def write_json(path: Path, obj) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(obj, indent=2, sort_keys=True, ensure_ascii=False) + "\n", encoding="utf-8")


def texts(i18n: dict, term) -> dict:
    return {lang: text for lang, text in i18n.get(term, {}).items() if text} if term else {}


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
        "languages": sorted({lang for t in dump["i18n"].values() for lang, text in t.items() if text}),
        "counts": {kind: len(dump[kind]) for kind in KINDS},
    }
    write_json(tmp / "index.json", index)
    # Swap in whole so ids the game dropped don't linger as stale files.
    shutil.rmtree(site_dir / "v1", ignore_errors=True)
    tmp.rename(site_dir / "v1")
    return index


def main(argv: list) -> int:
    dump_dir, site_dir = Path(argv[1]), Path(argv[2])
    build_site(load_dump(dump_dir), dump_dir, site_dir)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

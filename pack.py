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


def plain(text: str) -> str:
    """The game's text without its TextMeshPro markup: <color>, <sprite>, <br>, and any other tag (one text
    closes a <color> with </cult>)."""
    text = re.sub(r"<br\s*/?>", "\n", text)
    text = re.sub(r"<[^>]*>", "", text)
    text = re.sub(r"[ \t]{2,}", " ", text)
    return re.sub(r"[ \t]+([.,!?;:])", r"\1", "\n".join(line.strip() for line in text.split("\n"))).strip()


def effect(i18n: dict, raw: dict) -> dict:
    """A meal effect with its text in every language; the game puts the chance where the text says {0}."""
    description = {lang: t.replace("{0}", str(raw["chance"])) for lang, t in texts(i18n, raw["descTerm"]).items()}
    return {"type": raw["type"], "chance": raw["chance"], "description": description,
            "descriptionPlain": {lang: plain(t) for lang, t in description.items()}}


def changed(old: dict, new: dict) -> bool:
    # Only fields both versions have: a field the API adds is not a change in the game.
    return any(old[k] != new[k] for k in old.keys() & new.keys())


def tree(root: Path) -> dict:
    return {p.relative_to(root).as_posix(): p.read_bytes() for p in root.rglob("*") if p.is_file()} if root.exists() else {}


SCHEMAS = {"index": "Index", "item": "Item", "structure": "Structure", "skin": "Skin", "changes": "Changes"}


def openapi(index: dict, ids: dict, icons: dict) -> dict:
    """OpenAPI description of the site, for the docs page; ids/icons: per kind, the ids that have a file."""
    schema_dir = Path(__file__).parent / "schema"
    schemas = {}
    for file, name in SCHEMAS.items():
        s = json.loads((schema_dir / f"{file}.schema.json").read_text(encoding="utf-8"))
        s.pop("$schema", None)
        schemas[name] = s

    def get(tag, summary, content, param=None):
        op = {"tags": [tag], "summary": summary, "responses": {"200": {"description": "OK", "content": content},
                                                "404": {"description": "No such id"}}}
        if param:
            op["parameters"] = [{"name": "id", "in": "path", "required": True, "schema": {"type": "string", "enum": param}}]
        return {"get": op}

    def ref(name, many=False):
        s = {"$ref": f"#/components/schemas/{name}"}
        return {"application/json": {"schema": {"type": "array", "items": s} if many else s}}

    paths = {"/v1/index.json": get("index", "Game version, capture date, languages and counts", ref("Index"))}
    paths["/v1/changes.json"] = get("index", "What each game version added, removed or changed, newest first", ref("Changes"))
    for kind in KINDS:
        name = SCHEMAS[kind[:-1]]
        paths[f"/v1/{kind}.json"] = get(kind, f"Every {kind[:-1]}", ref(name, many=True))
        paths[f"/v1/{kind}/{{id}}.json"] = get(kind, f"One {kind[:-1]}", ref(name), sorted(ids[kind]))
        if icons[kind]:
            paths[f"/v1/icons/{kind}/{{id}}.png"] = get(kind, f"A {kind[:-1]}'s icon", {"image/png": {}}, sorted(icons[kind]))
    return {
        "openapi": "3.1.0",
        "info": {
            "title": "Cult of the Lamb data API",
            "version": index["gameVersion"],
            "description": "Items, structures and follower skins from *Cult of the Lamb*, taken from the game "
                           f"(version {index['gameVersion']}, captured {index['capturedAt']}). Static JSON: no keys, "
                           "no rate limits, open CORS. Within `v1/` fields are only added, never removed.\n\n"
                           "Unofficial fan project; the game, its data and art belong to Massive Monster and Devolver Digital. "
                           "Source: [github.com/AuthFailed/cotl-api](https://github.com/AuthFailed/cotl-api).",
        },
        "servers": [{"url": "/"}],
        "tags": [{"name": "index", "description": "What this data is"},
                 {"name": "items", "description": "Resources, food, seeds, necklaces and everything else in the inventory"},
                 {"name": "structures", "description": "Buildings, decorations and tiles: cost, upgrade chain, footprint"},
                 {"name": "skins", "description": "Follower forms: where they drop, whether hidden until unlocked"}],
        "paths": paths,
        "components": {"schemas": schemas},
    }


def build_site(dump: dict, dump_dir: Path, site_dir: Path) -> dict:
    tmp = site_dir / "v1.tmp"
    shutil.rmtree(tmp, ignore_errors=True)
    ids, icons = {}, {}
    for kind in KINDS:
        entities = []
        for raw in sorted(dump[kind], key=lambda e: e["id"]):
            e = {k: v for k, v in raw.items() if k not in ("nameTerm", "descTerm")}
            e["name"] = texts(dump["i18n"], raw.get("nameTerm"))
            e["description"] = texts(dump["i18n"], raw.get("descTerm"))
            e["namePlain"] = {lang: plain(t) for lang, t in e["name"].items()}
            e["descriptionPlain"] = {lang: plain(t) for lang, t in e["description"].items()}
            if e.get("meal"):
                e["meal"] = dict(e["meal"], effects=[effect(dump["i18n"], x) for x in e["meal"]["effects"]])
            icon = dump_dir / "icons" / kind / f"{raw['id']}.png"
            e["icon"] = None
            if icon.exists():
                e["icon"] = f"icons/{kind}/{raw['id']}.png"
                (tmp / "icons" / kind).mkdir(parents=True, exist_ok=True)
                shutil.copyfile(icon, tmp / e["icon"])
                icons.setdefault(kind, []).append(e["id"])
            write_json(tmp / kind / f"{e['id']}.json", e)
            entities.append(e)
        write_json(tmp / f"{kind}.json", entities)
        ids[kind] = [e["id"] for e in entities]
        icons.setdefault(kind, [])
    index = {
        "gameVersion": dump["meta"]["gameVersion"],
        "capturedAt": dump["meta"]["capturedAt"],
        "languages": sorted({lang for t in dump["i18n"].values() for lang, text in t.items() if text and LANGUAGE.match(lang)}),
        "counts": {kind: len(dump[kind]) for kind in KINDS},
    }
    old_dir = site_dir / "v1"
    old_index = read_json(old_dir / "index.json")
    changes = read_json(old_dir / "changes.json") or []
    if old_index:
        diff = {"added": {}, "removed": {}, "changed": {}}
        for kind in KINDS:
            old_ids = {e["id"] for e in read_json(old_dir / f"{kind}.json") or []}
            new_ids = set(ids[kind])
            diff["added"][kind] = sorted(new_ids - old_ids)
            diff["removed"][kind] = sorted(old_ids - new_ids)
            diff["changed"][kind] = sorted(i for i in new_ids & old_ids if
                                           changed(read_json(old_dir / kind / f"{i}.json"), read_json(tmp / kind / f"{i}.json")))
        if any(v for d in diff.values() for v in d.values()):
            changes = [{"gameVersion": index["gameVersion"], "capturedAt": index["capturedAt"], **diff}] + changes
    write_json(tmp / "changes.json", changes)
    shutil.copytree(Path(__file__).parent / "schema", tmp / "schema")
    # Recapturing the same game shouldn't change the site: keep the old capture time if nothing else moved.
    if old_index and old_index.get("gameVersion") == index["gameVersion"] and \
            {k: v for k, v in tree(old_dir).items() if k != "index.json"} == tree(tmp):
        index["capturedAt"] = old_index["capturedAt"]
    write_json(tmp / "index.json", index)
    # Swap in whole so ids the game dropped don't linger as stale files.
    shutil.rmtree(site_dir / "v1", ignore_errors=True)
    tmp.rename(site_dir / "v1")
    write_json(site_dir / "openapi.json", openapi(index, ids, icons))
    return index


def problems(dump: dict, dump_dir: Path, site_dir: Path) -> list:
    found = [f"job {e['job']} failed: {e['message']}" for e in dump["meta"].get("errors", [])]
    previous = read_json(site_dir / "v1" / "index.json")
    for kind in KINDS:
        ids = [e["id"] for e in dump[kind]]
        if not ids:
            found.append(f"{kind}: none in the dump")
        found += [f"{kind}: duplicate id {id}" for id in sorted({i for i in ids if ids.count(i) > 1})]
        found += [f"{kind}: id {id!r} is not safe as a file name" for id in ids if not SAFE_ID.match(id)]
        found += [f"{kind}: {e['id']} has no English name" for e in dump[kind]
                  if not texts(dump["i18n"], e.get("nameTerm")).get("en")]
        found += [f"{kind}: {e['id']} effect {x['type']} has no English text" for e in dump[kind]
                  for x in (e.get("meal") or {}).get("effects", []) if not texts(dump["i18n"], x["descTerm"]).get("en")]
        # A game update can break one lookup and leave every icon or size empty while the rest looks fine.
        old = read_json(site_dir / "v1" / f"{kind}.json") or []
        for what, now, before in (
                ("entries", len(ids), (previous or {}).get("counts", {}).get(kind, 0)),
                ("icons", sum((dump_dir / "icons" / kind / f"{i}.png").exists() for i in ids), sum(bool(e.get("icon")) for e in old)),
                ("sizes", sum(bool(e.get("size")) for e in dump[kind]), sum(bool(e.get("size")) for e in old))):
            if now < before * 0.9:
                found.append(f"{kind}: {now} {what} now, {before} before (more than 10% fewer)")
    languages = {lang for t in dump["i18n"].values() for lang, text in t.items() if text and LANGUAGE.match(lang)}
    found += [f"language {lang} is gone" for lang in sorted(set((previous or {}).get("languages", [])) - languages)]
    return found


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8")) if path.exists() else None


def main(argv: list) -> int:
    dump_dir, site_dir = Path(argv[1]), Path(argv[2])
    dump = load_dump(dump_dir)
    found = problems(dump, dump_dir, site_dir)
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

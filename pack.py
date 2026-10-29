"""Turn a raw CotlDataDump folder into the public v1 API: python3 pack.py <dump dir> <site dir>"""
import json
import re
import shutil
import sys
from pathlib import Path

from PIL import Image

KINDS = ("items", "structures", "skins", "rituals")
SAFE_ID = re.compile(r"[A-Za-z0-9_-]+")
# The game's localisation sheet also has non-language columns ("Chara Count", "Comments", ...).
LANGUAGE = re.compile(r"[a-z]{2,3}(-[A-Za-z0-9]{2,8})*")
# The raw fields each kind publishes (name, description, icon and cross-references are added by the packer). A new
# field the mod dumps stays private until it is listed here, because within v1 a published field is forever.
PUBLIC = {
    "items": ("category", "tags", "meal", "seed", "crop"),
    "structures": ("cost", "upgradesFrom", "size", "menu", "buildable", "category"),
    "skins": ("unlock", "hidden"),
    "rituals": ("cost", "faith", "warmth", "sin", "trait", "group", "alternative", "doctrine"),
}


def load_dump(dump_dir: Path) -> dict:
    return {name: json.loads((dump_dir / f"{name}.json").read_text(encoding="utf-8"))
            for name in (*KINDS, "i18n", "meta")}


def write_json(path: Path, obj) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(obj, indent=2, sort_keys=True, ensure_ascii=False) + "\n", encoding="utf-8")


def texts(i18n: dict, term) -> dict:
    return {lang: text for lang, text in i18n.get(term, {}).items() if text and LANGUAGE.fullmatch(lang)} if term else {}


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


THUMB = 64


def thumbnail(src: Path, dst: Path) -> None:
    """The icon fitted into THUMB x THUMB, aspect kept; small icons are copied as they are."""
    with Image.open(src) as im:
        if max(im.size) <= THUMB:
            shutil.copyfile(src, dst)
            return
        im = im.convert("RGBA")
        im.thumbnail((THUMB, THUMB), Image.Resampling.LANCZOS)
        im.save(dst, optimize=True)


def changed(old: dict, new: dict) -> bool:
    # Only fields both versions have: a field the API adds is not a change in the game.
    return any(old[k] != new[k] for k in old.keys() & new.keys())


def tree(root: Path) -> dict:
    return {p.relative_to(root).as_posix(): p.read_bytes() for p in root.rglob("*") if p.is_file()} if root.exists() else {}


SCHEMAS = {"index": "Index", "item": "Item", "structure": "Structure", "skin": "Skin", "ritual": "Ritual", "changes": "Changes", "slim": "Slim"}


def openapi(index: dict, ids: dict, icons: dict, examples: dict, changes: list) -> dict:
    """OpenAPI description of the site, for the docs page; ids/icons: per kind, the ids that have a file;
    examples: per kind, the first entities, shown as real example responses."""
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

    def ref(name, many=False, example=None):
        s = {"$ref": f"#/components/schemas/{name}"}
        return {"application/json": {"schema": {"type": "array", "items": s} if many else s, "example": example}}

    paths = {"/v1/index.json": get("index", "Game version, capture date, languages and counts", ref("Index", example=index))}
    paths["/v1/changes.json"] = get("index", "What each game version added, removed or changed, newest first", ref("Changes", example=changes[:1]))
    for kind in KINDS:
        name = SCHEMAS[kind[:-1]]
        paths[f"/v1/{kind}.json"] = get(kind, f"Every {kind[:-1]}", ref(name, many=True, example=examples[kind][:2]))
        paths[f"/v1/{kind}/{{id}}.json"] = get(kind, f"One {kind[:-1]}", ref(name, example=(examples[kind] or [None])[0]), sorted(ids[kind]))
        paths[f"/v1/{{lang}}/{kind}.json"] = {"get": {
            "tags": [kind], "summary": f"Every {kind[:-1]}: id, name in one language, thumbnail",
            "parameters": [{"name": "lang", "in": "path", "required": True, "schema": {"type": "string", "enum": index["languages"]}}],
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {"$ref": "#/components/schemas/Slim"}}}}}}}
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
                           "What changed with each game version: [changes.json](/v1/changes.json). Field-by-field "
                           "documentation and examples: the [README](https://github.com/AuthFailed/cotl-api#readme).\n\n"
                           "Unofficial fan project; the game, its data and art belong to Massive Monster and Devolver Digital. "
                           "Source: [github.com/AuthFailed/cotl-api](https://github.com/AuthFailed/cotl-api).",
        },
        "externalDocs": {"description": "README", "url": "https://github.com/AuthFailed/cotl-api#readme"},
        "servers": [{"url": "/"}],
        "tags": [{"name": "index", "description": "What this data is"},
                 {"name": "items", "description": "Resources, food, seeds, necklaces and everything else in the inventory"},
                 {"name": "structures", "description": "Buildings, decorations and tiles: cost, upgrade chain, footprint"},
                 {"name": "skins", "description": "Follower forms: where they drop, whether hidden until unlocked"},
                 {"name": "rituals", "description": "Rituals: cost, effect on faith, warmth and sin, the doctrine that unlocks them"}],
        "paths": paths,
        "components": {"schemas": schemas},
    }


def cross_references(dump: dict):
    """Where each item is used (structure and ritual costs, recipes), and what each structure upgrades into."""
    used = {i["id"]: {"structures": set(), "items": set(), "rituals": set()} for i in dump["items"]}
    for kind, entity, costs in (
            [("structures", s, [s.get("cost", [])]) for s in dump["structures"]] +
            [("rituals", r, [r.get("cost", [])]) for r in dump["rituals"]] +
            [("items", i, (i.get("meal") or {}).get("recipes", [])) for i in dump["items"]]):
        for cost in costs:
            for c in cost:
                if c["item"] in used:
                    used[c["item"]][kind].add(entity["id"])
    upgrades = {}
    for st in dump["structures"]:
        if st.get("upgradesFrom"):
            upgrades.setdefault(st["upgradesFrom"], []).append(st["id"])
    return ({i: {k: sorted(v) for k, v in u.items()} for i, u in used.items()},
            {i: sorted(v) for i, v in upgrades.items()})


def build_site(dump: dict, dump_dir: Path, site_dir: Path) -> dict:
    tmp = site_dir / "v1.tmp"
    shutil.rmtree(tmp, ignore_errors=True)
    ids, icons = {}, {}
    item_ids = {e["id"] for e in dump["items"]}
    used_in, upgrades_to = cross_references(dump)
    for kind in KINDS:
        entities = []
        for raw in sorted(dump[kind], key=lambda e: e["id"]):
            e = {"id": raw["id"], **{k: raw[k] for k in PUBLIC[kind] if k in raw}}
            e["name"] = texts(dump["i18n"], raw.get("nameTerm"))
            e["description"] = texts(dump["i18n"], raw.get("descTerm"))
            e["namePlain"] = {lang: plain(t) for lang, t in e["name"].items()}
            e["descriptionPlain"] = {lang: plain(t) for lang, t in e["description"].items()}
            # A seed or crop the game never names isn't published; don't point at it.
            if e.get("seed") and e["seed"] not in item_ids:
                e["seed"] = None
            if e.get("crop") and e["crop"]["grows"] not in item_ids:
                e["crop"] = dict(e["crop"], grows=None)
            if kind == "items":
                e["usedIn"] = used_in[e["id"]]
            if kind == "structures":
                e["upgradesTo"] = upgrades_to.get(e["id"], [])
            if e.get("meal"):
                e["meal"] = dict(e["meal"], effects=[effect(dump["i18n"], x) for x in e["meal"]["effects"]])
            icon = dump_dir / "icons" / kind / f"{raw['id']}.png"
            e["icon"] = e["thumb"] = None
            if icon.exists():
                e["icon"] = f"icons/{kind}/{raw['id']}.png"
                e["thumb"] = f"icons/{kind}/{THUMB}/{raw['id']}.png"
                (tmp / "icons" / kind / str(THUMB)).mkdir(parents=True, exist_ok=True)
                shutil.copyfile(icon, tmp / e["icon"])
                thumbnail(icon, tmp / e["thumb"])
                icons.setdefault(kind, []).append(e["id"])
            write_json(tmp / kind / f"{e['id']}.json", e)
            entities.append(e)
        write_json(tmp / f"{kind}.json", entities)
        ids[kind] = [e["id"] for e in entities]
        icons.setdefault(kind, [])
    index = {
        "gameVersion": dump["meta"]["gameVersion"],
        "capturedAt": dump["meta"]["capturedAt"],
        "languages": sorted({lang for t in dump["i18n"].values() for lang, text in t.items() if text and LANGUAGE.fullmatch(lang)}),
        "counts": {kind: len(dump[kind]) for kind in KINDS},
    }
    # Per language, just what a list or a picker needs: id, name (English if untranslated), thumbnail.
    for lang in index["languages"]:
        for kind in KINDS:
            write_json(tmp / lang / f"{kind}.json", [
                {"id": e["id"], "name": e["name"].get(lang) or e["name"]["en"], "thumb": e["thumb"]}
                for e in read_json(tmp / f"{kind}.json")])
    old_dir = site_dir / "v1"
    old_index = read_json(old_dir / "index.json")
    changes = read_json(old_dir / "changes.json") or []
    # Only a new game version gets an entry; a recapture of the same one changes the API, not the game.
    if old_index and old_index.get("gameVersion") != index["gameVersion"]:
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
    examples = {kind: read_json(site_dir / "v1" / f"{kind}.json")[:2] for kind in KINDS}
    write_json(site_dir / "openapi.json", openapi(index, ids, icons, examples, read_json(site_dir / "v1" / "changes.json")))
    return index


def problems(dump: dict, dump_dir: Path, site_dir: Path) -> list:
    found = [f"job {e['job']} failed: {e['message']}" for e in dump["meta"].get("errors", [])]
    previous = read_json(site_dir / "v1" / "index.json")
    for kind in KINDS:
        ids = [e["id"] for e in dump[kind]]
        if not ids:
            found.append(f"{kind}: none in the dump")
        lower = [i.lower() for i in ids]  # ids are file names, and macOS file names ignore case
        found += [f"{kind}: duplicate id {id}" for id in sorted({i for i in ids if lower.count(i.lower()) > 1})]
        found += [f"{kind}: id {id!r} is not safe as a file name" for id in ids if not SAFE_ID.fullmatch(id)]
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
    languages = {lang for t in dump["i18n"].values() for lang, text in t.items() if text and LANGUAGE.fullmatch(lang)}
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

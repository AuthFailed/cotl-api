# Cult of the Lamb data API: design

A public, static JSON API with *Cult of the Lamb* game data, served from GitHub Pages and built from a
dump that a BepInEx plugin takes from the running game in a few minutes.

## Goals

- Public and documented: other people's tools, bots and sites can depend on it.
- v1 covers **items**, **structures** and **skins**, with names in every game language and a small icon each.
- One command on the Mac captures, packs and publishes a new version after a game update.
- Saves are never at risk.

Not in v1: recipes, tarot, rituals, doctrines, weapons, crops; building renders and backgrounds (they stay
in the planner); query endpoints. The planner keeps its own data for now; switching it to this API is a later decision.

## Repository

A new public GitHub repository (`cotl-api`), separate from the planner:

```
mod/CotlDataDump.cs, build.sh     BepInEx plugin
pack.py                           raw dump -> site/v1/
publish.sh                        capture + pack + publish
schema/*.schema.json              JSON Schemas for index, items, structures, skins
site/v1/...                       generated API, committed
tests/fixture-dump/               small raw dump for the packer test
.github/workflows/pages.yml       validate and deploy site/
README.md                         API documentation
```

No machine names, addresses or key paths are committed: `publish.sh` reads them from an untracked `.env`.

## The mod: `CotlDataDump`

A BepInEx 5 plugin built like the planner's `tools/capture/build.sh` (csc against the game's `Managed`
assemblies, BepInEx.dll, 0Harmony.dll).

- Starts its work on the main menu once localisation and Addressables are ready. It never continues or
  loads a save, and patches out saving as a safeguard.
- Each job runs inside a `Try` that records failures instead of stopping the run:
  - **Items**: every `InventoryItem.ITEM_TYPE`: id, name and description terms, category, icon (via
    `InventoryIconMapping.GetImage`, as the planner's `DumpItems` does).
  - **Structures**: every `StructureBrain.TYPES`: id, name and description terms, cost
    (`StructuresData.GetCost`), the type it upgrades from (`StructuresData.GetInfoByType`), footprint size,
    range indicators, icon.
  - **Skins**: every follower skin in the game's skin table: id, name term, unlock source, icon.
  - **Translations**: the terms the jobs above reference, in every language.
- Writes a raw dump to `BepInEx/plugins/CotlDataDump/`: `items.json`, `structures.json`, `skins.json`,
  `i18n.json`, `meta.json` (game version, plugin version, per-job errors), `icons/{kind}/{id}.png`, then a
  `done` marker, and kills the process (Quit is swallowed under Proton).
- The dump is game-shaped (localisation terms, raw enum names). Reshaping the public format happens in the
  packer, so it never needs a new game run.

### First task: feasibility spike

Before building the jobs, confirm in the decompiled code (or a throwaway run) that from the main menu,
without a save:

1. the follower skin list and each skin's unlock source can be read;
2. `StructuresData.GetCost` gives the base costs (the planner's job notes it lists costs "for this save");
3. structure footprints and ranges can be read from prefabs or data defaults;
4. `InventoryIconMapping` instances are loaded.

Any item that needs a loaded save moves the mod to auto-continuing save slot 0 as the planner's capture
does, with all saving blocked, and that is recorded in this spec before going on.

## Public format: `site/v1/`

```
v1/index.json                 { gameVersion, capturedAt, languages[], counts{items,structures,skins} }
v1/items.json                 [ item, ... ]       v1/items/{id}.json
v1/structures.json            [ structure, ... ]  v1/structures/{id}.json
v1/skins.json                 [ skin, ... ]       v1/skins/{id}.json
v1/icons/{items|structures|skins}/{id}.png
v1/schema/*.schema.json
```

`{id}` is the game's enum or skin name (e.g. `BERRY`, `FARM_PLOT`), stable across game versions.

Common fields: `id`, `name` and `description` as `{ "<lang>": "<text>" }` maps (missing languages left
out), `icon` (path relative to `v1/`, or null).

- item: `category`
- structure: `cost: [{ item, quantity }]`, `upgradesFrom` (id or null), `size: { w, h }`, `ranges`
- skin: `unlock` (the game's unlock source, as a string)

Lists are sorted by id; JSON is written with sorted keys so an unchanged game gives an unchanged site.

**Compatibility:** within `v1/` fields are only added, never renamed or removed. A breaking change ships as
`v2/` alongside `v1/`. Each publish is one commit, so the git history is the changelog per game version.

## Packer: `pack.py`

Python 3, standard library only. `pack.py <dump dir> <site dir>`:

1. Reads the raw dump, resolves terms into language maps, writes `site/v1/` as above, copies icons.
2. Refuses to write anything (non-zero exit, reasons listed) if:
   - `meta.json` reports any job error;
   - an entity has no English name;
   - a kind's count is more than 10% below the current `site/v1/index.json`.
3. Prints a summary: added and removed ids per kind.

## Publishing: `publish.sh`

Runs on the Mac, following the existing capture protocol (local notes), shortened for a run of minutes:

1. Abort if the game is running (`pgrep -f "[C]ult Of The Lamb.exe"`).
2. Check the save checksums; on mismatch or no list, back up the saves and rewrite the list.
3. Remove old dump output, write a fresh cfg, install the DLL (`ssh … 'cat > …' <`).
4. Launch through Steam in the background; wait for the `done` marker or the game exiting, 10-minute timeout.
5. rsync the dump; remove the DLL, cfg and output on the PC; re-check the save checksums.
6. Run `pack.py`, show the summary, ask before committing and pushing.

No keep-awake unit: Game Mode only suspends after an hour without input.

## Deploy

`.github/workflows/pages.yml` on push to `main`: validate every `site/v1/**/*.json` against its schema with
`check-jsonschema`, then upload `site/` as the Pages artifact and deploy. GitHub Pages already sends
`Access-Control-Allow-Origin: *`. Site size stays far under the 1 GB limit (JSON plus a few thousand small PNGs).

## Testing

- `tests/fixture-dump/` (a handful of items, structures, skins, two languages) packed by `pack.py`; the result
  must validate against the schemas and match a checked-in expected tree. Runs in CI before deploy.
- Packer refusal cases (job error, missing English name, count drop) each covered by the same test script.
- The mod is verified by its first real run: counts in `index.json` compared with the planner's existing
  item and structure lists.

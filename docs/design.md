# Cult of the Lamb data API: design

A public, static JSON API with *Cult of the Lamb* game data, served from GitHub Pages and built from a
dump that a BepInEx plugin takes from the running game in a few minutes.

## Goals

- Public and documented: other people's tools, bots and sites can depend on it.
- v1 covers **items**, **structures** and **skins**, with names in every game language and a small icon
  for items and structures.
- One command on the Mac captures, packs and publishes a new version after a game update.
- Saves are never at risk.

Not in v1: recipes, tarot, rituals, doctrines, weapons, crops; building renders and backgrounds (they stay
in the planner); query endpoints; structure range indicators and skin portraits (later, additive). The planner keeps its own data for now; switching it to this API is a later decision.

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

- Works in two phases, with saving patched out throughout:
  1. **Main menu**, before any save loads: costs (`StructuresData.GetCost` applies the loaded save's
     `Economy_MineII` discount, so it must run while `DataManager` is still the fresh, empty one), item and
     structure data, skins, translations.
  2. **Base scene**: the mod continues save slot 0 as the planner's capture does, waits for the base, then
     reads footprints and icons, which only exist there.
- Each job runs inside a `Try` that records failures instead of stopping the run:
  - **Items** (phase 1, icons phase 2): every name of `InventoryItem.ITEM_TYPE`: id, name term
    `Inventory/{id}`, description term `Inventory/{id}/Description`, category
    (`InventoryItem.GetItemCategory`), icon (via `InventoryIconMapping.GetImage`, as the planner's `DumpItems` does).
  - **Structures** (phase 1, footprint and icon phase 2): every name of `StructureBrain.TYPES`: id, name
    term `Structures/{id}`, description term `Structures/{id}/Description`, cost (`StructuresData.GetCost`),
    the type it upgrades from (`StructuresData.GetInfoByType`), footprint (`PlacementObject.Bounds` of the
    entry's placement prefab in the base scene's `TypeAndPlacementObjects`), icon (that entry's `IconImage`).
  - **Skins** (phase 1): every entry of `WorshipperData.Instance.Characters` that is not `Invariant`: id,
    English title (`Title`; the game has no translated skin names), unlock source (`DropLocation`), hidden flag.
  - **Translations**: the terms the jobs above reference, in every language.
- Writes a raw dump to `BepInEx/plugins/CotlDataDump/`: `items.json`, `structures.json`, `skins.json`,
  `i18n.json`, `meta.json` (game version, plugin version, per-job errors), `icons/{kind}/{id}.png`, then a
  `done` marker, and kills the process (Quit is swallowed under Proton).
- The dump is game-shaped (localisation terms, raw enum names). Reshaping the public format happens in the
  packer, so it never needs a new game run.

### Feasibility findings (from the decompiled game code)

1. **Skins**: `WorshipperData.Instance` loads `Resources/Prefabs/Worshipper Data`, no save needed. The unlock
   source is `SkinAndData.DropLocation` (`Dungeon1`…`Dungeon6`, `Other`, `DLC`, `SpecialEvents`, `Major_DLC`).
   Skins have no translated names and no icon sprite: the game draws them live with Spine
   (`UINewItemOverlayController`). v1 skins get the English title and `icon: null`; a rendered portrait is
   a later addition.
2. **Costs**: `GetCost` multiplies by `UpgradeSystem.GetPriceModifier` when the save has `Economy_MineII`,
   and always doubles `BLACK_GOLD`/`GOLD_REFINED` (that doubling is the base price). Read before any save loads.
3. **Footprints** are not in `GetInfoByType` (only one type sets `Bounds`); they come from the placement
   prefabs listed by `TypeAndPlacementObjects`, a base-scene object, which also holds each structure's icon.
   **Ranges** are sized in code per building script (e.g. `Scarecrow.EFFECTIVE_DISTANCE`,
   `Structures_Shrine_Passive.Range`), not readable generically: left out of v1, to be added later.
4. **Item icons**: `InventoryIconMapping` is a ScriptableObject held by UI prefabs; read in phase 2.

## Public format: `site/v1/`

```
v1/index.json                 { gameVersion, capturedAt, languages[], counts{items,structures,skins} }
v1/items.json                 [ item, ... ]       v1/items/{id}.json
v1/structures.json            [ structure, ... ]  v1/structures/{id}.json
v1/skins.json                 [ skin, ... ]       v1/skins/{id}.json
v1/icons/{items|structures|skins}/{id}.png
v1/schema/*.schema.json
```

`{id}` is the game's enum name (e.g. `BERRY`, `FARM_PLOT`) or the skin's Spine name with any character
outside `A-Za-z0-9_-` replaced by `_`, stable across game versions.

Common fields: `id`, `name` and `description` as `{ "<lang>": "<text>" }` maps (missing languages left
out), `icon` (path relative to `v1/`, or null).

- item: `category`
- structure: `cost: [{ item, quantity }]`, `upgradesFrom` (id or null), `size: { w, h }` (or null)
- skin: `unlock` (the game's `DropLocation` name), `hidden` (the game hides it until unlocked)

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

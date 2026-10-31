# Save schema Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish `v1/save-schema.json`: the names and types of every keyed field in a 1.5+ save, per game version.

**Architecture:** A new `SaveSchema` job in `CotlDataDump` walks the game's MessagePack-CSharp types by reflection
and writes `saveSchema.json` into the raw dump; `pack.py` adds the game version, publishes it beside the other kinds,
keeps every earlier version's copy, and lists the versions in `index.json`.

**Tech Stack:** C# (BepInEx 5 plugin, built by `mod/build.sh`), Python 3.12 (`pack.py`, `unittest`), JSON Schema, `check.sh`.

**Spec:** `/Users/roman-laptop/Проекты/cotl-save-editor/docs/superpowers/specs/2026-10-31-cotl-save-editor-design.md` (section "Save schema (cotl-api change)").

## Global Constraints

- Work on a branch (`save-schema`), not `main`; one PR.
- Within `v1/` fields are only added, never removed: `index.json` gains a field, nothing else changes shape.
- The mod never writes saves; the new job only reads types (phase 1, main menu).
- Published shape (exact keys): `{ "gameVersion", "roots": { "slot": "DataManager", "meta": "MetaData" }, "types": { "<Type>": { "fields": [ { "key", "name", "type" } ] } }, "enums": { "<Enum>": { "<int>": "<NAME>" } } }`.
- Type names: CLR `FullName` with `+` replaced by `.`, leading `.` dropped for the global namespace (`DataManager`, `InventoryItem.ITEM_TYPE`).
- `type` values: `"bool"`, `"int8"`, `"uint8"`, `"int16"`, `"uint16"`, `"int32"`, `"uint32"`, `"int64"`, `"uint64"`, `"float32"`, `"float64"`, `"string"`, `"bytes"`, or one object: `{"list": T}`, `{"map": [K, V]}`, `{"nullable": T}`, `{"enum": "<Enum>"}`, `{"object": "<Type>"}`, `{"other": "<CLR name>"}`.
- Known facts for 1.5.26.1058 the tests can rely on: `DataManager` key 0 `AllowSaving`, 410 `CultName`, 1109 `Followers`, 1111 `Followers_Dead`, 1162 `items`; `FollowerInfo` key 1 `_name`, 15 `SkinName`; `InventoryItem` keys 0 `type`, 1 `quantity`, 2 `QuantityReserved`; `MetaData` key 29 `Version`.

## Review Focus

- Re-running the packer on a new game version must keep `save-schema/{old}.json`: the editor needs old schemas for old saves (`v1/` is swapped in whole, so old copies vanish unless carried over). Test in Task 2.
- A dump without `saveSchema.json` (an old mod) must refuse to pack, not publish a site without it. Test in Task 2.
- A game update that breaks reflection and yields a near-empty schema must refuse, like the existing 10% count-drop guard. Test in Task 2.
- A recapture of the same game version must leave the site byte-identical (existing `test_recapture_of_same_game_keeps_site_identical` keeps passing with the new files).
- Cyclic or self-referencing types (`FollowerInfo` → list of `FollowerInfo`) must not loop the mod: walk with a visited set.

---

### Task 1: `SaveSchema` job in the mod

**Files:**
- Modify: `mod/CotlDataDump.cs` (call in `MainMenu()` after `ReadCrownAbilities`; new method near the other `Read*`; `Write("saveSchema.json", …)` in `Base()` next to the other `Write` calls)
- Modify: `docs/design.md` (raw dump format: add `saveSchema.json`)

**Interfaces:**
- Produces: raw dump file `saveSchema.json` with `roots`, `types`, `enums` exactly as in Global Constraints (no `gameVersion`; the packer adds it from `meta.json`).

- [ ] **Step 1: Add `void ReadSaveSchema()` and a `Dict saveSchema` field**

Breadth-first from `typeof(DataManager)` and `typeof(MetaData)` with a visited set. For each type, every instance field
and property (public and non-public) carrying `MessagePack.KeyAttribute` with an `IntKey` becomes
`{key, name, type}`, sorted by key. `type` from a recursive `string|Dict Describe(Type t)`: primitives by the table
in Global Constraints (`byte[]` → `"bytes"`), `T[]` and `List<T>`/`HashSet<T>` → `{"list"}`, `Dictionary<K,V>` →
`{"map"}`, `Nullable<T>` → `{"nullable"}`, enums → `{"enum"}` and record all `Enum.GetValues` as `int → name` in
`enums`, a type with `MessagePack.MessagePackObjectAttribute` → `{"object"}` and enqueue it, anything else →
`{"other"}`. Run it as `Try("SaveSchema", ReadSaveSchema)` in phase 1 behind the `doMore` switch, log the type count.

- [ ] **Step 2: Build the plugin**

Run: `mod/build.sh "<game>/Cult Of The Lamb_Data/Managed" "<game>/BepInEx/core"` (on the PC, as before)
Expected: `built …/CotlDataDump.dll`, no errors.

- [ ] **Step 3: Capture on the PC and check the output**

Run: `./publish.sh` up to the capture (it backs up and verifies saves as usual), then:
`python3 -c "import json;s=json.load(open('dump/saveSchema.json'));f={x['key']:x['name'] for x in s['types']['DataManager']['fields']};print(len(s['types']),f[0],f[410],f[1109],f[1162])"`
Expected: about 137 types, `AllowSaving CultName Followers items`.

- [ ] **Step 4: Commit**

```bash
git add mod/CotlDataDump.cs docs/design.md
git commit -m "Mod: dump the save's MessagePack field names and types"
```

### Task 2: Publish it from `pack.py`

**Files:**
- Modify: `pack.py` (`load_dump`, `build_site`, `problems`, `SCHEMAS`, `openapi`)
- Create: `schema/saveSchema.schema.json`
- Create: `tests/fixture-dump/saveSchema.json` (a few fields of `DataManager`, `FollowerInfo`, `InventoryItem`, `MetaData` and `InventoryItem.ITEM_TYPE` with the 1.5.26 keys from Global Constraints)
- Modify: `tests/test_pack.py`, regenerate `tests/expected-site/` with `python3 pack.py tests/fixture-dump tests/expected-site`
- Modify: `check.sh` (validate `v1/save-schema.json` and `v1/save-schema/*.json`), `README.md` (table row and a "Save schema" field section)

**Interfaces:**
- Consumes: Task 1's `saveSchema.json`.
- Produces: `v1/save-schema.json` (current version), `v1/save-schema/{gameVersion}.json` (every version ever packed), `index.json` field `"saveSchemas": ["<version>", …]` sorted oldest first.

- [ ] **Step 1: Write the failing tests**

```python
def test_save_schema_published(self):
    self.pack()
    s = json.loads((self.site / "v1" / "save-schema.json").read_text())
    self.assertEqual(s["gameVersion"], "1.4.6")
    self.assertEqual(s["roots"], {"slot": "DataManager", "meta": "MetaData"})
    self.assertEqual({f["key"]: f["name"] for f in s["types"]["DataManager"]["fields"]}[1109], "Followers")
    self.assertEqual((self.site / "v1" / "save-schema" / "1.4.6.json").read_bytes(),
                     (self.site / "v1" / "save-schema.json").read_bytes())
    self.assertEqual(json.loads((self.site / "v1" / "index.json").read_text())["saveSchemas"], ["1.4.6"])

def test_old_save_schemas_are_kept(self):
    self.pack()
    self.edit("meta", lambda m: m.update(gameVersion="1.4.7"))
    self.pack()
    self.assertTrue((self.site / "v1" / "save-schema" / "1.4.6.json").exists())
    self.assertEqual(json.loads((self.site / "v1" / "index.json").read_text())["saveSchemas"], ["1.4.6", "1.4.7"])

def test_refuses_missing_save_schema(self):
    (self.dump / "saveSchema.json").unlink()
    self.assertIn("saveSchema: none in the dump", pack.problems(pack.load_dump(self.dump), self.dump, self.site))

def test_refuses_save_schema_shrink(self):
    self.pack()
    self.edit("saveSchema", lambda s: s["types"]["DataManager"].update(fields=s["types"]["DataManager"]["fields"][:1]))
    self.assertTrue(any(p.startswith("saveSchema: ") for p in pack.problems(pack.load_dump(self.dump), self.dump, self.site)))
```

Also add `"saveSchema"` to the expected names in `test_schemas_published` and `"SaveSchema"` to
`test_openapi_describes_every_endpoint`'s schema set, with paths `/v1/save-schema.json` and `/v1/save-schema/{version}.json`.

- [ ] **Step 2: Run them to see them fail**

Run: `python3 -m unittest tests.test_pack -v`
Expected: the new tests FAIL (no `save-schema.json`, no `saveSchemas`).

- [ ] **Step 3: Implement**

`load_dump` reads `saveSchema.json` when present (`None` otherwise). `problems` reports `saveSchema: none in the dump`,
and `saveSchema: N DataManager fields now, M before (more than 10% fewer)` against the previous site's file.
`build_site` writes `{"gameVersion": …, **dump["saveSchema"]}` to `tmp/save-schema.json` and
`tmp/save-schema/{version}.json`, first copying the old site's `save-schema/*.json` into `tmp`; `index["saveSchemas"]`
lists the folder's versions sorted with `packaging`-free numeric sort (`tuple(int(p) for p in v.split("."))`).
`SCHEMAS` gains `"saveSchema": "SaveSchema"`; `openapi` gets the two paths (version param enum = `saveSchemas`) and an
`index`-tag entry. `schema/saveSchema.schema.json` describes the shape in Global Constraints (`type` as a `oneOf` of the
string enum and the six one-key objects); `schema/index.schema.json` gains optional `saveSchemas` (array of strings).

- [ ] **Step 4: Regenerate the expected site and run everything**

Run: `python3 pack.py tests/fixture-dump tests/expected-site && python3 -m unittest discover tests && ./check.sh tests/expected-site && ./types.sh "$TMPDIR"`
Expected: all tests OK, `ok -- site consistent`, `ok -- …/types.d.ts` (now with `SaveSchema`).

- [ ] **Step 5: Commit**

```bash
git add pack.py schema tests check.sh README.md
git commit -m "Publish the save schema per game version"
```

### Task 3: Publish real data

- [ ] **Step 1:** Run `./publish.sh` end to end with the Task 1 plugin (saves are backed up and checked as usual).
- [ ] **Step 2:** Check `site/v1/save-schema.json` has `gameVersion` `1.5.26.1058` and the Global Constraints facts:
  `python3 -c "import json;s=json.load(open('site/v1/save-schema.json'));t=s['types'];print(s['gameVersion'],{f['key']:f['name'] for f in t['FollowerInfo']['fields']}[15],{f['key']:f['name'] for f in t['MetaData']['fields']}[29])"` → `1.5.26.1058 SkinName Version`.
- [ ] **Step 3:** Commit the site (`publish.sh` does) and open the PR.

# cotl-api

Static JSON data for *Cult of the Lamb*: items, structures and follower skins, with names in every language
the game ships and icons for items and structures. Taken straight from the game by a BepInEx plugin
and served from GitHub Pages. No keys, no rate limits; any site can fetch it (CORS is open).

Base URL: `https://api.cotl.info/v1/`

| Path | What |
|---|---|
| `index.json` | game version, capture date, languages, counts |
| `items.json`, `items/{id}.json` | every item |
| `structures.json`, `structures/{id}.json` | every structure |
| `skins.json`, `skins/{id}.json` | every follower skin (form) |
| `icons/items/{id}.png`, `icons/structures/{id}.png` | icons |
| `schema/{index,item,structure,skin}.schema.json` | JSON Schemas |

`{id}` is the game's own name (`BERRY`, `FARM_PLOT`, …) and stays the same across game versions.

## Fields

Every entity has `id`, `name` and `description` (maps from language code to text; a language without a
translation is left out, `en` is always there in `name`), and `icon` (a path relative to `v1/`, or null).

- **item**: `category` (the game's item category)
- **structure**: `cost` (`[{ "item": "<item id>", "quantity": n }]`, base prices without in-game discounts),
  `upgradesFrom` (structure id or null), `size` (`{ "w", "h" }` in grid cells, or null when the game doesn't
  place it from the build menu)
- **skin**: `unlock` (where the game drops it: `Dungeon1`…`Dungeon6`, `Other`, `DLC`, `SpecialEvents`,
  `Major_DLC`), `hidden` (the game hides it until unlocked). Skins only have English names, as in the game,
  and no icons yet.

Examples (only `en` and `ru` shown; the real files have every language):

`structures/COOKING_FIRE.json`
```json
{
  "cost": [
    {
      "item": "LOG",
      "quantity": 5
    },
    {
      "item": "STONE",
      "quantity": 2
    }
  ],
  "description": {
    "en": "Cook raw items and turn them into <color=#FFD201>Meals</color> <sprite name=\"icon_Meal\"> that Followers can eat.",
    "ru": "Сооружение, на котором можно готовить для последователей <color=#FFD201>пищу</color> <sprite name=\"icon_Meal\">."
  },
  "icon": "icons/structures/COOKING_FIRE.png",
  "id": "COOKING_FIRE",
  "name": {
    "en": "Cooking Fire",
    "ru": "Походная печь"
  },
  "size": {
    "h": 4,
    "w": 4
  },
  "upgradesFrom": null
}
```

`items/BERRY.json`
```json
{
  "category": "NONE",
  "description": {
    "en": "A tasty ingredient for cooking meals.",
    "ru": "Вкусный и сочный плод."
  },
  "icon": "icons/items/BERRY.png",
  "id": "BERRY",
  "name": {
    "en": "Berry",
    "ru": "Ягода"
  }
}
```

`skins/Cat.json`
```json
{
  "description": {},
  "hidden": false,
  "icon": null,
  "id": "Cat",
  "name": {
    "en": "Cat"
  },
  "unlock": "Other"
}
```

Text is the game's own and can hold its TextMeshPro markup, e.g. `<sprite name="icon_spirits">` for an
inline icon; strip `<...>` tags if you only want plain text. An item's `category` is `NONE` when the game
doesn't put it in one.

## Compatibility

Within `v1/`, fields are only ever added, never renamed or removed. A breaking change would ship as `v2/`
next to it. Each data update is one commit, so the history is the changelog per game version.

## Refreshing the data

On a machine with the game and BepInEx 5 (reached over SSH):

1. `mod/build.sh "<game>/Cult Of The Lamb_Data/Managed" "<game>/BepInEx/core"` (needs the .NET SDK)
2. Copy `.env.example` to `.env` and fill it in.
3. `./publish.sh`: checks the game isn't running, checksums (and if needed backs up) the saves, installs the
   plugin, runs the game (a few minutes: main menu, then save slot 0 with saving blocked), pulls the dump,
   removes the plugin, re-checks the saves, packs `site/`, and asks before committing and pushing.

`python3 -m unittest discover tests` tests the packer; `./check.sh site` validates against the schemas.

## Credits

Cult of the Lamb, its data and its art belong to Massive Monster and Devolver Digital. This is an unofficial
fan project. The code here is MIT licensed.

# cotl-api

Static JSON data for *Cult of the Lamb*: items, structures and follower skins, with names in every language
the game ships and icons for items and structures. Taken straight from the game by a BepInEx plugin
and served from GitHub Pages. No keys, no rate limits; any site can fetch it (CORS is open).

Base URL: `https://api.cotl.info/v1/`

| Path | What |
|---|---|
| `index.json` | game version, capture date, languages, counts |
| `changes.json` | per game version, newest first: ids added, removed, changed |
| `items.json`, `items/{id}.json` | every item |
| `structures.json`, `structures/{id}.json` | every structure |
| `skins.json`, `skins/{id}.json` | every follower skin (form) |
| `rituals.json`, `rituals/{id}.json` | every ritual in the ritual menu |
| `icons/items/{id}.png`, `icons/structures/{id}.png`, `icons/rituals/{id}.png` | icons |
| `schema/{index,item,structure,skin}.schema.json` | JSON Schemas |

`{id}` is the game's own name (`BERRY`, `FARM_PLOT`, …) and stays the same across game versions.

## Fields

Every entity has `id`, `name` and `description` (maps from language code to text; a language without a
translation is left out, `en` is always there in `name`), `namePlain` and `descriptionPlain` (the same without
the game's text markup), and `icon` (a path relative to `v1/`, or null).

- **item**: `usedIn` (`{ "structures", "items", "rituals" }`: what costs it, or uses it in a recipe), `category` (the game's item category), `tags` (any of `food`, `fish`, `big_fish`, `meat`, `heart`, `gift`,
  `meal`, `drink`), `meal` (meals and drinks only, else null): `recipes` (every ingredient combination the game
  accepts, as `[[{ "item", "quantity" }]]`), `satiation`, `satiationPlayer`, `stars` (the cooking menu's quality
  stars), `tummyRating`, `faith`, `cookTime`, `dlc`, and `effects` (`type`, `chance` in percent, and its text as
  `description` / `descriptionPlain`). Numbers are the game's own values and units.
  `seed` (crops: the seed that grows it, or null), `crop` (farm seeds only, else null): `grows` (the crop),
  `growthTime` (watered day phases; a day has 5, a nearby Harvest Totem speeds it up by half), `stages`,
  `harvests` (`{ "min", "max" }` times one seed can be harvested), `season` (`spring` or `winter`: the only
  season it grows in without a Farm Crop Grower or rotstone fertiliser).
- **structure**: `cost` (`[{ "item": "<item id>", "quantity": n }]`, base prices without in-game discounts),
  `upgradesFrom` (structure id or null), `upgradesTo` (the reverse, a list), `size` (`{ "w", "h" }` in grid cells, or null when the game doesn't
  place it from the build menu), `menu` (build menu tab and section, e.g. `Follower/Food`,
  `Faith/Sin`, `Aesthetic/Path`, or null), `buildable` (in the build menu), `category` (the game's structure
  category, `FOOD`, `FAITH`, …, for buildable ones, else null)
- **skin**: `unlock` (where the game drops it: `Dungeon1`…`Dungeon6`, `Other`, `DLC`, `SpecialEvents`,
  `Major_DLC`), `hidden` (the game hides it until unlocked). Skins only have English names, as in the game,
  and no icons yet.

- **ritual**: `cost`, `faith`, `warmth`, `sin` (what performing it changes; faith is the base value, before
  cult traits), `trait` (cult trait it can give, or null), `group` (`secondary`, `pair`, `single`, `special`, as
  the ritual menu lists them), `alternative` (pair rituals: the other choice of the same doctrine; the Crystal
  Doctrine unlocks both), `doctrine` (the doctrine that unlocks it, or null)

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
inline icon; `namePlain`/`descriptionPlain` have it removed. An item's `category` is `NONE` when the game
doesn't put it in one.

## What's left out

Item types the game has no name for in any language are left out: internal values, placeholders and cut
content. Each capture checks every localisation term in the game for them (the result is in the dump's
`meta.json`). Currently:

`ANIMALS`, `BLACK_SOUL`, `BLOOD_STONE`, `BLUE_PRINT`, `BROKEN_WEAPON_BLUNDERBUSS`, `CRYSTAL_DOCTRINE_FRAGMENT`, `CURSE_CARD`, `DISCIPLE_POINTS`, `DLC_NECKLACE`, `EGG_FOLLOWER`, `FISH_CATFISH`, `FISH_COD`, `FISH_PIKE`, `FLOCKADE_PIECE`, `FLOWERS`, `FOUND_ITEM_CURSE`, `FOUND_ITEM_DECORATION_ALT`, `FOUND_ITEM_OUTFIT`, `FOUND_ITEM_WEAPON`, `GENERIC`, `GRASS2`, `GRASS3`, `GRASS4`, `GRASS5`, `INGREDIENTS`, `KEY`, `KEY_PIECE`, `MAP`, `MEALS`, `MEAL_ROTTEN`, `MEAT_ROTTEN`, `MUSHROOM_BIG`, `NONE`, `Necklace_Bell`, `PERMANENT_HALF_HEART`, `RATAU_STAFF`, `RELIC`, `REPAIRED_WEAPON_BLUNDERBUSS`, `ROCK2`, `ROCK3`, `ROPE`, `SEEDS`, `SEED_FLOWER_PURPLE`, `SEED_FLOWER_WHITE`, `SEED_SWORD`, `SEED_TREE`, `SOUL_FRAGMENT`, `STAINED_GLASS`, `THORNS`, `TIME_TOKEN`, `TRINKET_CARD_UNLOCKED`, `UNUSED`, `VINES`, `WEAPON_CARD`, `WEBBER_SKULL`, `WHEAT`.

Structures without a name are left out the same way.

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

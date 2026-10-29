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
| `doctrines.json`, `doctrines/{id}.json` | every doctrine the temple and Death Cat offer |
| `sermons.json`, `sermons/{id}.json` | the sermon categories doctrines belong to |
| `tarot.json`, `tarot/{id}.json` | every tarot card in the deck |
| `relics.json`, `weapons.json`, `curses.json` (and `{kind}/{id}.json`) | relics, weapons, curses |
| `traits.json`, `traits/{id}.json` | follower and cult traits |
| `outfits.json`, `outfits/{id}.json` | follower outfits from the tailor |
| `crownAbilities.json`, `fleeces.json` (and `{kind}/{id}.json`) | what the crown menu sells, fleeces |
| `icons/{kind}/{id}.png`, `icons/{kind}/64/{id}.png` | icons, and the same fitted into 64×64 |
| `{lang}/{kind}.json` | per language and kind, just `id`, `name` (English if untranslated) and `thumb` |
| `schema/{index,item,structure,skin}.schema.json` | JSON Schemas |

`{id}` is the game's own name (`BERRY`, `FARM_PLOT`, …) and stays the same across game versions.

## TypeScript

`https://api.cotl.info/v1/types.d.ts` has types for every response, generated from the JSON Schemas
(`Item`, `Structure`, `Skin`, `Ritual`, `Index`, `Changes`, `Slim`). Each game version also gets a
[release](https://github.com/AuthFailed/cotl-api/releases) with the whole `v1/` zipped and the types attached.

## Fields

Every entity has `id`, `name` and `description` (maps from language code to text; a language without a
translation is left out, `en` is always there in `name`), `namePlain` and `descriptionPlain` (the same without
the game's text markup), `icon` (a path relative to `v1/`, or null), and `thumb` (the icon at 64 px, or null).

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
  `upgradesFrom` (structure id or null), `upgradesTo` (the reverse, a list), `range` (the square area it works in: `size` in
  cells and `centerOffset` from its anchor cell along the grid diagonal; from `ranges.json`, or null), `size` (`{ "w", "h" }` in grid cells, or null when the game doesn't
  place it from the build menu), `menu` (build menu tab and section, e.g. `Follower/Food`,
  `Faith/Sin`, `Aesthetic/Path`, or null), `buildable` (in the build menu), `category` (the game's structure
  category, `FOOD`, `FAITH`, …, for buildable ones, else null)
- **skin**: `unlock` (where the game drops it: `Dungeon1`…`Dungeon6`, `Other`, `DLC`, `SpecialEvents`,
  `Major_DLC`), `hidden` (the game hides it until unlocked). Skins only have English names, as in the game.

- **ritual**: `cost`, `faith`, `warmth`, `sin` (what performing it changes; faith is the base value, before
  cult traits), `trait` (cult trait it can give, or null), `group` (`secondary`, `pair`, `single`, `special`, as
  the ritual menu lists them), `alternative` (pair rituals: the other choice of the same doctrine; the Crystal
  Doctrine unlocks both), `doctrine` (the doctrine that unlocks it, or null)

- **doctrine**: `category` (a sermon id, or `Special`), `level`, `choice` (`first`/`second`, null for Special),
  `alternative` (the other choice at that level), `kind` (`Trait`, `FollowerAction`, `Ritual` or null), `ritual`
- **sermon**: `glyph` (the icon as the game writes it in text: a Font Awesome character or a `<sprite>` tag)
- **tarot**: `levels` (text of each upgrade level above the first), `lore`, `maxLevel`, `groups` (`default`,
  `coop`, `dlc`, `majorDlc`, `corrupted`, `mystic`, `rot`, `winter`), `dlc`, `coop`, `weight` (defined but unused
  by the game, which draws uniformly). The icon is the card as the tarot menu draws it.
- **relic**: `lore`, `subType`, `interactionType`, `damageType`, `chargeDamage`, `chargeCategory`, `weight`,
  `requiresCult`, `unlockedBy`, `dlc`, `coop`, `majorDlc`
- **weapon**: `lore`, `family`, `legendary`, `speed`, `damage` (each hit of the light combo), `heavyDamage`
- **curse**: `lore`, `family`, `damage`, `delay`, `castingDuration`, `chance`, `canAim`
- **trait**: `positive`, `groups` (the game's trait lists: `starting`, `rareStarting`, `single`, `faithful`, …),
  `exclusiveWith`
- **outfit**: `cost`, `requirements`, `protection`, `effects` (as a meal's), `tab` (the tailor menu tab), `groups`,
  `forSale`. The icon is the outfit as the tailor draws it.
- **crownAbility**: `cost`, `dlc`; **fleece**: `fleece` (the game's number), `cost`, `pack`

Skins, tarot cards and outfits have no sprites in the game; their icons are rendered from the game's own
Spine models at capture time (follower forms with their default colours). The five Palworld forms
(`PalworldOne` … `PalworldFive`) have no portrait: their skins aren't in the follower skeleton or any other skeleton
the game has loaded when the capture runs, so there's nothing to draw them from.

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

## Text the game fills at run time

Two published texts keep a `{0}` that the game fills from the player's save or by mistake; `placeholders.json`
lists them, and the checks fail if a new one appears:

- `traits/BishopOfCult` `name`: "Bishop of {0}", where `{0}` is the cult's name.
- `traits/Mutated` `name`, Korean only: the game's Korean name is its description sentence ("dies in {0} days").

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

A daily workflow (`watch.py`) opens an issue when Steam's public build of the game is newer than the one the
data was captured from.


On a machine with the game and BepInEx 5 (reached over SSH):

1. `mod/build.sh "<game>/Cult Of The Lamb_Data/Managed" "<game>/BepInEx/core"` (needs the .NET SDK)
2. Copy `.env.example` to `.env` and fill it in.
3. `./publish.sh`: checks the game isn't running, checksums (and if needed backs up) the saves, installs the
   plugin, runs the game (a few minutes: main menu, then save slot 0 with saving blocked), pulls the dump,
   removes the plugin, re-checks the saves, packs `site/`, and asks before committing and pushing.

The packer needs Python 3 with Pillow. `python3 -m unittest discover tests` tests it; `./check.sh site` validates against the schemas.

## Credits

Cult of the Lamb, its data and its art belong to Massive Monster and Devolver Digital. This is an unofficial
fan project. The code here is MIT licensed.

import json
import shutil
import tempfile
import unittest
from pathlib import Path

import pack

HERE = Path(__file__).parent
FIXTURE = HERE / "fixture-dump"
EXPECTED = HERE / "expected-site"


def tree(root: Path) -> dict:
    return {p.relative_to(root).as_posix(): p.read_bytes() for p in sorted(root.rglob("*")) if p.is_file()}


class PackTest(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.dump = self.tmp / "dump"
        shutil.copytree(FIXTURE, self.dump)
        self.site = self.tmp / "site"

    def pack(self):
        return pack.build_site(pack.load_dump(self.dump), self.dump, self.site)

    def item(self, id):
        return json.loads((self.site / "v1" / "items" / f"{id}.json").read_text())

    def test_matches_expected(self):
        self.pack()
        self.assertEqual(tree(self.site), tree(EXPECTED))

    def test_rerun_is_identical(self):
        self.pack()
        first = tree(self.site)
        self.pack()
        self.assertEqual(tree(self.site), first)

    def test_removed_id_disappears(self):
        self.pack()
        items = json.loads((self.dump / "items.json").read_text())
        (self.dump / "items.json").write_text(json.dumps([i for i in items if i["id"] != "LOG"]))
        self.pack()
        self.assertFalse((self.site / "v1" / "items" / "LOG.json").exists())
        self.assertFalse((self.site / "v1" / "icons" / "items" / "LOG.png").exists())

    def test_non_language_columns_dropped(self):
        index = self.pack()
        self.assertEqual(index["languages"], ["en", "ru"])
        self.assertEqual(set(self.item("LOG")["name"]), {"en", "ru"})

    def test_empty_translation_dropped(self):
        self.pack()
        self.assertEqual(self.item("BERRY")["name"], {"en": "Berries"})

    def test_schemas_published(self):
        self.pack()
        for name in ("index", "item", "structure", "skin", "ritual", "doctrine", "sermon", "tarot", "relic", "weapon", "curse", "trait",
                     "outfit", "crownAbility", "fleece", "changes", "slim", "saveSchema"):
            self.assertEqual((self.site / "v1" / "schema" / f"{name}.schema.json").read_bytes(),
                             (HERE.parent / "schema" / f"{name}.schema.json").read_bytes())

    def edit(self, name, change):
        path = self.dump / f"{name}.json"
        data = json.loads(path.read_text())
        result = change(data)
        path.write_text(json.dumps(data if result is None else result))

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
        self.edit("meta", lambda m: m.update(gameVersion="1.4.10"))
        self.pack()
        self.assertTrue((self.site / "v1" / "save-schema" / "1.4.6.json").exists())
        self.assertEqual(json.loads((self.site / "v1" / "index.json").read_text())["saveSchemas"], ["1.4.6", "1.4.10"])

    def test_plain_text_drops_markup(self):
        self.pack()
        stone = self.item("STONE")
        self.assertEqual(stone["descriptionPlain"], {"en": "Hard rock.\nHeavy.", "ru": "Твёрдый камень."})
        self.assertEqual(stone["namePlain"], {"en": "Stone", "ru": "Камень"})

    def test_recapture_of_same_game_keeps_site_identical(self):
        self.pack()
        first = tree(self.site)
        self.edit("meta", lambda m: m.update(capturedAt="2026-11-01T00:00:00Z"))
        self.pack()
        self.assertEqual(tree(self.site), first)

    def test_real_change_updates_captured_at(self):
        self.pack()
        self.edit("meta", lambda m: m.update(capturedAt="2026-11-01T00:00:00Z"))
        self.edit("items", lambda items: [dict(i, category="Stones") if i["id"] == "STONE" else i for i in items])
        self.assertEqual(self.pack()["capturedAt"], "2026-11-01T00:00:00Z")

    def changes(self):
        return json.loads((self.site / "v1" / "changes.json").read_text())

    def test_first_publish_has_empty_changelog(self):
        self.pack()
        self.assertEqual(self.changes(), [])

    def test_changelog_records_added_removed_changed(self):
        self.pack()
        self.edit("meta", lambda m: m.update(gameVersion="1.4.7", capturedAt="2026-11-01T00:00:00Z"))
        self.edit("items", lambda items: [dict(i, category="Stones") if i["id"] == "STONE" else i
                                          for i in items if i["id"] != "LOG"] + [{"id": "COAL", "nameTerm": "Inventory/STONE", "category": "Resource"}])
        shutil.copy(self.dump / "icons/items/LOG.png", self.dump / "icons/items/COAL.png")
        self.pack()
        self.assertEqual(self.changes(), [{
            "gameVersion": "1.4.7", "capturedAt": "2026-11-01T00:00:00Z",
            "added": {k: ["COAL"] if k == "items" else [] for k in pack.KINDS},
            "removed": {k: ["LOG"] if k == "items" else [] for k in pack.KINDS},
            "changed": {k: ["STONE"] if k == "items" else [] for k in pack.KINDS},
        }])
        self.edit("meta", lambda m: m.update(capturedAt="2026-11-02T00:00:00Z"))
        self.pack()  # nothing changed: no new entry
        self.assertEqual(len(self.changes()), 1)

    def test_same_game_version_adds_no_changelog_entry(self):
        self.pack()
        self.edit("items", lambda items: [dict(i, category="Stones") if i["id"] == "STONE" else i for i in items])
        self.pack()  # a different capture of the same game is a change in the API, not the game
        self.assertEqual(self.changes(), [])

    def test_new_field_is_not_a_game_change(self):
        self.pack()
        self.edit("meta", lambda m: m.update(gameVersion="1.4.7"))
        self.edit("items", lambda items: [dict(i, rarity="common") for i in items])
        self.pack()
        self.assertEqual(self.changes(), [])

    def test_meal_effects_are_translated_with_their_chance(self):
        self.pack()
        meal = self.item("MEAL_BERRIES")["meal"]
        self.assertEqual(meal["recipes"], [[{"item": "BERRY", "quantity": 6}]])
        self.assertEqual(meal["effects"], [{
            "type": "CausesIllPoopy", "chance": 5,
            "description": {"en": "<color=#FFD201>5%</color> chance of feeling ill", "ru": "5% шанс заболеть"},
            "descriptionPlain": {"en": "5% chance of feeling ill", "ru": "5% шанс заболеть"},
        }])
        self.assertIsNone(self.item("LOG")["meal"])

    def test_seed_and_crop_link_both_ways(self):
        self.pack()
        self.assertEqual(self.item("SEED")["crop"]["grows"], "BERRY")
        self.assertEqual(self.item("BERRY")["seed"], "SEED")

    def test_link_to_unpublished_item_is_null(self):
        self.pack()  # LOG grows from SEED_TREE, which the game never names
        self.assertIsNone(self.item("LOG")["seed"])

    def test_rituals_are_published(self):
        index = self.pack()
        self.assertEqual(index["counts"]["rituals"], 2)
        feast = json.loads((self.site / "v1" / "rituals" / "Ritual_Feast.json").read_text())
        self.assertEqual(feast["name"], {"en": "Feast", "ru": "Пир"})
        self.assertEqual(feast["alternative"], "Ritual_Fast")
        self.assertEqual(feast["icon"], "icons/rituals/Ritual_Feast.png")
        self.assertTrue((self.site / "v1" / "icons" / "rituals" / "Ritual_Feast.png").exists())

    def structure(self, id):
        return json.loads((self.site / "v1" / "structures" / f"{id}.json").read_text())

    def test_items_list_where_they_are_used(self):
        self.pack()
        self.assertEqual(self.item("LOG")["usedIn"], {"structures": ["FARM_PLOT", "SHRINE_II"], "items": [], "rituals": ["Ritual_Feast"]})
        self.assertEqual(self.item("BERRY")["usedIn"], {"structures": [], "items": ["MEAL_BERRIES"], "rituals": []})
        self.assertEqual(self.item("SEED")["usedIn"], {"structures": [], "items": [], "rituals": []})

    def test_structures_list_what_they_upgrade_to(self):
        self.edit("structures", lambda st: [dict(x, upgradesFrom="FARM_PLOT") if x["id"] == "SHRINE_II" else x for x in st])
        self.pack()
        self.assertEqual(self.structure("FARM_PLOT")["upgradesTo"], ["SHRINE_II"])
        self.assertEqual(self.structure("SHRINE_II")["upgradesTo"], [])

    def test_openapi_describes_every_endpoint(self):
        self.pack()
        spec = json.loads((self.site / "openapi.json").read_text())
        self.assertEqual(spec["openapi"], "3.1.0")
        self.assertEqual(spec["info"]["version"], "1.4.6")
        self.assertEqual(set(spec["components"]["schemas"]), {"Index", "Item", "Structure", "Skin", "Ritual", "Doctrine", "Sermon", "Tarot", "Relic", "Weapon", "Curse", "Trait",
                                                              "Outfit", "CrownAbility", "Fleece", "Changes", "Slim", "SaveSchema"})
        for path in ("/v1/index.json", "/v1/items.json", "/v1/items/{id}.json", "/v1/icons/items/{id}.png",
                     "/v1/structures.json", "/v1/skins/{id}.json", "/v1/rituals.json", "/v1/rituals/{id}.json",
                     "/v1/icons/rituals/{id}.png", "/v1/save-schema.json", "/v1/save-schema/{version}.json"):
            self.assertIn(path, spec["paths"])
        id_param = spec["paths"]["/v1/items/{id}.json"]["get"]["parameters"][0]
        self.assertEqual(id_param["schema"]["enum"], ["BERRY", "LOG", "MEAL_BERRIES", "SEED", "STONE"])
        icon_param = spec["paths"]["/v1/icons/items/{id}.png"]["get"]["parameters"][0]
        self.assertEqual(icon_param["schema"]["enum"], ["LOG", "STONE"])
        self.assertNotIn("/v1/icons/skins/{id}.png", spec["paths"])  # no skin has an icon

    def test_openapi_shows_real_examples_and_links(self):
        self.pack()
        spec = json.loads((self.site / "openapi.json").read_text())
        one = spec["paths"]["/v1/items/{id}.json"]["get"]["responses"]["200"]["content"]["application/json"]["example"]
        self.assertEqual(one, self.item(one["id"]))
        many = spec["paths"]["/v1/rituals.json"]["get"]["responses"]["200"]["content"]["application/json"]["example"]
        self.assertEqual(many[0]["id"], "Ritual_Fast")
        self.assertIn("changes.json", spec["info"]["description"])
        self.assertEqual(spec["externalDocs"]["url"], "https://github.com/AuthFailed/cotl-api#readme")

    def test_icons_get_64px_thumbnails(self):
        from PIL import Image
        Image.new("RGBA", (200, 100), (255, 0, 0, 255)).save(self.dump / "icons" / "items" / "STONE.png")
        self.pack()
        stone = self.item("STONE")
        self.assertEqual(stone["thumb"], "icons/items/64/STONE.png")
        self.assertEqual(Image.open(self.site / "v1" / stone["thumb"]).size, (64, 32))
        self.assertIsNone(self.item("BERRY")["thumb"])  # no icon, no thumbnail

    def test_slim_lists_per_language(self):
        self.pack()
        ru = {e["id"]: e for e in json.loads((self.site / "v1" / "ru" / "items.json").read_text())}
        self.assertEqual(ru["LOG"], {"id": "LOG", "name": "Брёвна", "thumb": "icons/items/64/LOG.png"})
        self.assertEqual(ru["BERRY"]["name"], "Berries")  # no Russian name: English
        self.assertTrue((self.site / "v1" / "en" / "rituals.json").exists())

    def test_index_has_the_steam_build(self):
        self.assertIsNone(self.pack()["steamBuildId"])  # the fixture's meta has none
        self.edit("meta", lambda m: m.update(steamBuildId=21234567))
        self.assertEqual(self.pack()["steamBuildId"], 21234567)

    def test_structures_get_their_range(self):
        pack.RANGES["FARM_PLOT"] = {"shape": "square", "size": 5, "centerOffset": 0.0}
        self.addCleanup(pack.RANGES.pop, "FARM_PLOT")
        self.pack()
        farm = json.loads((self.site / "v1" / "structures" / "FARM_PLOT.json").read_text())
        self.assertEqual(farm["range"], {"shape": "square", "size": 5, "centerOffset": 0.0})
        self.assertIsNone(json.loads((self.site / "v1" / "structures" / "SHRINE_II.json").read_text())["range"])

    def entity(self, kind, id):
        return json.loads((self.site / "v1" / kind / f"{id}.json").read_text())

    def test_doctrines_and_sermons(self):
        self.pack()
        feast = self.entity("doctrines", "Sustenance_Feast")
        self.assertEqual((feast["category"], feast["level"], feast["alternative"], feast["ritual"]), ("Food", 2, "Sustenance_Fast", "Ritual_Feast"))
        self.assertEqual(feast["icon"], "icons/doctrines/Sustenance_Feast.png")
        self.assertEqual(self.entity("sermons", "Food")["name"], {"en": "Sustenance", "ru": "Пища"})

    def test_tarot_levels_lore_and_filled_placeholders(self):
        self.pack()
        hearts = self.entity("tarot", "Hearts1")
        self.assertEqual(hearts["levels"], [{"description": {"en": "Gain two red hearts.", "ru": "Два сердца."},
                                             "descriptionPlain": {"en": "Gain two red hearts.", "ru": "Два сердца."}}])
        self.assertEqual(hearts["lore"], {"en": "Life.", "ru": "Жизнь."})
        corrupted = self.entity("tarot", "CorruptedHeal")
        self.assertEqual(corrupted["description"], {"en": "<color=green>heal</color> but lose gold", "ru": "лечение, но потеря золота"})
        self.assertEqual(corrupted["descriptionPlain"]["en"], "heal but lose gold")
        self.assertEqual(corrupted["lore"], {})

    def test_outfit_texts_and_effects(self):
        self.pack()
        robe = self.entity("outfits", "Robe")
        self.assertEqual(robe["requirements"], {"en": "Build a tailor.", "ru": "Постройте портного."})
        self.assertEqual(robe["effects"][0]["descriptionPlain"]["en"], "10% chance of the Follower pooping instantly.")
        self.assertEqual(robe["cost"], [{"item": "WOOL", "quantity": 2}])

    def test_every_kind_is_published(self):
        index = self.pack()
        for kind in ("relics", "weapons", "curses", "traits", "outfits", "crownAbilities", "fleeces"):
            self.assertEqual(index["counts"][kind], 1, kind)

    def test_missing_icon_is_null(self):
        self.pack()
        self.assertIsNone(self.item("BERRY")["icon"])
        self.assertEqual(self.item("LOG")["icon"], "icons/items/LOG.png")


class RefuseTest(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.dump = self.tmp / "dump"
        shutil.copytree(FIXTURE, self.dump)
        self.site = self.tmp / "site"
        self.site.mkdir()

    def edit(self, name, change):
        path = self.dump / f"{name}.json"
        data = json.loads(path.read_text())
        result = change(data)
        path.write_text(json.dumps(data if result is None else result))

    def previous(self, items):
        (self.site / "v1").mkdir()
        (self.site / "v1" / "index.json").write_text(json.dumps({"counts": {"items": items, "structures": 2, "skins": 2, "rituals": 2, "doctrines": 2, "sermons": 1, "tarot": 2}}))

    def assertRefused(self):
        before = tree(self.site)
        self.assertEqual(pack.main(["pack.py", str(self.dump), str(self.site)]), 1)
        self.assertEqual(tree(self.site), before)

    def test_refuses_missing_save_schema(self):
        (self.dump / "saveSchema.json").unlink()
        self.assertRefused()

    def test_refuses_save_schema_shrink(self):
        self.previous(5)
        fields = [{"key": n, "name": f"F{n}", "type": "bool"} for n in range(10)]
        (self.site / "v1" / "save-schema.json").write_text(json.dumps({"types": {"DataManager": {"fields": fields}}}))
        self.assertRefused()

    def test_refuses_save_schema_losing_types(self):
        self.previous(5)
        dump = json.loads((self.dump / "saveSchema.json").read_text())
        types = {**dump["types"], **{f"Lost{n}": {"fields": []} for n in range(10)}}
        (self.site / "v1" / "save-schema.json").write_text(json.dumps({"roots": dump["roots"], "types": types}))
        self.assertRefused()

    def test_refuses_job_error(self):
        self.edit("meta", lambda m: m["errors"].append({"job": "Skins", "message": "boom"}))
        self.assertRefused()

    def test_refuses_missing_english(self):
        self.edit("i18n", lambda t: {k: v for k, v in t.items() if k != "Inventory/STONE"})
        self.assertRefused()

    def test_refuses_count_drop(self):
        self.previous(10)
        self.edit("items", lambda items: items + [{"id": f"X{n}", "nameTerm": "Inventory/LOG"} for n in range(3)])  # 8
        self.assertRefused()

    def test_allows_small_count_drop(self):
        self.previous(10)
        self.edit("items", lambda items: items + [{"id": f"X{n}", "nameTerm": "Inventory/LOG"} for n in range(4)])  # 9
        self.assertEqual(pack.main(["pack.py", str(self.dump), str(self.site)]), 0)

    def test_refuses_duplicate_id(self):
        self.edit("items", lambda items: items + [dict(items[0])])
        self.assertRefused()

    def test_refuses_unsafe_id(self):
        for bad in ("a/b", "a b", ".."):
            with self.subTest(bad=bad):
                self.edit("skins", lambda skins: skins[:2] + [{"id": bad, "nameTerm": "Skins/Cat"}])
                self.assertRefused()

    def packed_before(self):
        self.assertEqual(pack.main(["pack.py", str(self.dump), str(self.site)]), 0)

    def test_refuses_lost_icons(self):
        self.packed_before()
        shutil.rmtree(self.dump / "icons" / "items")
        self.assertRefused()

    def test_refuses_lost_sizes(self):
        self.packed_before()
        self.edit("structures", lambda st: [dict(s, size=None) for s in st])
        self.assertRefused()

    def test_refuses_lost_language(self):
        self.packed_before()
        self.edit("i18n", lambda t: {k: {l: x for l, x in v.items() if l != "ru"} for k, v in t.items()})
        self.assertRefused()

    def test_refuses_empty_kind_on_first_publish(self):
        self.edit("skins", lambda skins: [])
        self.assertRefused()

    def test_refuses_effect_without_english_text(self):
        self.edit("i18n", lambda t: {k: v for k, v in t.items() if k != "CookingData/CausesIllPoopy/Description"})
        self.assertRefused()

    def test_refuses_id_with_trailing_newline(self):
        self.edit("skins", lambda skins: skins[:2] + [{"id": "Cat2\n", "nameTerm": "Skins/Cat"}])
        self.assertRefused()

    def test_refuses_ids_that_differ_only_in_case(self):
        self.edit("skins", lambda skins: skins + [dict(skins[0], id=skins[0]["id"].lower())])
        self.assertRefused()

    def test_unknown_raw_fields_are_not_published(self):
        self.edit("items", lambda items: [dict(i, debugNote="internal") for i in items])
        self.assertEqual(pack.main(["pack.py", str(self.dump), str(self.site)]), 0)
        self.assertNotIn("debugNote", json.loads((self.site / "v1" / "items" / "LOG.json").read_text()))

    def test_newer_language_codes_are_kept(self):
        self.edit("i18n", lambda t: {k: dict(v, **{"zh-Hans": "木头", "es-419": "Troncos"}) if k == "Inventory/LOG" else v for k, v in t.items()})
        self.assertEqual(pack.main(["pack.py", str(self.dump), str(self.site)]), 0)
        name = json.loads((self.site / "v1" / "items" / "LOG.json").read_text())["name"]
        self.assertEqual(name["zh-Hans"], "木头")
        self.assertEqual(name["es-419"], "Troncos")

    def test_first_publish_has_no_count_check(self):
        self.assertEqual(pack.main(["pack.py", str(self.dump), str(self.site)]), 0)
        self.assertTrue((self.site / "v1" / "index.json").exists())


if __name__ == "__main__":
    unittest.main()

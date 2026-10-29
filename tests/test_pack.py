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
        for name in ("index", "item", "structure", "skin"):
            self.assertEqual((self.site / "v1" / "schema" / f"{name}.schema.json").read_bytes(),
                             (HERE.parent / "schema" / f"{name}.schema.json").read_bytes())

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
        path.write_text(json.dumps(change(data) or data))

    def previous(self, items):
        (self.site / "v1").mkdir()
        (self.site / "v1" / "index.json").write_text(json.dumps({"counts": {"items": items, "structures": 2, "skins": 2}}))

    def assertRefused(self):
        before = tree(self.site)
        self.assertEqual(pack.main(["pack.py", str(self.dump), str(self.site)]), 1)
        self.assertEqual(tree(self.site), before)

    def test_refuses_job_error(self):
        self.edit("meta", lambda m: m["errors"].append({"job": "Skins", "message": "boom"}))
        self.assertRefused()

    def test_refuses_missing_english(self):
        self.edit("i18n", lambda t: {k: v for k, v in t.items() if k != "Inventory/STONE"})
        self.assertRefused()

    def test_refuses_count_drop(self):
        self.previous(10)
        self.edit("items", lambda items: items + [{"id": f"X{n}", "nameTerm": "Inventory/LOG"} for n in range(5)])  # 8
        self.assertRefused()

    def test_allows_small_count_drop(self):
        self.previous(10)
        self.edit("items", lambda items: items + [{"id": f"X{n}", "nameTerm": "Inventory/LOG"} for n in range(6)])  # 9
        self.assertEqual(pack.main(["pack.py", str(self.dump), str(self.site)]), 0)

    def test_refuses_duplicate_id(self):
        self.edit("items", lambda items: items + [dict(items[0])])
        self.assertRefused()

    def test_refuses_unsafe_id(self):
        for bad in ("a/b", "a b", ".."):
            with self.subTest(bad=bad):
                self.edit("skins", lambda skins: skins[:2] + [{"id": bad, "nameTerm": "Skins/Cat"}])
                self.assertRefused()

    def test_first_publish_has_no_count_check(self):
        self.assertEqual(pack.main(["pack.py", str(self.dump), str(self.site)]), 0)
        self.assertTrue((self.site / "v1" / "index.json").exists())


if __name__ == "__main__":
    unittest.main()

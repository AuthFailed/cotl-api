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

    def test_empty_translation_dropped(self):
        self.pack()
        self.assertEqual(self.item("BERRY")["name"], {"en": "Berries"})

    def test_missing_icon_is_null(self):
        self.pack()
        self.assertIsNone(self.item("BERRY")["icon"])
        self.assertEqual(self.item("LOG")["icon"], "icons/items/LOG.png")


if __name__ == "__main__":
    unittest.main()

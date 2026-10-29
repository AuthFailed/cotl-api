"""check.sh against broken copies of the expected site."""
import json
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).parent.parent


@unittest.skipUnless(shutil.which("check-jsonschema") and shutil.which("openapi-spec-validator"), "validators not installed")
class CheckTest(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.site = self.tmp / "site"
        shutil.copytree(ROOT / "tests" / "expected-site", self.site)
        self.v1 = self.site / "v1"

    def check(self):
        return subprocess.run([str(ROOT / "check.sh"), str(self.site)], capture_output=True, text=True)

    def test_expected_site_passes(self):
        r = self.check()
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_missing_icon_fails(self):
        (self.v1 / "icons" / "items" / "LOG.png").unlink()
        self.assertNotEqual(self.check().returncode, 0)

    def test_stale_schema_copy_fails(self):
        (self.v1 / "schema" / "item.schema.json").write_text("{}")
        self.assertNotEqual(self.check().returncode, 0)

    def test_list_disagreeing_with_entity_file_fails(self):
        items = json.loads((self.v1 / "items.json").read_text())
        items[0]["category"] = "something else"
        (self.v1 / "items.json").write_text(json.dumps(items))
        self.assertNotEqual(self.check().returncode, 0)

    def test_invalid_openapi_fails(self):
        spec = json.loads((self.site / "openapi.json").read_text())
        del spec["info"]
        (self.site / "openapi.json").write_text(json.dumps(spec))
        self.assertNotEqual(self.check().returncode, 0)


if __name__ == "__main__":
    unittest.main()

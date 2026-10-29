#!/usr/bin/env bash
# Validate a packed site against the schemas: ./check.sh <site dir>  (needs check-jsonschema on PATH)
set -euo pipefail
V1=$1/v1
S=$(dirname "$0")/schema
check-jsonschema --schemafile "$S/index.schema.json" "$V1/index.json"
check-jsonschema --schemafile "$S/item.schema.json" "$V1"/items/*.json
check-jsonschema --schemafile "$S/structure.schema.json" "$V1"/structures/*.json
check-jsonschema --schemafile "$S/skin.schema.json" "$V1"/skins/*.json
check-jsonschema --schemafile "$S/ritual.schema.json" "$V1"/rituals/*.json
check-jsonschema --schemafile "$S/changes.schema.json" "$V1/changes.json"
openapi-spec-validator "$1/openapi.json"
# What the schemas can't see: list files match their per-id files, icons exist, the schema copies are current.
python3 -I - "$V1" "$S" <<'PY'
import json, sys
from pathlib import Path
v1, schema = Path(sys.argv[1]), Path(sys.argv[2])
bad = []
for kind in json.loads((v1 / "index.json").read_text())["counts"]:
    listed = json.loads((v1 / f"{kind}.json").read_text())
    for e in listed:
        one = v1 / kind / f"{e['id']}.json"
        if not one.exists() or json.loads(one.read_text()) != e:
            bad.append(f"{kind}.json disagrees with {kind}/{e['id']}.json")
        if e.get("icon") and not (v1 / e["icon"]).exists():
            bad.append(f"{kind}/{e['id']}: missing {e['icon']}")
    if len(listed) != len(list((v1 / kind).glob("*.json"))):
        bad.append(f"{kind}: list and folder differ in size")
for f in schema.glob("*.json"):
    if (v1 / "schema" / f.name).read_bytes() != f.read_bytes():
        bad.append(f"v1/schema/{f.name} is not the current schema/{f.name}")
print(*bad, sep="\n") if bad else print("ok -- site consistent")
sys.exit(1 if bad else 0)
PY

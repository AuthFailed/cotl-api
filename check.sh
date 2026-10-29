#!/usr/bin/env bash
# Validate a packed site against the schemas: ./check.sh <site dir>  (needs check-jsonschema on PATH)
set -euo pipefail
V1=$1/v1
S=$(dirname "$0")/schema
check-jsonschema --schemafile "$S/index.schema.json" "$V1/index.json"
# each kind against its schema (schema/<singular>.schema.json, as pack.SINGULAR names them)
while read -r kind one; do
  check-jsonschema --schemafile "$S/$one.schema.json" "$V1/$kind"/*.json
done < <(python3 -c "import sys; sys.path.insert(0, sys.argv[1]); import pack; [print(k, v) for k, v in pack.SINGULAR.items()]" "$(dirname "$0")")
check-jsonschema --schemafile "$S/changes.schema.json" "$V1/changes.json"
for lang in $(python3 -I -c "import json,sys;print(*json.load(open(sys.argv[1]))['languages'])" "$V1/index.json"); do
  check-jsonschema --schemafile "$S/slim.schema.json" "$V1/$lang"/*.json
done
openapi-spec-validator "$1/openapi.json"
# What the schemas can't see: list files match their per-id files, icons exist, the schema copies are current.
python3 -I - "$V1" "$S" <<'PY'
import json, re, sys
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
# A {0} left in published text is something the game fills at run time; each must be documented in placeholders.json.
documented = json.loads((schema.parent / "placeholders.json").read_text())
def placeholders(x):
    if isinstance(x, dict): return [p for k, y in x.items() if k not in ("namePlain", "descriptionPlain") for p in placeholders(y)]
    if isinstance(x, list): return [p for y in x for p in placeholders(y)]
    return re.findall(r"\{\d+\}", x) if isinstance(x, str) else []
for kind in json.loads((v1 / "index.json").read_text())["counts"]:
    for e in json.loads((v1 / f"{kind}.json").read_text()):
        for field in ("name", "description"):
            if placeholders(e.get(field)) and f"{kind}/{e['id']}/{field}" not in documented:
                bad.append(f"{kind}/{e['id']}/{field} has an undocumented {', '.join(sorted(set(placeholders(e.get(field)))))} (see placeholders.json)")
for f in schema.glob("*.json"):
    if (v1 / "schema" / f.name).read_bytes() != f.read_bytes():
        bad.append(f"v1/schema/{f.name} is not the current schema/{f.name}")
print(*bad, sep="\n") if bad else print("ok -- site consistent")
sys.exit(1 if bad else 0)
PY

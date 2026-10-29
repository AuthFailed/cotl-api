#!/usr/bin/env bash
# Validate a packed site against the schemas: ./check.sh <site dir>  (needs check-jsonschema on PATH)
set -euo pipefail
V1=$1/v1
S=$(dirname "$0")/schema
check-jsonschema --schemafile "$S/index.schema.json" "$V1/index.json"
check-jsonschema --schemafile "$S/item.schema.json" "$V1"/items/*.json
check-jsonschema --schemafile "$S/structure.schema.json" "$V1"/structures/*.json
check-jsonschema --schemafile "$S/skin.schema.json" "$V1"/skins/*.json
check-jsonschema --schemafile "$S/changes.schema.json" "$V1/changes.json"

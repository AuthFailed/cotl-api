#!/usr/bin/env bash
# TypeScript types for the API, generated from schema/ and type-checked: ./types.sh <out dir>  (writes types.d.ts)
set -euo pipefail
out=$1
S=$(dirname "$0")/schema
{
  echo "// Types for https://api.cotl.info/v1/, generated from its JSON Schemas. Don't edit by hand."
  echo 'export declare const BASE_URL: "https://api.cotl.info/v1/";'
  for f in "$S"/*.schema.json; do
    npx -y json-schema-to-typescript@16.0.0 -i "$f" --bannerComment ""
  done
} > "$out/types.d.ts"
npx -y -p typescript@7.0.2 tsc --noEmit --strict "$out/types.d.ts"
echo "ok -- $out/types.d.ts"

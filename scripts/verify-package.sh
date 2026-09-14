#!/usr/bin/env bash
# Verify that a jprm-built plugin zip is consistent with build.yaml and carries the
# version we intend to release. This is the last check before anything is published.
#
# Usage: scripts/verify-package.sh <plugin.zip> <version>
#   <version> may be given with or without a leading "v".
set -euo pipefail

zip="${1:?usage: verify-package.sh <plugin.zip> <version>}"
expected_version="${2:?usage: verify-package.sh <plugin.zip> <version>}"
expected_version="${expected_version#v}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
manifest="$root/build.yaml"

fail() {
  echo "::error::verify-package: $*" >&2
  exit 1
}

[ -f "$zip" ] || fail "zip not found: $zip"
[ -f "$manifest" ] || fail "build.yaml not found at $manifest"
command -v unzip >/dev/null || fail "unzip is required"
command -v python3 >/dev/null || fail "python3 is required"

# Scalars are `key: "value"` lines; the artifacts block is `  - "file.dll"` lines.
yaml_scalar() {
  sed -n "s/^$1: *\"\{0,1\}\([^\"]*\)\"\{0,1\} *\$/\1/p" "$manifest" | head -1
}
yaml_artifacts() {
  awk '
    /^artifacts:/ { in_list = 1; next }
    in_list && /^[[:space:]]+-[[:space:]]*/ {
      sub(/^[[:space:]]+-[[:space:]]*"?/, ""); sub(/"?[[:space:]]*$/, ""); print; next
    }
    in_list { in_list = 0 }
  ' "$manifest"
}

name="$(yaml_scalar name)"
guid="$(yaml_scalar guid)"
abi="$(yaml_scalar targetAbi)"
[ -n "$name" ] || fail "build.yaml has no name"
[ -n "$guid" ] || fail "build.yaml has no guid"
[ -n "$abi" ] || fail "build.yaml has no targetAbi"

listing="$(unzip -Z1 "$zip")" || fail "cannot list zip contents (corrupt or not a zip?): $zip"

artifact_count=0
while IFS= read -r artifact; do
  [ -n "$artifact" ] || continue
  artifact_count=$((artifact_count + 1))
  grep -qx -- "$artifact" <<<"$listing" || fail "artifact listed in build.yaml is missing from zip: $artifact"
done <<<"$(yaml_artifacts)"
[ "$artifact_count" -gt 0 ] || fail "build.yaml lists no artifacts"

grep -qx -- "meta.json" <<<"$listing" || fail "meta.json missing from zip"
meta="$(unzip -p "$zip" meta.json)" || fail "cannot read meta.json from zip"
python3 -c 'import json, sys; json.load(sys.stdin)' <<<"$meta" 2>/dev/null || fail "meta.json is not valid JSON"

meta_get() {
  python3 -c 'import json, sys; print(json.load(sys.stdin).get(sys.argv[1], ""))' "$1" <<<"$meta"
}

meta_name="$(meta_get name)"
meta_guid="$(meta_get guid | tr '[:lower:]' '[:upper:]')"
meta_abi="$(meta_get targetAbi)"
meta_version="$(meta_get version)"

[ "$meta_name" = "$name" ] || fail "meta.json name '$meta_name' != build.yaml name '$name'"
[ "$meta_guid" = "$(tr '[:lower:]' '[:upper:]' <<<"$guid")" ] || fail "meta.json guid '$meta_guid' != build.yaml guid '$guid'"
[ "$meta_abi" = "$abi" ] || fail "meta.json targetAbi '$meta_abi' != build.yaml targetAbi '$abi'"
[ "$meta_version" = "$expected_version" ] || fail "meta.json version '$meta_version' != expected '$expected_version'"

dll_size="$(unzip -l "$zip" | awk '$4 == "Gelato.dll" { print $1 }')" || fail "cannot list zip contents: $zip"
[ -n "$dll_size" ] || fail "Gelato.dll missing from zip"
[ "$dll_size" -gt 100000 ] || fail "Gelato.dll is implausibly small: ${dll_size} bytes"

echo "verify-package: OK — $(basename "$zip"), version ${expected_version}, ${artifact_count} artifacts, Gelato.dll ${dll_size} bytes"

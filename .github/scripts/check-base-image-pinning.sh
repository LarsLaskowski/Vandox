#!/usr/bin/env bash
# Checks that every FROM in deploy/backend/Dockerfile uses a base image build argument pinned by digest.
# Used by ci.yml and release.yml (records 0041, 0053).
set -euo pipefail
f=deploy/backend/Dockerfile
if grep -nEi '^[[:space:]]*(from|arg)([[:space:]]|$)' "$f" | grep -vE '^[0-9]+:(FROM|ARG)[[:space:]]'; then
  echo "::error::FROM and ARG must be upper-case and start at the beginning of the line in $f"
  exit 1
fi
if grep -nE '^ARG[[:space:]]+[^[:space:]]+[[:space:]]+[^[:space:]]' "$f"; then
  echo "::error::each ARG line in $f must declare exactly one argument"
  exit 1
fi
if grep -nE '^ARG.*[\\`][[:space:]]*$' "$f"; then
  echo "::error::an ARG line in $f must not continue onto the next line"
  exit 1
fi
if LC_ALL=C grep -n $'\xef\xbb\xbf' "$f"; then
  echo "::error::$f must not contain a byte order mark"
  exit 1
fi
if grep -nEi '^[[:space:]]*(#|//)[[:space:]]*(syntax|escape)[[:space:]]*=' "$f"; then
  echo "::error::$f must not set the syntax or escape parser directive"
  exit 1
fi
first_from="$(grep -nE '^FROM[[:space:]]' "$f" | head -n1 | cut -d: -f1)"
if [ -z "$first_from" ]; then
  echo "::error::no FROM line in $f"
  exit 1
fi
# Prints the default of a global ARG. Fails unless it is declared exactly once in the
# file, with a default, before the first FROM.
arg_default() {
  local name="$1" all before
  all="$(grep -cE "^ARG[[:space:]]+${name}=" "$f" || true)"
  before="$(head -n "$((first_from - 1))" "$f" | grep -cE "^ARG[[:space:]]+${name}=" || true)"
  if [ "$all" != "1" ] || [ "$before" != "1" ]; then
    return 1
  fi
  sed -nE "s/^ARG[[:space:]]+${name}=\"?([^\"[:space:]]+)\"?[[:space:]]*$/\1/p" "$f"
}
re='^FROM[[:space:]]+\$\{(BASE_[A-Z]+)_IMAGE\}@\$\{(BASE_[A-Z]+)_DIGEST\}([[:space:]]+AS[[:space:]]+[a-z][a-z0-9_-]*)?[[:space:]]*$'
bad=0
while IFS= read -r line; do
  if [[ ! "$line" =~ $re ]] || [ "${BASH_REMATCH[1]}" != "${BASH_REMATCH[2]}" ]; then
    echo "::error::FROM line must be FROM \${BASE_<NAME>_IMAGE}@\${BASE_<NAME>_DIGEST}: $line"
    bad=1
    continue
  fi
  p="${BASH_REMATCH[1]}"
  if ! digest="$(arg_default "${p}_DIGEST")" || ! printf '%s' "$digest" | grep -Eq '^sha256:[0-9a-f]{64}$'; then
    echo "::error::${p}_DIGEST must be declared once before the first FROM with a sha256 digest default"
    bad=1
  fi
  for kind in IMAGE TAG; do
    if ! val="$(arg_default "${p}_${kind}")" || [ -z "$val" ]; then
      echo "::error::${p}_${kind} must be declared once before the first FROM with a non-empty default"
      bad=1
    fi
  done
done < <(grep -E '^FROM[[:space:]]' "$f")
if [ "$bad" -ne 0 ]; then
  exit 1
fi

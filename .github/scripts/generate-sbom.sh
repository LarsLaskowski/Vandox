#!/usr/bin/env bash
# Generates and checks the release SBOMs (SPDX 2.3 JSON) for the agent binary and the backend image tarball
# with syft, run as a digest-pinned container without network, capabilities or access to dist/.
# Usage: generate-sbom.sh <agent-binary> <image-tar> <version> <dest-dir>
# Writes <dest-dir>/vandox-agent-linux-amd64.spdx.json and <dest-dir>/vandox-image.spdx.json.
# Exit status: 0 both SBOMs written and checked, 1 any argument, pin, generator, file or content error.
# Needs docker and jq. Used by release.yml (job build) and ci.yml (Release build check), record 0056.
set -euo pipefail
export LC_ALL=C

# The only generator reference: a release tag of syft and the index digest of that tag on ghcr.io
# (docker buildx imagetools inspect ghcr.io/anchore/syft:<tag>, "Digest:" line). Never "latest".
syft_image='ghcr.io/anchore/syft:v1.54.0@sha256:0356562f495d432056237fbea5cbc2d4839c9c75cd500784a66de2e7cc95ca7c'

agent_name=vandox-agent-linux-amd64
agent_sbom=vandox-agent-linux-amd64.spdx.json
image_sbom=vandox-image.spdx.json
main_module=github.com/LarsLaskowski/Vandox
max_bytes=$((16 * 1024 * 1024))

syft_re='^ghcr\.io/anchore/syft:v[0-9]+\.[0-9]+\.[0-9]+@sha256:[0-9a-f]{64}$'
path_re='^/[A-Za-z0-9._/+-]+$'
version_re='^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$'

fail() {
  echo "::error::$1" >&2
  exit 1
}

if [[ $# -ne 4 ]]; then
  fail "usage: generate-sbom.sh <agent-binary> <image-tar> <version> <dest-dir>"
fi

# Makes a path absolute without following symlinks (no realpath).
absolute() {
  if [[ $1 == /* ]]; then
    printf '%s' "$1"
  else
    printf '%s/%s' "$PWD" "$1"
  fi
}

agent="$(absolute "$1")"
image_tar="$(absolute "$2")"
version="$3"
dest="$(absolute "$4")"

# Every input is checked as a whole string with [[ =~ ]] before any value reaches a command.
if ! [[ $version =~ $version_re ]]; then
  fail "version is not a strict SemVer tag (vMAJOR.MINOR.PATCH[-prerelease])"
fi
for input in "$agent" "$image_tar"; do
  if ! [[ -f $input && ! -L $input ]]; then
    fail "an input file is missing, not a regular file or a symlink"
  fi
done
if ! [[ -d $dest && ! -L $dest ]]; then
  fail "the destination is not a directory or is a symlink"
fi
for name in "$agent_sbom" "$image_sbom"; do
  if [[ -e $dest/$name || -L $dest/$name ]]; then
    fail "the destination already holds $name"
  fi
done
for path in "$agent" "$image_tar" "$dest"; do
  if ! [[ $path =~ $path_re ]]; then
    fail "a path contains a character outside [A-Za-z0-9._/+-]"
  fi
done
if ! [[ $syft_image =~ $syft_re ]]; then
  fail "the syft image reference is not ghcr.io/anchore/syft:vX.Y.Z@sha256:<64 hex>"
fi
for tool in docker jq; do
  if ! command -v "$tool" > /dev/null; then
    fail "$tool is required"
  fi
done

# out_dir is mounted writable into the container; log_dir is not mounted anywhere.
out_dir=''
log_dir=''
wrap_token=''
wrap_open=0
cleanup() {
  if [[ $wrap_open -eq 1 ]]; then
    echo "::$wrap_token::"
    wrap_open=0
  fi
  if [[ -n $out_dir ]]; then rm -rf -- "$out_dir"; fi
  if [[ -n $log_dir ]]; then rm -rf -- "$log_dir"; fi
}
trap cleanup EXIT

out_dir="$(mktemp -d)"
log_dir="$(mktemp -d)"
for path in "$out_dir" "$log_dir"; do
  if ! [[ $path =~ $path_re ]]; then
    fail "a scratch directory path contains a character outside [A-Za-z0-9._/+-]"
  fi
done

# Runs the generator once. Arguments: input file, target name inside the container, scan source prefix,
# source name, output file name, log file name. The container's output (and the Docker CLI's pull
# progress) goes to a file, never to the job log.
run_syft() {
  local input=$1 target=$2 prefix=$3 source_name=$4 output=$5 log=$6
  docker run --rm --network none --read-only --tmpfs /tmp --cap-drop ALL \
    --security-opt no-new-privileges --user "$(id -u):$(id -g)" \
    --env SYFT_CHECK_FOR_APP_UPDATE=false --env HOME=/tmp --env XDG_CACHE_HOME=/tmp/cache \
    --mount "type=bind,source=$input,target=/in/$target,readonly" \
    --mount "type=bind,source=$out_dir,target=/out" \
    "$syft_image" scan "$prefix:/in/$target" \
    --output "spdx-json@2.3=/out/$output" \
    --source-name "$source_name" --source-version "$version" \
    > "$log_dir/$log" 2>&1
}

agent_rc=0
run_syft "$agent" "$agent_name" file "$agent_name" "$agent_sbom" agent.log || agent_rc=$?
image_rc=0
run_syft "$image_tar" vandox-image.tar docker-archive networlddev/vandox "$image_sbom" image.log || image_rc=$?

# Workflow commands are switched off while generator output is printed. The token is generated after the
# container runs, so the generator never sees it.
wrap_token="$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
if ! [[ $wrap_token =~ ^[0-9a-f]{32}$ ]]; then
  wrap_token=''
  fail "could not generate the log token"
fi
echo "::stop-commands::$wrap_token"
wrap_open=1
for log in agent.log image.log; do
  echo "--- generator output: $log ---"
  cat -- "$log_dir/$log"
done
echo "::$wrap_token::"
wrap_open=0

if [[ $agent_rc -ne 0 || $image_rc -ne 0 ]]; then
  fail "the SBOM generator container failed (agent exit $agent_rc, image exit $image_rc); see its output above"
fi

for name in "$agent_sbom" "$image_sbom"; do
  if ! [[ -f $out_dir/$name && ! -L $out_dir/$name ]]; then
    fail "the generator did not write $name as a regular file"
  fi
  cp -- "$out_dir/$name" "$dest/$name"
done

check() { # <file> <jq filter> <error text>
  if ! jq -e "$2" "$dest/$1" > /dev/null 2>&1; then
    fail "$3 ($1)"
  fi
}
for name in "$agent_sbom" "$image_sbom"; do
  size="$(stat -c %s -- "$dest/$name")"
  if [[ $size -gt $max_bytes ]]; then
    fail "$name is larger than 16 MiB, the limit of actions/attest"
  fi
  check "$name" '.spdxVersion == "SPDX-2.3"' "spdxVersion is not SPDX-2.3"
  check "$name" 'any(.packages[]?; .name == "stdlib")' "no package named stdlib"
  check "$name" "any(.packages[]?; .name == \"$main_module\")" "no package for the main module $main_module"
done
check "$image_sbom" \
  'any(.packages[]?; any(.externalRefs[]?; (.referenceLocator | type == "string") and (.referenceLocator | startswith("pkg:deb/"))))' \
  "the image SBOM lists no Debian package (pkg:deb/)"

# Summary derived from the SBOMs: printed only inside the wrap.
echo "::stop-commands::$wrap_token"
wrap_open=1
for name in "$agent_sbom" "$image_sbom"; do
  jq -r '"\(input_filename): \(.packages | length) packages, stdlib \([.packages[] | select(.name == "stdlib") | .versionInfo] | join(",")), \([.packages[]?.externalRefs[]? | select((.referenceLocator | type == "string") and (.referenceLocator | startswith("pkg:deb/")))] | length) pkg:deb refs"' "$dest/$name" || true
done
echo "::$wrap_token::"
wrap_open=0
echo "SBOMs written and checked: $dest/$agent_sbom, $dest/$image_sbom"

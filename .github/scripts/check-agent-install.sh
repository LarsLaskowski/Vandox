#!/usr/bin/env bash
# End-to-end check of deploy/agent/install.sh, uninstall.sh and the unit on a real systemd host (CI job
# agent-install, ubuntu-22.04 with systemd 249; plan of feature 42, Approach, "CI job").
# Usage (as root): check-agent-install.sh <dist>
#   <dist> holds the already built vandox-agent-linux-amd64 (version v0.0.0-rc.0), the install bundle
#   vandox-agent-install-v0.0.0-rc.0.tar.gz and the release's SHA256SUMS (two lines, bundle first); the
#   scripts of the bundle take the binary from there with --from, so nothing is downloaded.
#
# The cases run in this order and the script stops at the first mismatch: verify on 249, bundle verification
# (the documented chain, extracted into a private directory; every later case runs the scripts of that
# extracted bundle), uninstall before install, the entry-check and bundle cases (a directory or file owned by
# another user or writable by others, the repository's own install.sh, a VERSION the binary does not match, a
# changed --version option, a tampered pin, another binary), E8 (a kept invalid agent.yaml, ends with a
# purge), first install, security score, ptrace, repeat install, restart after a removed spool directory,
# address allow-list, tailnet device, uninstall, reinstall, purge. It changes the host (user, /etc/vandox, /var/lib/vandox, a unit), so it is meant for a throw-away
# runner and always ends with a purge. The agent's backend (100.64.0.1) is not reachable here; the agent
# spools, which is what the checks need.
set -euo pipefail

readonly VERSION="v0.0.0-rc.0"
readonly BINARY_NAME="vandox-agent-linux-amd64"
readonly AGENT_USER="vandox-agent"
readonly AGENT_ID="ci-runner"
readonly BACKEND_URL="https://100.64.0.1:8081"
readonly CONFIG_DIR="/etc/vandox"
readonly CONFIG_FILE="/etc/vandox/agent.yaml"
readonly TOKEN_FILE="/etc/vandox/agent-token"
readonly STATE_ROOT="/var/lib/vandox"
readonly SPOOL="/var/lib/vandox/spool"
readonly BINARY="/usr/local/bin/vandox-agent"
readonly UNIT="/etc/systemd/system/vandox-agent.service"
readonly WANTS_LINK="/etc/systemd/system/multi-user.target.wants/vandox-agent.service"
# The token of the CI agent: it only has to satisfy the secret rules (32 to 4096 characters of 0x21-0x7E).
readonly CI_TOKEN="ci-agent-token-0123456789abcdef0123456789abcdef"

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly repo
readonly repo_agent_dir="$repo/deploy/agent"
readonly BUNDLE_NAME="vandox-agent-install-$VERSION"
readonly PIN_NAME="vandox-agent-linux-amd64.sha256"
readonly FILES_DIR="/root/vandox-files"
# The extracted bundle (set by the case "bundle verification") and the unit file inside it.
agent_dir=""
unit_source=""
# The transient unit of the address allow-list case and the file its client writes the result to.
readonly IPCHECK_UNIT="/run/systemd/system/vandox-agent-ipcheck.service"
readonly IPCHECK_OUT="/run/vandox-agent-ipcheck.out"
# The case "tailnet device" (issue #337): a provider stand-in behind the networkd-managed link vx-up and a tailnet
# stand-in behind a veth named tailscale0, each a namespace that holds the backend's addresses on its loopback.
readonly TN_PROVIDER_NS="vx-provider"
readonly TN_TAILNET_NS="vx-tailnet"
readonly TN_DEVICE="tailscale0"
readonly TN_DEVICE_PEER="vx-ts-peer"
readonly TN_UPLINK="vx-up"
readonly TN_UPLINK_PEER="vx-up-peer"
readonly TN_NETWORK_FILE="/run/systemd/network/05-vx-up.network"
readonly TN_BACKEND4="100.64.0.1"
readonly TN_BACKEND6="fd7a:115c:a1e0::1"
readonly TN_RANGE4="100.64.0.0/10"
readonly TN_RANGE6="fd7a:115c:a1e0::/48"
readonly TN_PORT=8081
readonly TN_IPV6_URL="http://[$TN_BACKEND6]:$TN_PORT"
readonly TN_ACCOUNTING_DROPIN="/run/systemd/system.control/vandox-agent.service.d/50-IPAccounting.conf"
readonly TN_NOT_ACCEPTING="backend not accepting batches"
readonly TN_ACCEPTED_RE="^accepted "
# Set while the case's links, namespaces, routes and runtime property exist; the cleanup removes them then.
tailnet_active=0
# The copy of agent.yaml while the IPv6 sub-case has changed backend.url.
tailnet_config_backup=""
tailnet_pids=()

# The case names of the messages that several cases share (SonarQube shelldre:S1192).
readonly CASE_PURGE_BEFORE_INSTALL="purge before install"
readonly CASE_UNINSTALL_BEFORE_INSTALL="uninstall before install"
readonly CASE_FIRST_INSTALL="first install"
readonly CASE_REPEAT_INSTALL="repeat install"
readonly CASE_UNINSTALL="uninstall"

# The install output that several cases expect (SonarQube shelldre:S1192).
readonly INSTALLED_MSG="vandox-agent $VERSION installed and running"
readonly KEEPING_MSG="install.sh: keeping existing $CONFIG_FILE"

OUT=""
ERR=""
RC=0
work=""
helper_pids=()

fail() {
  echo "::error::$*" >&2
  exit 1
}

note() {
  echo "== $*"
}

# tailnet_teardown: removes what the case "tailnet device" created and restores agent.yaml; does nothing when the
# case has not set anything up. Every step tolerates a part that is already gone.
tailnet_teardown() {
  local pid

  if [[ $tailnet_active -ne 1 ]]; then
    return 0
  fi

  tailnet_active=0

  for pid in "${tailnet_pids[@]}"; do
    kill "$pid" 2>/dev/null || true
  done

  while ip rule del pref 5270 2>/dev/null; do
    :
  done

  ip route flush table 52 2>/dev/null || true
  ip link del "$TN_DEVICE" 2>/dev/null || true
  rm -f -- "$TN_NETWORK_FILE"
  networkctl reload 2>/dev/null || true
  ip link del "$TN_UPLINK" 2>/dev/null || true
  ip netns del "$TN_PROVIDER_NS" 2>/dev/null || true
  ip netns del "$TN_TAILNET_NS" 2>/dev/null || true

  if [[ -n "$tailnet_config_backup" && -f "$tailnet_config_backup" ]]; then
    cat -- "$tailnet_config_backup" >"$CONFIG_FILE" || true
    rm -f -- "$tailnet_config_backup"
  fi

  tailnet_config_backup=""

  if [[ -e "$TN_ACCOUNTING_DROPIN" ]]; then
    rm -f -- "$TN_ACCOUNTING_DROPIN"
    systemctl daemon-reload >/dev/null 2>&1 || true
  fi

  return 0
}

cleanup() {
  local pid

  for pid in "${helper_pids[@]}"; do
    kill "$pid" 2>/dev/null || true
  done


  if [[ -e "$IPCHECK_UNIT" ]]; then
    rm -f -- "$IPCHECK_UNIT" "$IPCHECK_OUT"
    systemctl daemon-reload >/dev/null 2>&1 || true
  fi

  tailnet_teardown

  # The host is left without the agent whatever happened (the scripts of the extracted bundle live in $work,
  # so they run before it is removed).
  if [[ -n "$agent_dir" && -x "$agent_dir/uninstall.sh" ]]; then
    "$agent_dir/uninstall.sh" --purge >/dev/null 2>&1 || true
  fi

  rm -rf -- "$FILES_DIR"

  if [[ -n "$work" ]]; then
    rm -rf -- "$work"
  fi
}

# run STDIN CMD ARGS...: runs a command with STDIN on its standard input; sets OUT, ERR and RC, never aborts.
run() {
  local stdin=$1
  shift

  RC=0
  printf '%s' "$stdin" | "$@" >"$work/out" 2>"$work/err" || RC=$?
  OUT="$(<"$work/out")"
  ERR="$(<"$work/err")"
}

# install_run STDIN ARGS...: runs install.sh with --from and the CI agent's values.
install_run() {
  local stdin=$1
  shift

  run "$stdin" "$agent_dir/install.sh" "$@"
}

install_args() {
  echo "--agent-id" "$AGENT_ID" "--backend-url" "$BACKEND_URL" "--from" "$dist"
}

show() {
  echo "$OUT"
  if [[ -n "$ERR" ]]; then
    echo "$ERR" >&2
  fi
}

expect_rc() {
  local want=$1
  local what=$2

  if [[ "$RC" != "$want" ]]; then
    show
    fail "$what: exit status $RC, want $want"
  fi
}

# expect_out LINE WHAT / expect_err LINE WHAT: the whole line is on standard output / standard error.
expect_out() {
  local line=$1
  local what=$2

  if ! grep -Fxq -- "$line" <<<"$OUT"; then
    show
    fail "$what: standard output has no line: $line"
  fi
}

expect_err() {
  local line=$1
  local what=$2

  if ! grep -Fxq -- "$line" <<<"$ERR"; then
    show
    fail "$what: standard error has no line: $line"
  fi
}

expect_out_re() {
  local pattern=$1
  local what=$2

  if ! grep -Eq -- "$pattern" <<<"$OUT"; then
    show
    fail "$what: standard output has no line matching: $pattern"
  fi
}

expect_no_out() {
  local text=$1
  local what=$2

  if grep -Fq -- "$text" <<<"$OUT"; then
    show
    fail "$what: standard output holds: $text"
  fi
}

expect_empty_err() {
  local what=$1

  if [[ -n "$ERR" ]]; then
    show
    fail "$what: standard error is not empty"
  fi
}

# expect_before FIRST SECOND WHAT: both lines are on standard output, FIRST earlier.
expect_before() {
  local first=$1
  local second=$2
  local what=$3
  local a b

  a="$(grep -Fxn -- "$first" <<<"$OUT" | head -n 1 | cut -d: -f1)"
  b="$(grep -Fxn -- "$second" <<<"$OUT" | head -n 1 | cut -d: -f1)"

  if [[ -z "$a" || -z "$b" ]] || ((a >= b)); then
    show
    fail "$what: line '$first' is not printed before '$second'"
  fi
}

# expect_stat PATH OWNER:GROUP MODE
expect_stat() {
  local path=$1
  local owner_group=$2
  local mode=$3
  local got

  got="$(stat -c '%U:%G %a' -- "$path" 2>/dev/null)" || fail "$path does not exist"

  if [[ "$got" != "$owner_group $mode" ]]; then
    fail "$path: owner and mode are '$got', want '$owner_group $mode'"
  fi
}

expect_absent() {
  local path=$1

  if [[ -e "$path" || -L "$path" ]]; then
    fail "$path still exists"
  fi
}

# nothing_left WHAT: nothing of S1 is on the host.
nothing_left() {
  local what=$1
  local path

  for path in "$CONFIG_FILE" "$TOKEN_FILE" "$CONFIG_DIR" "$STATE_ROOT" "$BINARY" "$UNIT" "$WANTS_LINK"; do
    if [[ -e "$path" || -L "$path" ]]; then
      fail "$what: $path is left"
    fi
  done

  if getent passwd "$AGENT_USER" >/dev/null; then
    fail "$what: user $AGENT_USER is left"
  fi

  if getent group "$AGENT_USER" >/dev/null; then
    fail "$what: group $AGENT_USER is left"
  fi

  if systemctl is-active --quiet vandox-agent; then
    fail "$what: vandox-agent is still active"
  fi

  if systemctl is-enabled --quiet vandox-agent 2>/dev/null; then
    fail "$what: vandox-agent is still enabled"
  fi
}

# wait_active SECONDS: waits until the unit is active.
wait_active() {
  local seconds=$1
  local i

  for ((i = 0; i < seconds; i++)); do
    if systemctl is-active --quiet vandox-agent; then
      return 0
    fi

    sleep 1
  done

  return 1
}

# unit_properties: one -p argument per line of the shipped unit file that the confinement and ptrace cases
# reuse, read from the file so they cannot drift from it.
unit_properties() {
  local key

  for key in "$@"; do
    grep -E "^${key}=" "$unit_source" | while IFS= read -r line; do
      printf '%s\n' "-p"
      printf '%s\n' "$line"
    done
  done
}

# ---------------------------------------------------------------------------------------------------------

[[ $# -eq 1 ]] || fail "usage: check-agent-install.sh <dist>"
[[ $EUID -eq 0 ]] || fail "check-agent-install.sh must run as root (sudo)"

dist="$(cd "$1" && pwd)"
readonly dist

[[ -f "$dist/$BINARY_NAME" && -f "$dist/SHA256SUMS" && -f "$dist/$BUNDLE_NAME.tar.gz" ]] ||
  fail "$dist holds no $BINARY_NAME, $BUNDLE_NAME.tar.gz and SHA256SUMS"

work="$(mktemp -d)"
trap cleanup EXIT

# --- case: verify on 249 ---------------------------------------------------------------------------------
note "case: verify on systemd 249"
systemctl --version
first="$(systemctl --version | head -n 1)"
[[ "$first" == "systemd 249"* ]] || fail "systemctl --version starts with '$first', want 'systemd 249'"

for tool in python3 setpriv curl sha256sum ss systemd-run systemd-analyze tar gzip awk; do
  command -v "$tool" >/dev/null 2>&1 || fail "$tool is missing on the runner"
done

# systemd 249 ignores an unknown key with a log line only, so the output is read, not the exit status.
verify_out="$(systemd-analyze verify "$repo_agent_dir/vandox-agent.service" 2>&1 || true)"
echo "$verify_out"

# Only lines about this unit count: the tool also loads other units of the runner (snapd.service has a key
# systemd 249 does not know), which are printed as a note.
findings="$(grep -E "Unknown|ignoring|specifier" <<<"$verify_out" || true)"
own_findings="$(grep -F "vandox-agent.service" <<<"$findings" || true)"
foreign_findings="$(grep -vF "vandox-agent.service" <<<"$findings" || true)"

if [[ -n "$foreign_findings" ]]; then
  note "ignored, these lines concern other units:"
  echo "$foreign_findings"
fi

if [[ -n "$own_findings" ]]; then
  fail "systemd-analyze verify reports an unknown key, an ignored line or a specifier of vandox-agent.service: $own_findings"
fi

nothing_left "before the cases"

# --- case: bundle verification ---------------------------------------------------------------------------
note "case: bundle verification (the documented offline lines, fed to bash -s without -e)"
bundle_file="$BUNDLE_NAME.tar.gz"

# split_blocks FILE DIR: writes every fenced code block of FILE to DIR/block-NN, without a CR at the line ends
# (.gitattributes checks *.md out with CRLF).
split_blocks() {
  local file=$1
  local dir=$2

  awk -v dir="$dir" '/^```/ { if (inb) { inb = 0; close(out) } else { inb = 1; n++; out = sprintf("%s/block-%02d", dir, n) } next }
    inb { sub(/\r$/, ""); print > out }' "$file"
}

# find_block DIR PATTERN...: prints the one block file of DIR that holds every PATTERN (fixed strings).
find_block() {
  local dir=$1
  shift
  local block pattern ok found=""
  local count=0

  for block in "$dir"/block-*; do
    ok=1
    for pattern in "$@"; do
      grep -qF -- "$pattern" "$block" || ok=0
    done

    if ((ok == 1)); then
      found=$block
      count=$((count + 1))
    fi
  done

  ((count == 1)) || fail "deploy/agent/README.md has $count blocks holding: $*; want exactly one"
  printf '%s\n' "$found"
}

mkdir "$work/readme-blocks"
split_blocks "$repo_agent_dir/README.md" "$work/readme-blocks"
mapfile -t online_lines < <(grep -v '^[[:space:]]*$' "$(find_block "$work/readme-blocks" 'sudo -i' 'mktemp -d' 'curl ')")
mapfile -t offline_lines < <(grep -v '^[[:space:]]*$' "$(find_block "$work/readme-blocks" 'cp /root/vandox-files/')")
((${#online_lines[@]} == 8)) || fail "the online block of deploy/agent/README.md has ${#online_lines[@]} lines, want 8"
((${#offline_lines[@]} == 3)) || fail "the offline server block of deploy/agent/README.md has ${#offline_lines[@]} lines, want 3"

# write_documented FILE WITH_VARIABLES: the documented offline lines of the server (lines 2, 3 and 5 of the online
# block, then the offline block), unchanged except that v is set to the CI version; no "sudo -i" (this case runs
# as root). Added around them: a line that prints d, a line that prints the status of the check line, and, right
# after the install line, the agent token for the prompt (a failed run line runs it as a command instead).
# shellcheck disable=SC2016  # the echo lines are text for the other shell, not expansions here
write_documented() {
  local file=$1
  local with_variables=$2
  local line

  {
    if [[ "$with_variables" == yes ]]; then
      printf '%s\n' "v=$VERSION" "${online_lines[2]}"
    fi

    printf '%s\n' "${online_lines[4]}" 'echo "D=$d"'

    for line in "${offline_lines[@]}"; do
      printf '%s\n' "$line"

      if [[ "$line" == *"sha256sum -c"* ]]; then
        printf '%s\n' 'echo "CHECK=$?"'
      fi
    done

    printf '%s\n' "$CI_TOKEN"
  } >"$file"
}

# run_documented FILE TMP CWD: (CWD is created, TMP is not) feeds FILE to bash -s (no -e: a failed line is followed by the next one, as
# when all lines are pasted) in the empty directory CWD under umask 022 with TMPDIR=TMP; sets OUT, ERR, RC,
# DIR (the d of the run) and CHECK (the status of the check line).
run_documented() {
  local file=$1
  local tmp=$2
  local cwd=$3

  mkdir -p -- "$cwd"
  RC=0
  (cd "$cwd" && umask 022 && TMPDIR="$tmp" bash -s <"$file") >"$work/out" 2>"$work/err" || RC=$?
  OUT="$(<"$work/out")"
  ERR="$(<"$work/err")"
  DIR="$(sed -n 's/^D=//p' <<<"$OUT")"
  CHECK="$(sed -n 's/^CHECK=//p' <<<"$OUT")"
}

# stage_files SRC: the three release files in /root/vandox-files, as the offline instructions have them.
stage_files() {
  local src=$1

  rm -rf -- "$FILES_DIR"
  mkdir -m 0700 -- "$FILES_DIR"
  cp -- "$src"/* "$FILES_DIR/"
}

# Good row: the offline lines install the agent; the extracted files are root-owned, the scripts 0755 and the rest
# 0644 (F3), and the agent runs. Afterwards it is purged with the extracted bundle's uninstall.sh.
write_documented "$work/documented-good.sh" yes
write_documented "$work/documented-lost.sh" no
stage_files "$dist"
mkdir -p -- "$work/tmp-good"
run_documented "$work/documented-good.sh" "$work/tmp-good" "$work/cwd-good"
show
[[ "$CHECK" == 0 ]] || fail "bundle verification: the check line of the good bundle exited $CHECK"
expect_rc 0 "bundle verification, documented offline lines"
expect_out "$INSTALLED_MSG" "bundle verification, good bundle"
[[ -n "$DIR" && -d "$DIR/$BUNDLE_NAME" ]] || fail "bundle verification: the good bundle was not extracted into d ($DIR)"
agent_dir="$DIR/$BUNDLE_NAME"
unit_source="$agent_dir/vandox-agent.service"

for name in install.sh uninstall.sh; do
  expect_stat "$agent_dir/$name" root:root 755
done

for name in common.sh vandox-agent.service mariadb-user.sql mariadb-user-remove.sql agent.yaml VERSION MANIFEST.sha256 "$PIN_NAME"; do
  expect_stat "$agent_dir/$name" root:root 644
done

expect_stat "$agent_dir" root:root 755
[[ "$(find "$agent_dir" -mindepth 1 | wc -l)" -eq 10 ]] || fail "bundle verification: the bundle does not hold exactly its 10 files: $(ls -A "$agent_dir")"
[[ "$(<"$agent_dir/VERSION")" == "$VERSION" ]] || fail "bundle verification: VERSION is not $VERSION"
(cd "$agent_dir" && sha256sum --check --strict --quiet MANIFEST.sha256) || fail "bundle verification: MANIFEST.sha256 does not match the extracted files"
run "" "$agent_dir/uninstall.sh" --purge
expect_rc 0 "bundle verification, purge of the documented install"
nothing_left "bundle verification, good bundle"

# documented_failure WHAT SCRIPT TMP IN_CWD: runs the documented lines on the staged files of a failure row.
# It must show all of: the check line exits non-zero (except where the private directory could not be made and
# the lines ran in the working directory, IN_CWD=yes); no vandox-agent-install-* entry in d or in the working
# directory; the install line fails with "No such file or directory"; nothing of S1 is created.
documented_failure() {
  local what=$1
  local script=$2
  local tmp=$3
  local in_cwd=$4
  local cwd="$work/cwd-$what"

  # The directory for mktemp exists, except in the row where mktemp must fail.
  if [[ "$in_cwd" == no ]]; then
    mkdir -p -- "$tmp"
  fi

  run_documented "$script" "$tmp" "$cwd"

  if [[ "$in_cwd" == no ]] && { [[ -z "$CHECK" ]] || [[ "$CHECK" == 0 ]]; }; then
    show
    fail "bundle verification, $what: the check line did not fail (status '$CHECK')"
  fi

  if ! grep -Fq "No such file or directory" <<<"$ERR"; then
    show
    fail "bundle verification, $what: the install line did not fail with 'No such file or directory'"
  fi

  # The copied bundle file vandox-agent-install-<v>.tar.gz is expected in d; an extracted directory is not.
  if [[ -n "$DIR" ]] && find "$DIR" -mindepth 1 -maxdepth 1 -type d -name 'vandox-agent-install-*' | grep -q .; then
    fail "bundle verification, $what: an extracted vandox-agent-install-* directory is in d ($DIR)"
  fi

  if [[ "$in_cwd" == no ]] && find "$cwd" -mindepth 1 -maxdepth 1 -type d -name 'vandox-agent-install-*' | grep -q .; then
    fail "bundle verification, $what: an extracted vandox-agent-install-* directory is in the working directory"
  fi

  nothing_left "bundle verification, $what"
}

# One changed byte of the bundle (replaced by a different one).
mkdir "$work/changed" "$work/nobundleline" "$work/nobundle"
cp -- "$dist"/* "$work/changed/"
orig="$(dd if="$work/changed/$bundle_file" bs=1 skip=100 count=1 2>/dev/null | od -An -tx1 | tr -d ' ')"
[[ "$orig" == "00" ]] && replacement='\001' || replacement='\000'
printf '%b' "$replacement" | dd of="$work/changed/$bundle_file" bs=1 seek=100 conv=notrunc 2>/dev/null
cmp -s "$dist/$bundle_file" "$work/changed/$bundle_file" && fail "the changed bundle equals the original"
stage_files "$work/changed"
documented_failure "one changed byte" "$work/documented-good.sh" "$work/tmp-changed" no

# A SHA256SUMS without the bundle line: awk prints nothing and sha256sum fails on empty input (F2).
cp -- "$dist"/* "$work/nobundleline/"
grep -vF "$bundle_file" "$dist/SHA256SUMS" >"$work/nobundleline/SHA256SUMS" || true
stage_files "$work/nobundleline"
documented_failure "SHA256SUMS without the bundle line" "$work/documented-good.sh" "$work/tmp-nobundleline" no

# The bundle is missing.
cp -- "$dist"/* "$work/nobundle/"
rm -- "$work/nobundle/$bundle_file"
stage_files "$work/nobundle"
documented_failure "bundle missing" "$work/documented-good.sh" "$work/tmp-nobundle" no

# mktemp fails (TMPDIR is a missing directory): the lines run in the working directory, where the files pass
# the check and are unpacked, but the install line names the directory d, which is empty, and does not exist.
stage_files "$dist"
documented_failure "mktemp fails" "$work/documented-good.sh" "$work/does-not-exist" yes
[[ -z "$DIR" ]] || fail "bundle verification, mktemp fails: d is '$DIR', want it empty"
expect_absent "/$BUNDLE_NAME"

# Variables lost (lines 5 to 8 only): f is empty, nothing matches, sha256sum fails on empty input.
stage_files "$dist"
documented_failure "variables lost" "$work/documented-lost.sh" "$work/tmp-lost" no
rm -rf -- "$FILES_DIR"

# Interrupted extraction: only the members before the scripts, or all but common.sh, are there.
interrupted="$(mktemp -d "$work/interrupted.XXXXXX")"
tar -xzf "$dist/$bundle_file" -C "$interrupted" --exclude="$BUNDLE_NAME/install.sh"
run "" "$interrupted/$BUNDLE_NAME/install.sh" --agent-id "$AGENT_ID" --backend-url "$BACKEND_URL" --from "$dist"
[[ "$RC" == 127 ]] || fail "interrupted extraction without install.sh: exit status $RC, want 127"
grep -Fq "No such file or directory" <<<"$ERR" || fail "interrupted extraction without install.sh: no 'No such file or directory'"
nothing_left "interrupted extraction without install.sh"

# Without common.sh or without MANIFEST.sha256, for both scripts: the one-line manifest message and nothing
# else (no stat error, no owner line), exit 1, nothing changed.
interrupted_missing() {
  local member=$1
  local script=$2
  local dir

  dir="$(mktemp -d "$work/interrupted.XXXXXX")"
  tar -xzf "$dist/$bundle_file" -C "$dir" --exclude="$BUNDLE_NAME/$member"

  if [[ "$script" == install.sh ]]; then
    run "$CI_TOKEN"$'\n' "$dir/$BUNDLE_NAME/install.sh" --agent-id "$AGENT_ID" --backend-url "$BACKEND_URL" --from "$dist"
  else
    run "" "$dir/$BUNDLE_NAME/uninstall.sh" --purge
  fi

  expect_rc 1 "interrupted extraction without $member, $script"

  if [[ "$ERR" != "$script: the bundle is incomplete or changed; extract it again in a new directory" ]]; then
    show
    fail "interrupted extraction without $member, $script: standard error is not the one manifest line"
  fi

  if [[ -n "$OUT" ]]; then
    show
    fail "interrupted extraction without $member, $script: the script printed on standard output, so it ran"
  fi

  nothing_left "interrupted extraction without $member, $script"
}

interrupted_missing common.sh install.sh
interrupted_missing MANIFEST.sha256 install.sh
interrupted_missing common.sh uninstall.sh
interrupted_missing MANIFEST.sha256 uninstall.sh

# --- case: uninstall before install ----------------------------------------------------------------------
note "case: uninstall before install"
run "" "$agent_dir/uninstall.sh"
expect_rc 0 "uninstall.sh on an absent installation"
expect_out "uninstall.sh: unit not present" "$CASE_UNINSTALL_BEFORE_INSTALL"
expect_out "uninstall.sh: binary not present" "$CASE_UNINSTALL_BEFORE_INSTALL"
expect_out "uninstall.sh: kept user $AGENT_USER, $CONFIG_FILE, $TOKEN_FILE, $SPOOL and the MariaDB account; remove them with --purge" "$CASE_UNINSTALL_BEFORE_INSTALL"
expect_empty_err "$CASE_UNINSTALL_BEFORE_INSTALL"

run "" "$agent_dir/uninstall.sh" --purge
expect_rc 0 "uninstall.sh --purge on an absent installation"
expect_out "uninstall.sh: $CONFIG_FILE not present" "$CASE_PURGE_BEFORE_INSTALL"
expect_out "uninstall.sh: $TOKEN_FILE not present" "$CASE_PURGE_BEFORE_INSTALL"
expect_out "uninstall.sh: $STATE_ROOT not present" "$CASE_PURGE_BEFORE_INSTALL"
expect_out "uninstall.sh: user $AGENT_USER not present" "$CASE_PURGE_BEFORE_INSTALL"
expect_out_re "^uninstall\\.sh: WARNING MariaDB account not removed; run: (plesk db|mariadb) < ${agent_dir//./\\.}/mariadb-user-remove\\.sql\$" "$CASE_PURGE_BEFORE_INSTALL"
expect_no_out "deleting" "$CASE_PURGE_BEFORE_INSTALL"

run "" "$agent_dir/uninstall.sh" --bogus
expect_rc 2 "uninstall.sh --bogus"
expect_err "usage: uninstall.sh [--purge]" "uninstall.sh --bogus"
nothing_left "$CASE_UNINSTALL_BEFORE_INSTALL"

# --- case: entry check and bundle cases (nothing of S1 is created by any of them) ------------------------
note "case: entry check, VERSION, option and pin cases"
install_agent_args=(--agent-id "$AGENT_ID" --backend-url "$BACKEND_URL" --from "$dist")

# bundle_copy: prints the path of a root-owned copy of the extracted bundle in a new private directory.
bundle_copy() {
  local copy

  copy="$(mktemp -d "$work/bundle-copy.XXXXXX")"
  cp -a -- "$agent_dir/." "$copy/"
  printf '%s\n' "$copy"
}

# rewrite_manifest COPY: writes MANIFEST.sha256 of a bundle copy again for its seven members, so that a change
# made on purpose reaches the checks behind the manifest check.
rewrite_manifest() {
  local copy=$1

  (cd "$copy" && sha256sum -- VERSION agent.yaml common.sh mariadb-user-remove.sql mariadb-user.sql "$PIN_NAME" \
    vandox-agent.service >MANIFEST.sha256)
}

entry_line_re='^install\.sh: (/.+) is not owned by root or is writable by others; extract the bundle as root in a private directory$'

# A bundle extracted into a directory that belongs to another user: its parent is refused, by name.
foreign="$(mktemp -d)"
chown nobody "$foreign"
tar --no-same-owner --no-same-permissions -xzf "$dist/$BUNDLE_NAME.tar.gz" -C "$foreign"
run "$CI_TOKEN"$'\n' "$foreign/$BUNDLE_NAME/install.sh" "${install_agent_args[@]}"
expect_rc 1 "install.sh from a directory owned by nobody"
expect_err "install.sh: $(cd -P "$foreign" && pwd -P) is not owned by root or is writable by others; extract the bundle as root in a private directory" "foreign directory"
rm -rf -- "$foreign"
nothing_left "foreign directory"

# A bundle file writable by its group: common.sh is named.
copy="$(bundle_copy)"
chmod g+w "$copy/common.sh"
run "$CI_TOKEN"$'\n' "$copy/install.sh" "${install_agent_args[@]}"
expect_rc 1 "install.sh with a group-writable common.sh"
expect_err "install.sh: $(cd -P "$copy" && pwd -P)/common.sh is not owned by root or is writable by others; extract the bundle as root in a private directory" "writable common.sh"
nothing_left "writable common.sh"

# The repository's own install.sh: the checkout belongs to the runner user, so the entry check refuses it.
run "$CI_TOKEN"$'\n' "$repo_agent_dir/install.sh" "${install_agent_args[@]}"
expect_rc 1 "the repository's install.sh"

if ! grep -Eq "$entry_line_re" <<<"$ERR"; then
  show
  fail "the repository's install.sh did not stop with the entry-check line"
fi

nothing_left "the repository's install.sh"

# A VERSION the binary does not match.
copy="$(bundle_copy)"
printf 'v0.0.0-rc.1\n' >"$copy/VERSION"
rewrite_manifest "$copy"
run "$CI_TOKEN"$'\n' "$copy/install.sh" "${install_agent_args[@]}"
expect_rc 1 "install.sh with another VERSION"
expect_err "install.sh: binary is not v0.0.0-rc.1" "another VERSION"
nothing_left "another VERSION"

# A common.sh cut after its first complete function (a part-way extraction): the manifest refuses it before
# anything is sourced, for the install script and for the uninstall script.
manifest_line_re="the bundle is incomplete or changed; extract it again in a new directory"
copy="$(bundle_copy)"
cut_line="$(awk '/^}$/ { print NR; exit }' "$copy/common.sh")"
[[ -n "$cut_line" ]] || fail "common.sh has no function end to cut after"
head -n "$cut_line" "$copy/common.sh" >"$copy/common.sh.cut"
mv -- "$copy/common.sh.cut" "$copy/common.sh"
chmod 0644 "$copy/common.sh"
run "$CI_TOKEN"$'\n' "$copy/install.sh" "${install_agent_args[@]}"
expect_rc 1 "install.sh with a truncated common.sh"
expect_err "install.sh: $manifest_line_re" "truncated common.sh"
nothing_left "truncated common.sh"
run "" "$copy/uninstall.sh" --purge
expect_rc 1 "uninstall.sh with a truncated common.sh"
expect_err "uninstall.sh: $manifest_line_re" "truncated common.sh, uninstall.sh"

if [[ -n "$OUT" ]]; then
  show
  fail "uninstall.sh with a truncated common.sh printed on standard output, so it ran"
fi

# A unit file cut in half.
copy="$(bundle_copy)"
unit_size="$(stat -c %s "$copy/vandox-agent.service")"
truncate -s "$((unit_size / 2))" "$copy/vandox-agent.service"
run "$CI_TOKEN"$'\n' "$copy/install.sh" "${install_agent_args[@]}"
expect_rc 1 "install.sh with a truncated unit"
expect_err "install.sh: $manifest_line_re" "truncated unit"
nothing_left "truncated unit"

# A missing VERSION is a missing member of the manifest: refused before VERSION is read.
copy="$(bundle_copy)"
rm -- "$copy/VERSION"
run "$CI_TOKEN"$'\n' "$copy/install.sh" "${install_agent_args[@]}"
expect_rc 1 "install.sh without VERSION"
expect_err "install.sh: $manifest_line_re" "missing VERSION"
nothing_left "missing VERSION"

# --version is gone: usage, exit 2.
run "$CI_TOKEN"$'\n' "$agent_dir/install.sh" --version "$VERSION" "${install_agent_args[@]}"
expect_rc 2 "install.sh --version"
expect_err "usage: install.sh [--agent-id ID --backend-url URL] [--from DIR] [--no-mariadb]" "--version option"
nothing_left "--version option"

# Tampered checksum: the pin file of a bundle copy names another hash; the binary is not installed.
copy="$(bundle_copy)"
sed 's/^0/1/; t; s/^./0/' "$agent_dir/$PIN_NAME" >"$copy/$PIN_NAME"
cmp -s "$agent_dir/$PIN_NAME" "$copy/$PIN_NAME" && fail "the tampered pin equals the original"
rewrite_manifest "$copy"
run "$CI_TOKEN"$'\n' "$copy/install.sh" "${install_agent_args[@]}"
expect_rc 1 "install.sh with a tampered pin"
expect_err "install.sh: checksum mismatch, nothing installed" "tampered pin"
nothing_left "tampered pin"

# A different binary in the --from directory: the pin of the intact bundle does not match it.
mkdir "$work/otherbin"
cp -- "$dist/$BINARY_NAME" "$work/otherbin/$BINARY_NAME"
printf 'x' >>"$work/otherbin/$BINARY_NAME"
run "$CI_TOKEN"$'\n' "$agent_dir/install.sh" --agent-id "$AGENT_ID" --backend-url "$BACKEND_URL" --from "$work/otherbin"
expect_rc 1 "install.sh with another binary"
expect_err "install.sh: checksum mismatch, nothing installed" "another binary"
nothing_left "another binary"

# --- case: E8 (a kept invalid agent.yaml) ----------------------------------------------------------------
note "case: E8, an invalid agent.yaml is kept and the agent does not start"
mkdir -m 0755 "$CONFIG_DIR"
printf '%s\n' 'agent_id: "bad id"' 'backend:' "  url: $BACKEND_URL" 'spool:' "  directory: $SPOOL" >"$CONFIG_FILE"
chmod 0644 "$CONFIG_FILE"

# shellcheck disable=SC2046
install_run "$CI_TOKEN"$'\n' $(install_args)
expect_rc 1 "install.sh with an invalid kept agent.yaml"
expect_out "$KEEPING_MSG" "E8"
expect_err "install.sh: vandox-agent did not start; see: journalctl -u vandox-agent" "E8"
[[ -x "$BINARY" && -f "$UNIT" ]] || fail "E8: the installed files do not stay in place"

run "" "$agent_dir/uninstall.sh" --purge
expect_rc 0 "uninstall.sh --purge after E8"
expect_before "uninstall.sh: deleting $STATE_ROOT (records not yet sent are lost)" "uninstall.sh: removed $STATE_ROOT" "purge after E8"
nothing_left "E8 purge"

# --- case: first install ---------------------------------------------------------------------------------
note "case: first install"
# shellcheck disable=SC2046
install_run "$CI_TOKEN"$'\n' $(install_args)
show
expect_rc 0 "$CASE_FIRST_INSTALL"
expect_out "$INSTALLED_MSG" "$CASE_FIRST_INSTALL"
expect_out "install.sh: user $AGENT_USER: created" "$CASE_FIRST_INSTALL"
expect_out_re "^install\\.sh: WARNING MariaDB account not created; run: (plesk db|mariadb) < ${agent_dir//./\\.}/mariadb-user\\.sql\$" "$CASE_FIRST_INSTALL"

for name in journal-group journal-clock ipc-filter capabilities; do
  expect_out_re "^check $name: (ok|WARNING .+)\$" "$CASE_FIRST_INSTALL"
done

if grep -Fq -- "$CI_TOKEN" <<<"$OUT$ERR"; then
  fail "first install: the token is in the script's output"
fi

expect_stat "$CONFIG_DIR" root:root 755
expect_stat "$CONFIG_FILE" "root:$AGENT_USER" 640
expect_stat "$TOKEN_FILE" root:root 600
expect_stat "$BINARY" root:root 755
expect_stat "$UNIT" root:root 644
expect_stat "$STATE_ROOT" root:root 755
expect_stat "$SPOOL" "$AGENT_USER:$AGENT_USER" 700

grep -Fxq "agent_id: $AGENT_ID" "$CONFIG_FILE" || fail "agent.yaml lacks the agent ID line"
grep -Fxq "  url: $BACKEND_URL" "$CONFIG_FILE" || fail "agent.yaml lacks the url line"
[[ "$(<"$TOKEN_FILE")" == "$CI_TOKEN" ]] || fail "the token file does not hold the token"

entry="$(getent passwd "$AGENT_USER")"
IFS=: read -r _ _ _ _ _ home shell <<<"$entry"
[[ "$shell" == "/usr/sbin/nologin" ]] || fail "user shell is '$shell', want /usr/sbin/nologin"
[[ "$home" == "/nonexistent" || ! -e "$home" ]] || fail "user has a home directory $home"
groups_of_user="$(id -Gn "$AGENT_USER" | tr ' ' '\n' | sort | tr '\n' ' ')"
[[ "$groups_of_user" == "adm systemd-journal $AGENT_USER " ]] || fail "groups of the user are '$groups_of_user', want adm, systemd-journal and $AGENT_USER"
[[ "$(id -gn "$AGENT_USER")" == "$AGENT_USER" ]] || fail "primary group of the user is not $AGENT_USER"

[[ "$(systemctl is-enabled vandox-agent)" == "enabled" ]] || fail "vandox-agent is not enabled"
[[ "$(systemctl is-active vandox-agent)" == "active" ]] || fail "vandox-agent is not active"

main_pid="$(systemctl show -p MainPID --value vandox-agent)"
[[ "$main_pid" =~ ^[1-9][0-9]*$ ]] || fail "vandox-agent has no main PID ('$main_pid')"
status="$(<"/proc/$main_pid/status")"

for want in "CapEff:	0000000000080000" "CapBnd:	0000000000080000" "NoNewPrivs:	1" "Seccomp:	2"; do
  grep -Fxq -- "$want" <<<"$status" || fail "/proc/$main_pid/status lacks the line '$want'"
done

if tr '\0' '\n' <"/proc/$main_pid/cmdline" | grep -Fq -- "$CI_TOKEN"; then
  fail "the token is in the command line of the agent"
fi

if tr '\0' '\n' <"/proc/$main_pid/environ" | grep -Fq -- "$CI_TOKEN"; then
  fail "the token is in the environment of the agent"
fi

tr '\0' '\n' <"/proc/$main_pid/environ" | grep -Fxq "VANDOX_AGENT_TOKEN_FILE=/run/credentials/vandox-agent.service/agent-token" ||
  fail "the agent's environment lacks VANDOX_AGENT_TOKEN_FILE"

if journalctl -u vandox-agent --no-pager 2>/dev/null | grep -Fq -- "$CI_TOKEN"; then
  fail "the token is in the journal of the unit"
fi

# --- case: security score --------------------------------------------------------------------------------
note "case: security score"
security_out="$(systemd-analyze security vandox-agent --no-pager 2>&1)" || true
echo "$security_out"
overall="$(grep -E 'Overall exposure level for vandox-agent\.service' <<<"$security_out" || true)"
[[ -n "$overall" ]] || fail "systemd-analyze security prints no overall exposure level"

if ! grep -Eq 'vandox-agent\.service: [0-9.]+ (SAFE|OK)' <<<"$overall"; then
  fail "the overall exposure level is not OK or better: $overall"
fi

# --- case: server checks and ptrace ----------------------------------------------------------------------
note "case: ptrace attach is refused with EPERM under the unit's confinement"
sleep 300 &
sleeper=$!
helper_pids+=("$sleeper")

mapfile -t props < <(unit_properties User AmbientCapabilities CapabilityBoundingSet NoNewPrivileges SystemCallFilter SystemCallErrorNumber SystemCallArchitectures)
[[ ${#props[@]} -ge 14 ]] || fail "read only ${#props[@]} words of properties from the unit file"

ptrace_code='import ctypes,sys
libc = ctypes.CDLL(None, use_errno=True)
r = libc.ptrace(16, int(sys.argv[1]), 0, 0)
print("EPERM" if r == -1 and ctypes.get_errno() == 1 else "result=%d errno=%d" % (r, ctypes.get_errno()))
for line in open("/proc/self/status"):
    if line.startswith("CapEff:"):
        print("CapEff=" + line.split()[1])'

ptrace_out="$(systemd-run --wait --pipe --collect --quiet "${props[@]}" python3 -c "$ptrace_code" "$sleeper" 2>&1)" || true
echo "$ptrace_out"
grep -Fxq "EPERM" <<<"$ptrace_out" || fail "ptrace(PTRACE_ATTACH) is not refused with EPERM: $ptrace_out"
grep -Fxq "CapEff=0000000000080000" <<<"$ptrace_out" || fail "the confined process does not hold CAP_SYS_PTRACE alone (CapEff): $ptrace_out"
kill "$sleeper" 2>/dev/null || true

# --- case: repeat install --------------------------------------------------------------------------------
note "case: repeat install"
inode_before="$(stat -c %i "$BINARY")"
config_before="$(sha256sum "$CONFIG_FILE")"
token_before="$(sha256sum "$TOKEN_FILE")"
printf 'kept\n' >"$SPOOL/keep-me"

# Standard input is empty: asking for the token would fail the script.
run "" "$agent_dir/install.sh" --agent-id other-name --backend-url https://100.64.0.9:9 --from "$dist"
show
expect_rc 0 "$CASE_REPEAT_INSTALL"
expect_out "$INSTALLED_MSG" "$CASE_REPEAT_INSTALL"
expect_out "$KEEPING_MSG" "$CASE_REPEAT_INSTALL"
expect_no_out "MariaDB account not created" "$CASE_REPEAT_INSTALL"
[[ "$(stat -c %i "$BINARY")" != "$inode_before" ]] || fail "repeat install: the binary was not replaced by a rename (same inode)"
[[ "$(sha256sum "$CONFIG_FILE")" == "$config_before" ]] || fail "repeat install: agent.yaml changed"
[[ "$(sha256sum "$TOKEN_FILE")" == "$token_before" ]] || fail "repeat install: the token file changed"
[[ -f "$SPOOL/keep-me" ]] || fail "repeat install: a spool file is gone"
wait_active 30 || fail "repeat install: vandox-agent is not active"

# --- case: restart after a removed spool directory -------------------------------------------------------
note "case: restart after a removed spool directory"
rm -rf -- "$SPOOL"
systemctl kill --signal=SIGKILL vandox-agent
# One failed start (RestartSec is 30 s): the unit keeps trying and never gives up.
sleep 40
if systemctl is-active --quiet vandox-agent; then
  fail "the agent is active although its spool directory is missing"
fi

[[ "$(systemctl show -p Result --value vandox-agent)" != "start-limit-hit" ]] || fail "the unit hit its start limit"
mkdir -m 0700 -- "$SPOOL"
chown -h "$AGENT_USER:$AGENT_USER" -- "$SPOOL"
wait_active 45 || fail "the agent did not come back within 45 s after the spool directory was recreated"
# The removal took the marker of the repeat-install case with it; the uninstall and reinstall cases need one.
printf 'kept\n' >"$SPOOL/keep-me"

# --- case: address allow-list ----------------------------------------------------------------------------
note "case: address allow-list (fact 16)"
own_ip="$(ip -4 route get 1.1.1.1 2>/dev/null | sed -n 's/.* src \([0-9.]*\).*/\1/p' | head -n 1)"
[[ -n "$own_ip" && "$own_ip" != 127.* ]] || fail "the runner has no non-loopback IPv4 address"

start_listener() {
  local address=$1
  local port=$2
  local log=$3
  local i

  python3 -c 'import socket,sys
s = socket.create_server((sys.argv[1], int(sys.argv[2])))
while True:
    c, _ = s.accept()
    print("accepted", flush=True)
    c.close()' "$address" "$port" >"$log" 2>&1 &
  helper_pids+=("$!")

  for ((i = 0; i < 20; i++)); do
    if ss -ltnH "sport = :$port" | grep -Fq -- "$address:$port"; then
      return 0
    fi

    sleep 0.5
  done

  fail "the listener on $address:$port did not start"
}

start_listener "$own_ip" 18081 "$work/listener-own.log"
start_listener 127.0.0.1 18082 "$work/listener-loopback.log"

# systemd-run -p does not know the keyword localhost of IPAddressAllow= (its command-line parser takes
# prefixes only), so the shipped lines are tested literally in a transient unit file.
mapfile -t net_lines < <(grep -E '^IPAddress(Deny|Allow)=' "$unit_source")
[[ ${#net_lines[@]} -eq 2 ]] || fail "read ${#net_lines[@]} IPAddress lines from the unit file, want 2"

cat >"$work/ipcheck.py" <<'PY'
import socket
import sys

for name, host, port in (("own", sys.argv[1], sys.argv[2]), ("loopback", sys.argv[3], sys.argv[4])):
    try:
        socket.create_connection((host, int(port)), timeout=5).close()
        print(name + "=reached", flush=True)
    except OSError:
        print(name + "=blocked", flush=True)
PY

python_path="$(command -v python3)"

{
  printf '%s\n' "[Unit]" "Description=Address allow-list check of the Vandox agent unit" "" "[Service]" "Type=oneshot"
  printf '%s\n' "${net_lines[@]}"
  printf '%s\n' "StandardOutput=truncate:$IPCHECK_OUT" "StandardError=journal"
  printf '%s\n' "ExecStart=$python_path $work/ipcheck.py $own_ip 18081 127.0.0.1 18082"
} >"$IPCHECK_UNIT"

systemctl daemon-reload
systemctl start vandox-agent-ipcheck.service || {
  journalctl -u vandox-agent-ipcheck.service --no-pager || true
  fail "the address check unit failed"
}

ip_result="$(<"$IPCHECK_OUT")"
echo "non-tailnet address $own_ip and loopback: $(tr '\n' ' ' <<<"$ip_result")"
[[ "$(grep -F 'own=' <<<"$ip_result")" == "own=blocked" ]] || fail "a request to $own_ip was not blocked: $ip_result"
[[ "$(grep -F 'loopback=' <<<"$ip_result")" == "loopback=reached" ]] || fail "a request to 127.0.0.1 was not allowed: $ip_result"
rm -f -- "$IPCHECK_UNIT" "$IPCHECK_OUT"
systemctl daemon-reload
grep -Fxq accepted "$work/listener-loopback.log" || fail "the loopback listener got no connection"

if grep -Fxq accepted "$work/listener-own.log"; then
  fail "the listener on $own_ip got a connection through the address filter"
fi

# --- case: tailnet device --------------------------------------------------------------------------------
# Issue #337: the agent binds every non-loopback connection to tailscale0. A provider route for the tailnet ranges
# on the networkd-managed link vx-up stands in for the default route while Tailscale is down; the agent must never
# reach the provider listener behind it, with or without a tailscale0, with or without Tailscale's table-52 route,
# and after networkd reconfigures vx-up or its carrier flaps. Unbound probes from the host, outside the unit, show
# first that each listener a state names is reachable; IP accounting of the unit counts the agent's packets.
note "case: tailnet device (issue #337)"
networkd_state="$(systemctl is-active systemd-networkd || true)"
echo "systemd-networkd: $networkd_state"
[[ "$networkd_state" == "active" ]] || fail "tailnet device: systemd-networkd is '$networkd_state', want active"

tn_provider4_log="$work/tn-provider4.log"
tn_provider6_log="$work/tn-provider6.log"
tn_tailnet4_log="$work/tn-tailnet4.log"
tn_tailnet6_log="$work/tn-tailnet6.log"
tn_journal=""
# Seconds the agent gets for one more send (SendInterval is 4 s) before a listener is checked for its connections.
tn_settle=5

cat >"$work/tnprobe.py" <<'PY'
import socket
import sys

host, port = sys.argv[1], int(sys.argv[2])
family = socket.AF_INET6 if ":" in host else socket.AF_INET
s = socket.socket(family, socket.SOCK_STREAM)
s.settimeout(5)
try:
    if len(sys.argv) > 3:
        s.setsockopt(socket.SOL_SOCKET, socket.SO_BINDTODEVICE, sys.argv[3].encode())
    s.connect((host, port))
    print("reached", flush=True)
except OSError as e:
    print("blocked: " + str(e), flush=True)
finally:
    s.close()
PY

# tn_listener NS ADDRESS LOG: an accept-and-record listener on ADDRESS:TN_PORT inside NS, one line
# "accepted <peer address>" per connection, appended to LOG (so LOG can be emptied while it runs).
tn_listener() {
  local ns=$1
  local address=$2
  local log=$3
  local i

  ip netns exec "$ns" python3 -c 'import socket,sys
family = socket.AF_INET6 if ":" in sys.argv[1] else socket.AF_INET
s = socket.create_server((sys.argv[1], int(sys.argv[2])), family=family)
while True:
    c, peer = s.accept()
    print("accepted " + peer[0], flush=True)
    c.close()' "$address" "$TN_PORT" >>"$log" 2>&1 &
  tailnet_pids+=("$!")
  helper_pids+=("$!")

  for ((i = 0; i < 20; i++)); do
    if ip netns exec "$ns" ss -ltnH "sport = :$TN_PORT" | grep -Fq -- "$address"; then
      return 0
    fi

    sleep 0.5
  done

  fail "tailnet device: the listener on $address:$TN_PORT in $ns did not start: $(<"$log")"
}

# tn_reaches WHAT HOST LOG [DEVICE]: the positive control. A probe from the host namespace, outside the unit,
# bound to DEVICE when given, connects to HOST:TN_PORT, and the listener writing LOG records it; LOG is emptied
# before and afterwards.
tn_reaches() {
  local what=$1
  local host=$2
  local log=$3
  local device=${4:-}
  local out
  local i

  : >"$log"
  out="$(python3 "$work/tnprobe.py" "$host" "$TN_PORT" ${device:+"$device"} 2>&1)" || true
  echo "$what: probe of $host${device:+ bound to $device}: $out"
  [[ "$out" == "reached" ]] || fail "$what: the probe of $host${device:+ bound to $device} did not connect: $out"

  for ((i = 0; i < 10; i++)); do
    if grep -Eq "$TN_ACCEPTED_RE" "$log"; then
      break
    fi

    sleep 0.5
  done

  grep -Eq "$TN_ACCEPTED_RE" "$log" || fail "$what: the listener behind $host recorded no connection of the probe"
  : >"$log"
}

# tn_restart WHAT [LOG]: restarts the agent; it must be active and, within 15 s, the journal of this invocation must
# show TN_NOT_ACCEPTING, or LOG (when given) a connection. Sets tn_journal to that journal.
tn_restart() {
  local what=$1
  local log=${2:-}
  local id
  local i
  local seen=0

  systemctl restart vandox-agent || {
    journalctl -u vandox-agent -n 50 --no-pager || true
    fail "$what: vandox-agent did not restart"
  }
  id="$(systemctl show -P InvocationID vandox-agent)"
  [[ -n "$id" ]] || fail "$what: vandox-agent has no invocation ID"

  for ((i = 0; i < 15; i++)); do
    sleep 1
    tn_journal="$(journalctl "_SYSTEMD_INVOCATION_ID=$id" --no-pager -o cat 2>/dev/null || true)"

    if grep -Fq -- "$TN_NOT_ACCEPTING" <<<"$tn_journal" || { [[ -n "$log" ]] && grep -Eq "$TN_ACCEPTED_RE" "$log"; }; then
      seen=1
      break
    fi
  done

  if [[ $seen -ne 1 ]]; then
    echo "$tn_journal"
    fail "$what: within 15 s of the restart the agent logged no '$TN_NOT_ACCEPTING'${log:+ and its tailnet listener recorded no connection}"
  fi

  [[ "$(systemctl is-active vandox-agent)" == "active" ]] || fail "$what: vandox-agent is not active after the restart"
}

# tn_no_agent WHAT LOG: the provider listener writing LOG accepted no connection since LOG was emptied.
tn_no_agent() {
  local what=$1
  local log=$2

  if grep -Eq "$TN_ACCEPTED_RE" "$log"; then
    fail "$what: the provider listener accepted a connection of the agent: $(tr '\n' ' ' <"$log")"
  fi
}

# tn_egress: the unit's IPEgressPackets.
tn_egress() {
  systemctl show -P IPEgressPackets vandox-agent
}

# tn_wait_uplink WHAT: waits up to 30 s until networkd's provider routes for both ranges are on vx-up.
tn_wait_uplink() {
  local what=$1
  local route4=""
  local route6=""
  local i

  for ((i = 0; i < 60; i++)); do
    route4="$(ip route show "$TN_RANGE4")"
    route6="$(ip -6 route show "$TN_RANGE6")"

    if [[ "$route4" == *"dev $TN_UPLINK"* && "$route4" == *"proto static"* && "$route6" == *"dev $TN_UPLINK"* ]]; then
      echo "$what: ip route show $TN_RANGE4: $route4"
      echo "$what: ip -6 route show $TN_RANGE6: $route6"
      return 0
    fi

    sleep 0.5
  done

  fail "$what: the provider routes are not on $TN_UPLINK with proto static: '$route4' '$route6'"
}

# tn_flap WHAT: takes the carrier of vx-up away and back (its peer down and up), then waits for networkd's routes.
tn_flap() {
  local what=$1

  ip -n "$TN_PROVIDER_NS" link set "$TN_UPLINK_PEER" down
  sleep 3
  ip -n "$TN_PROVIDER_NS" link set "$TN_UPLINK_PEER" up
  tn_wait_uplink "$what"
}

# tn_reconfigure WHAT: networkd configures vx-up again, then its routes are waited for.
tn_reconfigure() {
  local what=$1

  networkctl reconfigure "$TN_UPLINK"
  sleep 2
  tn_wait_uplink "$what"
}

# tn_standin_add: the tailnet stand-in, a veth tailscale0 to the tailnet namespace with addresses outside the
# tailnet's backend address and return routes for them; no route for the tailnet ranges on it.
tn_standin_add() {
  ip link add "$TN_DEVICE" type veth peer name "$TN_DEVICE_PEER" netns "$TN_TAILNET_NS"
  sysctl -qw "net.ipv6.conf.$TN_DEVICE.disable_ipv6=0" || fail "tailnet device: IPv6 cannot be enabled on $TN_DEVICE"
  ip addr add 100.100.0.1/32 dev "$TN_DEVICE"
  ip addr add fd7a:115c:a1e0:ffff::1/128 dev "$TN_DEVICE" nodad
  ip link set "$TN_DEVICE" up
  ip -n "$TN_TAILNET_NS" link set "$TN_DEVICE_PEER" up
  ip -n "$TN_TAILNET_NS" route add 100.100.0.1/32 dev "$TN_DEVICE_PEER"
  ip -n "$TN_TAILNET_NS" -6 route add fd7a:115c:a1e0:ffff::1/128 dev "$TN_DEVICE_PEER"
  echo "ip route get $TN_BACKEND4 oif $TN_DEVICE: $(ip route get "$TN_BACKEND4" oif "$TN_DEVICE" 2>&1 || true)"
  echo "ip route show $TN_RANGE4: $(ip route show "$TN_RANGE4")"
}

# tn_state_absent WHAT: no tailscale0: every send fails as connection failed, the provider listener accepts nothing
# and the unit sends no packet over 20 s.
tn_state_absent() {
  local what=$1
  local before
  local after

  if ip link show "$TN_DEVICE" >/dev/null 2>&1; then
    fail "$what: $TN_DEVICE exists, want it absent"
  fi

  tn_reaches "$what" "$TN_BACKEND4" "$tn_provider4_log"
  tn_restart "$what"
  grep -F -- "$TN_NOT_ACCEPTING" <<<"$tn_journal" | grep -Fq -- 'reason="connection failed"' ||
    fail "$what: the agent's '$TN_NOT_ACCEPTING' line lacks reason=\"connection failed\": $tn_journal"
  before="$(tn_egress)"
  sleep 20
  after="$(tn_egress)"
  echo "$what: IPEgressPackets $before, 20 s later $after"
  [[ "$before" == "0" && "$after" == "0" ]] || fail "$what: IPEgressPackets is $before, then $after, want 0 both times"
  tn_no_agent "$what" "$tn_provider4_log"
}

# tn_state_unrouted WHAT: tailscale0 without a route for the tailnet: the provider listener accepts nothing from the
# agent; what the tailnet listener got is printed.
tn_state_unrouted() {
  local what=$1

  tn_reaches "$what" "$TN_BACKEND4" "$tn_provider4_log"
  tn_reaches "$what" "$TN_BACKEND4" "$tn_tailnet4_log" "$TN_DEVICE"
  tn_restart "$what" "$tn_tailnet4_log"
  sleep "$tn_settle"
  echo "$what: tailnet listener (printed, not asserted): $(grep -Ec "$TN_ACCEPTED_RE" "$tn_tailnet4_log" || true) connection(s)"
  tn_no_agent "$what" "$tn_provider4_log"
}

# tn_state_ipv6 WHAT: with backend.url on the IPv6 tailnet address, the provider listener accepts nothing.
tn_state_ipv6() {
  local what=$1

  tn_reaches "$what" "$TN_BACKEND6" "$tn_provider6_log"
  tn_restart "$what"
  sleep "$tn_settle"
  tn_no_agent "$what" "$tn_provider6_log"
}

tailnet_active=1

for ns in "$TN_PROVIDER_NS" "$TN_TAILNET_NS"; do
  ip netns add "$ns"
  ip netns exec "$ns" sysctl -qw net.ipv6.conf.all.disable_ipv6=0 net.ipv6.conf.default.disable_ipv6=0 ||
    fail "tailnet device: IPv6 cannot be enabled in $ns"
  ip -n "$ns" link set lo up
  ip -n "$ns" addr add "$TN_BACKEND4/32" dev lo
  ip -n "$ns" addr add "$TN_BACKEND6/128" dev lo nodad
done

ip link add "$TN_UPLINK" type veth peer name "$TN_UPLINK_PEER" netns "$TN_PROVIDER_NS"
sysctl -qw "net.ipv6.conf.$TN_UPLINK.disable_ipv6=0" || fail "tailnet device: IPv6 cannot be enabled on $TN_UPLINK"
# vx-up's addresses are on the peer's subnets, so the provider namespace has its return routes as link routes.
ip -n "$TN_PROVIDER_NS" addr add 198.51.100.2/30 dev "$TN_UPLINK_PEER"
ip -n "$TN_PROVIDER_NS" addr add 2001:db8:76::2/64 dev "$TN_UPLINK_PEER" nodad
ip -n "$TN_PROVIDER_NS" link set "$TN_UPLINK_PEER" up
mkdir -p -- "$(dirname "$TN_NETWORK_FILE")"
cat >"$TN_NETWORK_FILE" <<NETWORK
[Match]
Name=$TN_UPLINK

[Link]
RequiredForOnline=no

[Network]
LinkLocalAddressing=no
IPv6AcceptRA=no

[Address]
Address=198.51.100.1/30

[Address]
Address=2001:db8:76::1/64
DuplicateAddressDetection=none

[Route]
Destination=$TN_RANGE4
Gateway=198.51.100.2

[Route]
Destination=$TN_RANGE6
Gateway=2001:db8:76::2
NETWORK
networkctl reload
tn_wait_uplink "tailnet device setup"

tn_listener "$TN_PROVIDER_NS" "$TN_BACKEND4" "$tn_provider4_log"
tn_listener "$TN_PROVIDER_NS" "$TN_BACKEND6" "$tn_provider6_log"
tn_listener "$TN_TAILNET_NS" "$TN_BACKEND4" "$tn_tailnet4_log"
tn_listener "$TN_TAILNET_NS" "$TN_BACKEND6" "$tn_tailnet6_log"

systemctl set-property --runtime vandox-agent IPAccounting=yes

# (a) No tailscale0.
tn_state_absent "tailnet device (a)"

# (b') The stand-in without Tailscale's table-52 route and rule, also after networkd reconfigures vx-up and after
# a carrier flap of vx-up.
tn_standin_add
tn_state_unrouted "tailnet device (b')"
tn_reconfigure "tailnet device (b') reconfigure"
tn_state_unrouted "tailnet device (b') after reconfigure"
tn_flap "tailnet device (b') carrier flap"
tn_state_unrouted "tailnet device (b') after a carrier flap"

# (b) With Tailscale's route in table 52 and its rule at pref 5270: the agent reaches the tailnet listener.
ip route add "$TN_RANGE4" dev "$TN_DEVICE" table 52
ip rule add pref 5270 table 52
tn_route="$(ip route get "$TN_BACKEND4")"
echo "tailnet device (b): ip route get $TN_BACKEND4: $tn_route"
[[ "$tn_route" == *"dev $TN_DEVICE"* ]] || fail "tailnet device (b): ip route get $TN_BACKEND4 does not name dev $TN_DEVICE: $tn_route"
tn_reaches "tailnet device (b)" "$TN_BACKEND4" "$tn_tailnet4_log"
tn_restart "tailnet device (b)"
for ((tn_i = 0; tn_i < 15; tn_i++)); do
  if grep -Eq "$TN_ACCEPTED_RE" "$tn_tailnet4_log"; then
    break
  fi

  sleep 1
done
grep -Eq "$TN_ACCEPTED_RE" "$tn_tailnet4_log" || fail "tailnet device (b): the agent did not reach the tailnet listener within 15 s"
sleep "$tn_settle"
tn_no_agent "tailnet device (b)" "$tn_provider4_log"
tn_egress_b="$(tn_egress)"
echo "tailnet device (b): IPEgressPackets $tn_egress_b"
[[ "$tn_egress_b" =~ ^[0-9]+$ && "$tn_egress_b" -gt 0 ]] || fail "tailnet device (b): IPEgressPackets is '$tn_egress_b', want it above 0"
ip rule del pref 5270
ip route flush table 52

# (c) The stand-in removed, networkd reconfigures vx-up and its carrier flaps: (a) holds again.
ip link del "$TN_DEVICE"
tn_reconfigure "tailnet device (c) reconfigure"
tn_flap "tailnet device (c) carrier flap"
tn_state_absent "tailnet device (c)"

# IPv6: backend.url on the backend's IPv6 tailnet address, a provider route for fd7a:115c:a1e0::/48 on vx-up and no
# route on tailscale0, without and with the stand-in.
tailnet_config_backup="$work/agent.yaml.tailnet"
cp -p -- "$CONFIG_FILE" "$tailnet_config_backup"
sed "s#^  url: $BACKEND_URL\$#  url: $TN_IPV6_URL#" "$tailnet_config_backup" >"$CONFIG_FILE"
grep -Fxq "  url: $TN_IPV6_URL" "$CONFIG_FILE" || fail "tailnet device (IPv6): agent.yaml lacks the line '  url: $TN_IPV6_URL'"
tn_state_ipv6 "tailnet device (IPv6, no $TN_DEVICE)"
tn_standin_add
tn_state_ipv6 "tailnet device (IPv6, $TN_DEVICE without a route)"
cat -- "$tailnet_config_backup" >"$CONFIG_FILE"
rm -f -- "$tailnet_config_backup"
tailnet_config_backup=""
[[ "$(sha256sum "$CONFIG_FILE")" == "$config_before" ]] || fail "tailnet device: agent.yaml was not restored"

tailnet_teardown
systemctl restart vandox-agent
wait_active 30 || fail "tailnet device: vandox-agent is not active after the case"

# --- case: uninstall -------------------------------------------------------------------------------------
note "case: uninstall"
run "" "$agent_dir/uninstall.sh"
show
expect_rc 0 "$CASE_UNINSTALL"
expect_out "uninstall.sh: removed unit" "$CASE_UNINSTALL"
expect_out "uninstall.sh: removed binary" "$CASE_UNINSTALL"
expect_out "uninstall.sh: kept user $AGENT_USER, $CONFIG_FILE, $TOKEN_FILE, $SPOOL and the MariaDB account; remove them with --purge" "$CASE_UNINSTALL"
expect_empty_err "$CASE_UNINSTALL"
expect_absent "$UNIT"
expect_absent "$BINARY"
expect_absent "$WANTS_LINK"
[[ -f "$CONFIG_FILE" && -f "$TOKEN_FILE" && -f "$SPOOL/keep-me" ]] || fail "uninstall removed configuration, token or spool"
getent passwd "$AGENT_USER" >/dev/null || fail "uninstall removed the user"

if systemctl is-active --quiet vandox-agent; then
  fail "uninstall: vandox-agent is still active"
fi

if systemctl is-enabled --quiet vandox-agent 2>/dev/null; then
  fail "uninstall: vandox-agent is still enabled"
fi

# --- case: reinstall -------------------------------------------------------------------------------------
note "case: reinstall"
# Standard input is empty: the token file exists, so none is asked.
run "" "$agent_dir/install.sh" --from "$dist"
show
expect_rc 0 "reinstall"
expect_out "$INSTALLED_MSG" "reinstall"
expect_out "$KEEPING_MSG" "reinstall"
[[ "$(sha256sum "$CONFIG_FILE")" == "$config_before" ]] || fail "reinstall: agent.yaml changed"
[[ "$(sha256sum "$TOKEN_FILE")" == "$token_before" ]] || fail "reinstall: the token file changed"
[[ -f "$SPOOL/keep-me" ]] || fail "reinstall: a spool file is gone"
wait_active 30 || fail "reinstall: vandox-agent is not active"

# --- case: purge -----------------------------------------------------------------------------------------
note "case: purge"
run "" "$agent_dir/uninstall.sh" --purge
show
expect_rc 0 "purge"
expect_out "uninstall.sh: removed unit" "purge"
expect_out "uninstall.sh: removed binary" "purge"
expect_out "uninstall.sh: removed $CONFIG_FILE" "purge"
expect_out "uninstall.sh: removed $TOKEN_FILE" "purge"
expect_before "uninstall.sh: deleting $STATE_ROOT (records not yet sent are lost)" "uninstall.sh: removed $STATE_ROOT" "purge"
expect_out "uninstall.sh: removed user $AGENT_USER" "purge"
expect_out "uninstall.sh: removed $CONFIG_DIR" "purge"
expect_out_re "^uninstall\\.sh: WARNING MariaDB account not removed; run: (plesk db|mariadb) < ${agent_dir//./\\.}/mariadb-user-remove\\.sql\$" "purge"
expect_empty_err "purge"
nothing_left "purge"

run "" "$agent_dir/uninstall.sh" --purge
expect_rc 0 "purge of an absent installation"
nothing_left "second purge"

note "all cases passed"

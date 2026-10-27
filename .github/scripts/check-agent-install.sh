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
# address allow-list, uninstall, reinstall, purge. It changes the host (user, /etc/vandox, /var/lib/vandox, a unit), so it is meant for a throw-away
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
# The extracted bundle (set by the case "bundle verification") and the unit file inside it.
agent_dir=""
unit_source=""
# The transient unit of the address allow-list case and the file its client writes the result to.
readonly IPCHECK_UNIT="/run/systemd/system/vandox-agent-ipcheck.service"
readonly IPCHECK_OUT="/run/vandox-agent-ipcheck.out"

# The case names of the messages that several cases share (SonarQube shelldre:S1192).
readonly CASE_PURGE_BEFORE_INSTALL="purge before install"
readonly CASE_UNINSTALL_BEFORE_INSTALL="uninstall before install"
readonly CASE_FIRST_INSTALL="first install"
readonly CASE_REPEAT_INSTALL="repeat install"
readonly CASE_UNINSTALL="uninstall"

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

cleanup() {
  local pid

  for pid in "${helper_pids[@]}"; do
    kill "$pid" 2>/dev/null || true
  done


  if [[ -e "$IPCHECK_UNIT" ]]; then
    rm -f -- "$IPCHECK_UNIT" "$IPCHECK_OUT"
    systemctl daemon-reload >/dev/null 2>&1 || true
  fi

  # The host is left without the agent whatever happened (the scripts of the extracted bundle live in $work,
  # so they run before it is removed).
  if [[ -n "$agent_dir" && -x "$agent_dir/uninstall.sh" ]]; then
    "$agent_dir/uninstall.sh" --purge >/dev/null 2>&1 || true
  fi

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
note "case: bundle verification (the documented chain, without the curl steps)"
bundle_file="$BUNDLE_NAME.tar.gz"

# chain SRC: the documented command chain verbatim except that the two curl steps are a copy from SRC; it ends
# in the install script of the extracted bundle, called with an option it refuses, so reaching it is visible
# as its usage line. The temporary directory comes from TMPDIR, which tells the case where to look afterwards.
read -r -d '' documented_chain <<'CHAIN' || true
d=$(mktemp -d) && cd "$d" &&
  cp -- "$src/$bundle_file" "$src/SHA256SUMS" . &&
  awk -v f="vandox-agent-install-$v.tar.gz" 'NF == 2 && $2 == f' SHA256SUMS | sha256sum --check --strict - &&
  tar --no-same-owner --no-same-permissions -xzf "vandox-agent-install-$v.tar.gz" &&
  "./vandox-agent-install-$v/install.sh" --bogus
CHAIN

# run_chain SRC TMP: runs the chain under umask 022 with TMPDIR=TMP; sets OUT, ERR and RC.
run_chain() {
  local src=$1
  local tmp=$2

  mkdir -p -- "$tmp"
  RC=0
  (umask 022 && export TMPDIR="$tmp" src v="$VERSION" bundle_file && bash -c "$documented_chain") >"$work/out" 2>"$work/err" || RC=$?
  OUT="$(<"$work/out")"
  ERR="$(<"$work/err")"
}

# Good row: the chain reaches install.sh, which refuses the option (usage, exit 2) after its entry check passed.
run_chain "$dist" "$work/chain-good"
expect_rc 2 "bundle verification, good bundle"
expect_err "usage: install.sh [--agent-id ID --backend-url URL] [--from DIR] [--no-mariadb]" "bundle verification, good bundle"
extracted="$(find "$work/chain-good" -mindepth 2 -maxdepth 2 -name "$BUNDLE_NAME" -type d)"
[[ -n "$extracted" ]] || fail "bundle verification: the good bundle was not extracted"
agent_dir="$extracted"
unit_source="$agent_dir/vandox-agent.service"

# F3: as root under umask 022 the files are root-owned, the scripts 0755 and the rest 0644.
for name in install.sh uninstall.sh; do
  expect_stat "$agent_dir/$name" root:root 755
done

for name in common.sh vandox-agent.service mariadb-user.sql mariadb-user-remove.sql agent.yaml VERSION "$PIN_NAME"; do
  expect_stat "$agent_dir/$name" root:root 644
done

expect_stat "$agent_dir" root:root 755
[[ "$(find "$agent_dir" -mindepth 1 | wc -l)" -eq 9 ]] || fail "bundle verification: the bundle does not hold exactly its 9 files: $(ls -A "$agent_dir")"
[[ "$(<"$agent_dir/VERSION")" == "$VERSION" ]] || fail "bundle verification: VERSION is not $VERSION"

# Failure rows: the chain stops before tar, so nothing is extracted and install.sh never runs.
chain_failure() {
  local what=$1
  local src=$2
  local tmp="$work/chain-$what"

  run_chain "$src" "$tmp"

  if [[ "$RC" == 0 ]]; then
    show
    fail "bundle verification, $what: the chain succeeded"
  fi

  if find "$tmp" -name 'vandox-agent-install-*' -type d | grep -q .; then
    fail "bundle verification, $what: a vandox-agent-install-* directory was extracted"
  fi

  nothing_left "bundle verification, $what"
}

mkdir "$work/changed" "$work/nobundleline"
cp -- "$dist/SHA256SUMS" "$work/changed/"
cp -- "$dist/SHA256SUMS" "$work/nobundleline/"

# One changed byte of the bundle (the byte is replaced by a different one).
cp -- "$dist/$bundle_file" "$work/changed/$bundle_file"
orig="$(dd if="$work/changed/$bundle_file" bs=1 skip=100 count=1 2>/dev/null | od -An -tx1 | tr -d ' ')"
[[ "$orig" == "00" ]] && replacement='\001' || replacement='\000'
printf '%b' "$replacement" | dd of="$work/changed/$bundle_file" bs=1 seek=100 conv=notrunc 2>/dev/null
cmp -s "$dist/$bundle_file" "$work/changed/$bundle_file" && fail "the changed bundle equals the original"
chain_failure "one changed byte" "$work/changed"

# A SHA256SUMS without the bundle line: awk prints nothing and sha256sum fails on empty input (F2).
cp -- "$dist/$bundle_file" "$work/nobundleline/$bundle_file"
grep -vF "$bundle_file" "$dist/SHA256SUMS" >"$work/nobundleline/SHA256SUMS" || true
chain_failure "SHA256SUMS without the bundle line" "$work/nobundleline"

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
run "$CI_TOKEN"$'\n' "$copy/install.sh" "${install_agent_args[@]}"
expect_rc 1 "install.sh with another VERSION"
expect_err "install.sh: binary is not v0.0.0-rc.1" "another VERSION"
nothing_left "another VERSION"

# A missing VERSION.
copy="$(bundle_copy)"
rm -- "$copy/VERSION"
run "$CI_TOKEN"$'\n' "$copy/install.sh" "${install_agent_args[@]}"
expect_rc 2 "install.sh without VERSION"
expect_err "install.sh: no valid VERSION file next to install.sh; use the install bundle of a release" "missing VERSION"
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
expect_out "install.sh: keeping existing $CONFIG_FILE" "E8"
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
expect_out "vandox-agent $VERSION installed and running" "$CASE_FIRST_INSTALL"
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
expect_out "vandox-agent $VERSION installed and running" "$CASE_REPEAT_INSTALL"
expect_out "install.sh: keeping existing $CONFIG_FILE" "$CASE_REPEAT_INSTALL"
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
expect_out "vandox-agent $VERSION installed and running" "reinstall"
expect_out "install.sh: keeping existing $CONFIG_FILE" "reinstall"
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

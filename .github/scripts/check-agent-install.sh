#!/usr/bin/env bash
# End-to-end check of deploy/agent/install.sh, uninstall.sh and the unit on a real systemd host (CI job
# agent-install, ubuntu-22.04 with systemd 249; plan of feature 42, Approach, "CI job").
# Usage (as root): check-agent-install.sh <dist>
#   <dist> holds the already built vandox-agent-linux-amd64 (version v0.0.0-rc.0) and its SHA256SUMS; the
#   install scripts take both from there with --from, so nothing is downloaded.
#
# The cases run in this order and the script stops at the first mismatch: verify on 249, uninstall before
# install, tampered checksum, E8 (a kept invalid agent.yaml, ends with a purge), first install, security score,
# ptrace, repeat install, restart after a removed spool directory, address allow-list, uninstall, reinstall,
# purge. It changes the host (user, /etc/vandox, /var/lib/vandox, a unit), so it is meant for a throw-away
# runner and always ends with a purge. The agent's backend (100.64.0.1) is not reachable here; the agent
# spools, which is what the checks need.
set -euo pipefail

readonly VERSION="v0.0.0-rc.0"
readonly BINARY_NAME="vandox-agent-linux-amd64"
readonly AGENT_USER="vandox-agent"
readonly AGENT_ID="ci-runner"
readonly BACKEND_URL="http://100.64.0.1:8081"
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
readonly agent_dir="$repo/deploy/agent"
readonly unit_source="$agent_dir/vandox-agent.service"

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

  if [[ -n "$work" ]]; then
    rm -rf -- "$work"
  fi

  # The host is left without the agent whatever happened.
  if [[ -x "$agent_dir/uninstall.sh" ]]; then
    "$agent_dir/uninstall.sh" --purge >/dev/null 2>&1 || true
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
  echo "--version" "$VERSION" "--agent-id" "$AGENT_ID" "--backend-url" "$BACKEND_URL" "--from" "$dist"
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
  if ! grep -Fxq -- "$1" <<<"$OUT"; then
    show
    fail "$2: standard output has no line: $1"
  fi
}

expect_err() {
  if ! grep -Fxq -- "$1" <<<"$ERR"; then
    show
    fail "$2: standard error has no line: $1"
  fi
}

expect_out_re() {
  if ! grep -Eq -- "$1" <<<"$OUT"; then
    show
    fail "$2: standard output has no line matching: $1"
  fi
}

expect_no_out() {
  if grep -Fq -- "$1" <<<"$OUT"; then
    show
    fail "$2: standard output holds: $1"
  fi
}

expect_empty_err() {
  if [[ -n "$ERR" ]]; then
    show
    fail "$1: standard error is not empty"
  fi
}

# expect_before FIRST SECOND WHAT: both lines are on standard output, FIRST earlier.
expect_before() {
  local a b

  a="$(grep -Fxn -- "$1" <<<"$OUT" | head -n 1 | cut -d: -f1)"
  b="$(grep -Fxn -- "$2" <<<"$OUT" | head -n 1 | cut -d: -f1)"

  if [[ -z "$a" || -z "$b" ]] || ((a >= b)); then
    show
    fail "$3: line '$1' is not printed before '$2'"
  fi
}

# expect_stat PATH OWNER:GROUP MODE
expect_stat() {
  local got

  got="$(stat -c '%U:%G %a' -- "$1" 2>/dev/null)" || fail "$1 does not exist"

  if [[ "$got" != "$2 $3" ]]; then
    fail "$1: owner and mode are '$got', want '$2 $3'"
  fi
}

expect_absent() {
  if [[ -e "$1" || -L "$1" ]]; then
    fail "$1 still exists"
  fi
}

# nothing_left WHAT: nothing of S1 is on the host.
nothing_left() {
  local path

  for path in "$CONFIG_FILE" "$TOKEN_FILE" "$CONFIG_DIR" "$STATE_ROOT" "$BINARY" "$UNIT" "$WANTS_LINK"; do
    if [[ -e "$path" || -L "$path" ]]; then
      fail "$1: $path is left"
    fi
  done

  if getent passwd "$AGENT_USER" >/dev/null; then
    fail "$1: user $AGENT_USER is left"
  fi

  if getent group "$AGENT_USER" >/dev/null; then
    fail "$1: group $AGENT_USER is left"
  fi

  if systemctl is-active --quiet vandox-agent; then
    fail "$1: vandox-agent is still active"
  fi

  if systemctl is-enabled --quiet vandox-agent 2>/dev/null; then
    fail "$1: vandox-agent is still enabled"
  fi
}

# wait_active SECONDS: waits until the unit is active.
wait_active() {
  local i

  for ((i = 0; i < $1; i++)); do
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

[[ -f "$dist/$BINARY_NAME" && -f "$dist/SHA256SUMS" ]] || fail "$dist holds no $BINARY_NAME and SHA256SUMS"

work="$(mktemp -d)"
trap cleanup EXIT

# --- case: verify on 249 ---------------------------------------------------------------------------------
note "case: verify on systemd 249"
systemctl --version
first="$(systemctl --version | head -n 1)"
[[ "$first" == "systemd 249"* ]] || fail "systemctl --version starts with '$first', want 'systemd 249'"

for tool in python3 setpriv curl sha256sum ss systemd-run systemd-analyze; do
  command -v "$tool" >/dev/null 2>&1 || fail "$tool is missing on the runner"
done

# systemd 249 ignores an unknown key with a log line only, so the output is read, not the exit status.
verify_out="$(systemd-analyze verify "$unit_source" 2>&1 || true)"
echo "$verify_out"

if grep -Eq "Unknown|ignoring|specifier" <<<"$verify_out"; then
  fail "systemd-analyze verify reports an unknown key, an ignored line or a specifier"
fi

nothing_left "before the cases"

# --- case: uninstall before install ----------------------------------------------------------------------
note "case: uninstall before install"
run "" "$agent_dir/uninstall.sh"
expect_rc 0 "uninstall.sh on an absent installation"
expect_out "uninstall.sh: unit not present" "uninstall before install"
expect_out "uninstall.sh: binary not present" "uninstall before install"
expect_out "uninstall.sh: kept user $AGENT_USER, $CONFIG_FILE, $TOKEN_FILE, $SPOOL and the MariaDB account; remove them with --purge" "uninstall before install"
expect_empty_err "uninstall before install"

run "" "$agent_dir/uninstall.sh" --purge
expect_rc 0 "uninstall.sh --purge on an absent installation"
expect_out "uninstall.sh: $CONFIG_FILE not present" "purge before install"
expect_out "uninstall.sh: $TOKEN_FILE not present" "purge before install"
expect_out "uninstall.sh: $STATE_ROOT not present" "purge before install"
expect_out "uninstall.sh: user $AGENT_USER not present" "purge before install"
expect_out_re '^uninstall\.sh: WARNING MariaDB account not removed; run: (plesk db|mariadb) < deploy/agent/mariadb-user-remove\.sql$' "purge before install"
expect_no_out "deleting" "purge before install"

run "" "$agent_dir/uninstall.sh" --bogus
expect_rc 2 "uninstall.sh --bogus"
expect_err "usage: uninstall.sh [--purge]" "uninstall.sh --bogus"
nothing_left "uninstall before install"

# --- case: tampered checksum -----------------------------------------------------------------------------
note "case: tampered checksum"
mkdir "$work/tampered"
cp -- "$dist/$BINARY_NAME" "$work/tampered/$BINARY_NAME"
sed 's/^0/1/; t; s/^./0/' "$dist/SHA256SUMS" >"$work/tampered/SHA256SUMS"
cmp -s "$dist/SHA256SUMS" "$work/tampered/SHA256SUMS" && fail "the tampered SHA256SUMS equals the original"

run "$CI_TOKEN"$'\n' "$agent_dir/install.sh" --version "$VERSION" --agent-id "$AGENT_ID" --backend-url "$BACKEND_URL" --from "$work/tampered"
expect_rc 1 "install.sh with a tampered checksum"
expect_err "install.sh: checksum mismatch, nothing installed" "tampered checksum"
nothing_left "tampered checksum (nothing is created)"

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
expect_rc 0 "first install"
expect_out "vandox-agent $VERSION installed and running" "first install"
expect_out "install.sh: user $AGENT_USER: created" "first install"
expect_out_re '^install\.sh: WARNING MariaDB account not created; run: (plesk db|mariadb) < (.*/)?deploy/agent/mariadb-user\.sql$' "first install"

for name in journal-group journal-clock ipc-filter capabilities; do
  expect_out_re "^check $name: (ok|WARNING .+)\$" "first install"
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
print("EPERM" if r == -1 and ctypes.get_errno() == 1 else "result=%d errno=%d" % (r, ctypes.get_errno()))'

ptrace_out="$(systemd-run --wait --pipe --collect --quiet "${props[@]}" python3 -c "$ptrace_code" "$sleeper" 2>&1)" || true
echo "$ptrace_out"
grep -Fxq "EPERM" <<<"$ptrace_out" || fail "ptrace(PTRACE_ATTACH) is not refused with EPERM: $ptrace_out"
kill "$sleeper" 2>/dev/null || true

# --- case: repeat install --------------------------------------------------------------------------------
note "case: repeat install"
inode_before="$(stat -c %i "$BINARY")"
config_before="$(sha256sum "$CONFIG_FILE")"
token_before="$(sha256sum "$TOKEN_FILE")"
printf 'kept\n' >"$SPOOL/keep-me"

# Standard input is empty: asking for the token would fail the script.
run "" "$agent_dir/install.sh" --version "$VERSION" --agent-id other-name --backend-url http://100.64.0.9:9 --from "$dist"
show
expect_rc 0 "repeat install"
expect_out "vandox-agent $VERSION installed and running" "repeat install"
expect_out "install.sh: keeping existing $CONFIG_FILE" "repeat install"
expect_no_out "MariaDB account not created" "repeat install"
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

# --- case: address allow-list ----------------------------------------------------------------------------
note "case: address allow-list (fact 16)"
own_ip="$(ip -4 route get 1.1.1.1 2>/dev/null | sed -n 's/.* src \([0-9.]*\).*/\1/p' | head -n 1)"
[[ -n "$own_ip" && "$own_ip" != 127.* ]] || fail "the runner has no non-loopback IPv4 address"
mkdir "$work/www"

start_listener() {
  local address=$1
  local port=$2
  local log=$3
  local i

  python3 -m http.server --bind "$address" --directory "$work/www" "$port" >"$log" 2>&1 &
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

mapfile -t net_props < <(unit_properties IPAddressDeny IPAddressAllow)
[[ ${#net_props[@]} -eq 4 ]] || fail "read ${#net_props[@]} words of IP properties from the unit file, want 4"

get_code='import sys,urllib.request
try:
    urllib.request.urlopen(sys.argv[1], timeout=5).read()
    print("reached")
except Exception:
    print("blocked")'

own_result="$(systemd-run --wait --pipe --collect --quiet "${net_props[@]}" python3 -c "$get_code" "http://$own_ip:18081/" 2>&1)" || true
loop_result="$(systemd-run --wait --pipe --collect --quiet "${net_props[@]}" python3 -c "$get_code" "http://127.0.0.1:18082/" 2>&1)" || true
echo "non-tailnet address $own_ip: $own_result; loopback: $loop_result"
[[ "$own_result" == "blocked" ]] || fail "a request to $own_ip was not blocked: $own_result"
[[ "$loop_result" == "reached" ]] || fail "a request to 127.0.0.1 was not allowed: $loop_result"
grep -q 'GET /' "$work/listener-loopback.log" || fail "the loopback listener got no request"

if grep -q 'GET /' "$work/listener-own.log"; then
  fail "the listener on $own_ip got a request through the address filter"
fi

# --- case: uninstall -------------------------------------------------------------------------------------
note "case: uninstall"
run "" "$agent_dir/uninstall.sh"
show
expect_rc 0 "uninstall"
expect_out "uninstall.sh: removed unit" "uninstall"
expect_out "uninstall.sh: removed binary" "uninstall"
expect_out "uninstall.sh: kept user $AGENT_USER, $CONFIG_FILE, $TOKEN_FILE, $SPOOL and the MariaDB account; remove them with --purge" "uninstall"
expect_empty_err "uninstall"
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
run "" "$agent_dir/install.sh" --version "$VERSION" --from "$dist"
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
expect_out_re '^uninstall\.sh: WARNING MariaDB account not removed; run: (plesk db|mariadb) < deploy/agent/mariadb-user-remove\.sql$' "purge"
expect_empty_err "purge"
nothing_left "purge"

run "" "$agent_dir/uninstall.sh" --purge
expect_rc 0 "purge of an absent installation"
nothing_left "second purge"

note "all cases passed"

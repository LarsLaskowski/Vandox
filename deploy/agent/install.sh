#!/usr/bin/env bash
# Installs or upgrades vandox-agent on a systemd server: user, files, unit and MariaDB account.
# Run as root from the extracted install bundle of a release (vandox-agent-install-<version>.tar.gz):
#   ./vandox-agent-install-vX.Y.Z/install.sh [--agent-id ID --backend-url URL] [--from DIR] [--no-mariadb]
# The version is the one in the bundle's VERSION file. The agent token is read from the terminal (no echo) or
# from one line of standard input, never from an option.

set -euo pipefail

if [[ -z "$(declare -F read_spool_directory)" ]]; then
    # --- entry check (before anything is sourced) ---
    # The physical directory of this script, every ancestor of it, the script and common.sh must belong to
    # root and not be writable by others, because bash reads this script while it runs and sources common.sh.
    _entry_refuse() {
        local path=$1

        echo "${0##*/}: $path is not owned by root or is writable by others; extract the bundle as root in a private directory" >&2
        exit 1
    }
    _entry_stat() {
        local path=$1
        local info

        info=$(stat -c '%u %a' -- "$path") || _entry_refuse "$path"
        _entry_uid=${info%% *}
        _entry_mode=$((8#${info#* }))
    }
    # The manifest lists the seven members the scripts read; it proves the extraction is complete and unchanged.
    # A bundle member that does not exist at all is the same refusal.
    _entry_fail() {
        echo "${BASH_SOURCE[0]##*/}: the bundle is incomplete or changed; extract it again in a new directory" >&2
        exit 1
    }
    _entry_src=${BASH_SOURCE[0]%/*}
    if [[ $_entry_src == "${BASH_SOURCE[0]}" ]]; then
        _entry_src=.
    fi
    _entry_dir=$(cd -P -- "$_entry_src" && pwd -P) || exit 1
    _entry_walk=${_entry_dir%/*}
    while true; do
        _entry_stat "${_entry_walk:-/}"
        if ((_entry_uid != 0 || ((_entry_mode & 01000) == 0 && (_entry_mode & 022) != 0))); then
            _entry_refuse "${_entry_walk:-/}"
        fi
        if [[ -z $_entry_walk ]]; then
            break
        fi
        _entry_walk=${_entry_walk%/*}
    done
    for _entry_path in "$_entry_dir" "$_entry_dir/${BASH_SOURCE[0]##*/}" "$_entry_dir/common.sh" "$_entry_dir/MANIFEST.sha256"; do
        if [[ ! -e $_entry_path && ! -L $_entry_path ]]; then
            _entry_fail
        fi
        if [[ -L $_entry_path ]]; then
            _entry_refuse "$_entry_path"
        fi
        _entry_stat "$_entry_path"
        if ((_entry_uid != 0 || (_entry_mode & 07022) != 0)); then
            _entry_refuse "$_entry_path"
        fi
    done
    # The manifest reader is bounded: a regular file of at most 4096 bytes, exactly seven lines in a fixed order.
    _entry_manifest=$_entry_dir/MANIFEST.sha256
    if [[ -L $_entry_manifest || ! -f $_entry_manifest ]]; then
        _entry_fail
    fi
    _entry_size=$(wc -c <"$_entry_manifest") || _entry_fail
    if ((${_entry_size// /} > 4096)); then
        _entry_fail
    fi
    _entry_names=(VERSION agent.yaml common.sh mariadb-user-remove.sql mariadb-user.sql vandox-agent-linux-amd64.sha256 vandox-agent.service)
    _entry_re='^[0-9a-f]{64}  (.*)$'
    {
        for _entry_name in "${_entry_names[@]}"; do
            IFS= read -r _entry_line || _entry_fail
            if [[ ! $_entry_line =~ $_entry_re || ${BASH_REMATCH[1]} != "$_entry_name" ]]; then
                _entry_fail
            fi
        done
        if IFS= read -r _entry_line || [[ -n $_entry_line ]]; then
            _entry_fail
        fi
    } <"$_entry_manifest"
    (cd -- "$_entry_dir" && sha256sum --check --strict --quiet MANIFEST.sha256) >/dev/null 2>&1 || _entry_fail
    unset -f _entry_refuse _entry_stat _entry_fail
    unset _entry_walk _entry_path _entry_uid _entry_mode _entry_manifest _entry_size _entry_names _entry_name _entry_line _entry_re
    # --- end of entry check ---
    # shellcheck source=common.sh disable=SC1091
    source "$_entry_dir/common.sh"
fi

readonly USAGE_LINE="usage: install.sh [--agent-id ID --backend-url URL] [--from DIR] [--no-mariadb]"
readonly RELEASE_BASE="https://github.com/LarsLaskowski/Vandox/releases/download"
readonly BINARY_NAME="vandox-agent-linux-amd64"
readonly SUMS_NAME="vandox-agent-linux-amd64.sha256"
readonly MAX_SUMS_BYTES=4096
readonly MAX_BINARY_BYTES=67108864
readonly CHECKSUM_MISMATCH="install.sh: checksum mismatch, nothing installed"
readonly AGENT_USER="vandox-agent"
readonly SERVICE_NAME="vandox-agent"
readonly NOLOGIN="/usr/sbin/nologin"
readonly CONFIG_DIR="/etc/vandox"
readonly CONFIG_FILE="/etc/vandox/agent.yaml"
readonly TOKEN_FILE="/etc/vandox/agent-token"
readonly BINARY_TARGET="/usr/local/bin/vandox-agent"
readonly UNIT_TARGET="/etc/systemd/system/vandox-agent.service"
readonly TEMP_SUFFIX=".XXXXXX"

# --- argument and value validation -------------------------------------------------------------------

# _usage: prints the usage line on standard error.
_usage() {
    echo "$USAGE_LINE" >&2
}

# _refuse_link PATH: the message for a link where the script writes.
_refuse_link() {
    local path=$1

    echo "install.sh: $path is a link; not following it" >&2
}

# _not_agents PATH: the message for a directory the agent does not own.
_not_agents() {
    local path=$1

    echo "install.sh: $path is not the agent's directory" >&2
}

# parse_args ARGS...: sets AGENT_ID, BACKEND_URL, FROM_DIR and NO_MARIADB; status 2 with the message.
parse_args() {
    AGENT_ID=""
    BACKEND_URL=""
    FROM_DIR=""
    NO_MARIADB=0

    local option value

    while (($# > 0)); do
        option=$1
        value=${2:-}

        case $option in
        --agent-id | --backend-url | --from)
            if (($# < 2)); then
                _usage
                return 2
            fi

            case $option in
            --agent-id) AGENT_ID=$value ;;
            --backend-url) BACKEND_URL=$value ;;
            *) FROM_DIR=$value ;;
            esac

            shift 2
            ;;
        --no-mariadb)
            NO_MARIADB=1
            shift
            ;;
        --token | --token=*)
            echo "install.sh: the token is read from standard input, never from an option" >&2
            _usage
            return 2
            ;;
        *)
            _usage
            return 2
            ;;
        esac
    done

    if [[ -n $AGENT_ID ]]; then
        validate_agent_id "$AGENT_ID" || return 2
    fi

    if [[ -n $BACKEND_URL ]]; then
        validate_backend_url "$BACKEND_URL" || return 2
    fi
}

# validate_version VALUE: vX.Y.Z or vX.Y.Z-rc.N; status 2, silent.
validate_version() {
    local value=$1
    local LC_ALL=C
    local re='^v[0-9]+\.[0-9]+\.[0-9]+(-rc\.[0-9]+)?$'

    if [[ $value =~ $re ]]; then
        return 0
    fi

    return 2
}

# validate_agent_id VALUE: the wire rule of the agent ID; status 2 with the message.
validate_agent_id() {
    local value=$1
    local LC_ALL=C
    local re='^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'

    if [[ $value =~ $re ]]; then
        return 0
    fi

    echo "install.sh: --agent-id is not a valid agent ID" >&2
    return 2
}

# _split_url URL: sets URL_HOST (an IPv6 literal without brackets) from a URL that passed validate_backend_url.
_split_url() {
    local url=$1
    local rest hostport

    rest=${url#*://}
    hostport=${rest%%/*}

    if [[ ${hostport:0:1} == '[' ]]; then
        URL_HOST=${hostport%%]*}
        URL_HOST=${URL_HOST:1}
    else
        URL_HOST=${hostport%%:*}
    fi
}

# _is_ipv4 VALUE: four decimal parts of 0 to 255.
_is_ipv4() {
    local value=$1
    local re='^([0-9]{1,3})\.([0-9]{1,3})\.([0-9]{1,3})\.([0-9]{1,3})$'
    local part

    [[ $value =~ $re ]] || return 1

    for part in "${BASH_REMATCH[@]:1}"; do
        if ((10#$part > 255)); then
            return 1
        fi
    done
}

# _is_host_name VALUE: labels of A-Za-z0-9- (1 to 63 bytes, not starting or ending with -) joined by single dots,
# 253 bytes at most.
_is_host_name() {
    local value=$1
    local label
    local re='^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?$'
    local re_chars='^[A-Za-z0-9.-]+$'
    local -a labels

    if [[ ! $value =~ $re_chars ]] || ((${#value} > 253)) || [[ $value == .* || $value == *. || $value == *..* ]]; then
        return 1
    fi

    IFS=. read -r -a labels <<<"$value"

    for label in "${labels[@]}"; do
        if ((${#label} > 63)) || [[ ! $label =~ $re ]]; then
            return 1
        fi
    done
}

# validate_backend_url VALUE: http(s)://host[:port][/]; status 2 with the message.
validate_backend_url() {
    local value=$1
    local LC_ALL=C
    local re_url='^https?://([^/]*)/?$'
    local re_v6='^\[([0-9A-Fa-f:]+)\](:([0-9]+))?$'
    local re_host='^([^:]+)(:([0-9]+))?$'
    local re_port='^[1-9][0-9]{0,4}$'
    local re_digits='^[0-9.]+$'
    local hostport host port=""

    if [[ $value =~ $re_url ]]; then
        hostport=${BASH_REMATCH[1]}

        if [[ $hostport =~ $re_v6 ]]; then
            host=${BASH_REMATCH[1]}
            port=${BASH_REMATCH[3]}
            [[ $host == *:*:* ]] || host=""
        elif [[ $hostport =~ $re_host ]]; then
            host=${BASH_REMATCH[1]}
            port=${BASH_REMATCH[3]}

            if [[ $host =~ $re_digits ]]; then
                _is_ipv4 "$host" || host=""
            else
                _is_host_name "$host" || host=""
            fi
        else
            host=""
        fi

        if [[ -n $host && -n $port ]]; then
            if [[ ! $port =~ $re_port ]] || ((port > 65535)); then
                host=""
            fi
        fi

        if [[ -n $host ]]; then
            return 0
        fi
    fi

    echo "install.sh: --backend-url is not http(s)://host[:port]" >&2
    return 2
}

# tailnet_warning URL: prints the warning for an IP literal outside the tailnet ranges and localhost.
tailnet_warning() {
    local url=$1
    local LC_ALL=C
    local lower first second

    _split_url "$url"

    if _is_ipv4 "$URL_HOST"; then
        IFS=. read -r first second _ <<<"$URL_HOST"
        first=$((10#$first))
        second=$((10#$second))

        if ((first == 127)) || ((first == 100 && second >= 64 && second <= 127)); then
            return 0
        fi
    elif [[ $URL_HOST == *:* ]]; then
        lower=${URL_HOST,,}

        if [[ $lower == "::1" || $lower == fd7a:115c:a1e0:* ]]; then
            return 0
        fi
    else
        return 0
    fi

    echo "install.sh: WARNING $URL_HOST is not a tailnet address; the unit blocks it"
}

# name_server_warning URL RESOLVCONF: for a host name, warns about each name server outside localhost and the
# tailnet; a value that is not an IP literal is skipped.
name_server_warning() {
    local url=$1
    local resolv=$2
    local LC_ALL=C
    local key value rest first second lower
    local re_v6='^[0-9A-Fa-f:]+$'

    _split_url "$url"

    if [[ $URL_HOST == *:* ]] || _is_ipv4 "$URL_HOST"; then
        return 0
    fi

    if [[ ! -r $resolv ]]; then
        return 0
    fi

    while read -r key value rest || [[ -n $key ]]; do
        if [[ $key != nameserver ]]; then
            continue
        fi

        value=${value%%\%*}

        if _is_ipv4 "$value"; then
            IFS=. read -r first second _ <<<"$value"
            first=$((10#$first))
            second=$((10#$second))

            if ((first == 127)) || ((first == 100 && second >= 64 && second <= 127)); then
                continue
            fi
        elif [[ $value == *:*:* && $value =~ $re_v6 ]]; then
            lower=${value,,}

            if [[ $lower == "::1" || $lower == fd7a:115c:a1e0:* ]]; then
                continue
            fi
        else
            continue
        fi

        echo "install.sh: WARNING name server $value is blocked by the unit; use the tailnet address or MagicDNS"
    done <"$resolv"
}

# --- the token (bash builtins only: it never reaches a child process) ------------------------------------

# needs_token FILE: status 0 when the token file is missing.
needs_token() {
    local file=$1

    if [[ -e $file || -L $file ]]; then
        return 1
    fi

    return 0
}

# read_token: reads one line into the global TOKEN, from the terminal without echo or from standard input.
read_token() {
    TOKEN=""

    if [[ -t 0 ]]; then
        IFS= read -r -s -p "Agent token: " TOKEN || true
        echo >&2
    else
        IFS= read -r TOKEN || true
    fi
}

# validate_token VALUE: 32 to 4096 bytes of 0x21-0x7E; status 1 with the rule, never the token.
validate_token() {
    local value=$1
    local LC_ALL=C
    local re='[^!-~]'

    if ((${#value} < 32)); then
        echo "install.sh: token rejected: fewer than 32 characters" >&2
        return 1
    fi

    if ((${#value} > 4096)); then
        echo "install.sh: token rejected: more than 4096 characters" >&2
        return 1
    fi

    if [[ $value =~ $re ]]; then
        echo "install.sh: token rejected: a character outside 0x21-0x7E" >&2
        return 1
    fi
}

# write_token TARGET: writes the global TOKEN and a newline to a temporary file next to TARGET (mode 0600) and
# renames it onto TARGET. Only bash builtins see the token.
write_token() {
    local target=$1
    local dir=${target%/*}
    local tmp

    if [[ -L $target ]]; then
        _refuse_link "$target"
        return 1
    fi

    tmp=$(mktemp -- "$dir/.agent-token$TEMP_SUFFIX") || return 1

    if ! { printf '%s\n' "$TOKEN" >"$tmp" && chmod 0600 -- "$tmp" && mv -T -- "$tmp" "$target"; }; then
        rm -f -- "$tmp"
        return 1
    fi
}

# --- the release --------------------------------------------------------------------------------------

# _file_size FILE: prints the size in bytes of a regular file.
_file_size() {
    local file=$1
    local size

    size=$(wc -c <"$file")
    printf '%s\n' "${size//[[:space:]]/}"
}

# _within_bound FILE LIMIT: succeeds for a regular file (not a link) of at most LIMIT bytes.
_within_bound() {
    local file=$1
    local limit=$2
    local size

    if [[ -L $file || ! -f $file ]]; then
        return 1
    fi

    size=$(_file_size "$file") || return 1
    ((size <= limit))
}

# read_bundle_version DIR: sets VERSION from DIR/VERSION, a regular file (no link) of at most 64 bytes, checked by size
# before it is read, with exactly one line that ends in a line feed and is vX.Y.Z or vX.Y.Z-rc.N; status 2 with
# the E4 line.
read_bundle_version() {
    local dir=$1
    local file="$dir/VERSION"
    local line="" extra=""

    if _within_bound "$file" 64; then
        {
            if IFS= read -r line && ! { IFS= read -r extra || [[ -n $extra ]]; }; then
                if validate_version "$line"; then
                    VERSION=$line
                    return 0
                fi
            fi
        } <"$file"
    fi

    echo "install.sh: no valid VERSION file next to install.sh; use the install bundle of a release" >&2
    return 2
}

# _copy_pin BUNDLEDIR WORKDIR: copies the binary's pin file of the bundle; it must be a regular file, no link,
# of at most 4096 bytes by its size before it is read; status 1 with the E2 line.
_copy_pin() {
    local bundle=$1
    local work=$2

    if ! _within_bound "$bundle/$SUMS_NAME" "$MAX_SUMS_BYTES" || ! cp -P -- "$bundle/$SUMS_NAME" "$work/$SUMS_NAME"; then
        echo "$CHECKSUM_MISMATCH" >&2
        return 1
    fi
}

# fetch_release VERSION FROMDIR WORKDIR BUNDLEDIR: copies the binary's pin from the bundle and gets the binary,
# over HTTPS or from FROMDIR; status 1 with the E2 line of a bad pin or the E3 line of the binary.
fetch_release() {
    local version=$1
    local from=$2
    local work=$3
    local bundle=$4
    local name=$BINARY_NAME

    _copy_pin "$bundle" "$work" || return 1

    if [[ -n $from ]]; then
        if ! _within_bound "$from/$name" "$MAX_BINARY_BYTES" || ! cp -P -- "$from/$name" "$work/$name"; then
            echo "install.sh: download failed: $name" >&2
            return 1
        fi

        return 0
    fi

    if ! curl --proto '=https' --proto-redir '=https' --fail --location --silent --show-error \
        --max-time 300 --max-filesize "$MAX_BINARY_BYTES" -o "$work/$name" "$RELEASE_BASE/$version/$name"; then
        echo "install.sh: download failed: $name" >&2
        return 1
    fi
}

# verify_checksum WORKDIR: both files regular, not links and within their bounds; the pin file exactly one
# well-formed line for the binary that matches; then the binary is made 0755. Status 1 with the E2 line.
verify_checksum() {
    local work=$1
    local sums="$work/$SUMS_NAME"
    local binary="$work/$BINARY_NAME"
    local re="^([0-9a-f]{64})  ${BINARY_NAME}\$"
    local first="" second="" extra=0 actual

    if ! _within_bound "$sums" "$MAX_SUMS_BYTES" || ! _within_bound "$binary" "$MAX_BINARY_BYTES"; then
        echo "$CHECKSUM_MISMATCH" >&2
        return 1
    fi

    {
        IFS= read -r first || true
        if IFS= read -r second; then
            extra=1
        fi
    } <"$sums"

    if ((extra == 1)) || [[ -n $second || ! $first =~ $re ]]; then
        echo "$CHECKSUM_MISMATCH" >&2
        return 1
    fi

    local expected=${BASH_REMATCH[1]}

    actual=$(sha256sum -- "$binary") || actual=""
    actual=${actual%% *}

    if [[ $actual != "$expected" ]]; then
        echo "$CHECKSUM_MISMATCH" >&2
        return 1
    fi

    chmod 0755 -- "$binary"
}

# check_binary_version BINARY VERSION: runs the verified binary unprivileged and requires its version line.
check_binary_version() {
    local binary=$1
    local version=$2
    local out=""

    out=$(setpriv --reuid=nobody --regid=nogroup --clear-groups --no-new-privs "$binary" -version 2>/dev/null) || out=""
    out=${out%%$'\n'*}

    if [[ $out != "vandox-agent $version ("* ]]; then
        echo "install.sh: binary is not $version" >&2
        return 1
    fi
}

# --- configuration, accounts and directories ---------------------------------------------------------

# render_config EXAMPLE ID URL OUT: copies the example line by line, replacing the agent_id and url lines whole.
render_config() {
    local example=$1
    local agent_id=$2
    local url=$3
    local out=$4
    local line replaced=0
    local id_line="agent_id: web-1"
    local url_line="  url: http://100.64.0.1:8081"

    : >"$out"

    while true; do
        if IFS= read -r line; then
            if [[ $line == "$id_line" ]]; then
                line="agent_id: $agent_id"
                replaced=$((replaced + 1))
            elif [[ $line == "$url_line" ]]; then
                line="  url: $url"
                replaced=$((replaced + 1))
            fi

            printf '%s\n' "$line" >>"$out"
        else
            if [[ -n $line ]]; then
                printf '%s' "$line" >>"$out"
            fi

            break
        fi
    done <"$example"

    if ((replaced < 2)); then
        echo "install.sh: $example lacks the agent_id and url lines" >&2
        return 1
    fi
}

# dropin_warning ETCDIR RUNDIR: names each existing drop-in directory of the unit.
dropin_warning() {
    local etc_dir=$1
    local run_dir=$2
    local dir

    for dir in "$etc_dir" "$run_dir"; do
        if [[ -d $dir ]]; then
            echo "install.sh: WARNING drop-in directory $dir changes the unit; check it"
        fi
    done
}

# ensure_user: the system user vandox-agent in the state of S1; prints each change.
ensure_user() {
    local prefix="install.sh: user $AGENT_USER: "
    local want_groups="adm systemd-journal"
    local entry shell home primary name found
    local -a have=()
    local -a others=()

    if ! getent group "$AGENT_USER" >/dev/null; then
        groupadd --system "$AGENT_USER"
        echo "install.sh: group $AGENT_USER created"
    fi

    if ! entry=$(getent passwd "$AGENT_USER"); then
        useradd --system --gid "$AGENT_USER" --groups adm,systemd-journal --shell "$NOLOGIN" \
            --no-create-home --home-dir /nonexistent --comment "Vandox monitoring agent" "$AGENT_USER"
        echo "${prefix}created"
        return 0
    fi

    IFS=: read -r _ _ _ _ _ home shell <<<"$entry"

    if [[ $shell != "$NOLOGIN" ]]; then
        usermod --shell "$NOLOGIN" "$AGENT_USER"
        echo "${prefix}shell set to $NOLOGIN"
    fi

    read -r _ found _ <<<"$(passwd -S "$AGENT_USER")"

    if [[ $found != L && $found != LK ]]; then
        usermod --lock "$AGENT_USER"
        echo "${prefix}password locked"
    fi

    primary=$(id -gn "$AGENT_USER")

    if [[ $primary != "$AGENT_USER" ]]; then
        usermod --gid "$AGENT_USER" "$AGENT_USER"
        echo "${prefix}primary group set to $AGENT_USER"
    fi

    read -r -a have <<<"$(id -Gn "$AGENT_USER")"

    for name in "${have[@]}"; do
        if [[ $name != "$AGENT_USER" ]]; then
            others+=("$name")
        fi
    done

    found=" ${others[*]} "

    if ((${#others[@]} != 2)) || [[ $found != *" adm "* || $found != *" systemd-journal "* ]]; then
        usermod --groups "${want_groups// /,}" "$AGENT_USER"
        echo "${prefix}groups set to ${want_groups// /,}"
    fi

    if [[ -n $home && $home != /nonexistent ]]; then
        echo "${prefix}home directory $home is kept in place"
    fi
}

# _check_parent PARENT: an existing parent must be a real directory owned by root and not group- or other-writable.
_check_parent() {
    local parent=$1
    local info owner mode

    if [[ -L $parent || ! -d $parent ]]; then
        _not_agents "$parent"
        return 1
    fi

    info=$(stat -c '%U %a' -- "$parent")
    owner=${info%% *}
    mode=${info#* }

    if [[ $owner != root ]] || ((${mode: -2:1} & 2)) || ((${mode: -1} & 2)); then
        _not_agents "$parent"
        return 1
    fi
}

# _check_spool SPOOL: an existing spool must be a real directory owned by the agent's user.
_check_spool() {
    local spool=$1
    local owner

    if [[ -L $spool || ! -d $spool ]]; then
        _not_agents "$spool"
        return 1
    fi

    if ! getent passwd "$AGENT_USER" >/dev/null; then
        _not_agents "$spool"
        return 1
    fi

    owner=$(stat -c '%U' -- "$spool")

    if [[ $owner != "$AGENT_USER" ]]; then
        _not_agents "$spool"
        return 1
    fi
}

# check_spool_directory PATH: the read-only form of ensure_spool_directory; missing entries pass, nothing is
# created or changed; status 1 with the message.
check_spool_directory() {
    local spool=$1
    local parent=${spool%/*}

    if [[ -e $parent || -L $parent ]]; then
        _check_parent "$parent" || return 1
    fi

    if [[ -e $spool || -L $spool ]]; then
        _check_spool "$spool" || return 1
    fi
}

# ensure_spool_directory PATH: the spool directory one level below a root-owned, non-writable parent; no link
# is followed and nothing recurses; status 1 with the message.
ensure_spool_directory() {
    local spool=$1
    local parent=${spool%/*}

    if [[ ! -e $parent && ! -L $parent ]]; then
        mkdir -m 0755 -- "$parent"
    fi

    _check_parent "$parent" || return 1

    if [[ ! -e $spool && ! -L $spool ]]; then
        mkdir -m 0700 -- "$spool"
        chown -h "$AGENT_USER:$AGENT_USER" "$spool"
    fi

    _check_spool "$spool"
}

# --- server checks (warnings only) -------------------------------------------------------------------

# check_journal_group DIR GROUP: every journal file below DIR (one level of machine directories) is readable by GROUP.
check_journal_group() {
    local dir=$1
    local group=$2
    local found bad

    found=$(find "$dir" -maxdepth 2 -type f -name '*.journal*' -print -quit 2>/dev/null) || found=""

    if [[ -n $found ]] && bad=$(find "$dir" -maxdepth 2 -type f -name '*.journal*' \
        ! \( -group "$group" -perm -040 \) -print -quit 2>/dev/null) && [[ -z $bad ]]; then
        echo "check journal-group: ok"
        return 0
    fi

    echo "check journal-group: WARNING journal files not readable by group systemd-journal; see deploy/agent/README.md"
}

# check_journal_clock UPTIME MONOTONIC: /proc/uptime (seconds, first field) against the journal's monotonic
# timestamp in microseconds; one second or more differing warns.
check_journal_clock() {
    local uptime=$1
    local mono=$2
    local whole frac up diff
    local re_up='^[0-9]+(\.[0-9]+)?$'
    local re_mono='^[0-9]+$'

    if [[ ! $uptime =~ $re_up || ! $mono =~ $re_mono ]]; then
        echo "check journal-clock: WARNING could not read the journal timestamp; see deploy/agent/README.md"
        return 0
    fi

    whole=${uptime%%.*}
    frac=""
    if [[ $uptime == *.* ]]; then
        frac=${uptime#*.}
    fi

    frac="${frac}000000"
    frac=${frac:0:6}
    up=$((10#$whole * 1000000 + 10#$frac))
    diff=$((up - 10#$mono))

    if ((diff < 0)); then
        diff=$((-diff))
    fi

    if ((diff >= 1000000)); then
        echo "check journal-clock: WARNING /proc/uptime and the journal differ by $((diff / 1000)) ms; live kills get no extra snapshots"
        return 0
    fi

    echo "check journal-clock: ok"
}

# check_ipc_filter: reads the listing of systemd-analyze syscall-filter @ipc on standard input.
check_ipc_filter() {
    if grep -Eq '^[[:space:]]*process_madvise[[:space:]]*$'; then
        echo "check ipc-filter: ok"
        return 0
    fi

    echo "check ipc-filter: WARNING systemd does not list process_madvise in @ipc"
}

# check_capabilities STATUSFILE: CapEff must be CAP_SYS_PTRACE (bit 19) alone.
check_capabilities() {
    local file=$1
    local key value rest="" eff="" re_hex='^[0-9a-fA-F]+$'

    if [[ -r $file ]]; then
        while read -r key value rest; do
            if [[ $key == "CapEff:" ]]; then
                eff=$value
                break
            fi
        done <"$file"
    fi

    if [[ $eff =~ $re_hex ]] && ((16#$eff == 16#80000)); then
        echo "check capabilities: ok"
        return 0
    fi

    echo "check capabilities: WARNING CapEff is ${eff:-unknown}, not CAP_SYS_PTRACE alone"
}

# --- the installation ----------------------------------------------------------------------------------

# _refuse_links: a link in place of a file the script writes is refused, never followed.
_refuse_links() {
    local path

    for path in "$CONFIG_DIR" "$CONFIG_FILE" "$TOKEN_FILE" "$BINARY_TARGET" "$UNIT_TARGET"; do
        if [[ -L $path ]]; then
            _refuse_link "$path"
            return 1
        fi
    done
}

# _install_file SOURCE TARGET MODE: copies SOURCE to a temporary file next to TARGET and renames it over TARGET.
_install_file() {
    local source_file=$1
    local target=$2
    local mode=$3
    local tmp

    tmp=$(mktemp -- "${target%/*}/.${target##*/}$TEMP_SUFFIX") || return 1

    if ! { install -m "$mode" -- "$source_file" "$tmp" && mv -T -- "$tmp" "$target"; }; then
        rm -f -- "$tmp"
        return 1
    fi
}

# _write_config SCRIPTDIR: writes agent.yaml from the example (root:vandox-agent 0640), by temporary file and rename.
_write_config() {
    local script_dir=$1
    local tmp

    tmp=$(mktemp -- "$CONFIG_DIR/.agent.yaml$TEMP_SUFFIX") || return 1

    if ! { render_config "$script_dir/agent.yaml" "$AGENT_ID" "$BACKEND_URL" "$tmp" &&
        chown "root:$AGENT_USER" "$tmp" && chmod 0640 -- "$tmp" && mv -T -- "$tmp" "$CONFIG_FILE"; }; then
        rm -f -- "$tmp"
        return 1
    fi
}

# _server_checks: prints the result of each check the documents leave to the server.
_server_checks() {
    local tag="vandox-agent-install" up="" mono="" pid status_file

    check_journal_group /var/log/journal systemd-journal

    if logger -t "$tag" "clock check" 2>/dev/null; then
        read -r up _ </proc/uptime || up=""
        journalctl --sync >/dev/null 2>&1 || true
        mono=$(journalctl -t "$tag" -n 1 -o export 2>/dev/null | sed -n 's/^__MONOTONIC_TIMESTAMP=//p' | tail -n 1) || mono=""
    fi

    check_journal_clock "$up" "$mono"

    { systemd-analyze syscall-filter @ipc 2>/dev/null || true; } | check_ipc_filter

    pid=$(systemctl show -p MainPID --value "$SERVICE_NAME" 2>/dev/null) || pid=0
    status_file=/proc/$pid/status

    check_capabilities "$status_file"
    dropin_warning "$UNIT_TARGET.d" "/run/systemd/system/vandox-agent.service.d"
}

# main ARGS...: the whole installation in the order of the plan; nothing changes before the apply step.
main() {
    local script_dir pid1 machine spool work first_install=0 write_new_token=0 new_config=0

    umask 077
    export LC_ALL=C
    export PATH=/usr/sbin:/usr/bin:/sbin:/bin
    unset TMPDIR TOKEN

    # Everything after the entry check is read through the directory the check looked at.
    if [[ -n ${_entry_dir:-} ]]; then
        script_dir=$_entry_dir
    else
        script_dir=${BASH_SOURCE[0]%/*}
        script_dir=$(cd -P -- "$script_dir" && pwd -P)
    fi

    parse_args "$@" || exit 2

    check_bundle_dir "$script_dir" install.sh common.sh VERSION vandox-agent.service mariadb-user.sql agent.yaml "$SUMS_NAME" || exit 1
    read_bundle_version "$script_dir" || exit 2

    read -r pid1 </proc/1/comm || pid1=""
    machine=$(uname -m)
    check_platform "$EUID" "$pid1" "$machine" || exit 1

    spool=$DEFAULT_SPOOL

    if [[ -e $CONFIG_FILE || -L $CONFIG_FILE ]]; then
        if [[ -L $CONFIG_FILE ]]; then
            _refuse_link "$CONFIG_FILE"
            exit 1
        fi

        echo "install.sh: keeping existing $CONFIG_FILE"
        spool=$(read_spool_directory "$CONFIG_FILE") || exit 2
    else
        new_config=1

        if [[ -z $AGENT_ID || -z $BACKEND_URL ]]; then
            _usage
            exit 2
        fi

        tailnet_warning "$BACKEND_URL"
        name_server_warning "$BACKEND_URL" /etc/resolv.conf
    fi

    check_spool_directory "$spool" || exit 1

    if [[ -L $CONFIG_DIR ]]; then
        _refuse_link "$CONFIG_DIR"
        exit 1
    fi

    if [[ -e $CONFIG_DIR && ! -d $CONFIG_DIR ]]; then
        echo "install.sh: $CONFIG_DIR is not a directory" >&2
        exit 1
    fi

    if needs_token "$TOKEN_FILE"; then
        write_new_token=1
        read_token
        validate_token "$TOKEN" || exit 1
    fi

    work=$(mktemp -d) || exit 1
    # shellcheck disable=SC2064
    trap "rm -rf -- '$work'" EXIT
    chmod 0711 -- "$work"

    fetch_release "$VERSION" "$FROM_DIR" "$work" "$script_dir" || exit 1
    verify_checksum "$work" || exit 1
    check_binary_version "$work/$BINARY_NAME" "$VERSION" || exit 1

    _refuse_links || exit 1
    ensure_user

    if [[ ! -e $CONFIG_DIR ]]; then
        mkdir -m 0755 -- "$CONFIG_DIR"
    fi

    if ((new_config == 1)); then
        _write_config "$script_dir" || exit 1
    fi

    if ((write_new_token == 1)); then
        write_token "$TOKEN_FILE" || exit 1
        unset TOKEN
    fi

    ensure_spool_directory "$spool" || exit 1
    _install_file "$work/$BINARY_NAME" "$BINARY_TARGET" 0755 || exit 1

    if [[ ! -e $UNIT_TARGET ]]; then
        first_install=1
    fi

    _install_file "$script_dir/vandox-agent.service" "$UNIT_TARGET" 0644 || exit 1
    systemctl daemon-reload

    if ((first_install == 1 && NO_MARIADB == 0)); then
        run_mariadb_sql "$script_dir/mariadb-user.sql" install.sh
    fi

    if ! systemctl enable --quiet "$SERVICE_NAME"; then
        echo "install.sh: vandox-agent could not be enabled" >&2
        exit 1
    fi

    if ! systemctl restart "$SERVICE_NAME"; then
        echo "install.sh: vandox-agent did not start; see: journalctl -u vandox-agent" >&2
        exit 1
    fi

    _server_checks
    echo "vandox-agent $VERSION installed and running"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    main "$@"
fi

#!/usr/bin/env bash
# Shared helpers of the agent install scripts (install.sh, uninstall.sh). Source it; it runs nothing.

set -euo pipefail

readonly SPOOL_ROOT=/var/lib/vandox
readonly DEFAULT_SPOOL=/var/lib/vandox/spool

# Scratch state of read_spool_directory (one line at a time, see _spool_line).
_SPOOL_SEEN_TOP=0
_SPOOL_SEEN_SPOOL=0
_SPOOL_IN_BLOCK=0
_SPOOL_HAVE_DIR=0
_SPOOL_DIR=""

# _spool_value: succeeds when the value of one block line is plain, single-quoted or double-quoted and
# ends on its line; prints the value without quotes and comment.
_spool_value() {
    local value=$1
    local plain re_single re_double

    re_single="^'([^']*)'( +#.*)?\$"
    re_double='^"([^"\\]*)"( +#.*)?$'

    case ${value:0:1} in
    "'")
        [[ $value =~ $re_single ]] || return 1
        printf '%s\n' "${BASH_REMATCH[1]}"
        return 0
        ;;
    '"')
        [[ $value =~ $re_double ]] || return 1
        printf '%s\n' "${BASH_REMATCH[1]}"
        return 0
        ;;
    '&' | '*' | '!' | '|' | '>' | '#' | '{' | '[' | '?' | '%' | '@' | '`' | ',')
        return 1
        ;;
    *) ;;
    esac

    plain=$value
    if [[ $plain == *' #'* ]]; then
        plain=${plain%% \#*}
    fi
    plain=${plain%"${plain##*[! ]}"}

    if [[ -z $plain || $plain == *'#'* || $plain == *': '* || $plain == *: || $plain == - || $plain == '- '* ]]; then
        return 1
    fi

    printf '%s\n' "$plain"
}

# _spool_line: checks one line of agent.yaml against the fail-closed rules of read_spool_directory and
# records the spool directory key. Fails on any line the rules do not name.
_spool_line() {
    local line=$1
    local re_top='^[a-z_]+:'
    local re_spool='^spool:( +(#.*)?)?$'
    local re_comment='^ *#'
    local re_entry='^  ([a-z_]+): +([^ ].*)$'
    local key value parsed

    case $line in
    '%'* | '---'* | '...'*) return 1 ;;
    *) ;;
    esac

    if [[ -z ${line//[ ]/} || $line =~ $re_comment ]]; then
        return 0
    fi

    if [[ ${line:0:1} != ' ' ]]; then
        [[ $line =~ $re_top ]] || return 1
        _SPOOL_SEEN_TOP=1
        _SPOOL_IN_BLOCK=0

        if [[ $line == spool:* ]]; then
            [[ $_SPOOL_SEEN_SPOOL == 0 ]] || return 1
            [[ $line =~ $re_spool ]] || return 1
            _SPOOL_SEEN_SPOOL=1
            _SPOOL_IN_BLOCK=1
        fi

        return 0
    fi

    [[ $_SPOOL_SEEN_TOP == 1 ]] || return 1

    if [[ $_SPOOL_IN_BLOCK == 0 ]]; then
        return 0
    fi

    [[ $line =~ $re_entry ]] || return 1
    key=${BASH_REMATCH[1]}
    value=${BASH_REMATCH[2]}
    parsed=$(_spool_value "$value") || return 1

    if [[ $key == directory ]]; then
        [[ $_SPOOL_HAVE_DIR == 0 ]] || return 1
        _SPOOL_HAVE_DIR=1
        _SPOOL_DIR=$parsed
    fi
}

# read_spool_directory FILE: prints the spool directory of an agent.yaml, read by fail-closed line rules.
# Status 0, or 2 with the message on standard error. The C locale applies to the whole call, helpers included.
read_spool_directory() {
    LC_ALL=C _read_spool_directory_body "$@"
}

# _read_spool_directory_body FILE: the body of read_spool_directory.
_read_spool_directory_body() {
    local file=$1
    local who=${0##*/}
    local line stray name
    local re_dir="^${SPOOL_ROOT}/([A-Za-z0-9._-]+)\$"

    _SPOOL_SEEN_TOP=0
    _SPOOL_SEEN_SPOOL=0
    _SPOOL_IN_BLOCK=0
    _SPOOL_HAVE_DIR=0
    _SPOOL_DIR=""

    if [[ ! -f $file ]]; then
        printf '%s: cannot read spool.directory from %s\n' "$who" "$file" >&2
        return 2
    fi

    stray=$(tr -d '\n\040-\176' <"$file" | wc -c)
    stray=${stray//[[:space:]]/}

    if [[ $stray != 0 ]]; then
        printf '%s: cannot read spool.directory from %s\n' "$who" "$file" >&2
        return 2
    fi

    while IFS= read -r line || [[ -n $line ]]; do
        if ! _spool_line "$line"; then
            printf '%s: cannot read spool.directory from %s\n' "$who" "$file" >&2
            return 2
        fi
    done <"$file"

    if [[ $_SPOOL_HAVE_DIR == 0 ]]; then
        printf '%s\n' "$DEFAULT_SPOOL"
        return 0
    fi

    name=""
    if [[ $_SPOOL_DIR =~ $re_dir ]]; then
        name=${BASH_REMATCH[1]}
    fi

    if [[ -z $name || $name == . || $name == .. ]]; then
        printf '%s: spool.directory must be one directory below %s\n' "$who" "$SPOOL_ROOT" >&2
        return 2
    fi

    printf '%s\n' "$_SPOOL_DIR"
}

# check_platform EUID PID1 MACHINE: root, systemd as PID 1, x86_64; status 0, or 1 with the message.
check_platform() {
    local euid=$1
    local pid1=$2
    local machine=$3

    if [[ $euid != 0 ]]; then
        echo "install.sh: must run as root" >&2
        return 1
    fi

    if [[ $pid1 != systemd ]]; then
        echo "install.sh: needs systemd" >&2
        return 1
    fi

    if [[ $machine != x86_64 ]]; then
        echo "install.sh: only linux-amd64 is supported" >&2
        return 1
    fi
}

# run_mariadb_sql SQLFILE SCRIPT: runs the SQL file with plesk db when plesk is on the search path, else
# with mariadb; prints the warning on failure and always succeeds.
run_mariadb_sql() {
    local sql=$1
    local script=$2
    local tool_name="mariadb"

    if command -v plesk >/dev/null 2>&1; then
        tool_name="plesk db"
        if [[ -r $sql ]] && plesk db <"$sql" >/dev/null 2>&1; then
            return 0
        fi
    elif command -v mariadb >/dev/null 2>&1; then
        if [[ -r $sql ]] && mariadb <"$sql" >/dev/null 2>&1; then
            return 0
        fi
    fi

    printf '%s: WARNING MariaDB account not created; run: %s < %s\n' "$script" "$tool_name" "$sql"
    return 0
}

# _bundle_entry_ok PATH: PATH is no link, is owned by uid 0 and has no group- or other-write bit and no
# set-user-ID, set-group-ID or sticky bit (the octal mode is compared as a number, not by digits).
_bundle_entry_ok() {
    local path=$1
    local info owner mode

    if [[ -L $path ]]; then
        return 1
    fi

    info=$(stat -c '%u %a' -- "$path") || return 1
    owner=${info%% *}
    mode=$((8#${info#* }))

    ((owner == 0 && (mode & 07022) == 0))
}

# check_bundle_dir DIR FILE...: DIR and each existing FILE (a name relative to DIR) must be no link, be owned by
# uid 0 and be free of group- and other-write and of every special bit; a missing file is left to the later
# checks. Status 1 with the message naming the first entry that fails.
check_bundle_dir() {
    local dir=$1
    local path

    shift

    for path in "$dir" "${@/#/$dir/}"; do
        if [[ $path != "$dir" && ! -e $path && ! -L $path ]]; then
            continue
        fi

        if ! _bundle_entry_ok "$path"; then
            printf '%s: %s is not owned by root or is writable by others; extract the bundle as root in a private directory\n' \
                "${0##*/}" "$path" >&2
            return 1
        fi
    done
}

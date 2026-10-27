#!/usr/bin/env bash
# Removes vandox-agent from a systemd server; with --purge also its user, configuration, token, spool and MariaDB account.
# Run as root:
#   deploy/agent/uninstall.sh [--purge]

set -euo pipefail

if [[ -z "$(declare -F read_spool_directory)" ]]; then
    _uninstall_source=${BASH_SOURCE[0]%/*}
    if [[ $_uninstall_source == "${BASH_SOURCE[0]}" ]]; then
        _uninstall_source=.
    fi
    # shellcheck source=common.sh disable=SC1091
    source "$_uninstall_source/common.sh"
fi

readonly U_SERVICE="vandox-agent"
readonly U_CONFIG_DIR="/etc/vandox"
readonly U_CONFIG_FILE="/etc/vandox/agent.yaml"
readonly U_TOKEN_FILE="/etc/vandox/agent-token"
readonly U_BINARY="/usr/local/bin/vandox-agent"
readonly U_UNIT="/etc/systemd/system/vandox-agent.service"
readonly U_STATE_ROOT="/var/lib/vandox"

# parse_args ARGS...: sets PURGE (0 or 1); status 2 with the usage line.
parse_args() {
    PURGE=0

    if (($# == 0)); then
        return 0
    fi

    if (($# == 1)); then
        local option=$1

        if [[ $option == --purge ]]; then
            PURGE=1
            return 0
        fi
    fi

    echo "usage: uninstall.sh [--purge]" >&2
    return 2
}

# _remove_file PATH LABEL: removes a file or a link (never what it points to) and says so.
_remove_file() {
    local path=$1
    local label=$2

    if [[ -e $path || -L $path ]]; then
        rm -f -- "$path"
        echo "uninstall.sh: removed $label"
    else
        echo "uninstall.sh: $label not present"
    fi
}

# _drop_account SQLFILE: removes the MariaDB account with plesk db when present, else mariadb; warns when it fails.
_drop_account() {
    local sql=$1
    local tool_name="mariadb"

    if command -v plesk >/dev/null 2>&1; then
        tool_name="plesk db"
        if [[ -r $sql ]] && plesk db <"$sql" >/dev/null 2>&1; then
            echo "uninstall.sh: removed MariaDB account"
            return 0
        fi
    elif command -v mariadb >/dev/null 2>&1; then
        if [[ -r $sql ]] && mariadb <"$sql" >/dev/null 2>&1; then
            echo "uninstall.sh: removed MariaDB account"
            return 0
        fi
    fi

    echo "uninstall.sh: WARNING MariaDB account not removed; run: $tool_name < deploy/agent/mariadb-user-remove.sql"
}

# _purge_user: deletes the user (never its home directory) and a group that remains.
_purge_user() {
    local entry home

    if entry=$(getent passwd "$U_SERVICE"); then
        IFS=: read -r _ _ _ _ _ home _ <<<"$entry"
        userdel "$U_SERVICE"
        echo "uninstall.sh: removed user $U_SERVICE"

        if [[ -n $home && $home != /nonexistent ]]; then
            echo "uninstall.sh: home directory $home left in place"
        fi
    else
        echo "uninstall.sh: user $U_SERVICE not present"
    fi

    if getent group "$U_SERVICE" >/dev/null; then
        groupdel "$U_SERVICE"
        echo "uninstall.sh: removed group $U_SERVICE"
    fi
}

# _purge SQLFILE: removes what the plain uninstall keeps.
_purge() {
    local sql=$1

    _remove_file "$U_CONFIG_FILE" "$U_CONFIG_FILE"
    _remove_file "$U_TOKEN_FILE" "$U_TOKEN_FILE"

    if [[ -e $U_STATE_ROOT || -L $U_STATE_ROOT ]]; then
        echo "uninstall.sh: deleting $U_STATE_ROOT (records not yet sent are lost)"
        rm -rf --one-file-system -- "$U_STATE_ROOT"
        echo "uninstall.sh: removed $U_STATE_ROOT"
    else
        echo "uninstall.sh: $U_STATE_ROOT not present"
    fi

    _drop_account "$sql"
    _purge_user

    if [[ -d $U_CONFIG_DIR && ! -L $U_CONFIG_DIR ]]; then
        if rmdir -- "$U_CONFIG_DIR" 2>/dev/null; then
            echo "uninstall.sh: removed $U_CONFIG_DIR"
        else
            echo "uninstall.sh: $U_CONFIG_DIR is not empty, kept"
        fi
    fi
}

# main ARGS...: removes the agent; with --purge also what it leaves behind.
main() {
    local script_dir spool=$DEFAULT_SPOOL

    umask 077
    export LC_ALL=C
    export PATH=/usr/sbin:/usr/bin:/sbin:/bin

    parse_args "$@" || exit 2

    if [[ $EUID != 0 ]]; then
        echo "uninstall.sh: must run as root" >&2
        exit 1
    fi

    script_dir=${BASH_SOURCE[0]%/*}
    script_dir=$(cd -- "$script_dir" && pwd)

    if [[ -f $U_CONFIG_FILE && ! -L $U_CONFIG_FILE ]]; then
        spool=$(read_spool_directory "$U_CONFIG_FILE") || exit 2
    fi

    systemctl disable --now "$U_SERVICE" >/dev/null 2>&1 || true

    _remove_file "$U_UNIT" "unit"
    _remove_file "$U_BINARY" "binary"
    systemctl daemon-reload || true
    systemctl reset-failed "$U_SERVICE" >/dev/null 2>&1 || true

    if ((PURGE == 1)); then
        _purge "$script_dir/mariadb-user-remove.sql"
        return 0
    fi

    echo "uninstall.sh: kept user $U_SERVICE, $U_CONFIG_FILE, $U_TOKEN_FILE, $spool and the MariaDB account; remove them with --purge"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    main "$@"
fi

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

# parse_args ARGS...: sets PURGE; status 2 with the message.
parse_args() {
    return 99
}

# main ARGS...: removes the agent; with --purge also what it leaves behind.
main() {
    return 99
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    main "$@"
fi

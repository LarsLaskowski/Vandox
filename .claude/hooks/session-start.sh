#!/bin/bash
# SessionStart hook for Claude Code on the web, for a repository with several stack profiles: runs the
# hook of every profile (`session-start-<profile>.sh` next to this script, written by adopt-template).
# Idempotent; each profile's hook does nothing outside remote sessions.
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
status=0
for hook in "$here"/session-start-*.sh; do
  [[ -f "$hook" ]] || continue
  bash "$hook" || { echo "$(basename "$hook") failed" >&2; status=1; }
done
exit "$status"

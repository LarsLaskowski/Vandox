#!/bin/bash
# SessionStart hook for Claude Code on the web (Go profile): downloads the modules so the squad can
# format, vet, lint, test and check coverage. Idempotent; does nothing outside remote sessions.
set -euo pipefail

if [[ "${CLAUDE_CODE_REMOTE:-}" != "true" ]]; then
  exit 0
fi

cd "${CLAUDE_PROJECT_DIR:-$(pwd)}"

go mod download

if ! command -v golangci-lint >/dev/null 2>&1; then
  echo "golangci-lint is not installed; the analyzer gate needs it (see .squad/stack.md, Toolchain)." >&2
fi

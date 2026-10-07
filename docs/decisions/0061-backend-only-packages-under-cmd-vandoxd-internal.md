# 0061: Backend-only packages live under cmd/vandoxd/internal; internal/ stays shared

- **Status:** Superseded by [0073](0073-backend-in-dotnet-10-with-blazor-agent-stays-go.md)
- **Date:** 2026-10-05
- **Source:** Issue #13
- **Supersedes:** —

## Context

Issue #13 adds the first code that only the backend uses: the HTTP server with `/healthz` and the SQLite
store. Later backend features (ingest, log import, analysis, rules, web UI, Telegram) will add more and will
share the store. `CLAUDE.md`, `AGENTS.md`, `.github/copilot-instructions.md`, `README.md` and
`docs/ARCHITECTURE.md` describe `internal/` as "packages shared by both binaries" (data model, wire format,
configuration, version, command line).

## Options considered

1. **Everything in package `main` of `cmd/vandoxd`** — no new directories, but one growing package without
   boundaries, and the store cannot be faked behind an interface owned by another package.
2. **Backend packages under `internal/` (e.g. `internal/server`, `internal/store`)** — conventional, but
   makes the documented meaning of `internal/` untrue, and the agent could import backend code by mistake.
3. **Backend packages under `cmd/vandoxd/internal/`** — Go's `internal` rule lets only `cmd/vandoxd` import
   them, so the agent cannot; `internal/` keeps its documented meaning.

## Decision

Option 3. Backend-only packages live in `cmd/vandoxd/internal/<name>`, starting with
`cmd/vandoxd/internal/server` (listeners, `/healthz`, graceful shutdown) and `cmd/vandoxd/internal/store`
(SQLite). Packages used by both binaries stay in `internal/`. The agent will follow the same rule with
`cmd/vandox-agent/internal/`.

## Consequences

- The compiler enforces that the agent never links backend code.
- A package that later turns out to be needed by both binaries moves to `internal/`.
- Tests follow the usual layout (`foo.go` → `foo_test.go`) inside these packages; the coverage gate and the
  analyzers cover them like any other Go path.

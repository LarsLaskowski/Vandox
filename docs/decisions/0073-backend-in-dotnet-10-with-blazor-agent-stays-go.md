# 0073: The backend is written in .NET 10 with a Blazor web UI; the agent stays in Go

- **Status:** Accepted
- **Date:** 2026-10-06
- **Area:** —
- **Source:** Product Manager request: move the backend to .NET 10 with Blazor, keep the agent in Go
- **Supersedes:** —

## Context

The agent runs permanently on the monitored server (about 2 GB RAM, next to Plesk, MariaDB and mail), so it must stay a small
static binary without a runtime to maintain there. The backend runs as one Docker container on a host the owner controls, its
web UI is a large part of its work, and the product owner works in .NET. The first decision had been Go for both binaries with
an own web UI served by `vandoxd` instead of Grafana (one container, one login, incident views that correlate logs and
snapshots, which panels cannot build). At the time of the change the Go backend contained the data model, the wire decoder,
the configuration, the SQLite storage, the log import and the service host; the web UI, ingest API, analysis and alerting
were not written yet.

## Options considered

1. **Keep Go for both** — one toolchain, shared packages; the web UI would be server-rendered HTML templates
   written by hand.
2. **.NET 10 backend with Blazor, Go agent** — a component-based UI, a mature host (Kestrel, configuration,
   logging) and the owner's main stack; the data model, the wire decoder and the storage exist twice, once per
   language, and a contract must keep them in step.
3. **.NET for both** — one language, but the agent would need a runtime (or a large self-contained binary) on
   the server it must not burden.
4. **Rust for both** — smallest footprint, but slower development for a one-person project.
5. **Grafana on top of the data instead of an own UI** — powerful dashboards, but another container and a SQLite
   data source plugin, and incident timelines are hard to build in panels; the own web UI stays, now in Blazor.

## Decision

Option 2. `vandox-agent` stays in Go. `vandoxd` is a .NET 10 ASP.NET Core application with a Blazor Web App in Interactive
Server render mode, in the solution `Vandox.slnx` with one test project per library. The shared Go packages stay in `internal/`
and are used only by the agent; everything backend-only in Go is removed. The project layout is in `CLAUDE.md` and
`docs/ARCHITECTURE.md`.

## Consequences

- The repository has two languages and two toolchains ([0074](0074-two-language-toolchain-and-combined-quality-gates.md)).
- Data model and wire format exist in Go (producer) and C# (consumer); [0075](0075-wire-contract-pinned-by-golden-fixtures.md)
  keeps them in step.
- The backend image carries the ASP.NET runtime ([0041](0041-backend-image-chiseled-runtime-base-images-pinned-by-digest.md)); it is larger than a static Go
  binary on distroless and the container memory limit matters more.
- Guarantees in `.squad/project.md` and the behavior in `docs/ARCHITECTURE.md` are unchanged; only the
  implementation language of the backend is.
- The agent stays a Go static binary; the web UI is still an own UI without Grafana, with one login (0016), and
  rendering external data (log lines, process names) in it remains a security area (injection).

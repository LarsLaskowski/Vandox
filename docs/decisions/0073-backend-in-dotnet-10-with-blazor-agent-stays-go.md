# 0073: The backend is written in .NET 10 with a Blazor web UI; the agent stays in Go

- **Status:** Accepted
- **Date:** 2026-10-06
- **Source:** Product Manager request: move the backend to .NET 10 with Blazor, keep the agent in Go
- **Supersedes:** [0005](0005-go-for-agent-and-backend.md), [0011](0011-own-go-web-ui-without-grafana.md), [0061](0061-backend-only-packages-under-cmd-vandoxd-internal.md)

## Context

The agent runs permanently on the monitored server (about 2 GB RAM, next to Plesk, MariaDB and mail), so it
must stay a small static binary without a runtime to maintain there. The backend is different: it runs as one
Docker container on a Docker host the owner controls (a NAS, a mini PC or a server), the web UI is a large
part of its work, and the product owner works in .NET. At the time of the change the Go backend contained the
data model, the wire decoder, the configuration, the SQLite storage, the log import and the service host; the
web UI, ingest API, analysis and alerting were not yet written.

## Options considered

1. **Keep Go for both** — one toolchain, shared packages; the web UI would be server-rendered HTML templates
   written by hand.
2. **.NET 10 backend with Blazor, Go agent** — a component-based UI, a mature host (Kestrel, configuration,
   logging) and the owner's main stack; the data model, the wire decoder and the storage exist twice, once per
   language, and a contract must keep them in step.
3. **.NET for both** — one language, but the agent would need a runtime (or a large self-contained binary) on
   the server it must not burden.

## Decision

Option 2. `vandox-agent` stays in Go (`cmd/vandox-agent`, `internal/`: model, wire encoder, agent
configuration, CLI helpers, version). `vandoxd` is a .NET 10 ASP.NET Core application with a Blazor Web App
using the Interactive Server render mode. The solution `Vandox.slnx` holds `src/Vandox.Core` (data model, wire
decoder, configuration, safe file access, log parsing), `src/Vandox.Storage` (SQLite), `src/Vandox.Import`
(log import) and `src/Vandox.Backend` (host, CLI, Blazor shell, assembly name `vandoxd`), each with a test
project under `tests/`. Everything that was backend-only in Go (`cmd/vandoxd`, `internal/logparse`,
`internal/config/backend.go`) is removed.

## Consequences

- The repository has two languages and two toolchains ([0074](0074-two-language-toolchain-and-combined-quality-gates.md)).
- Data model and wire format exist in Go (producer) and C# (consumer); [0075](0075-wire-contract-pinned-by-golden-fixtures.md)
  keeps them in step.
- The backend image carries the ASP.NET runtime ([0080](0080-backend-image-on-the-chiseled-aspnet-runtime.md)); it is larger than a static Go
  binary on distroless and the container memory limit matters more.
- Guarantees in `.squad/project.md` and the behavior in `docs/ARCHITECTURE.md` are unchanged; only the
  implementation language of the backend is.
- The agent decision of 0005 (Go, static binary) still stands for the agent.

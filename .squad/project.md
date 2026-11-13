# Project

What the squad needs to know beyond the stack; read by the Lead, the Devil's Advocate, Security, the Tester and the
Reviewer. The agent `vandox-agent` is Go (`cmd/vandox-agent`, `internal/`), the backend `vandoxd` .NET 11 with
Blazor (`src/Vandox.*`, records 0073, 0197). Not template-managed: `adopt-template` creates it once; a product PR updates
it when the change makes an entry untrue (`.squad/routing.md`, *Scope of a product PR*).

Every entry points: it names files or endpoints and links the area document (`docs/areas/README.md`) or record that
states the rules; values, limits and sequences stand only there. `config-check.py` warns past `PROJECT_MAX_WORDS`
words.

## Security areas

A change that touches one of these lists it under *Security areas touched* in its plan, so Security reviews the
plan (`.squad/routing.md`, *Tiers*, which also says when a change is tier `security`). One line per area: what is
protected, where, and the area documents that state its rules.

1. **Ingest authentication** — only the agent holding its bound token stores data, within memory and disk bounds; a
   resent record is stored once. `POST /v1/batches`, `src/Vandox.Backend/Ingest/`, `src/Vandox.Backend/Hosting/`,
   `BatchWriter` and `AgentTimeline` (`src/Vandox.Storage/`) — [ingest-endpoint](../docs/areas/ingest-endpoint.md),
   [storage-writing](../docs/areas/storage-writing.md).
2. **Tailscale ACL and port binding** — a compromised server reaches only the ingest port on the tailnet address.
   `deploy/backend/docker-compose.yml`, `deploy/backend/Dockerfile`, the tailnet policy in `deploy/agent/README.md` —
   [network-and-security-model](../docs/areas/network-and-security-model.md),
   [backend-host](../docs/areas/backend-host.md), [agent-installation](../docs/areas/agent-installation.md).
3. **Web UI login** — only a signed-in user reads data or writes a note (hash, sessions, CSRF, throttling, trusted
   proxy). `src/Vandox.Backend/Security/`, `Components/Account/Login.razor`, every page under `Components/Pages/`,
   `POST /incidents/<number>/notes`, `GET /report/markdown`, `TrustedProxyList` —
   [web-login](../docs/areas/web-login.md), [web-ui](../docs/areas/web-ui.md) and its `web-ui-*.md` views.
4. **Command signing for remote actions** (planned) — only signed, confirmed commands from a fixed list run —
   [notification-and-remote-actions](../docs/areas/notification-and-remote-actions.md).
5. **Telegram allowlist** (planned) — the bot talks only to allowlisted private chats —
   [notification-and-remote-actions](../docs/areas/notification-and-remote-actions.md).
6. **Agent privileges** — never root, only the listed groups and capability, no write into other users' processes
   (record 0122). `deploy/agent/` (`vandox-agent.service`, `install.sh`, `uninstall.sh`, `common.sh`, `README.md`),
   the collectors that use a right (`internal/procstats`, `internal/connstats`, `internal/sockmount`, `internal/logship`,
   `internal/kernevents`, `internal/svcstats`, `internal/mariadb`) — [agent-rights](../docs/areas/agent-rights.md),
   [agent-installation](../docs/areas/agent-installation.md).
7. **MariaDB monitoring user** — a password-less socket account with only `PROCESS`, read-only queries.
   `deploy/agent/mariadb-user.sql`, `mariadb-user-remove.sql`, `internal/mariadb` —
   [agent-mariadb](../docs/areas/agent-mariadb.md), [network-and-security-model](../docs/areas/network-and-security-model.md)
   (*MariaDB access*), [agent-installation](../docs/areas/agent-installation.md).
8. **Secrets handling** — read only from the environment, Docker secrets or a systemd credential, compared in constant
   time, never logged, shown or spooled. `internal/config`, `internal/sender`, `src/Vandox.Core/Configuration/`,
   `PasswordHash`, `/etc/vandox/agent-token` —
   [configuration-and-secrets](../docs/areas/configuration-and-secrets.md),
   [agent-sending](../docs/areas/agent-sending.md), [agent-installation](../docs/areas/agent-installation.md).
9. **File writes and paths derived from external input** — writes stay in the configured directories, follow no link,
   use restrictive modes; the spool is bounded. `vandox.db` (`src/Vandox.Storage/`), `src/Vandox.Import/`,
   `src/Vandox.Core/IO/`, `spool.directory` (`internal/spool`, `internal/agentstate`), `deploy/agent/install.sh` and
   `uninstall.sh` — [storage](../docs/areas/storage.md), [log-import](../docs/areas/log-import.md),
   [agent-spool](../docs/areas/agent-spool.md), [agent](../docs/areas/agent.md) (*Agent state*),
   [agent-installation](../docs/areas/agent-installation.md).
10. **Parsing of external input** — hostile input yields an error or a skipped record, never a crash, unbounded
    allocation or hang. `internal/` (every reader of external input); `src/Vandox.Core/` (`Wire`, `Configuration`,
    `LogParsing`, `Signatures`, `Templates`, `Incidents`, `Reports`), `src/Vandox.Import/`, the log search of
    `src/Vandox.Storage/`, `/logs` and `/report` addresses, `vandoxd` flags, `install.sh` inputs; later Telegram
    commands — [wire-format](../docs/areas/wire-format.md),
    [wire-format-records](../docs/areas/wire-format-records.md),
    [configuration-and-secrets](../docs/areas/configuration-and-secrets.md),
    [configuration-options](../docs/areas/configuration-options.md), the `agent*.md` and `log-*.md` documents,
    [incidents](../docs/areas/incidents.md), [forensics-report](../docs/areas/forensics-report.md),
    [storage-reading](../docs/areas/storage-reading.md), the `web-ui-*.md` views,
    [ingest-endpoint](../docs/areas/ingest-endpoint.md).
11. **Outbound calls** — each call has a timeout and reaches only its destination, encrypted to a verified peer.
    `internal/sender` (the token goes only to the IP literal in `backend.url`; no name is looked up: the `backend.url` rule in
    `internal/config/agent.go`, `validate_backend_url` in `deploy/agent/install.sh`), `internal/sdnotify`, `internal/sdbus`, `internal/mariadb`, `deploy/agent/vandox-agent.service`,
    the download in `install.sh`, the health check in `src/Vandox.Backend/Cli/`; Telegram, external checks, AI service planned —
    [agent-sending](../docs/areas/agent-sending.md),
    [network-and-security-model](../docs/areas/network-and-security-model.md),
    [backend-host](../docs/areas/backend-host.md), [agent-installation](../docs/areas/agent-installation.md),
    [notification-and-remote-actions](../docs/areas/notification-and-remote-actions.md).
12. **Logging and display of external data** — external text cannot inject into logs, pages, reports or Telegram.
    `src/Vandox.Backend/Logging/`, the components and security headers of `src/Vandox.Backend/`, `vandoxd` output, the
    agent's logger (`internal/agent`) — [web-ui](../docs/areas/web-ui.md) and its `web-ui-*.md` views,
    [forensics-report](../docs/areas/forensics-report.md), [listener-changes](../docs/areas/listener-changes.md),
    [incidents](../docs/areas/incidents.md), [log-templates](../docs/areas/log-templates.md),
    [log-signatures](../docs/areas/log-signatures.md), [backend-host](../docs/areas/backend-host.md),
    [log-import](../docs/areas/log-import.md),
    [configuration-and-secrets](../docs/areas/configuration-and-secrets.md), the `agent*.md` documents,
    [notification-and-remote-actions](../docs/areas/notification-and-remote-actions.md).
13. **Release pipeline and published artifacts** — published only from an admin-tagged release, built from pinned
    inputs, checksummed and attested; token and signing permission each in one job. `.github/workflows/release.yml`,
    `base-image-digests.yml`, the *Release build check* of `ci.yml`, `.github/scripts/`, `deploy/backend/Dockerfile`,
    `.dockerignore`, `.config/dotnet-tools.json`, the GitHub environment `release`, vendored
    `src/Vandox.Backend/wwwroot/lib/uplot/` — [CONTRIBUTING](../docs/CONTRIBUTING.md) (*Versioning and releases*),
    records [0027](../docs/decisions/0027-project-name-and-docker-image.md),
    [0037](../docs/decisions/0037-releases-version-tag-plain-tooling-and-attested-artifacts.md),
    [0041](../docs/decisions/0041-backend-image-chiseled-runtime-base-images-pinned-by-digest.md),
    [0052](../docs/decisions/0052-image-labels-description-added-no-logo-label.md); uPlot:
    [web-ui](../docs/areas/web-ui.md) (*Static assets*); without Docker: `.squad/stack.md` (*Not checkable in a cloud
    session*).

## Guarantees

Deliberate behavior that changes only with the Product Manager.

- **No data gaps unless explicitly recorded**: missing data is a recorded gap, unknown, never normal —
  [0028](../docs/decisions/0028-data-gaps-are-always-recorded.md),
  [0045](../docs/decisions/0045-batch-identified-by-agent-id-and-record-sequence-numbers.md);
  [agent](../docs/areas/agent.md) (*Gaps*), [agent-spool](../docs/areas/agent-spool.md),
  [agent-sending](../docs/areas/agent-sending.md).
- **A hanging collector or database never blocks the agent**: a hung source loses only its own sample, as a gap —
  [0029](../docs/decisions/0029-hanging-collector-never-blocks-the-agent.md),
  [0110](../docs/decisions/0110-agent-runtime-frame-runner-per-collector-gaps-for-every-missed-sample.md);
  [agent](../docs/areas/agent.md) (*Scheduling*).
- **Backfilled data never raises an alert by itself**: every record is classified live or backfilled once, when
  stored; alerts run on live data only — [0022](../docs/decisions/0022-backfill-detection-and-live-only-alerts.md),
  [0130](../docs/decisions/0130-live-or-backfilled-is-decided-at-write-time.md);
  [detection-and-reports](../docs/areas/detection-and-reports.md) (*Live data and backfill*).

## Integration surface

The places the Reviewer checks for each kind of change.

**A configuration option** touches:
- `internal/config` (`Agent`, `LoadAgent`, `AgentKeys`) or `src/Vandox.Core/Configuration` (`BackendConfig`,
  `ConfigKey`, `BackendConfigLoader`)
- the example `deploy/agent/agent.yaml` or `deploy/backend/vandoxd.yaml` (0049)
- the *Agent options* or *Backend options* table in `README.md` and
  [configuration-options](../docs/areas/configuration-options.md)
- `internal/config/agent_test.go`, `tests/Vandox.Core.Tests/BackendConfigLoaderTests.cs`

**A secret** touches: its `Env*` constant (`internal/config`; `ConfigConstants`, `BackendSecrets`), each binary's
known-variable list, the secrets table in `README.md`

**A service or module** touches:
- its public interface and the package (`internal/`) or project (`src/`) that owns it
- its wiring: `cmd/vandox-agent`, or `src/Vandox.Backend` (`Hosting`, `Cli`, `Program.cs`)
- the test double its callers' tests use
- the component overview in `docs/ARCHITECTURE.md`

**A web UI page, component or static asset** touches:
- a page: the navigation in `src/Vandox.Backend/Components/Layout/MainLayout.razor`
- a browser-loaded file: *Static assets* in [web-ui](../docs/areas/web-ui.md),
  `ServeTests.ServeServesWebAssetsOnlyOnWebPort`
- record 0099, checked by `WebUiContentTests`
- a brand file: the byte copy of `docs/assets/` in `wwwroot/brand/`

**A log parser** touches:
- its `ILogParser` type in `src/`, with a type `ParserTypes.IsValid` accepts
- its registration, in priority order, where `ImportCommand` builds the `ParserRegistry`
- detection tests against the other registered parsers
- *Import logs* in `README.md` and its `log-parsers-*.md` document

**A wire format field or record kind** touches (record 0075):
- `internal/model`, `internal/wire`, `src/Vandox.Core/Model`, `src/Vandox.Core/Wire`
- the golden batches `testdata/wire/all-kinds.jsonl` and `replaced-text.jsonl` (`VANDOX_UPDATE_GOLDEN=1 go test
  ./internal/wire`), read by `internal/wire/golden_test.go` and `WireContractTests`
- [wire-format](../docs/areas/wire-format.md) or [wire-format-records](../docs/areas/wire-format-records.md); a
  version change follows record 0043

**An external API call or DTO** touches:
- the client and its types — the external shape must not leak past it
- that client's fake in *Test doubles*

**An external source or destination** (`/proc`, `/sys`, D-Bus, journald, the MariaDB socket, Telegram, the
tailnet) touches:
- the small interface it is read through, and its fake in *Test doubles*
- the deadline that keeps it from blocking the agent (0029) and its gap on timeout (0028)
- the privilege in `deploy/agent/` (0030) or the ACL and port binding (0010, 0017)
- its *Security areas* entry

**A file a decision record constrains** — `.github/dependabot.yml`, a workflow, `.golangci.yml`,
`deploy/backend/Dockerfile`, `deploy/backend/docker-compose.yml` — touches:
- the records found by topic: `grep -l -i -E 'dependabot|workflow|dockerfile|compose|golangci|ci\.yml'
  docs/decisions/0*.md` (among them 0037, 0041, 0047, 0052, 0060, 0112)
- a contradicted record: edited (unreleased) or superseded (released) in the same pull request, or the change dropped

## Test doubles

Hand-written and reused (no mocking library); rules in `docs/UNIT_TESTS.md` (*Test doubles*). Implemented unless
marked planned.

| Surface | Double (where) | Stands in for |
| ------- | -------------- | ------------- |
| `/proc`, `/sys` | `io/fs.FS` over `testdata/` or `fstest.MapFS`, blocking and erroring wrappers (collector tests) | kernel files |
| `statfs`, `adjtimex` | scripted functions in `Sources` (`internal/sysstats`) | system calls |
| notification socket | `unixgram` socket in `t.TempDir()`, recording `notifier` (`internal/sdnotify`, `internal/agent`) | `NOTIFY_SOCKET` |
| scheduler | scripted and hanging collectors, recording sink (`internal/scheduler`) | collectors, spool |
| systemd D-Bus | `fakeSystemd` (`internal/svcstats`); replaying bus server (`internal/sdbus`) | the system bus |
| journald | `internal/journal/journaltest` writer, `testdata/journal/`, `world` harness (`internal/logship`) | journal and log files |
| agent state | a real store in `t.TempDir()` (`internal/agentstate`) | `agent-state.json` |
| MariaDB socket | replaying fake server, `...FromDocs` builders (`internal/mariadb`) | the server |
| ingest port | `sender.BatchSender` fakes (`internal/sender`, `internal/agent`), `httptest` server | the backend |
| time | Go: injected clock, `testing/synctest` (0062); C#: `FakeTimeProvider`, `TimerSignalingClock` (Backend tests), `CountingClock` (Storage tests) | the clock |
| database (C#) | real `SqliteStore` in `TempDirectory`; `FakeImportStore` (Import tests); `Recording*`, `Repeating*`, `Scripted*`, `*IncidentStore`, `FakeIncidentBrowser`, `ListLogger` (Backend tests) | store, readers |
| log parsers (C#) | `FakeParser`, `*Emitter`, `*Samples`, lazy streams (Core tests); `LineParser`, `ProbeParser` (Import tests) | parsers, input |
| files (C#) | `TempDirectory`, `RepositoryFiles.Path` (each test project) | directories, fixtures |
| Blazor (C#) | `ComponentTestRenderer`, `RecordingJsRuntime`, `*NavigationManager`, `FakeLogExplorer`, `*PageHarness`, `ReportPieces`, `WebSourceScanner` (Backend tests) | renderer, browser |
| web login (C#) | `BackendFixture`, `LoginTestClient`, `ScriptedPasswordVerifier`, `Fixed*` and `Unset*` providers (Backend tests) | service, browser |
| ingest (C#) | `IngestRig`, `IngestCall`, `RecordingRecordWriter`, `CountingBodyStream`, `StartingResponseBody` (Backend tests) | request, writer |
| Telegram Bot API | planned: a fake client | the Bot API |

# Architecture

<!-- project:begin architecture -->
Vandox is lean monitoring for a Plesk-managed Linux server, with analysis first: it reconstructs outages
from logs and system metrics and warns early. `vandox-agent` runs on the monitored server; `vandoxd`, the
backend with web UI, runs as a Docker container on any Docker host in the home network (for example a NAS such as Synology or QNAP, a mini PC or a server; called the *backend host* below).

This document describes the target architecture; as of now the binaries' `--version`, the data model and wire format
(Go: `internal/model`, `internal/wire`; backend: `src/Vandox.Core`), the configuration loading (`internal/config` for the agent,
`Vandox.Core.Configuration` for `vandoxd`) and the backend service skeleton and the log import framework exist (`vandoxd import`, with the parsers for the journal export, `syslog` and `kern.log`): `vandoxd` loads its configuration, opens and migrates its SQLite database (schema, batched writes, queries and log search
exist as the storage layer; the service does not call it yet, `vandoxd import` writes through it), listens on the
web and ingest ports, answers `/healthz` and shuts down gracefully, and ships as a container with a health
check. Sections are marked as implemented as features land. The decisions behind it are recorded in
[`docs/decisions/`](decisions/README.md); each section links the records it rests on. Vandox is an own
project rather than an off-the-shelf stack ([0004](decisions/0004-own-project-instead-of-off-the-shelf-stack.md)).

## Monitored server

The monitored server runs Ubuntu 22.04 and Plesk 18 with about 2 GB of RAM. The failure mode Vandox is built
to explain is memory exhaustion, then the OOM killer, then MariaDB, Plesk and mail going down. The RAM stays
at 2 GB; relief comes from swap, tuning the backups and an inventory of running services
([0025](decisions/0025-server-ram-stays-at-2-gb.md)). Services are only disabled in a reversible, documented
way, and agents of the hosting provider are never disabled or changed
([0026](decisions/0026-services-disabled-reversibly-only.md)).

## Components

- `cmd/vandox-agent` — Go (the agent stays small and static, so the monitored server needs no runtime;
  [0073](decisions/0073-backend-in-dotnet-10-with-blazor-agent-stays-go.md)), runs as a systemd service on the monitored server (rules: [Agent](areas/agent.md)). It collects metrics, process
  and network snapshots (read from `/proc`), service and MariaDB state, kernel events and logs, keeps them in
  an on-disk spool and sends them to the backend. It checks the state of the mail services, not individual
  mail accounts. Each collector runs in its own goroutine under a deadline and is abandoned when the
  deadline passes, so a hanging collector or database never blocks the agent; a missed sample is recorded
  as a gap, and a collector that is still stuck is not started again.
- `src/Vandox.Backend` — `vandoxd` (rules: [Backend host](areas/backend-host.md)), .NET 10 (ASP.NET Core with a Blazor Web App, Interactive Server render mode),
  one container on the backend host: ingest API, SQLite storage, analysis, rules, Telegram notifier, reports and
  web UI. Without arguments it runs the service; `-healthcheck` probes its `/healthz`; `vandoxd import <path>`
  imports logs saved on the backend host
  ([0072](decisions/0072-vandoxd-import-sub-command-output-and-exit-codes.md)). Two Kestrel listeners serve
  the web and the ingest port; a middleware routes each request by the label of its listener, and logs are JSON
  lines ([0081](decisions/0081-backend-host-two-listeners-json-logs-blazor-interactive-server.md)).
- The backend's libraries, one project each with a test project under `tests/`
  ([0073](decisions/0073-backend-in-dotnet-10-with-blazor-agent-stays-go.md)):
  `Vandox.Core` (data model and validation, the wire decoder, configuration and secrets, safe file access, the
  log parser interface and registry, the line reader that bounds the line length, and the built-in parsers: the
  journal export, `syslog` and `kern.log` with their kernel report grouper and the time zone rules of year-less
  times),
  `Vandox.Storage` (the SQLite database, see [Storage](areas/storage.md): schema, migrations, writing batches, queries and log search) and
  `Vandox.Import` (reads a directory, archive or file as streams, detects the parser per file, hashes the
  content, writes the parsers' records in resumable batches and builds the summary)
  ([0069](decisions/0069-log-import-idempotent-per-file-content-hash-with-resumable-batches.md),
  [0077](decisions/0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md),
  [0079](decisions/0079-log-parsing-and-import-in-the-backend-without-following-links.md)).
- `internal/` — the agent's Go packages: data model and the versioned wire encoder (see
  [wire format](areas/wire-format.md)), signatures, version information, agent configuration loading (see
  *Configuration*) and command-line handling (`internal/cli`). The model and the wire format exist twice, in Go
  (producer) and in C# (consumer); a golden batch produced by the Go encoder and decoded by the C# decoder keeps
  them in step ([0075](decisions/0075-wire-contract-pinned-by-golden-fixtures.md)).

Importing historical logs (including the legacy `top`/`lsof` log) and continuously shipping new log lines are
core parts of Vandox. The agent is written in Go, the backend in .NET; the repository holds both.

```mermaid
flowchart LR
    subgraph Server["Monitored server"]
        C[Collectors] --> SP[(On-disk spool)]
        SP --> SN[Sender]
    end
    subgraph NAS["vandoxd on the backend host"]
        IN[Ingest API] --> DB[(SQLite)]
        DB --> AN[Analysis]
        AN --> RU[Rules]
        RU --> TG[Telegram notifier]
        DB --> RP[Reports]
        RP --> TG
        DB --> UI[Web UI]
    end
    SN --> IN
    WIRE[Wire format: Go encoder, C# decoder] -.-> SN
    WIRE -.-> IN
```

Records: [0004](decisions/0004-own-project-instead-of-off-the-shelf-stack.md),
[0073](decisions/0073-backend-in-dotnet-10-with-blazor-agent-stays-go.md),
[0074](decisions/0074-two-language-toolchain-and-combined-quality-gates.md),
[0013](decisions/0013-mariadb-access-via-unix-socket-process-privilege.md),
[0014](decisions/0014-log-import-is-a-core-component.md),
[0015](decisions/0015-mail-services-checked-not-mail-accounts.md),
[0019](decisions/0019-agent-reads-proc-instead-of-top-lsof.md),
[0027](decisions/0027-project-name-and-docker-image.md),
[0029](decisions/0029-hanging-collector-never-blocks-the-agent.md),
[0042](decisions/0042-wire-format-gzip-json-lines-standard-library.md),
[0043](decisions/0043-wire-format-major-minor-versioning.md),
[0044](decisions/0044-batch-validated-as-a-whole-agent-records-only.md),
[0084](decisions/0084-log-line-record-gets-an-optional-host-field.md).

## Data flow

The agent collects data and writes it to its spool, then sends it in batches over Tailscale to the ingest
API. The backend stores the batches in SQLite; analysis, rules, the web UI and Telegram work from the stored
data. Detection, incident reconstruction and alerting are deterministic (rules, thresholds, log signatures);
AI is optional and only used to write the nightly report, which is sent at 06:00, or as soon as the backfill
has completed if the backend host was off at that time. The first release (v0.1.0) is the forensics release:
collection, log import, spool and backfill, storage and the historical views; alerting, the nightly report
and remote actions build on it.

```mermaid
flowchart LR
    A[vandox-agent] -- "batches over Tailscale" --> I[Ingest API]
    I --> D[(SQLite)]
    D --> AN[Analysis]
    D --> R[Rules]
    D --> W[Web UI]
    AN --> R
    R --> T[Telegram]
```

The log import is a second path into the same database. `vandoxd import <path>` reads a directory, an archive or a
file as streams in two passes (list and hash, then write in bounded batches through the storage layer), next to the
service in the same container. Imported records have origin `import`; they are never live, so they never raise an
alert ([0022](decisions/0022-backfill-detection-and-live-only-alerts.md)). The accepted input, the limits, the
repeatable-import rules, the parser contract and the output are in [Log import](areas/log-import.md).

Records: [0006](decisions/0006-agent-connects-outbound-only.md),
[0007](decisions/0007-sqlite-with-fts5-no-external-database.md),
[0008](decisions/0008-deterministic-detection-and-alerting.md),
[0012](decisions/0012-agent-never-contacts-telegram.md),
[0020](decisions/0020-analysis-before-alerting-forensics-release.md),
[0024](decisions/0024-nightly-report-timing.md),
[0069](decisions/0069-log-import-idempotent-per-file-content-hash-with-resumable-batches.md),
[0079](decisions/0079-log-parsing-and-import-in-the-backend-without-following-links.md),
[0072](decisions/0072-vandoxd-import-sub-command-output-and-exit-codes.md),
[0085](decisions/0085-syslog-time-zone-from-import-time-zone-with-embedded-tzdb.md),
[0086](decisions/0086-system-log-parsers-generic-syslog-claim-and-grouped-kernel-reports.md).

## Network

The rules for the transport, the web UI exposure and the MariaDB access are in [Network and security model](areas/network-and-security-model.md).

The agent connects outbound only and never listens on a port. It sends to the ingest port published on the
backend host's tailnet address; `vandoxd` does not embed Tailscale. The Tailscale ACL allows the monitored server to
reach only that port and nothing else. The web UI is reachable in the home LAN only and requires a login;
TLS for it is terminated by a reverse proxy in front of the container (e.g. the NAS's built-in one), while the ingest path bypasses the proxy and is
encrypted by Tailscale. Telegram is contacted only by the backend, never by the agent.

The compose file `deploy/backend/docker-compose.yml` publishes the web port on `127.0.0.1` by default, for the
reverse proxy on the same host. This is defense in depth, not the access control: depending on the Docker
Engine version and the host firewall, hosts on the LAN may reach a published container port directly, so the
web login is the boundary. The ingest port is not published until the ingest API exists (issue #40), which
binds it to the backend host's tailnet address as described above
([0060](decisions/0060-compose-file-port-bindings-volumes-and-memory-limit.md)).

```mermaid
flowchart LR
    subgraph Internet["Internet / server"]
        AG[vandox-agent]
        TGA[Telegram API]
    end
    subgraph Tailnet["Tailnet"]
        ING["Backend host tailnet address : ingest port"]
    end
    subgraph LAN["Home LAN"]
        BR[Browser] -- "HTTPS, login" --> RP[Reverse proxy, TLS]
        RP --> UI[vandoxd web UI]
        ING --> BE[vandoxd]
        BE -- "outbound" --> TGA
    end
    AG -- "outbound only, ACL: ingest port" --> ING
```

Records: [0010](decisions/0010-tailscale-with-strict-acl.md),
[0016](decisions/0016-web-ui-in-home-lan-with-login.md),
[0017](decisions/0017-ingest-via-tailnet-address-and-published-port.md),
[0023](decisions/0023-tls-through-a-reverse-proxy.md).

## Offline behavior and backfill

The rules for live data, alerts and the nightly report are in [Detection, alerts and reports](areas/detection-and-reports.md).

The backend host runs 24/7 but is sometimes switched off at night (typically 22:00–09:00) a few times a year. While
the backend is unreachable the agent keeps collecting and spools at least 7 days on disk. When the backend
is reachable again it sends current data first, then backfills the spool chronologically and throttled.
Every agent record carries a sequence number from a persistent per-agent counter, and every batch the
agent's ID; a batch is identified by the agent ID and the sequence numbers of its records, so a resend is
idempotent (the backend stores each agent ID and sequence number once) and the backend can detect gaps. `vandoxd` classifies every record as live or backfilled from its capture time, its receive time and
gaps in the sequence numbers; alert rules are evaluated on live data only, and backfilled data is stored and
analyzed but never alerts. The nightly report waits for the backfill if the backend host was off at 06:00. Data that is
nevertheless lost (agent stopped, spool full, collector timed out) is recorded as a gap, so there are no
data gaps unless explicitly recorded.

Records: [0022](decisions/0022-backfill-detection-and-live-only-alerts.md),
[0024](decisions/0024-nightly-report-timing.md),
[0028](decisions/0028-data-gaps-are-always-recorded.md),
[0044](decisions/0044-batch-validated-as-a-whole-agent-records-only.md),
[0045](decisions/0045-batch-identified-by-agent-id-and-record-sequence-numbers.md),
[0046](decisions/0046-batch-header-describes-the-capture-context.md).

## Storage and retention

`vandoxd` stores everything in SQLite with the FTS5 extension for log search, in WAL mode, with these
retention tiers. The database is the file `vandox.db` in `storage.directory`, created with mode 0600, opened through
`Microsoft.Data.Sqlite` with its bundled SQLite (FTS5 included, no system library needed): one writer connection
with `BEGIN IMMEDIATE`, a pool of query-only readers, WAL and `synchronous=FULL`
([0077](decisions/0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md), which carries the Go storage rules over). The storage directory is not created (a missing mount must not be hidden), and a
symbolic link in place of the database or its `-wal`/`-shm` files is refused.

Implemented by `src/Vandox.Storage`:

- **Schema** ([0063](decisions/0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md)):
  one `records` table holds the metadata of every record (kind, origin, source, agent ID, sequence number,
  capture and receive time as nanoseconds since the Unix epoch, boot ID, clock offset). Metrics and log lines
  have typed tables (`metrics`, `log_lines`) keyed by the record ID; every other kind keeps its payload as a
  JSON document in `records.data`. A partial unique index on (agent ID, sequence number) for origin `agent`
  makes a resent record a no-op that never overwrites the stored one; imported and backend records are not
  deduplicated (an import is made idempotent per file content, see below). Capture times are storable between 1677-09-21 and 2262-04-11 and sequence numbers up to
  2^63 - 1.
- **Migrations** ([0077](decisions/0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md)): at
  start-up every step above the stored `meta.schema_version` runs in its own transaction; a database with a
  newer version is refused. Schema version 3 adds the table `import_files`, version 4 the column `log_lines.host`.
- **Imports** ([0069](decisions/0069-log-import-idempotent-per-file-content-hash-with-resumable-batches.md)):
  `import_files` holds one row per imported file content (SHA-256 of the decompressed content, size, the name
  and modification time the parser got, the source type, the number of records stored so far and whether the
  import is complete). A batch with an import step advances the row in the same transaction as its records, as
  a compare-and-set on the expected count, so an interrupted import resumes where it stopped and two runs of
  the same content never store it twice. Records do not reference their file; imports are idempotent per
  file content, not per record.
- **Connections** ([0077](decisions/0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md)): one writer connection (immediate transactions, one batch per transaction) and a pool
  of query-only readers, in WAL mode with `synchronous=FULL`, so a committed batch is durable and reads (and
  `/healthz`) do not wait for a write.
- **Log search** ([0066](decisions/0066-log-search-takes-literal-terms-only.md)): the FTS5 index of the log
  messages is filled by the write path in the same transaction, so a line is searchable when it is committed.
  The search text is taken as literal terms only (no FTS5 operators, bounded length and term count). The cost
  of a search grows with the stored lines that contain its terms, so callers bound it with a context deadline.
- **Interfaces** ([0077](decisions/0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md)):
  `store.Writer`, `store.RecordReader`, `store.LogSearcher` and `store.ImportTracker`, with a scripted fake in
  `store/storetest`.
- **Write throughput** ([0077](decisions/0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md)):
  measured by a benchmark, see [`BENCHMARKS.md`](BENCHMARKS.md).

| Data | Retention |
| ---- | --------- |
| Raw data | 30 days |
| 5-minute rollups | 1 year |
| Hourly rollups | 3 years |
| Incidents | unlimited |
| Logs | 90 days |
| Process and connection snapshots | 30 days |

Log data is stored as it is, without pseudonymization: it is the operator's own server and the data stays in
the home network.

Records: [0007](decisions/0007-sqlite-with-fts5-no-external-database.md),
[0021](decisions/0021-no-pseudonymization-of-log-data.md).

## Remote actions (later)

The rules for the Telegram bot and for remote actions are in [Notification and remote actions](areas/notification-and-remote-actions.md).

Remote actions are not part of v0.1.0. They exist only as signed commands that reference an action from a
fixed list configured locally on the monitored server, and only after the user has confirmed them. The agent
pulls the commands from the backend and verifies the signature before executing; commands are never pushed
to the server.

Records: [0006](decisions/0006-agent-connects-outbound-only.md),
[0009](decisions/0009-remote-actions-as-signed-commands.md).

## Configuration

Both binaries are configured through one strictly parsed YAML file each (`/etc/vandox/agent.yaml`,
`/etc/vandox/vandoxd.yaml`; commented examples under `deploy/agent/` and `deploy/backend/`) and, for secrets only,
`VANDOX_*` environment variables or `*_FILE` files. Each binary implements the rules in its own language:
`internal/config` (Go) for the agent and `Vandox.Core.Configuration` (C#) for the backend
([0078](decisions/0078-configuration-and-secrets-in-the-backend-with-yamldotnet.md)). `vandoxd` loads its
configuration at start-up; the agent will call `LoadAgent` with the agent feature. The rules, the options and the
error behavior are in [Configuration and secrets](areas/configuration-and-secrets.md).

## Security model

The agent and the backend communicate only over a private Tailscale network. The agent connects outbound
only and never listens on a port, and the Tailscale ACL lets the monitored server reach only the ingest port
on the backend host. The web UI is protected by a login and runs behind a reverse proxy. The Telegram token
exists only on the backend host. The agent reads MariaDB over the local socket as the user `vandox-agent`, identified
via `unix_socket` and granted only `PROCESS`. Log data is not pseudonymized. Secrets are never logged. The agent runs as the
dedicated user `vandox-agent`, never as root, with only the groups and capabilities listed in `deploy/agent/`
and, from v0.6.0, a polkit rule that allows restarting only the configured units. The two capabilities it
needs to read other users' processes (`CAP_SYS_PTRACE`, `CAP_DAC_READ_SEARCH`) give it root's read access,
so its systemd unit denies the process-attach system calls and grants no write-side capability: a
compromised agent can read everything on the server but cannot use its capabilities to write as or run
code as another user; credentials it reads may still lead to root through other services (password reuse,
Plesk or MariaDB administration)
([0030](decisions/0030-agent-runs-unprivileged-with-named-capabilities.md)). The Telegram bot sends to and
accepts updates only from allowlisted users in their private chats
([0031](decisions/0031-telegram-user-allowlist.md)). Secrets come only from environment variables or Docker
secrets ([0032](decisions/0032-secrets-only-from-environment-or-docker-secrets.md)). The only release
credential, the Docker Hub token, is readable only by the tag-triggered publish job and limited to pushing
`networlddev/vandox` ([0037](decisions/0037-releases-version-tag-plain-tooling-and-attested-artifacts.md)). The OIDC
signing permission exists only in the secret-free `attest` job
([0037](decisions/0037-releases-version-tag-plain-tooling-and-attested-artifacts.md)). Kept
in sync with `SECURITY.md` and the *Security areas* in `.squad/project.md`.

Records: [0006](decisions/0006-agent-connects-outbound-only.md),
[0010](decisions/0010-tailscale-with-strict-acl.md),
[0013](decisions/0013-mariadb-access-via-unix-socket-process-privilege.md),
[0021](decisions/0021-no-pseudonymization-of-log-data.md),
[0030](decisions/0030-agent-runs-unprivileged-with-named-capabilities.md),
[0031](decisions/0031-telegram-user-allowlist.md),
[0032](decisions/0032-secrets-only-from-environment-or-docker-secrets.md),
[0037](decisions/0037-releases-version-tag-plain-tooling-and-attested-artifacts.md).

## Deployment

The agent is released as a binary for the monitored server, the backend as a Docker image published on
Docker Hub as `networlddev/vandox` ([0027](decisions/0027-project-name-and-docker-image.md)). See the
*Versioning and releases* section in [`CONTRIBUTING.md`](CONTRIBUTING.md).

- The agent binary `vandox-agent-linux-amd64` and `SHA256SUMS` are GitHub release assets.
- The image is built from `deploy/backend/Dockerfile` (the .NET SDK image publishes the application onto the
  chiseled ASP.NET runtime image, [0041](decisions/0041-backend-image-chiseled-runtime-base-images-pinned-by-digest.md)) and runs as UID 65532.
  The builder and runtime base images are pinned by digest: each `FROM` names an image and a digest from
  build arguments, and the tag is kept in a separate build argument and in the image's OCI base-image
  labels. The release build sets none of these arguments. The digests are refreshed by hand in a pull
  request, a weekly workflow reports a stale digest as an issue, and a script checks that the builder and runtime tags name the
  .NET version of the target framework.
- The image has a `HEALTHCHECK` that runs `dotnet /app/vandoxd.dll -healthcheck` (the chiseled image has no shell or curl),
  ships an empty `/data` owned by UID 65532 so that a new named volume is writable, and declares no `EXPOSE`
  ([0059](decisions/0059-healthz-checks-the-database-and-the-binary-is-the-health-probe.md)). The compose file
  `deploy/backend/docker-compose.yml` runs it with a data volume, a read-only root file system, dropped
  capabilities and a memory limit; CI starts the image from it
  ([0072](decisions/0072-vandoxd-import-sub-command-output-and-exit-codes.md),
  [0060](decisions/0060-compose-file-port-bindings-volumes-and-memory-limit.md)).
- Releases are built by `.github/workflows/release.yml` only from SemVer tags on `main`. Only the
  repository admin may create these tags (tag ruleset `release-tags`), and the workflow checks that the
  tagged commit is on `main`.
- Release binaries are built from source without restored CI caches and only after `govulncheck` and the NuGet vulnerability check pass.
  The image that was verified is the image that is pushed, and a published version is never overwritten.
- The binary and the image digest get SLSA build provenance attestations and SPDX SBOM attestations (GitHub
  artifact attestations) from a separate job that holds only the signing permission and no secret; the
  workflow verifies them before it creates the GitHub release. The SBOMs are generated in the build job by a
  digest-pinned syft container that runs without network and without access to `dist/`.

Records: [0037](decisions/0037-releases-version-tag-plain-tooling-and-attested-artifacts.md),
[0041](decisions/0041-backend-image-chiseled-runtime-base-images-pinned-by-digest.md),
[0072](decisions/0072-vandoxd-import-sub-command-output-and-exit-codes.md),
[0059](decisions/0059-healthz-checks-the-database-and-the-binary-is-the-health-probe.md),
[0060](decisions/0060-compose-file-port-bindings-volumes-and-memory-limit.md).
<!-- project:end architecture -->

## Development process

This repository is developed with AI agents (Claude Code, Codex/GPT, GitHub Copilot) that follow the same
rules: `CLAUDE.md`, `AGENTS.md` and `.github/copilot-instructions.md` hold one shared rule set, and the
skills under `.claude/skills/`, `.agents/skills/` and `.github/skills/` are identical copies. Every pull
request is reviewed before it is opened by the read-only reviewer in `.claude/agents/squad-reviewer.md`
— round 1 is a full review, every later round looks only at the delta, and only blocking findings earn
another round, because a fresh full re-review of unchanged code always finds something new.

The squad skills (`squad-issue`, `squad-spec`) wrap that review in a larger, bounded pipeline described in
[`.squad/routing.md`](../.squad/routing.md): an Opus Lead plans, classifies the change into a tier (`docs`,
`trivial`, `standard`, `security`) that decides how much of the pipeline runs, and owns every decision
including PR approval; for `standard` and `security` a Devil's Advocate challenges the plan once (no veto)
before Security sees it; a Security member reviews the plan (tier `security`) and the diff; tests are
written first and new/changed code reaches at least 80 % line coverage; a Code Officer clears formatting
and analyzer diagnostics *before* the review so the reviewed code is the merged code; and the review loop
is one full pass plus at most two delta rounds. Every limit ends in a Lead decision, and only a decision
the Lead cannot make reaches the human. The stack-specific commands live in
[`.squad/stack.md`](../.squad/stack.md), the project's guarantees and attack surface in
[`.squad/project.md`](../.squad/project.md).

The reasoning behind individual choices is kept out of this document and recorded instead as decision
records in [`docs/decisions/`](decisions/README.md); this document describes how the system works and
links a record where a guarantee or flow is the result of one.

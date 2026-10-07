# Decision records

Why the code is the way it is. Each file records one decision — its context, the options considered, what
was chosen and the consequences — so that months later the reasoning is still available without the
pull request, the issue thread or the session that produced it.

`docs/ARCHITECTURE.md` describes *how* the system works today; these records explain *why* individual
choices were made. When a decision changes the architecture, `ARCHITECTURE.md` is updated as well and
links the record.

## Rules

- One decision per file: `NNNN-short-title.md` (four digits, next free number), created from
  [`_template.md`](_template.md).
- Written by the squad Lead (see [`.squad/agents/lead/charter.md`](../../.squad/agents/lead/charter.md));
  anyone may add one for a change made outside the squad.
- Committed together with the change it explains.
- Records are **append-only**: an accepted record is never rewritten. A changed decision gets a new record
  that names the old one under *Supersedes*, and the old record's status becomes
  `Superseded by NNNN` (the only edit allowed).
- Not for routine changes: a record is needed when a choice between real alternatives was made, a
  trade-off or limitation was accepted, a review finding was deliberately not fixed, a documented
  guarantee was touched, a dependency was added or removed, or work was split into a follow-up issue.

## Index

<!-- project:begin index -->
| #    | Title | Status | Date |
| ---- | ----- | ------ | ---- |
| [0001](0001-quality-gates-before-the-pull-request.md) | Quality gates before the pull request | Accepted | 2026-10-03 |
| [0002](0002-squad-working-records-off-main.md) | Squad working records stay off main, and product PRs never change the squad | Accepted | 2026-10-03 |
| [0003](0003-squash-merge-pull-requests.md) | Squash-merge pull requests | Accepted | 2026-10-03 |
| [0004](0004-own-project-instead-of-off-the-shelf-stack.md) | Own project instead of an off-the-shelf monitoring stack | Accepted | 2026-10-03 |
| [0005](0005-go-for-agent-and-backend.md) | Go for agent and backend | Superseded by [0073](0073-backend-in-dotnet-10-with-blazor-agent-stays-go.md) | 2026-10-03 |
| [0006](0006-agent-connects-outbound-only.md) | The agent connects outbound only; commands are pulled | Accepted | 2026-10-03 |
| [0007](0007-sqlite-with-fts5-no-external-database.md) | SQLite with FTS5, no external database | Accepted | 2026-10-03 |
| [0008](0008-deterministic-detection-and-alerting.md) | Deterministic detection and alerting; AI only for the nightly report | Accepted | 2026-10-03 |
| [0009](0009-remote-actions-as-signed-commands.md) | Remote actions only as signed commands from a fixed local action list | Accepted | 2026-10-03 |
| [0010](0010-tailscale-with-strict-acl.md) | Connection over Tailscale with a strict ACL | Accepted | 2026-10-03 |
| [0011](0011-own-go-web-ui-without-grafana.md) | Own Go web UI with historical views, no Grafana | Superseded by [0073](0073-backend-in-dotnet-10-with-blazor-agent-stays-go.md) | 2026-10-03 |
| [0012](0012-agent-never-contacts-telegram.md) | The agent never contacts Telegram itself | Accepted | 2026-10-03 |
| [0013](0013-mariadb-access-via-unix-socket-process-privilege.md) | MariaDB access through a unix_socket user with only the PROCESS privilege | Accepted | 2026-10-03 |
| [0014](0014-log-import-is-a-core-component.md) | Log import is a core component | Accepted | 2026-10-03 |
| [0015](0015-mail-services-checked-not-mail-accounts.md) | Mail services are checked, mail accounts are not | Accepted | 2026-10-03 |
| [0016](0016-web-ui-in-home-lan-with-login.md) | Web UI reachable in the home LAN with a login | Accepted | 2026-10-03 |
| [0017](0017-ingest-via-tailnet-address-and-published-port.md) | Ingest via the backend host's tailnet address and a published port, not tsnet | Accepted | 2026-10-03 |
| [0018](0018-agent-spools-seven-days-and-backfills.md) | The agent spools at least 7 days and backfills gaplessly and idempotently | Superseded by [0045](0045-batch-identified-by-agent-id-and-record-sequence-numbers.md) | 2026-10-03 |
| [0019](0019-agent-reads-proc-instead-of-top-lsof.md) | The agent reads /proc itself instead of running top or lsof | Accepted | 2026-10-03 |
| [0020](0020-analysis-before-alerting-forensics-release.md) | Analysis before alerting — v0.1.0 is the forensics release | Accepted | 2026-10-03 |
| [0021](0021-no-pseudonymization-of-log-data.md) | No pseudonymization of log data | Accepted | 2026-10-03 |
| [0022](0022-backfill-detection-and-live-only-alerts.md) | Backfill is recognized from the data; alerts only on live values | Accepted | 2026-10-03 |
| [0023](0023-tls-through-a-reverse-proxy.md) | TLS through a reverse proxy | Accepted | 2026-10-03 |
| [0024](0024-nightly-report-timing.md) | Nightly report at 06:00, or after the backfill if the backend host was off | Accepted | 2026-10-03 |
| [0025](0025-server-ram-stays-at-2-gb.md) | The server's RAM stays at 2 GB | Accepted | 2026-10-03 |
| [0026](0026-services-disabled-reversibly-only.md) | Services are only disabled reversibly; hosting-provider agents are never touched | Accepted | 2026-10-03 |
| [0027](0027-project-name-and-docker-image.md) | Project name Vandox; images on Docker Hub as networlddev/vandox | Accepted | 2026-10-03 |
| [0028](0028-data-gaps-are-always-recorded.md) | Data gaps are always recorded, never silent | Accepted | 2026-10-04 |
| [0029](0029-hanging-collector-never-blocks-the-agent.md) | A hanging collector or database never blocks the agent | Accepted | 2026-10-04 |
| [0030](0030-agent-runs-unprivileged-with-named-capabilities.md) | The agent runs as a dedicated user with only named rights, not as root | Accepted | 2026-10-04 |
| [0031](0031-telegram-user-allowlist.md) | The Telegram bot talks only to allowlisted users in private chats | Accepted | 2026-10-04 |
| [0032](0032-secrets-only-from-environment-or-docker-secrets.md) | Secrets only from environment variables or Docker secrets | Accepted | 2026-10-04 |
| [0033](0033-pre-existing-coverage-gap-accepted-for-issue-7.md) | Pre-existing overall coverage gap accepted for a documentation-only change | Accepted | 2026-10-04 |
| [0034](0034-entry-points-delegate-to-a-testable-run-function.md) | Entry points delegate to a testable run function; main stays uncovered wiring | Superseded by [0058](0058-vandoxd-runs-the-service-by-default-with-a-shutdown-deadline.md) | 2026-10-04 |
| [0035](0035-format-check-step-in-ci-coverage-gate-stays-local.md) | Explicit format check step in CI; the coverage gate stays local, SonarQube measures coverage in CI | Accepted | 2026-10-04 |
| [0036](0036-dependabot-docker-entry-before-the-dockerfile-exists.md) | Dependabot watches /deploy/backend for Docker before the Dockerfile exists | Superseded by [0041](0041-base-images-pinned-by-digest-through-build-arguments.md) | 2026-10-04 |
| [0037](0037-release-workflow-with-plain-go-docker-and-gh.md) | Release workflow built from plain go build, the Docker CLI and gh; verified once, published as built | Accepted | 2026-10-04 |
| [0038](0038-backend-image-distroless-nonroot-pinned-by-digest.md) | Backend image on distroless static, non-root, base images pinned by digest; version tags without "v" | Superseded by [0041](0041-base-images-pinned-by-digest-through-build-arguments.md) | 2026-10-04 |
| [0039](0039-docker-hub-token-in-a-tag-only-environment.md) | Docker Hub token is repository-scoped and lives in a tag-only GitHub environment | Accepted | 2026-10-04 |
| [0040](0040-go-1-27-toolchain-and-govulncheck-v1-8.md) | Go 1.27 toolchain without a patch version in go.mod; govulncheck raised to v1.8.0 | Accepted | 2026-10-04 |
| [0041](0041-base-images-pinned-by-digest-through-build-arguments.md) | Base images pinned by digest through build arguments, tag kept alongside; digests refreshed by hand | Accepted | 2026-10-04 |
| [0042](0042-wire-format-gzip-json-lines-standard-library.md) | Wire format is gzip-compressed JSON Lines, built on the standard library only | Accepted | 2026-10-04 |
| [0043](0043-wire-format-major-minor-versioning.md) | Wire format versioned by integer major and minor; unknown majors are rejected before parsing | Accepted | 2026-10-04 |
| [0044](0044-batch-validated-as-a-whole-agent-records-only.md) | A batch is valid only as a whole, carries only agent records and is bounded by format limits | Accepted | 2026-10-04 |
| [0045](0045-batch-identified-by-agent-id-and-record-sequence-numbers.md) | The agent spools at least 7 days and backfills; a batch is identified by the agent ID and its records' sequence numbers | Accepted | 2026-10-04 |
| [0046](0046-batch-header-describes-the-capture-context.md) | The batch header's boot ID and clock offset describe when the records were captured, not when they were sent | Accepted | 2026-10-04 |
| [0047](0047-gocognit-as-local-stand-in-for-sonar-cognitive-complexity.md) | gocognit at 15 as the local stand-in for SonarQube's cognitive complexity rule; two existing validators excluded by name | Accepted | 2026-10-04 |
| [0048](0048-yaml-library-go-yaml-in-yaml-v3.md) | go.yaml.in/yaml/v3 parses the configuration files, through a node tree, not direct decoding | Accepted | 2026-10-04 |
| [0049](0049-strict-configuration-file-schema-and-errors.md) | Strict configuration file: schema-only keys, no YAML extras, errors name file, line and key but never the value | Accepted | 2026-10-04 |
| [0050](0050-secret-sources-rules-and-redaction.md) | Secrets from VANDOX_* variables or *_FILE files, strict value rules, unknown VANDOX_ variables rejected, redacted type | Accepted | 2026-10-04 |
| [0051](0051-brand-assets-in-docs-assets-web-ui-and-telegram-with-their-features.md) | Brand assets live in docs/assets; the web UI and the Telegram bot adopt them with their own issues | Accepted | 2026-10-05 |
| [0052](0052-image-labels-description-added-no-logo-label.md) | Backend image gets a description label; no logo label | Accepted | 2026-10-05 |
| [0053](0053-releases-are-manual-and-started-only-by-a-version-tag.md) | Releases are always created manually; the only trigger is a new vX.Y.Z tag, the PR dry run lives in ci.yml | Accepted | 2026-10-05 |
| [0054](0054-release-provenance-attestations-from-a-secret-free-job.md) | Release binary and image digest get GitHub build provenance attestations from a separate, secret-free attest job; no SBOM yet | Accepted | 2026-10-05 |
| [0055](0055-stale-base-image-digests-reported-weekly-builder-go-checked-in-build.md) | Stale base image digests reported weekly as an issue; the build stage checks the builder's Go version against its tag | Superseded by [0080](0080-backend-image-on-the-chiseled-aspnet-runtime.md) | 2026-10-05 |
| [0056](0056-release-sboms-from-a-digest-pinned-syft-container.md) | Release SBOMs (SPDX 2.3) come from a digest-pinned, network-less syft container in the build job and are attested in the attest job | Accepted | 2026-10-05 |
| [0057](0057-sqlite-driver-modernc-pure-go.md) | modernc.org/sqlite as the SQLite driver: pure Go, no cgo, FTS5 included | Superseded by [0065](0065-sqlite-connections-single-writer-query-only-readers-synchronous-full.md) | 2026-10-05 |
| [0058](0058-vandoxd-runs-the-service-by-default-with-a-shutdown-deadline.md) | vandoxd runs the service without arguments, logs JSON with slog and stops within a 10 s deadline | Superseded by [0072](0072-vandoxd-import-sub-command-output-and-exit-codes.md) | 2026-10-05 |
| [0059](0059-healthz-checks-the-database-and-the-binary-is-the-health-probe.md) | /healthz on the web listener checks the database; vandoxd -healthcheck is the image's health probe | Accepted | 2026-10-05 |
| [0060](0060-compose-file-port-bindings-volumes-and-memory-limit.md) | Compose file publishes the web port on loopback and the ingest port not yet; named data volume, 512 MiB limit | Accepted | 2026-10-05 |
| [0061](0061-backend-only-packages-under-cmd-vandoxd-internal.md) | Backend-only packages live under cmd/vandoxd/internal; internal/ stays shared | Superseded by [0073](0073-backend-in-dotnet-10-with-blazor-agent-stays-go.md) | 2026-10-05 |
| [0062](0062-timeout-tests-use-synctest-or-injected-durations.md) | Timeout tests use testing/synctest without network, and injected short durations over loopback | Accepted | 2026-10-05 |
| [0063](0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md) | Storage schema: one records table holds every record's identity, metrics and log lines get own tables, other payloads are stored as JSON | Accepted | 2026-10-06 |
| [0064](0064-versioned-schema-migrations-in-go-one-transaction-per-step.md) | Versioned schema migrations in Go, applied at start-up in one transaction per step; a newer schema is refused | Superseded by [0077](0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md) | 2026-10-06 |
| [0065](0065-sqlite-connections-single-writer-query-only-readers-synchronous-full.md) | SQLite connections: modernc.org/sqlite kept; one writer connection with BEGIN IMMEDIATE, a query-only reader pool, synchronous FULL | Superseded by [0077](0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md) | 2026-10-06 |
| [0066](0066-log-search-takes-literal-terms-only.md) | Log search takes literal terms only; every term is quoted for FTS5, operators and prefixes are not offered yet | Accepted | 2026-10-06 |
| [0067](0067-storage-repository-interfaces-and-a-scripted-fake-in-storetest.md) | The store package defines small repository interfaces; storetest.Fake is a scripted fake, the store itself is tested against real files | Superseded by [0077](0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md) | 2026-10-06 |
| [0068](0068-write-throughput-measured-by-a-benchmark-ds918-measurement-in-a-follow-up.md) | Write throughput is measured by a Go benchmark and never asserted in tests; the DS918+ measurement is a follow-up issue | Superseded by [0077](0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md) | 2026-10-06 |
| [0069](0069-log-import-idempotent-per-file-content-hash-with-resumable-batches.md) | The log import is idempotent per file content: SHA-256 of the decompressed content, two passes, batches that resume by count | Accepted | 2026-10-06 |
| [0070](0070-log-parser-interface-and-explicit-registry-in-internal-logparse.md) | Log parsers implement one interface in internal/logparse; an explicit registry picks the parser by confidence | Superseded by [0079](0079-log-parsing-and-import-in-the-backend-without-following-links.md) | 2026-10-06 |
| [0071](0071-log-import-reads-input-without-following-links-or-extracting.md) | The log import reads its input as streams without following links or extracting anything; formats are sniffed, one gzip layer, no nested archives, fixed limits | Superseded by [0079](0079-log-parsing-and-import-in-the-backend-without-following-links.md) | 2026-10-06 |
| [0072](0072-vandoxd-import-sub-command-output-and-exit-codes.md) | vandoxd gets the sub-command import; progress as JSON on stderr, the summary as text on stdout, exit code 1 when a file failed | Accepted | 2026-10-06 |
| [0073](0073-backend-in-dotnet-10-with-blazor-agent-stays-go.md) | The backend is written in .NET 10 with a Blazor web UI; the agent stays in Go | Accepted | 2026-10-06 |
| [0074](0074-two-language-toolchain-and-combined-quality-gates.md) | Two-language toolchain: Go for the agent, .NET for the backend, one set of quality gates | Accepted | 2026-10-06 |
| [0075](0075-wire-contract-pinned-by-golden-fixtures.md) | The wire contract between the Go encoder and the C# decoder is pinned by golden fixtures | Accepted | 2026-10-06 |
| [0076](0076-strict-gzip-validation-in-the-backend.md) | The backend decodes gzip with strict validation switched on for every process | Accepted | 2026-10-06 |
| [0077](0077-storage-on-microsoft-data-sqlite-same-schema-and-rules.md) | Storage on Microsoft.Data.Sqlite with the unchanged schema, migrations and connection rules | Accepted | 2026-10-06 |
| [0078](0078-configuration-and-secrets-in-the-backend-with-yamldotnet.md) | The backend reads its strict configuration with YamlDotNet and the same secret rules | Accepted | 2026-10-06 |
| [0079](0079-log-parsing-and-import-in-the-backend-without-following-links.md) | Log parsing and import in the backend: statx/openat2 file access, same limits and guarantees | Accepted | 2026-10-06 |
| [0080](0080-backend-image-on-the-chiseled-aspnet-runtime.md) | The backend image runs on the chiseled ASP.NET runtime, built by the .NET SDK image, both pinned by digest | Accepted | 2026-10-06 |
| [0081](0081-backend-host-two-listeners-json-logs-blazor-interactive-server.md) | The backend host: two labelled Kestrel listeners, JSON logs, Blazor Interactive Server | Accepted | 2026-10-06 |
| [0082](0082-storage-writer-cached-parameters-and-synchronous-calls.md) | The storage writer sets cached parameters and calls SQLite synchronously after the first NAS measurement | Accepted | 2026-10-07 |
| [0083](0083-storage-write-criterion-is-the-median-of-five-batches-on-the-data-volume.md) | The storage write criterion is the median of five batches on the volume that holds the database | Accepted | 2026-10-07 |
<!-- project:end index -->

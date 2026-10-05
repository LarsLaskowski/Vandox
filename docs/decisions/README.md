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
| [0005](0005-go-for-agent-and-backend.md) | Go for agent and backend | Accepted | 2026-10-03 |
| [0006](0006-agent-connects-outbound-only.md) | The agent connects outbound only; commands are pulled | Accepted | 2026-10-03 |
| [0007](0007-sqlite-with-fts5-no-external-database.md) | SQLite with FTS5, no external database | Accepted | 2026-10-03 |
| [0008](0008-deterministic-detection-and-alerting.md) | Deterministic detection and alerting; AI only for the nightly report | Accepted | 2026-10-03 |
| [0009](0009-remote-actions-as-signed-commands.md) | Remote actions only as signed commands from a fixed local action list | Accepted | 2026-10-03 |
| [0010](0010-tailscale-with-strict-acl.md) | Connection over Tailscale with a strict ACL | Accepted | 2026-10-03 |
| [0011](0011-own-go-web-ui-without-grafana.md) | Own Go web UI with historical views, no Grafana | Accepted | 2026-10-03 |
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
| [0034](0034-entry-points-delegate-to-a-testable-run-function.md) | Entry points delegate to a testable run function; main stays uncovered wiring | Accepted | 2026-10-04 |
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
<!-- project:end index -->

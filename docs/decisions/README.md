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
| [0017](0017-ingest-via-tailnet-address-and-published-port.md) | Ingest via the NAS's tailnet address and a published port, not tsnet | Accepted | 2026-10-03 |
| [0018](0018-agent-spools-seven-days-and-backfills.md) | The agent spools at least 7 days and backfills gaplessly and idempotently | Accepted | 2026-10-03 |
| [0019](0019-agent-reads-proc-instead-of-top-lsof.md) | The agent reads /proc itself instead of running top or lsof | Accepted | 2026-10-03 |
| [0020](0020-analysis-before-alerting-forensics-release.md) | Analysis before alerting — v0.1.0 is the forensics release | Accepted | 2026-10-03 |
| [0021](0021-no-pseudonymization-of-log-data.md) | No pseudonymization of log data | Accepted | 2026-10-03 |
| [0022](0022-backfill-detection-and-live-only-alerts.md) | Backfill is recognized from the data; alerts only on live values | Accepted | 2026-10-03 |
| [0023](0023-tls-through-synology-reverse-proxy.md) | TLS through the Synology reverse proxy | Accepted | 2026-10-03 |
| [0024](0024-nightly-report-timing.md) | Nightly report at 06:00, or after the backfill if the NAS was off | Accepted | 2026-10-03 |
| [0025](0025-server-ram-stays-at-2-gb.md) | The server's RAM stays at 2 GB | Accepted | 2026-10-03 |
| [0026](0026-services-disabled-reversibly-only.md) | Services are only disabled reversibly; hosting-provider agents are never touched | Accepted | 2026-10-03 |
| [0027](0027-project-name-and-docker-image.md) | Project name Vandox; images on Docker Hub as networlddev/vandox | Accepted | 2026-10-03 |
<!-- project:end index -->

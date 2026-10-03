# Plan: Architecture document and decision records

Source: Issue #6
Status: Draft
Tier: trivial — documentation only (no production, test, build or config file), but the change consists
mainly of decision records in `docs/decisions/`, which `.squad/routing.md` excludes from tier `docs`.

## Problem / root cause

The planning decisions for Vandox exist only in the issue text; `docs/ARCHITECTURE.md` describes the
system in a few lines (one component diagram, no data-flow or network diagram, no offline behavior, no
storage, no monitored-server profile), and `docs/decisions/` holds only the three squad-process records
0001–0003.

Claims of the issue checked against the repository:

- "docs/decisions/ exists, index in README.md" — confirmed; records 0001–0003 are taken (squad process),
  so the 24 product decisions are numbered **0004–0027** in issue order (issue decision *n* → record
  `0003 + n`).
- Components, data flow, Tailscale, SQLite, Telegram — confirmed as the *planned* design only: the code
  today is `cmd/vandox-agent/main.go`, `cmd/vandoxd/main.go` (both only `--version`) and
  `internal/version`; `deploy/agent` and `deploy/backend` are empty. ARCHITECTURE.md must therefore say
  that it describes the target architecture of v0.1.0 and later, not implemented behavior.
- Existing ARCHITECTURE.md content (Tailscale only, login + Synology reverse proxy, secrets never logged)
  — consistent with the issue; nothing contradicts it.
- "images on Docker Hub as networlddev/vandox" — consistent with `sonar-project.properties`
  (organization `networlddev`); no workflow publishes an image yet.
- Dependency #5 (template adoption) — closed.
- Related observation (no defect): `docs/ARCHITECTURE.md` is a *marked* template file; only the
  `<!-- project:begin architecture -->` block may be edited, the *Development process* section outside it
  is the template's and stays unchanged.

## Acceptance criteria

No production code changes, so there are no unit tests; the Reviewer checks these against the diff.

- [ ] AC1: `docs/ARCHITECTURE.md` (inside the `project:architecture` block only) contains three Mermaid
      diagrams: components, data flow, network.
- [ ] AC2: It describes the components as listed in the issue: `vandox-agent` (Go, systemd service on the
      monitored server; metrics, process and network snapshots, service and MariaDB state, kernel events,
      logs; on-disk spool) and `vandoxd` (Go, one container on the NAS: ingest API, SQLite storage,
      analysis, rules, Telegram, reports, web UI), plus `internal/` shared packages.
- [ ] AC3: Data flow: agent → batches over Tailscale → ingest API → SQLite → analysis / rules / web UI /
      Telegram.
- [ ] AC4: Network: agent outbound only, over Tailscale, to the ingest port on the NAS's tailnet address;
      Tailscale ACL allows the server only that port; web UI in the home LAN with login; TLS terminated by
      the Synology reverse proxy; Telegram only from the backend.
- [ ] AC5: Offline behavior: NAS 24/7 but sometimes off at night (typically 22:00–09:00) a few times a
      year; agent keeps collecting, spools ≥ 7 days, sends current data first, then backfills
      chronologically and throttled; backend classifies every record as live or backfilled from capture
      time, receive time and sequence gaps, and alerts on live data only.
- [ ] AC6: Storage: SQLite with FTS5, WAL mode, retention table exactly as in the issue (raw 30 days,
      5-minute rollups 1 year, hourly 3 years, incidents unlimited, logs 90 days, process/connection
      snapshots 30 days).
- [ ] AC7: Monitored server: Ubuntu 22.04, Plesk 18, about 2 GB RAM; failure mode memory exhaustion → OOM
      killer → MariaDB, Plesk and mail down.
- [ ] AC8: Every section links the decision records it rests on (by relative link `decisions/NNNN-….md`).
- [ ] AC9: 24 records `docs/decisions/0004-…` to `0027-…` exist, one per issue decision, each with
      Context, Options considered, Decision, Consequences; records 0001–0003 unchanged.
- [ ] AC10: At approval all 24 are `Accepted` and listed in the index of `docs/decisions/README.md`
      (inside the `project:index` block) — done by the Lead in step 9 (approval edit).
- [ ] AC11: The existing statements of ARCHITECTURE.md (configuration not implemented, secrets never
      logged, deployment as binary + Docker image, link to CONTRIBUTING) are preserved, and the text
      outside the project block is byte-identical.

## Approach

1. Lead (done in this step): records 0004–0027 as `Proposed`, from `docs/decisions/_template.md`.
2. Dev: rewrite the content of the `project:architecture` block of `docs/ARCHITECTURE.md` with these
   sections, in this order:
   - Intro paragraph (keep) plus one sentence: "This document describes the target architecture; as of
     now only the two binaries' `--version` exist, and sections are marked as implemented as features
     land."
   - **Monitored server** — AC7; link 0025, 0026.
   - **Components** — AC2; Mermaid `flowchart` of agent (collectors, spool, sender), backend (ingest API,
     SQLite, analysis, rules, reports, web UI, Telegram notifier) and `internal/`; link 0004, 0005, 0011,
     0013, 0014, 0015, 0019, 0027.
   - **Data flow** (replaces *Main flow*) — AC3; Mermaid `flowchart` or `sequenceDiagram`; link 0006,
     0007, 0008, 0012, 0020, 0024.
   - **Network** — AC4; Mermaid `flowchart` with subgraphs *Internet/server*, *Tailnet*, *Home LAN*
     (server → NAS tailnet address:ingest port; LAN browser → Synology reverse proxy (TLS) → web UI;
     backend → Telegram API); link 0010, 0016, 0017, 0023.
   - **Offline behavior and backfill** — AC5; link 0018, 0022, 0024.
   - **Storage and retention** — AC6 as a table; link 0007, 0021.
   - **Remote actions (later)** — signed commands from a fixed local action list, pulled by the agent,
     after confirmation; link 0006, 0009.
   - **Configuration**, **Security model**, **Deployment** — keep the current text; Security model adds
     outbound-only, ACL, Telegram token only on the NAS, MariaDB `unix_socket` user with `PROCESS` only,
     no pseudonymization (link 0013, 0021); Deployment adds the image name `networlddev/vandox` (0027).
   Port numbers, the live/backfill threshold and spool size limits are **not** invented: they are left
   to the features.
3. Lead in step 9: set 0004–0027 to `Accepted` (adjusting any record the review changed) and add 24 rows
   to the index (title = record heading without the number, status Accepted, date of approval).

## Affected projects and types

| Project | Type / file | Change |
| ------- | ----------- | ------ |
| docs | `docs/ARCHITECTURE.md` (`project:architecture` block) | rewritten as above (Dev) |
| docs | `docs/decisions/0004-…md` – `0027-…md` | new records (Lead) |
| docs | `docs/decisions/README.md` (`project:index` block) | 24 index rows (Lead, step 9) |

## Signatures (for the Dev's skeleton)

None.

## Test files

None — no production code changes. Existing test code calling a changed signature: none.

## Documentation updates

`docs/ARCHITECTURE.md` as described (Dev). `README.md`, `SECURITY.md`, `docs/CONTRIBUTING.md`: none —
checked, they are consistent with the records. `.squad/project.md` *Guarantees*: unchanged, because its
text says guarantees are added "as the features land" and none has landed; the first feature that
implements spool/backfill or live-only alerting adds them there.

## Architecture check

No guarantee is weakened; the document gains the planned guarantees (gapless spool/backfill, live-only
alerting, outbound-only agent). The template part of ARCHITECTURE.md is untouched.

## Security considerations

Documentation only. The records describe security-relevant design (ACL, outbound-only, signed commands,
`unix_socket` MariaDB user, no pseudonymization); they fix no secrets, hosts, tailnet addresses or ports,
and must not — the Reviewer checks that no real address, hostname or token appears.

## Decision records

All `Proposed`, Source Issue #6:

- `docs/decisions/0004-own-project-instead-of-off-the-shelf-stack.md`
- `docs/decisions/0005-go-for-agent-and-backend.md`
- `docs/decisions/0006-agent-connects-outbound-only.md`
- `docs/decisions/0007-sqlite-with-fts5-no-external-database.md`
- `docs/decisions/0008-deterministic-detection-and-alerting.md`
- `docs/decisions/0009-remote-actions-as-signed-commands.md`
- `docs/decisions/0010-tailscale-with-strict-acl.md`
- `docs/decisions/0011-own-go-web-ui-without-grafana.md`
- `docs/decisions/0012-agent-never-contacts-telegram.md`
- `docs/decisions/0013-mariadb-access-via-unix-socket-process-privilege.md`
- `docs/decisions/0014-log-import-is-a-core-component.md`
- `docs/decisions/0015-mail-services-checked-not-mail-accounts.md`
- `docs/decisions/0016-web-ui-in-home-lan-with-login.md`
- `docs/decisions/0017-ingest-via-tailnet-address-and-published-port.md`
- `docs/decisions/0018-agent-spools-seven-days-and-backfills.md`
- `docs/decisions/0019-agent-reads-proc-instead-of-top-lsof.md`
- `docs/decisions/0020-analysis-before-alerting-forensics-release.md`
- `docs/decisions/0021-no-pseudonymization-of-log-data.md`
- `docs/decisions/0022-backfill-detection-and-live-only-alerts.md`
- `docs/decisions/0023-tls-through-synology-reverse-proxy.md`
- `docs/decisions/0024-nightly-report-timing.md`
- `docs/decisions/0025-server-ram-stays-at-2-gb.md`
- `docs/decisions/0026-services-disabled-reversibly-only.md`
- `docs/decisions/0027-project-name-and-docker-image.md`

The *Options considered* in these records reconstruct the alternatives implied by the issue's planning
decisions; the Product Manager may correct them in review.

## Out of scope / follow-ups

- Guarantees in `.squad/project.md` (added with the features that implement them).
- Ports, spool size bound, live/backfill threshold, report content — decided in the feature issues.

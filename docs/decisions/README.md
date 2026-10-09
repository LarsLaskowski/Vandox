# Decision records

Why the code is the way it is. Each file records one decision — its context, the options considered, what
was chosen and the consequences — so that months later the reasoning is still available without the
pull request, the issue thread or the session that produced it.

A record explains the *why*. What holds today — formats, limits, error behavior, guarantees — is written down
once in the [area document](../areas/README.md) of the record's area, and the record links to it.
`docs/ARCHITECTURE.md` describes how the parts fit together. When a decision changes the architecture,
`ARCHITECTURE.md` is updated as well and links the record.

## Rules

- One decision per file: `NNNN-short-title.md` (four digits, next free number), created from
  [`_template.md`](_template.md).
- **The record states the decision in one to three sentences and does not restate behavior.** Context,
  *Options considered* and *Consequences* carry the reasoning; the rules of the resulting behavior (values,
  formats, limits, error texts) belong in the area document and are linked from the record.
- The `Area:` field names the record's area as listed in [`docs/areas/README.md`](../areas/README.md), or `—`
  for a record about the process or about no area. Once areas are defined, every record needs the field. For a
  released record it is part of the content: it changes only through a new record.
- Written by the squad Lead (see [`.squad/agents/lead/charter.md`](../../.squad/agents/lead/charter.md));
  anyone may add one for a change made outside the squad.
- Committed together with the change it explains.
- **Released or not decides how a record changes.** A record is *released* once the commit that added it is
  contained in a release tag (`v*`; check with `git tag --contains <commit>`, the commit is
  `git log --diff-filter=A --format=%H -- <file>`). Until the first release nothing is released.
- A record that is **not yet released** is edited in place, in the pull request that changes the decision
  (status stays `Proposed` or `Accepted`). Its history is Git and the pull request; there is no
  `Superseded by` chain for it. A record that no longer applies is deleted (numbers are never reused, gaps are
  fine) together with its index row and every link to it.
- A **released** record is **append-only**: it is never rewritten. A changed decision gets a new record that
  names the old one under *Supersedes*, and the old record's status becomes `Superseded by NNNN` (the only
  edit allowed). `Superseded by` is therefore only ever set on a released record.
- A new record that **amends** a released record without superseding it names the older one under
  *Supersedes* and says there, in one sentence, what it amends. The older record is **not edited** — not even
  with a pointer; a reader of the older record finds the amendment through the index and the new record.
- **One record per decision that has lasting weight, grouped by topic — not one per pull request.** Not for
  routine changes: a record is needed when a choice between real alternatives was made that shapes the
  architecture or behavior, a trade-off or limitation was accepted, or a documented guarantee was touched.
  Before adding a record, look for an unreleased record on the same topic and extend it instead. A review
  finding that was deliberately not fixed, a follow-up split, or a dependency change gets its own record only
  if it touches a guarantee or the architecture; otherwise it belongs in the pull request description or the
  follow-up issue, or as a sentence in the record of the topic it concerns.
- `.squad/tools/decision-check.py` (also run by `config-check.py`) checks the index, the links between records
  and that no released record was changed.

## Index

<!-- project:begin index -->
| #    | Title | Status | Date |
| ---- | ----- | ------ | ---- |
| 0001 | Quality gates before the pull request | Accepted | 2026-10-03 |
| 0002 | Squad working records stay off main, and product PRs never change the squad | Accepted | 2026-10-03 |
| 0003 | Squash-merge pull requests | Accepted | 2026-10-03 |
| 0004 | Own project instead of an off-the-shelf monitoring stack | Accepted | 2026-10-03 |
| 0006 | The agent connects outbound only; commands are pulled | Accepted | 2026-10-03 |
| 0007 | SQLite with FTS5, no external database | Accepted | 2026-10-03 |
| 0008 | Deterministic detection and alerting; AI only for the nightly report | Accepted | 2026-10-03 |
| 0009 | Remote actions only as signed commands from a fixed local action list | Accepted | 2026-10-03 |
| 0010 | Connection over Tailscale with a strict ACL | Accepted | 2026-10-03 |
| 0012 | The agent never contacts Telegram itself | Accepted | 2026-10-03 |
| 0013 | MariaDB access through a unix_socket user with only the PROCESS privilege | Accepted | 2026-10-03 |
| 0014 | Log import is a core component | Accepted | 2026-10-03 |
| 0015 | Mail services are checked, mail accounts are not | Accepted | 2026-10-03 |
| 0016 | Web UI reachable in the home LAN with a login | Accepted | 2026-10-03 |
| 0017 | Ingest via the backend host's tailnet address and a published port, not tsnet | Accepted | 2026-10-03 |
| 0019 | The agent reads /proc itself instead of running top or lsof | Accepted | 2026-10-03 |
| 0020 | Analysis before alerting — v0.1.0 is the forensics release | Accepted | 2026-10-03 |
| 0021 | No pseudonymization of log data | Accepted | 2026-10-03 |
| 0022 | Backfill is recognized from the data; alerts only on live values | Accepted | 2026-10-03 |
| 0023 | TLS through a reverse proxy | Accepted | 2026-10-03 |
| 0024 | Nightly report at 06:00, or after the backfill if the backend host was off | Accepted | 2026-10-03 |
| 0025 | The server's RAM stays at 2 GB | Accepted | 2026-10-03 |
| 0026 | Services are only disabled reversibly; hosting-provider agents are never touched | Accepted | 2026-10-03 |
| 0027 | Project name Vandox; images on Docker Hub as networlddev/vandox | Accepted | 2026-10-03 |
| 0028 | Data gaps are always recorded, never silent | Accepted | 2026-10-04 |
| 0029 | A hanging collector or database never blocks the agent | Accepted | 2026-10-04 |
| 0030 | The agent runs as a dedicated user with only named rights, not as root | Accepted | 2026-10-04 |
| 0031 | The Telegram bot talks only to allowlisted users in private chats | Accepted | 2026-10-04 |
| 0032 | Secrets only from environment variables or Docker secrets | Accepted | 2026-10-04 |
| 0033 | Pre-existing overall coverage gap accepted for a documentation-only change | Accepted | 2026-10-04 |
| 0035 | Explicit format check step in CI; the coverage gate stays local, SonarQube measures coverage in CI | Accepted | 2026-10-04 |
| 0037 | Release workflow built from plain go build, the Docker CLI and gh; verified once, published as built | Accepted | 2026-10-04 |
| 0039 | Docker Hub token is repository-scoped and lives in a tag-only GitHub environment | Accepted | 2026-10-04 |
| 0040 | Go 1.27 toolchain without a patch version in go.mod; govulncheck raised to v1.8.0 | Accepted | 2026-10-04 |
| 0041 | Backend image on the chiseled ASP.NET runtime; base images pinned by digest through build arguments, refreshed by hand, staleness reported weekly | Accepted | 2026-10-04 |
| 0042 | Wire format is gzip-compressed JSON Lines, built on the standard library only | Accepted | 2026-10-04 |
| 0043 | Wire format versioned by integer major and minor; unknown majors are rejected before parsing | Accepted | 2026-10-04 |
| 0044 | A batch is valid only as a whole, carries only agent records and is bounded by format limits | Accepted | 2026-10-04 |
| 0045 | The agent spools at least 7 days and backfills; a batch is identified by the agent ID and its records' sequence numbers | Accepted | 2026-10-04 |
| 0046 | The batch header's boot ID and clock offset describe when the records were captured, not when they were sent | Accepted | 2026-10-04 |
| 0047 | gocognit at 15 as the local stand-in for SonarQube's cognitive complexity rule; two existing validators excluded by name | Accepted | 2026-10-04 |
| 0048 | go.yaml.in/yaml/v3 parses the configuration files, through a node tree, not direct decoding | Accepted | 2026-10-04 |
| 0049 | Strict configuration file: schema-only keys, no YAML extras, errors name file, line and key but never the value | Accepted | 2026-10-04 |
| 0050 | Secrets from VANDOX_* variables or *_FILE files, strict value rules, unknown VANDOX_ variables rejected, redacted type | Accepted | 2026-10-04 |
| 0051 | Brand assets live in docs/assets; the web UI and the Telegram bot adopt them with their own issues | Accepted | 2026-10-05 |
| 0052 | Backend image gets a description label; no logo label | Accepted | 2026-10-05 |
| 0053 | Releases are always created manually; the only trigger is a new vX.Y.Z tag, the PR dry run lives in ci.yml | Accepted | 2026-10-05 |
| 0054 | Release binary and image digest get GitHub build provenance attestations from a separate, secret-free attest job; no SBOM yet | Accepted | 2026-10-05 |
| 0056 | Release SBOMs (SPDX 2.3) come from a digest-pinned, network-less syft container in the build job and are attested in the attest job | Accepted | 2026-10-05 |
| 0059 | /healthz on the web listener checks the database; vandoxd -healthcheck is the image's health probe | Accepted | 2026-10-05 |
| 0060 | Compose file publishes the web port on loopback and the ingest port not yet; named data volume, 512 MiB limit | Accepted | 2026-10-05 |
| 0062 | Timeout tests use testing/synctest without network, and injected short durations over loopback | Accepted | 2026-10-05 |
| 0063 | Storage schema: one records table holds every record's identity, metrics and log lines get own tables, other payloads are stored as JSON | Accepted | 2026-10-06 |
| 0066 | Log search takes literal terms only; every term is quoted for FTS5, operators and prefixes are not offered yet | Accepted | 2026-10-06 |
| 0069 | The log import is idempotent per file content: SHA-256 of the decompressed content, two passes, batches that resume by count | Accepted | 2026-10-06 |
| 0072 | vandoxd gets the sub-command import; progress as JSON on stderr, the summary as text on stdout, exit code 1 when a file failed | Accepted | 2026-10-06 |
| 0073 | The backend is written in .NET 10 with a Blazor web UI; the agent stays in Go | Accepted | 2026-10-06 |
| 0074 | Two-language toolchain: Go for the agent, .NET for the backend, one set of quality gates | Accepted | 2026-10-06 |
| 0075 | The wire contract between the Go encoder and the C# decoder is pinned by golden fixtures | Accepted | 2026-10-06 |
| 0076 | The backend decodes gzip with strict validation switched on for every process | Accepted | 2026-10-06 |
| 0077 | Storage on Microsoft.Data.Sqlite with the unchanged schema, migrations and connection rules | Accepted | 2026-10-06 |
| 0078 | The backend reads its strict configuration with YamlDotNet and the same secret rules | Accepted | 2026-10-06 |
| 0079 | Log parsing and import in the backend: statx/openat2 file access, same limits and guarantees | Accepted | 2026-10-06 |
| 0081 | The backend host: two labelled Kestrel listeners, JSON logs, Blazor Interactive Server | Accepted | 2026-10-06 |
| 0082 | The storage writer sets cached parameters and calls SQLite synchronously after the first NAS measurement | Accepted | 2026-10-07 |
| 0083 | The storage write criterion is the median of five batches on the volume that holds the database | Accepted | 2026-10-07 |
<!-- project:end index -->

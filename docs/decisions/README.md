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
  [`_template.md`](_template.md). In the index row and in a `Superseded by NNNN` status the number may be
  written as a link, `[NNNN](NNNN-short-title.md)`.
- **The record states the decision in one to three sentences and does not restate behavior.** Context,
  *Options considered* and *Consequences* carry the reasoning; the rules of the resulting behavior (values,
  formats, limits, error texts) belong in the area document and are linked from the record.
- The `Area:` field names the record's area as listed in [`docs/areas/README.md`](../areas/README.md), or `—`
  for a record about the process or about no area. Once areas are defined, every record needs the field. For a
  released record it is part of the content: it changes only through a new record. A released record without
  the field gets its area from an `## Area assignment` table (`| Record | Area |`) in a new record; the
  released record itself stays unedited.
- Written by the squad Lead (see [`.claude/agents/squad-lead.md`](../../.claude/agents/squad-lead.md));
  anyone may add one for a change made outside the squad.
- The squad's own process decisions are no records: they stand in [`process.md`](process.md), whose introduction
  says how they change.
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
- **One record per decision that has lasting weight — not one per pull request, and not one per topic.** Not for
  routine changes: a record is needed when a choice between real alternatives was made that shapes the
  architecture or behavior, a trade-off or limitation was accepted, or a documented guarantee was touched.
  Before adding a record, look for an unreleased record of the same decision and extend it instead (a changed
  option, a new consequence); another decision on a topic that already has a record gets a record of its own,
  which links the other. A review finding that was deliberately not fixed, a follow-up split, or a dependency
  change gets its own record only if it touches a guarantee or the architecture; otherwise it belongs in the pull
  request description or the follow-up issue, or as a sentence in the record of the decision it concerns.
- **Size.** A record that an extension would take past `RECORD_MAX_WORDS` words or `RECORD_MAX_OPTIONS` options
  (`.squad/tools/squad_settings.py`) holds more than one decision and is split instead: one record per decision,
  each with its own context, options and consequences. An unreleased record keeps its number for one of them, the
  others take the next free numbers with their index rows, and every link to it points at the record of its
  decision; a released record is never split.
- `.squad/tools/decision-check.py` (also run by `config-check.py`) checks the index, the links between records
  and that no released record was changed, and warns about an unreleased record past the size limits and an area
  document past `AREA_MAX_WORDS`, naming the split.

## Index

<!-- project:begin index -->
| #    | Title | Status | Date |
| ---- | ----- | ------ | ---- |
| 0001 | Quality gates run locally before the pull request; CI builds, tests, checks the format and runs the code analysis | Superseded by [0135](0135-the-squads-process-decisions-live-in-process-md.md) | 2026-10-03 |
| 0002 | Squad working records stay off main, and product PRs never change the squad | Superseded by [0135](0135-the-squads-process-decisions-live-in-process-md.md) | 2026-10-03 |
| 0003 | Squash-merge pull requests | Superseded by [0135](0135-the-squads-process-decisions-live-in-process-md.md) | 2026-10-03 |
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
| 0037 | Releases: a published release with a version tag only, plain tooling, tag-only token environment, secret-free attestation job, digest-pinned SBOM generator | Accepted | 2026-10-04 |
| 0040 | Go 1.27 toolchain; `go` directive without a patch version; govulncheck raised to v1.8.0 | Accepted | 2026-10-04 |
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
| 0059 | /healthz on the web listener checks the database; vandoxd -healthcheck is the image's health probe | Accepted | 2026-10-05 |
| 0060 | Compose file publishes the web port on loopback and the ingest port not yet; named data volume, 512 MiB limit | Accepted | 2026-10-05 |
| 0062 | Timeout tests use testing/synctest without network, and injected short durations over loopback | Accepted | 2026-10-05 |
| 0063 | Storage schema: one records table holds every record's identity, metrics and log lines get own tables, other payloads are stored as JSON | Accepted | 2026-10-06 |
| 0066 | Log search takes literal terms only; every term is quoted for FTS5, operators and prefixes are not offered yet | Accepted | 2026-10-06 |
| 0069 | The log import is idempotent per file content: SHA-256 of the decompressed content, two passes, batches that resume by count, a grown file continued after its imported start, an older copy recognized by the line-end hashes of the imported one | Accepted | 2026-10-11 |
| 0072 | vandoxd gets the sub-command import; progress as JSON on stderr, the summary as text on stdout, exit code 1 when a file failed | Accepted | 2026-10-06 |
| 0073 | The backend is written in .NET 10 with a Blazor web UI; the agent stays in Go | Superseded by 0197 | 2026-10-06 |
| 0074 | Two-language toolchain: Go for the agent, .NET for the backend, one set of quality gates | Superseded by [0203](0203-two-language-toolchain-on-the-templates-shared-dotnet-rules.md) | 2026-10-06 |
| 0075 | The wire contract between the Go encoder and the C# decoder is pinned by golden fixtures | Accepted | 2026-10-06 |
| 0076 | The backend decodes gzip with strict validation switched on for every process | Accepted | 2026-10-06 |
| 0077 | Storage on Microsoft.Data.Sqlite with the unchanged schema, migrations and connection rules | Accepted | 2026-10-06 |
| 0078 | The backend reads its strict configuration with YamlDotNet and the same secret rules | Accepted | 2026-10-06 |
| 0079 | Log parsing and import in the backend: statx/openat2 file access, same limits and guarantees | Accepted | 2026-10-06 |
| 0081 | The backend host: two labelled Kestrel listeners, JSON logs, Blazor Interactive Server | Accepted | 2026-10-06 |
| 0083 | The storage writer is cheap, and its write criterion is the median of five batches on the volume that holds the database | Accepted | 2026-10-07 |
| 0084 | The log_line record gets an optional host field in both languages and in storage | Accepted | 2026-10-09 |
| 0085 | Year-less syslog times need the option import.time_zone, without a default, resolved with NodaTime's embedded zone database, and take the year from the file's rotation date or modification time | Accepted | 2026-10-09 |
| 0086 | System log parsers: journal export by content, a generic weak syslog parser, kernel reports grouped with head and tail | Accepted | 2026-10-09 |
| 0087 | C# style rules follow the standard settings, adapted to Vandox's analyzers and Linux tooling | Accepted | 2026-10-10 |
| 0088 | MariaDB error log: entries detected by content and kept with their continuation lines, lifecycle events in an optional log_line field; the same rules for MariaDB lines of the journal and syslog | Accepted | 2026-10-10 |
| 0089 | A JSON null in a batch reads as an absent key in both decoders | Accepted | 2026-10-10 |
| 0090 | Both wire decoders check every line for valid UTF-8, paired surrogate escapes, ASCII and unique keys and bounded nesting, match keys ignoring ASCII case, and report a misshapen header as malformed | Accepted | 2026-10-10 |
| 0091 | Postfix and Dovecot lines are classified by the daemon's severity marker, with events for fatal, panic and an unavailable database: in the syslog and journal parsers, and in a parser of its own for Dovecot's log file | Accepted | 2026-10-11 |
| 0092 | Plesk panel, sw-cp-server, nginx and Apache error logs are claimed by a configured path or by content; lines they cannot read are kept as raw records with a warning | Accepted | 2026-10-11 |
| 0093 | The legacy top/lsof monitoring log is read snapshot by snapshot, timed by the zone abbreviation of its header within import.time_zone, and a snapshot whose top output is broken is skipped whole | Accepted | 2026-10-11 |
| 0094 | Log signatures are data: an embedded YAML catalog of linear-time patterns, matched on stored log lines on demand, with the copies of one event merged | Accepted | 2026-10-11 |
| 0095 | Log templates: fixed token rules, statistics kept by the write path per source and program, the usual level from the hours the source logged | Accepted | 2026-10-12 |
| 0096 | Outage incidents are rebuilt from the stored data by `vandoxd incidents`: OOM-kill windows merged by overlap, an incident's identity kept by overlap, a structured cause hypothesis from fixed rules | Accepted | 2026-10-12 |
| 0097 | A cancelled token interrupts a running reader statement through a progress handler; the interruption is an OperationCanceledException | Accepted | 2026-10-12 |
| 0098 | The web UI serves only its own static web assets: brand copies checked against docs/assets, uPlot vendored, theme and reconnect without inline code | Accepted | 2026-10-12 |
| 0099 | The web listener sends one strict Content-Security-Policy with self-only sources and frame-ancestors 'none', set by the application | Accepted | 2026-10-12 |
| 0100 | The forensics report is written as Markdown by `vandoxd report`, computed on demand in bounded passes; the web page is its own record (0108) | Accepted | 2026-10-13 |
| 0101 | Recurrence and correlation rules compare the share of outages with the share of observed system-log hours and decide by a binomial tail | Accepted | 2026-10-13 |
| 0102 | The report language comes from the flag `-lang` (`en` by default, or `de`); the texts are tables in code, not .NET resources or cultures | Accepted | 2026-10-13 |
| 0103 | Web login: one password, server-side sessions in memory, a static login page, live connections bound to the session | Accepted | 2026-10-13 |
| 0104 | The password hash is argon2id in strict PHC form within bounds, verified with Konscious.Security.Cryptography.Argon2; `vandoxd hash-password` creates it | Accepted | 2026-10-13 |
| 0105 | Forwarded headers only from `web.trusted_proxies`; failed sign-ins locked per client and capped overall; the loopback binding stays defense in depth | Accepted | 2026-10-13 |
| 0106 | The logs view takes every filter from the address, searches in bounded pages from the index or by a time-ordered scan, and finds a template's lines by normalizing candidates | Accepted | 2026-10-13 |
| 0107 | The incidents view reads bounded summaries and the window's own records; notes are rows per incident number that follow merges, saved by a form post with the antiforgery token; every time links to the logs of that moment | Accepted | 2026-10-14 |
| 0108 | The report page renders the blocks the Markdown is written from, lists the outages in pages of 50 by a new run, takes the language from the address, and runs one report at a time under a deadline of 2 minutes; the Markdown is a GET download | Accepted | 2026-10-15 |
| 0110 | The agent's runtime frame: one runner per collector at a fixed rate, a gap for every missed sample | Accepted | 2026-10-17 |
| 0111 | System metrics: three collectors read /proc, /sys, statfs and adjtimex, with no program, D-Bus or socket | Accepted | 2026-10-17 |
| 0112 | gosec G115 as the local stand-in for CodeQL's integer-conversion query; enabled for the whole module, no exclusions | Accepted | 2026-10-17 |
| 0113 | Process collector: PSS from smaps_rollup, top processes by memory and CPU with every program summed, a full snapshot under memory pressure | Accepted | 2026-10-17 |
| 0114 | The Go model measures text limits as the encoder writes them, invalid UTF-8 counted as its replacement | Accepted | 2026-10-18 |
| 0115 | Connection collector: the /proc/net tables, regular and full snapshots in one record kind | Accepted | 2026-10-18 |
| 0116 | New listeners across an agent start are compared by the storage layer when the snapshots are stored | Accepted | 2026-10-18 |
| 0117 | systemd collector: a hand-written D-Bus client with one connection per run | Accepted | 2026-10-20 |
| 0118 | MariaDB collector: a hand-written client over the local socket that sends only the captured statements | Accepted | 2026-10-21 |
| 0119 | Kernel messages from the binary journal files, read in pure Go without decompression | Accepted | 2026-10-22 |
| 0120 | A live OOM kill triggers extra process and connection snapshots through the scheduler, linked by the kill's identity | Accepted | 2026-10-22 |
| 0121 | Log shipping decodes zstd journal items with a bounded library decoder | Accepted | 2026-10-31 |
| 0122 | CAP_SYS_PTRACE alone cannot write another user's process memory through `/proc/<pid>/mem` or `/proc/<pid>/fd`: CAP_DAC_READ_SEARCH dropped, `process_madvise` denied, a file-mode residual accepted | Accepted | 2026-10-23 |
| 0123 | Agent spool in uncompressed segment files with checksummed frames and a bounded file count | Accepted | 2026-10-23 |
| 0124 | Ingest request: one batch per POST, bearer token, acknowledged by the status alone | Accepted | 2026-10-24 |
| 0125 | Sender with a live and a backfill stream over one shared backoff | Accepted | 2026-10-24 |
| 0126 | The ingest token is bound to one configured agent ID | Accepted | 2026-10-25 |
| 0127 | The backend host needs Tailscale in TUN mode; the ingest port is published on a required bind address | Accepted | 2026-10-25 |
| 0128 | The C# decoder reads the gzip framing itself, so trailing bytes and header rules match the Go decoder | Accepted | 2026-10-25 |
| 0129 | Configuration and secret files are opened without blocking and their type is checked on the opened file | Accepted | 2026-10-25 |
| 0130 | Live or backfilled is decided at write time | Accepted | 2026-10-26 |
| 0131 | The agent's install scripts, unit and files reach the server as an install bundle released with the binary | Accepted | 2026-10-27 |
| 0132 | Tailnet policy documented as console settings, without a default grant for maintainer devices, with the token's exposure while Tailscale is down split off | Accepted | 2026-11-05 |
| 0133 | Agent footprint measured on the server as peak RSS and CPU time of one core | Accepted | 2026-10-30 |
| 0134 | The sender binds every non-loopback connection to the tailnet device, so the token never leaves through another interface | Accepted | 2026-11-04 |
| 0135 | The squad's process decisions live in process.md | Accepted | 2026-11-06 |
| 0136 | systemd notification by hand, with a watchdog fed only while every collector runner is on time | Accepted | 2026-10-17 |
| 0137 | The agent logs text lines on standard error with the journal's priority prefix, cut below the journal's line limit | Accepted | 2026-10-17 |
| 0138 | The clock counts as synchronized below 16 seconds of maximum error; the offset is the kernel PLL's, only while the PLL is on | Accepted | 2026-10-17 |
| 0139 | A system file that cannot be read fails the whole sample; a measurement the host does not offer is omitted and logged once | Accepted | 2026-10-17 |
| 0140 | A null element in a list option of the agent's configuration is refused, never dropped | Accepted | 2026-10-17 |
| 0141 | The memory pressure mode is switched by the system collector with fixed, hysteretic thresholds | Accepted | 2026-10-17 |
| 0142 | Process and program names follow the legacy derivation, after configurable literal grouping rules | Accepted | 2026-10-17 |
| 0143 | New listeners are found against the last recorded inventory, bounded, with the privileged ports first | Accepted | 2026-10-18 |
| 0144 | Socket owners come from /proc/<pid>/fdinfo on the socket mount the agent learns at its start | Accepted | 2026-10-18 |
| 0145 | The owner scan accepts the first fdinfo lines in the range the kernel prints them | Accepted | 2026-10-18 |
| 0146 | The socket's UID comes from the tables' uid column, with the lowest UID and a mixed flag per endpoint | Accepted | 2026-10-18 |
| 0147 | A known endpoint whose socket UID changes is reported as a changed listener | Accepted | 2026-10-18 |
| 0148 | The agent marks its first connection snapshot after a start and states what each snapshot's listener list holds | Accepted | 2026-10-18 |
| 0149 | The report's New listeners section caps each kind of finding separately and lists changed endpoints without the command | Accepted | 2026-10-18 |
| 0150 | Service unit states are polled and recorded on change, after a start or a lost sample, and hourly | Accepted | 2026-10-20 |
| 0151 | A MariaDB login the server refuses is recorded as not_answering with its error number | Accepted | 2026-10-21 |
| 0152 | MariaDB's exposure is counted from the kernel's TCP tables, not from bind_address | Accepted | 2026-10-21 |
| 0153 | An OOM kill is timed by its receipt in the journal, corrected for the kernel clock's drift | Accepted | 2026-10-22 |
| 0154 | The journal reader reads the COMPACT layout with the same code, chosen per file from the header's flag | Accepted | 2026-10-22 |
| 0155 | The kernel collector commits its journal position only after a kept run, checks its own records and caps the re-reads | Accepted | 2026-10-22 |
| 0156 | The agent keeps one state file in the spool directory, written by a state job | Accepted | 2026-10-22 |
| 0157 | A boot is recognized from the saved boot ID, and the previous uptime is the one the agent saved | Accepted | 2026-10-22 |
| 0158 | Backup runs are a record kind of their own, recognized by process rules taken from a captured Plesk backup | Accepted | 2026-10-22 |
| 0159 | A boot and a backup run's change reach the agent's state only once the scheduler reports their run kept | Accepted | 2026-10-22 |
| 0160 | Log shipping keeps a journal position per seqnum ID | Accepted | 2026-10-31 |
| 0161 | Log shipping commits its positions when the scheduler keeps a run | Accepted | 2026-10-31 |
| 0162 | Configured log files are opened through a link-free walk | Accepted | 2026-10-31 |
| 0163 | The state file keys a configured log file by the SHA-256 of its path | Accepted | 2026-10-31 |
| 0164 | Error logs are shipped with an operator-named format and a Go port of the import's grammars | Accepted | 2026-10-31 |
| 0165 | Error logs the agent cannot read get a documented grant by hand, not a new right | Accepted | 2026-10-31 |
| 0166 | A log line dated after its run's start never moves the file's bookkeeping times | Accepted | 2026-10-31 |
| 0167 | Live log lines are classified by a Go port of the import's stages in the agent | Accepted | 2026-10-31 |
| 0168 | Open log groups are sent at a clean stop, and a holding source is saved at its oldest held line | Accepted | 2026-10-31 |
| 0169 | A renamed log file whose read keeps failing is given up after 150 kept runs | Accepted | 2026-10-31 |
| 0170 | Log shipping drop patterns are bounded by their compiled size | Accepted | 2026-10-31 |
| 0171 | A fresh spool starts its sequence numbers above a clock bound | Accepted | 2026-10-23 |
| 0172 | The spool keeps a bounded number of acknowledged ranges | Accepted | 2026-10-23 |
| 0173 | The spool opens its files relative to its directory's descriptor | Accepted | 2026-10-23 |
| 0174 | Spool file sizes add up saturating, and a saturated sum is no room | Accepted | 2026-10-23 |
| 0175 | The state file syncs the spool before every save | Accepted | 2026-10-23 |
| 0176 | The sender gives up refused records one by one with a recorded gap, at a bounded rate | Accepted | 2026-10-24 |
| 0177 | Ingest is bounded by two slots through the commit and a free-space floor, not a rate cap | Accepted | 2026-10-25 |
| 0178 | A full ingest listener evicts the oldest connection that has not passed the token check | Accepted | 2026-10-25 |
| 0179 | Ingest writes end only at the shutdown deadline and are drained before the store closes | Accepted | 2026-10-25 |
| 0180 | Sequence and time gaps are stateful rows that become final after a quiet wait | Accepted | 2026-10-26 |
| 0181 | The agent's clock offset is measured from caught-up self samples | Accepted | 2026-10-26 |
| 0182 | The agent token reaches the agent as a systemd credential | Accepted | 2026-10-27 |
| 0183 | The agent unit allows only localhost and the tailnet ranges as addresses | Accepted | 2026-10-27 |
| 0184 | The agent unit sets a hard memory limit with GOMEMLIMIT below it | Accepted | 2026-10-27 |
| 0185 | The agent unit's system-call filter is built from @system-service plus adjtimex | Accepted | 2026-10-27 |
| 0186 | install.sh checks the binary against the SHA-256 the bundle carries | Accepted | 2026-10-27 |
| 0187 | Uninstalling the agent removes it, and purging also deletes its data | Accepted | 2026-10-27 |
| 0188 | The agent unit and scripts are verified by a CI job on systemd 249 | Accepted | 2026-10-27 |
| 0189 | The install scripts read spool.directory of a kept configuration with a fail-closed line reader | Accepted | 2026-10-27 |
| 0190 | The install scripts refuse a bundle another user could change and check its manifest before sourcing | Accepted | 2026-10-27 |
| 0191 | The documented install steps are single commands with check and extraction in one line | Accepted | 2026-10-27 |
| 0192 | `backend.url` names the backend by IP address; host names are refused | Accepted | 2026-11-07 |
| 0193 | The report page shows each external text in at most 16 runs | Accepted | 2026-11-07 |
| 0194 | `install.sh` accepts only the IPv6 literals the agent parses | Accepted | 2026-11-09 |
| 0195 | A backfill's CPU above 1 % of one core is an accepted deviation; the criterion stays the mean over the run | Accepted | 2026-11-09 |
| 0196 | Load-dependent Go tests are stress-run from a list in a path-filtered workflow | Accepted | 2026-11-10 |
| 0197 | The backend moves to .NET 11; the runtime image becomes the chiseled Ubuntu 26.04 ASP.NET image | Accepted | 2026-11-11 |
| 0198 | Razor component endpoints answer HEAD from .NET 11 on; the login page answers HEAD without a session | Accepted | 2026-11-11 |
| 0199 | The live connection's session check follows authentication state changes instead of reading the state per event | Accepted | 2026-11-11 |
| 0200 | A chart draw on a lost connection ends quietly and is drawn again; BL0016 is off for test code | Accepted | 2026-11-11 |
| 0201 | The output writes of `vandoxd` commands opt out of cancellation explicitly | Accepted | 2026-11-11 |
| 0202 | A reconnected live connection follows the session of its new connection | Accepted | 2026-11-11 |
| 0203 | Two-language toolchain with one set of quality gates, the .NET rules from the template's shared baseline | Accepted | 2026-11-12 |
| 0204 | SQL command text meets CA2100 through justified suppressions at the command helpers | Accepted | 2026-11-12 |
| 0205 | Disposable fields meet CA2213 through suppressions that name their owner | Accepted | 2026-11-12 |
| 0206 | Test assemblies run their tests one at a time, stated explicitly | Accepted | 2026-11-12 |
| 0207 | The incidents view shows each external text in at most 16 runs, the logs view keeps 256 | Accepted | 2026-11-13 |
<!-- project:end index -->

# Project

What the squad needs to know about this project that is not stack-specific. The repository has two
languages (record 0073): the agent `vandox-agent` is Go (`cmd/vandox-agent`, `internal/`), the backend `vandoxd`
is .NET 10 with Blazor (`src/Vandox.*`). An entry names the place in each language where it applies. Read by the Lead, the Devil's
Advocate, Security, the Tester and the Reviewer. Not template-managed: `adopt-template` creates it once
and never overwrites it. A product PR updates it when the change makes an entry untrue
(`.squad/routing.md`, *Scope of a product PR*).

## Security areas

A change that touches one of these is tier `security` (`.squad/routing.md`). Name the concrete types,
files or endpoints.

1. **Ingest authentication** (backend ingest API, not implemented yet): the agent token, request limits and
   deduplication. *Goal:* only an agent holding a valid token can store data; the token is compared in
   constant time; request size, batch size and rate are bounded so a valid agent cannot exhaust the
   backend's memory or disk; a resent record (same agent ID
   and sequence number), and so a resent batch, is stored once and never overwrites stored data. The
   deduplication is implemented by `BatchWriter` in `src/Vandox.Storage` (partial unique index `records_agent_seq`,
   `INSERT ... ON CONFLICT DO NOTHING`, no code path updates a record row). Records 0032, 0044, 0045, 0063, 0077.
2. **Tailscale ACL and port binding** (deployment files under `deploy/backend/` and the documented ACL):
   *Goal:* a compromised monitored server can reach only the ingest port on the backend host's tailnet
   address and nothing else in the tailnet or home LAN; the ingest port is bound only to the tailnet
   address; the web UI is not offered on the tailnet to the monitored server.
   `deploy/backend/docker-compose.yml` publishes the web port on `127.0.0.1` by default (defense in depth, not
   the boundary) and publishes no ingest port yet (issue #40); the Dockerfile has no `EXPOSE`. Records 0010,
   0016, 0017, 0059, 0060.
3. **Web UI login** (implemented): password hashing, sessions, CSRF, rate limiting, reverse-proxy
   trust. *Goal:* the password is stored only as a salted, deliberately slow hash; session IDs are random,
   expire, are invalidated on logout and travel only in `HttpOnly`, `SameSite` cookies that are `Secure`
   whenever the request reached the proxy over HTTPS; every state-changing request is CSRF-protected;
   failed logins are rate-limited; forwarded headers (`X-Forwarded-For`, `X-Forwarded-Proto`) are trusted
   only from the configured reverse proxy, and the UI port is published for that proxy only, as defense in
   depth (0060). In `src/Vandox.Backend/Security` (`PasswordHash`, `LoginService`, `LoginThrottle`,
   `SessionStore`, `SessionCircuitHandler`, `SessionRevalidatingStateProvider`, `ForwardedHeadersSetup`,
   `WebAuthentication`), `Components/Account/Login.razor`, `Hosting/RelativeRedirectMiddleware` and `Vandox.Core` `TrustedProxyList`; the hash
   uses the dependency Konscious.Security.Cryptography.Argon2. The logs view (`/logs`, `/logs/templates`) is behind this login like
   every page below `Components/Pages` (`[Authorize]` in `Pages/_Imports.razor`) and reads through `GatedLogExplorer` (two concurrent
   calls across the service, so two of the four reader connections stay free for `/healthz`); the number of circuits, each holding up to
   about 5 MiB of capped log text, is not bounded, but only a signed-in client can open one, and the host has no `Host`-header
   allow-list (a DNS-rebound name gets no session cookie, which is host-only, and still needs the password). The incidents view (`/incidents`, `/incidents/<number>`) is behind
   the same login and reads through the same gate: `GatedIncidentBrowser` and `GatedRecordReader` (under `IncidentWindowReader`) take one `WebReaderGate` of two slots with `GatedLogExplorer`, held per
   store call, so the bound spans both views; the number of circuits, each holding up to about 10 MB while an incident's charts are built (the per-circuit amounts of
   the web UI area, *Limits and errors*), is not bounded, and only a signed-in client can open one. The incidents view adds the first write of the web UI, a note: `POST /incidents/<number>/notes`
   (`IncidentNoteEndpoint`) has no `AllowAnonymous` (the fallback policy demands a session: anonymous is `401`) and carries `RequiredAntiforgery`; the handler checks the
   antiforgery validation feature first (missing or invalid is `400`, before the form is read), then the route number, then reads `note` and `written_for` from the form only (never the
   query string or the route; a field twice, in any letter case, is refused), refuses U+0000 and more than 1,800 UTF-16 characters (`IncidentNoteText`), builds the redirect from the
   parsed number and logs a failed save with the exception only, **never the note text**, a form value or the written-for number (`IncidentNoteSaveFailed`). The save does not use the reader gate, and the note
   is the service's first write outside an import (one immediate transaction under the write lock). The report page (`/report`, `Pages/ReportPage`) is behind the same login, and so is its
   download `GET /report/markdown` (`ReportDownloadEndpoint`, no `AllowAnonymous`: anonymous is redirected to the login, and the ingest listener answers `404`). Both run
   `ReportService`, which allows one report at a time across the service (a second one waits within its own deadline of 2 minutes, `ReportsView.Deadline`, and stops waiting when cancelled) and reads
   through `GatedRecordReader` and `GatedIncidentStore` of the shared `WebReaderGate`; `GatedIncidentStore.SaveIncidentAsync` refuses, so the web cannot write an incident. The
   page keeps a document of at most 50 outages per circuit (about 6.6 MB of capped text in the worst case, the web UI area, *Limits and errors*); the run keeps up to about 60 MB once for the whole
   service. The session cookie is `SameSite=Lax`, so another site can make a signed-in browser start a run; it reveals nothing to that site and is bounded by the run slot and the deadline.
   Records 0016, 0023, 0060, 0103, 0104, 0105, 0106, 0107, 0108.
4. **Command signing for remote actions** (later, not part of v0.1.0): *Goal:* the agent executes only
   commands with a valid signature that name an action from its local fixed list and were confirmed by the
   user; a forged, altered or replayed command is rejected; the signing key exists only on the backend
   host. Records 0006, 0009.
5. **Telegram allowlist** (not implemented yet): *Goal:* the bot sends only to the private chats of
   allowlisted Telegram user IDs and acts on an update (message, command, callback) only when its sender is
   allowlisted and it comes from that user's private chat; every other update, including any from a group,
   is ignored without acting on its content and logged only as sanitized metadata. Updates arrive by
   outbound polling (`getUpdates`) or through a webhook; a webhook is a new inbound endpoint reachable from
   the internet that 0006, 0012 and 0016 do not cover and needs its own decision (choice left to #60).
   Records 0012, 0031.
6. **Agent privileges** (`deploy/agent/`: user, systemd unit; from v0.6.0 the polkit rule): *Goal:* the
   agent never runs as root; it holds only the group memberships (`adm`, `systemd-journal`), from #42 the
   capabilities `CAP_SYS_PTRACE` and `CAP_DAC_READ_SEARCH` (bounding set limited to exactly these), and,
   once self-healing exists, a polkit rule that allows restarting only the configured units, each listed
   and justified in `deploy/agent/`. While any capability is granted, the unit is confined:
   `NoNewPrivileges=yes`; `SystemCallFilter=` denies at least `ptrace`, `process_vm_readv`,
   `process_vm_writev`, `pidfd_getfd` and `open_by_handle_at` (with `SystemCallErrorNumber=EPERM`), and
   `SystemCallArchitectures=native`; no write-side capability. So a compromised agent cannot use its
   capabilities to write as or run code as another user. Accepted residual, stated openly: its read access
   equals root's, i.e. every file on the server, and the memory and environment of every process, and
   credentials read that way may still lead to root through other services (password reuse, Plesk or
   MariaDB administration). `ProtectHome=yes` and `InaccessiblePaths=` (at least `/etc/shadow`,
   `/etc/gshadow`) are defence in depth against accidental reads only, not a limit on a compromised agent,
   which bypasses them through `/proc/<pid>/root`. The system call filter of the unit must allow `adjtimex`, which the `clock` collector calls
with a zero `Timex` (mode 0, read-only; no capability needed), so the unit uses neither `ProtectClock=yes` nor an allow-list without it (#42). Records 0013, 0030, 0111.
7. **MariaDB monitoring user**: *Goal:* the agent connects over the local socket as `vandox-agent`,
   authenticated by `unix_socket`, with only the `PROCESS` privilege: no database password exists and no
   table data is readable; the agent issues read-only status queries only. Record 0013.
8. **Secrets handling** (backend login, Telegram bot token, agent/ingest token, later the command-signing
   key): *Goal:* secrets are read only from environment variables or Docker secrets, never from the
   configuration file or the command line; never logged, shown in the UI, written to the spool or put into
   error messages; a secret checked against input (ingest token, later TOTP codes) is compared in constant
   time, the UI password through its hash function's comparison (`PasswordHash`, `CryptographicOperations.FixedTimeEquals`; the
   hash is checked at start-up and the password is never logged, echoed or stored). Implemented for the agent in `internal/config`
   (`readSecret`, `checkEnviron`, `Secret`) and for the backend in `src/Vandox.Core/Configuration`
   (`SecretReader`, `Secret`, `BackendSecrets`). The running agent holds the token in `config.Agent` (`Secrets`) and never logs it: `internal/agent` `Run` passes
   a logger only `agent_id`, the version and the errors of the loader and the notifier (`TestRun_DoesNotLogSecret`). Records 0032, 0050, 0078, 0104, 0110.
9. **File writes and paths derived from external input** (log import, the agent's on-disk spool, database
   backups): *Goal:* no write outside the configured directories (no path traversal), the spool is
   size-bounded, files are created with restrictive permissions. The database file `vandox.db`
   (`src/Vandox.Storage`: `DatabaseFile`, `SqliteStore`) is created with mode 0600 inside the existing
   `storage.directory`, which is never created, and a symbolic link or other non-regular file in place of
   `vandox.db` or its `-wal`/`-shm` files is refused. The log import (`src/Vandox.Import`: `Scanner`,
   `SourceRoot`, `Importer`; `src/Vandox.Core/IO`: `SecureRoot`, `FileProbe`) extracts nothing and writes no
   file: every open below the import root goes through `SecureRoot` (`openat2` with `RESOLVE_BENEATH` and
   `RESOLVE_NO_SYMLINKS`; on kernels without `openat2`, such as NAS kernels 4.4, `lstat` of every directory of the path
   and a refusal of `..`, then an open of the last element with `O_NOFOLLOW`, so containment is checked by the
   application, not enforced by the kernel, and a link swapped in between check and open is not caught), links and special files are listed and never
   followed or opened (`O_NONBLOCK`), entry names and paths are labels only, and a run handles at most 20,000
   entries, counted while scanning. Records 0045, 0069, 0077, 0079.
10. **Parsing of external input** (log files: journal, syslog, MariaDB, mail, Plesk, web server; the ingest
    wire format (backend: `Vandox.Core.Wire.BatchDecoder`, `WireLimits`; Go: `internal/wire`: `NewDecoder`, `Decoder.Next`,
    `wire.Limits`; truncated or corrupt gzip is an error, record 0076); CLI arguments and configuration; later Telegram commands): *Goal:* malformed or hostile
    input yields an error or a skipped record, never a crash, an unbounded allocation or a hang. The
    configuration files are read by `internal/config` (`decodeStrict`, `readFile`; agent) and
    `Vandox.Core.Configuration` (`StrictYamlDecoder`, `BackendConfigLoader`; backend). The agent's command line (`internal/cli` `Parse`: `-config` takes any
    non-empty path, which the loader checks; a positional argument or an empty `-config` is a usage error) and the systemd variables it reads from the environment slice it
    is given (`internal/sdnotify` `FromEnviron` and `WatchdogInterval`: ASCII digit guards, `NOTIFY_SOCKET` an absolute path or an abstract name within `sun_path`, `WATCHDOG_USEC`
    at least 100 ms so a hostile value cannot make a busy loop, a variable set twice an error; `internal/agent` `journalStream`: `JOURNAL_STREAM` counts only as `<dev>:<ino>`
    of stderr's own device and inode) never crash, hang or allocate without bound; `self/status` is read up to 64 KiB (`internal/selfstats`). The log search text
    (`SqliteStore` search, `FtsQuery`) reaches SQLite's FTS5 query parser and is reduced to quoted literal terms
    with bounded length and term count. Log files and archives reach the log import
    (`src/Vandox.Import`: `Scanner`, `FormatSniffer`, `ImportLimits`: entries, path length and batch size) and the
    parsers through `Vandox.Core.LogParsing` (`LogLineReader` cuts lines at 16 KiB, a parser's memory is bounded
    independently of the input size): `JournalExportParser` and `JournalExportReader` (binary-safe entry reader with
    bounded memory), `SyslogParser`, `SyslogLine` and `SyslogClock` (hand-written line parser, years and time
    zones of year-less times, range-checked dates), `SystemLogGrouper`, `MailLineClassifier` (Postfix and Dovecot lines of the journal and syslog and Dovecot's own log file: severity marker and MySQL
    connection failures at the anchored positions, ordinal text operations, no state; a classification, not a trust
    boundary: local processes can forge markers and events, record 0091), `MariaDbLineGrouper` and `KernelReportGrouper` (bounded multi-line
    reports; MariaDB lines of the journal and syslog are joined per host, program and pid, within 60 seconds of the
    header line and 16,384 bytes, into one open entry, and a line that does not fit ends it and is kept as a plain
    line; the program filter `mariadbd`/`mysqld` is a classification, not a trust boundary: local processes can
    forge entries and events and, in a syslog file, join or split the server's entry, and a journal entry without
    `_PID` takes the sender-chosen `SYSLOG_PID` and so behaves like a syslog line for forging, record 0088 option 36),
    `MariaDbErrorLogParser`, `MariaDbParseSession`, `MariaDbLine` and `MariaDbMessage` (hand-written header parser on raw
    bytes, entries bounded to 16 KiB, claimed by the first non-empty line only and never for a syslog name),
    `DovecotLogParser`, `DovecotLine` (Dovecot's own log file: traditional stamp plus marker at Dovecot's position, no state beyond the year clock, claimed by the first line only and never for a syslog name),
    `ErrorLogParser`, `ErrorLogParseSession`, `ErrorLogLine` and `LogPath` (the Plesk panel log and the nginx, `sw-cp-server` and Apache error logs: hand-written, linear, ordinal
    ASCII line parser without regular expressions, no date built before the range checks of `SyslogClock.ResolveLocal`; a configured path is compared with the
    listed name as text by parent directory and base name and never opened or resolved; unreadable lines are kept as raw
    records whose time is the previous record's or a modification time inside the storable range, else skipped; forged lines in
    these logs are a documented limitation, record 0092), `LegacyMonitorParser`, `LegacyMonitorHeader`, `LegacyMonitorSnapshot` (with `LegacyProcessTable`, `LegacyConnectionTally`),
    `LegacyTopLine`, `LegacyProcLine` and `LegacyLsofLine` (the legacy `top`/`lsof` monitoring log: hand-written ASCII grammars, numbers validated as digit strings with
    digit bounds and 64-bit checks before any conversion or scaling, no date built before the range checks, caps that count every line of a part, skips reported at once;
    `LegacyLsofLine` puts a strict address guard in front of `IPAddress.TryParse`, so only the forms `lsof` prints reach it and no zone is looked up on the host;
    `LegacyServiceNames` is a built-in table of port names, never a lookup; forged snapshots are a documented limitation, record 0093), and the backend options `import.time_zone` (`SourceTimeZone`) and
    `import.plesk_panel_log`, `import.sw_cp_server_error_log`, `import.nginx_error_log` and `import.apache_error_log`
    (`BackendConfigLoader`: absolute, clean path of a file in a directory, the value never shown in an error). The signature detection over stored log lines
    (`Vandox.Core.Signatures`: `SignatureCatalog`, `SignatureDetector`, `BuiltInSignatures`) reads the embedded catalog and the decoded messages of stored
    records: patterns run on the non-backtracking engine in linear time, with the constructs it cannot run refused when the catalog loads; a signature
    without an event condition that reads a message which can hold remote text (`disk_full`, `mariadb_too_many_connections`, `php_too_many_connections`, `php_disk_full`, `nginx_disk_full`) carries an origin condition (`program` or `source`) and a `\A` anchor at server-written text, so remote text in the fields a web server or
    mail server adds and in PHP's own messages cannot trigger it; an application that logs request data at the start of a message (`trigger_error`, `error_log`, standard error of programs it starts)
    or in PHP output that nginx, `mod_fcgid` or `mod_proxy_fcgi` splits into several entries can create PHP events (record 0094); integer fields are accepted only as ASCII digits by an ordinal scan of the whole captured text and range-checked before
    use (`NumberGuard`); text fields are cut to 1,024 bytes; at most 1,024 events are open and 256 IDs kept per event, and an open event keeps no message,
    match or record; an event is a classification, not proof of origin: local processes can log as `kernel`, `systemd`, `mariadbd`, `postfix/*` or
    `dovecot`, and MariaDB entries forged through a login name reach the MariaDB signatures (record 0094). The log template normalizer
    (`Vandox.Core.Templates.LogTemplateNormalizer`, with `TokenRules` and `TemplateWriter`) turns the message of every stored log line into a template:
    hand-written, one linear forward pass, ordinal ASCII comparisons, no regular expression and no culture, no shared mutable state (no static or
    thread-static buffer; the output goes into a buffer on the stack of the call with the fixed size of 1,024 characters, so no input can make it
    grow), the template cut at 1,024 UTF-8 bytes at a character boundary; control characters of category Cc are removed as separators, format
    characters (Cf, e.g. U+202E, U+200B) are kept, and the normalizer is a grouping, not a guard. The template queries (`TemplateQueries`,
    `ILogTemplateReader`) bind every value as a parameter, bound the range (the usual-level query at most 744 hours), the limit (1 to 10,000)
    and the baseline (at most 2,160 hours), and refuse a *find* message of more than 16 KiB; the work of the usual-level query is **not** bounded by
    range or limit (it evaluates every template hour of the range that reaches the minimum count); a cancelled token or
    deadline interrupts a running reader statement (`ReaderQuery`: a progress handler on every reader connection, checked every
    1,000 instructions), which then ends with `OperationCanceledException`, so a caller that serves a user bounds it with a deadline
    (records 0095, 0097). The logs view (record 0106) takes every filter from the address: `LogsAddress` (`Parse`, `Validate`, `ToQuery`; the
    query string is decoded by `QueryHelpers.ParseQuery` and the page passes `new Uri(NavigationManager.Uri).Query`, so no fragment) refuses a repeated
    parameter, and a `q`, `source` or `program` of more than 1,024 UTF-8 bytes or with a character of category Cc, Cf, Zl or Zp or invalid UTF-16, a `severity` that is not one
    of the eight names (`Severities`), a `template` that is not 1 to 18 ASCII digits, and a `from` or `to` that is not `yyyy-MM-ddTHH:mm[:ss]` in ASCII digits within the
    storable range, with texts that name only the parameter, and writes an address only from validated values (`ToQuery` throws otherwise). The values then reach
    only `FtsQuery`, bound SQL parameters or `long` and `DateTimeOffset.ParseExact` with fixed formats. The explorer (`ILogExplorer`, `SqliteStore`; `LogFilterRules`,
    `LogCriteria`, `LogFinder`, `LogFinderQueries`, `LogExplorerQueries`, `TemplateTerms`) bounds every request: a page of 1 to 1,000 lines, a scan of at most
    `ScanLimit` (1 to 100,000; the page uses 8,000, divided by the word parts of the user's text, `FtsQuery.CountTokens`) examined lines whose full-text test (of the user's text only) is a per-line `EXISTS` that collects nothing, a selectivity probe that reads at most
    12,000 row IDs under a 0.7 s time budget (`LogFinder.ProbeBudget`, `ReaderQuery.AttachBudget`), a context of 0 to 20 lines per side examining at most 2,000 rows per side, 64 sources, a template list of 1 to 1,000 templates, source hours of 1 to
    10,000; the template words reach the search only through `FtsQuery` (at most 16 terms and 1,024 bytes joined with the user's) and the candidates are compared with
    the template by `LogTemplateNormalizer`. A text of several common words makes FTS5 walk their doclists, but the progress handler does run inside FTS5 (measured, gaps of at most 31 ms), so
    the probe is stopped by the deadline or by its budget; a stopped probe turns the request into a scan (`LogPage.IndexCheckStopped`). The scan tests every one-part word with its own `EXISTS` (`LogFinderQueries.BuildScan`)
    and the terms of several parts in a private in-memory FTS5 index of at most 64 examined lines or 256 KiB of characters (`LineIndex`: bound parameters only, the expression
    only from `FtsQuery.BuildTerms`, the call's token as progress handler, disposed on every path, no reader lease), and ends after 1.5 s (`LogFinder.PageBudget`), at least
    one line examined; residual: one `MATCH` inside a chunk runs to its end (about 0.3 s on the development host, 1.5 s on the reference host with crafted messages, a page up to
    about 3 s; the deadline still ends the call). Cases 12 to 15 of `tools/Vandox.StorageBenchmark --search-lines`, `docs/BENCHMARKS.md`. Each page runs one store call at a time under a 10-second deadline, and `GatedLogExplorer` lets two calls run at once across the service
    (a wait counts against the deadline). The backend options `incidents.window_before` and
    `incidents.window_after` (`BackendConfigLoader`, `ConfigDuration`) accept exactly one to four ASCII digits and `m` or `h` by an ordinal scan (no parsing API, no culture, sign,
    white space, other digit script or second unit), within 1 minute to 24 hours, the value never shown in an error. The incident reconstruction (`vandoxd incidents`)
    reads stored external text, including forged lines, under bounds it must keep: `KillClusterer` keeps 16 kills per incident and ignores a kill at the last storable
    instant; `IncidentBuilder` keeps at most 1,000 timeline entries within a 4 MiB budget measured with `IncidentJson` (the options the store serializes with), 3 + 3
    candidates, 20 + 20 templates and two program maps of at most 4,096 entries (`Vandox.Core.Incidents`: `IncidentLimits`); `IncidentReconstructor` reads log lines and
    metrics in pages of 1,000 and process and connection snapshots one per page, pages by capture time and record ID (`RecordQuery.AfterId`) and fails a page that does not
    lead past the previous one; `RecordQueries.ReadJson` reads the payload as UTF-8 bytes so that one forged maximum process snapshot of an earlier build peaks near 240 MiB (this version stores no payload of more than 16 MiB of JSON: `StorageLimits.MaxPayloadBytes`, checked by `RecordRules.CheckPayloadSize` in the importer's `RecordEmitter` and by `BatchWriter` while it serializes with `PayloadJson` into a `BoundedBufferWriter`, which drops the bytes above the limit); `IncidentRepository` refuses an
    incident of more than 8 MiB of serialized JSON (`InvalidIncidentException`), which the reconstructor skips and counts, and reads stored incident data as UTF-8 bytes. `vandoxd templates` (`SqliteStore.RebuildTemplatesAsync`, `TemplateRebuilder`) reads stored log lines in batches of 10,000 by record ID up to the high-water ID fixed when the tables are emptied, one transaction each, with the write path's normalizer and tally, so a batch's tally holds at most `batchLines` keys of source (at most 128 UTF-8 bytes), program (at most 1,024) and template (at most 1,024), plus as many source hours, each with its own source string (about 46 MB of UTF-16 strings at 10,000, plus dictionary overhead), and reads one message (at most 16 KiB) at a time; every counting transaction checks the run marker in `meta`, so a run superseded by a later one counts nothing more and fails; the transaction that empties the tables grows with the statistics. The forensics report (`vandoxd report`:
    `ForensicsReporter` in `src/Vandox.Backend/Reports`, `ForensicsAnalysis` and `ReportLimits` in `src/Vandox.Core/Reports`) reads every stored
    log line in pages of 1,000 through `RecordPager` and every process snapshot one per page, and the incidents one at a time (`IncidentQuery.Limit` 1, each page
    starting at the end of the previous incident's window, a page that does not lead past it is `store: reading incidents: page did not advance`), twice; it keeps
    only counters for the incidents and, across the run, at most 1,000,000 snapshot points (24 bytes each), 10,000 boots, 100,000 runs of consecutive observed hours and
    4,096 programs (`ReportLimits`; the caps count decoded, stored items, never bytes), so its memory does not grow with the database (about 60 MB at the
    caps); over a cap the statements that need the data are unknown and the notes name the cap. The incident browser (`IIncidentBrowser`, `SqliteStore`, `IncidentRepository`,
    `IncidentNoteRepository`) bounds what the incidents view reads: a list page of 1 to 100 summaries (the web UI asks for 20) whose `data` is read as UTF-8 bytes and decoded by a
    projection of three members, one row at a time, a keyset position (start and number) that is validated, one incident decoded whole (up to 8 MiB, transient) by a
    positive number, at most 64 notes read (`StorageLimits.MaxNotesRead`) of at most 1,800 UTF-16 characters and 5,400 UTF-8 bytes each, the table's byte `CHECK` holding whatever
    the text contains (U+0000 is refused before SQL because `length()` stops at it) and the foreign key without a cascade; `IncidentWindowReader` reads the window's snapshots one per
    page with the source of the chart (`RecordPager` carries `Source` and `Name` into every page query), at most 300 process snapshots, 300 connection snapshots and 1,000 points
    per metric series, and `ProgramMemorySeries` tells at most 4,096 programs apart (the rest is summed), so a window read holds one snapshot and about 4,096 x 300 values. The `-lang` flag of `vandoxd report` is the one CLI value
    that selects behavior: `CommandLine` compares it ordinally with exactly `en` and `de` (`ReportText.For`), any other value, also another case, `de-DE`, a
    padded or Cf-bearing look-alike, is a usage error that shows it only through `Terminal.Quote`, and the unknown flag names of `CommandLine` are escaped the
    same way. The report page and its download take the same choice from the address: `ReportAddress.Parse` reads `new Uri(NavigationManager.Uri).Query` (the page) or the request's query
    string (the download), so never the fragment, through `QueryHelpers.ParseQuery`, and accepts a `lang` only when its decoded value is exactly `en` or `de` (`ReportText.For`, ordinal);
    `DE`, `de-DE`, an empty value, `lang` without `=`, a value with a space, a control or format character, a percent-encoded look-alike such as `%E2%80%AEde` or `en%00`, and a key given twice
    (in any letter case, with any value) are refused with a fixed text that names only the parameter, a refused address runs nothing (the download answers `400` without a body before any
    store call), other keys are ignored, and the address is written only from the validated language (`ToQuery`, `ReportsView.PageHref`, `DownloadHref`, `FileName`); the `Content-Disposition`
    of the download carries only that fixed file name. Forged kill lines make forged incidents
    (a documented limitation, record 0096); forged boot lines, `pzstd` or `sw-engi*` processes shift the report's activities and rates, and forged process names can
    take the 4,096 program places first (records 0100, 0101). The agent's readers of `/proc` and `/sys` (`internal/sysstats`) bound every read before parsing: the first line of `stat` 4 KiB, `loadavg` 256 B, `meminfo` 16 KiB and 128 lines, `vmstat` 64 KiB, a pressure file 256 B, a block `stat` 1 KiB, `self/mountinfo` streamed line by line at 64 KiB per line; at most 16 disks and 16 mount points; every number is checked as 1 to 20 ASCII digits before conversion; the products of `statfs` counts are checked for 64-bit overflow with `bits.Mul64`; no error text holds file content or a configured path (a mount point is named by its index). `TestSysstats_ImportsOnlyAllowedPackages` holds the package to an allow-list of imports and of the `syscall` and `os` selectors it uses (no program, socket, cgo, `unsafe`, raw system call or `//go:` directive but `//go:build`). The option `filesystem.mount_points` is checked by `checkMountPoints` (`internal/config`), and `decode.go` refuses a null list element before decoding because the YAML library drops it silently. Records 0048, 0049, 0066, 0069, 0076, 0078, 0079, 0084,
    0085, 0086, 0088, 0091, 0092, 0093, 0094, 0095, 0096, 0097, 0100, 0101, 0102, 0106, 0108, 0110, 0111.
11. **Outbound calls** (Telegram, external checks, the optional AI service of the nightly report, the
    agent's connection to the backend): *Goal:* every call has a timeout, goes only to its configured
    destination and leaves encrypted to a verified peer. The agent's only destination is the ingest port,
    reached only over the tailnet (WireGuard encryption and node authentication by Tailscale), never over a
    public address. Telegram and the AI service are called over HTTPS with certificate verification;
    external checks verify the certificate whenever they use TLS, and a failed verification is a check
    result. Certificate verification is never switched off (no `InsecureSkipVerify`), and no secret is sent
    over an unencrypted connection. The image's health check (`HealthCheckCommand` in `src/Vandox.Backend/Cli`) calls only the
    configured web listener, on loopback for an unspecified host, with a 4 s timeout, no proxy, no redirects,
    no credentials and no environment. Besides the ingest port the agent writes only to the local systemd notification socket named by `NOTIFY_SOCKET`
    (`internal/sdnotify`: one unix datagram of the fixed states `READY=1`, `WATCHDOG=1` and `STOPPING=1`, a 1 s deadline per notification, no network). Records 0006, 0008, 0010, 0012, 0017, 0023, 0059, 0110.
12. **Logging and display of external data** (log lines, process names and text derived from them, e.g. an
    AI-written report): *Goal:* they cannot inject into log output (control characters, newlines), into the
    web UI (HTML is escaped) or into Telegram messages (escaped for the parse mode used, or sent as plain
    text without a parse mode). Record 0031 for Telegram; Razor escapes HTML by default in the web UI (record
    0081). Every web response carries one self-only Content-Security-Policy (`default-src 'none'`, no inline code,
    no external origin), `X-Content-Type-Options`, `Referrer-Policy` and `X-Frame-Options`, set by
    `SecurityHeadersMiddleware` (record 0099); `WebUiContentTests` checks the sources with `WebSourceScanner`: the
    own scripts (`wwwroot/js`) contain none of its HTML sinks, no `eval`/`Function`/string timer and no
    `setAttribute`; component markup and code contain no `MarkupString` or `AddMarkupContent`, so external text
    never reaches the page as raw HTML. What only the policy covers is listed in the Web UI area (*Limits and
    errors*). The logs view shows log lines and templates only through `ExternalText` and `LogText`: Razor's text encoding, every character of category Cc (except
    line feed and tab), Cf, Zl, Zp, Co, Cn and U+FFFD written as a visible `\uXXXX` escape in a span of its own, at most 256 runs per field, a cap of 16,384 characters
    for a message and 2,048 for source, host, program and template text with a visible `[cut: <n> more characters]` marker (`LogLineView` builds the capped runs, so a page
    keeps no `StoredRecord`), `unicode-bidi: isolate` per field, no `MarkupString`; links are the fixed paths `logs` and `logs/templates` plus the `ToQuery()` of a validated
    address, and the chart (`TimeSeriesChart`) gets only the fixed texts `LogsView.ChartTitle` and `LogsView.ChartSeries`, never external text. Forged lines appear in the view
    like real ones. The incidents view shows the external text of a stored incident (kill hosts and victims, timeline types, hosts and texts, programs, commands, metric names
    and sources, templates) only through `ExternalText` and `LogText` with the same escaping: timeline texts capped at 512 characters, every other field at 2,048; a timeline row's
    CSS class comes from a fixed map (`oom_kill`, `boot`, `backup_first_seen`, `backup_last_seen` to `mark-oom`, `mark-boot`, `mark-backup`) and any other type gets none and is shown as text;
    links are the fixed paths `incidents/<number>`, `incidents/<number>/notes` and `logs` plus the `ToQuery()` of a validated address and numbers; no `MarkupString`, no `style` attribute.
    The program names of the stacked memory chart are chart labels: `LogText.Escape(name, 64)`, written by uPlot through `textContent` (read in the vendored file), so a label carries no
    markup and its direction controls are visible escapes; chart titles and the other series labels are fixed texts. A note is user text, shown only as the content of a `textarea`
    (Razor-encoded, so `</textarea>` cannot end it), and the headings of moved notes hold only numbers. The report page (`Components/Reports/ReportContent`) shows the victim commands
    and program names of the forensics report only as `<code><ExternalText Runs="…" /></code>` with runs of `LogText.Runs(text, LogText.MaxShortChars)` (Razor's text encoding, the visible
    escapes, 2,048 displayed characters, 256 runs, the `[cut: <n> more characters]` marker, `unicode-bidi: isolate`); external text is only ever a piece of a paragraph or list item
    (`ReportPiece.Code`), never of a heading or a table cell, an empty text is the plain dash `—`, the labels, numbers and times are plain text from `ReportText` and the composer, and the page
    uses no `MarkupString`, no `style` attribute and only the fixed links `report?lang=<en|de>` and `report/markdown?lang=<en|de>`. The Markdown of the download writes the same external text through
    `MarkdownText.Code` (backslashes doubled, invisible characters as `\uXXXX`, a fence longer than any backtick run), and the log of a failed run holds the exception only.
    Records 0098, 0099, 0106, 0107, 0108.
    Configuration error texts never echo document text other than schema or safe key names
    (`decodeStrict`, `StrictYamlDecoder`), never a `_FILE` value or path or an unsafe variable name
    (`readSecret`/`SecretReader`, `checkEnviron`). Configuration values are free of Cc, Cf, Zl and Zp characters but
    are still logged only as structured attributes, never concatenated into a message. `vandoxd` logs through the
    JSON line provider (`src/Vandox.Backend/Logging`), which escapes control characters, with `[LoggerMessage]`
    methods (`BackendLog`), and `/healthz` never returns an error text. `vandoxd import` prints every path and
    reason in its summary through `Terminal.Quote`; its log lines carry the input-derived attributes (`path`, `reason`,
    a run error's text) as JSON strings, whose encoder escapes control characters, DEL, C1 controls and
    format characters (`ImportSummaryWriter`, `ImportProgressLogger`). The text fields of detected events (`DetectedEvent.Texts`: victim command, unit,
    description, result, kernel version) are captures of log text and external text: whoever shows them escapes them like any log line. Log templates
    (`LogTemplate.Template`, and the templates of `TemplateSpike`) are external text as well: the normalizer removes control characters of category Cc but
    keeps format characters (Cf, e.g. U+202E, which reverses the display direction, and U+200B), `<`, `&` and any other markup, so whoever shows a template escapes it
    like any log line and handles format characters. `vandoxd incidents` prints the victim command of an incident's first kill through `Terminal.Quote`
    (`IncidentSummaryWriter`); its log lines carry only the store's text and the first kill time, no input text, and the texts of a stored incident (kill victims and hosts,
    timeline texts, programs, commands, templates) are external text for whoever shows them. `vandoxd report` writes the victim commands and program names of
    the report (`IncidentKill.VictimCommand`, `ProgramGrowth.Program`, `ProgramUsage.Program`, `ConsumerSummary.Program`) only through `MarkdownText.Code`
    (`src/Vandox.Backend/Reports`), as Markdown code spans: backslashes doubled, line feed, carriage return and tab as `\n`, `\r`, `\t`, other Cc, Cf, Zl, Zp,
    private-use, unassigned and invalid characters as the escapes of `Terminal.Quote` (`\u202e`), a fence longer than any backtick run, padding spaces, an empty
    text as a dash; a code span keeps inline HTML, entities, links, autolinks and emphasis literal, no external text stands in a table or a heading, and the
    report's own labels, numbers and times come from `ReportText`; the `-lang` value in the usage error and an unknown flag name go through `Terminal.Quote`;
    standard output is UTF-8 without a byte order mark (`Program`). The agent logs through `slog.TextHandler` on stderr (`internal/agent` `NewLogger`): values with control
    characters are quoted and escaped, so a value cannot start a forged line; panic values and stacks of collectors are attributes, cut to 1 KiB and 4 KiB; the message and every
    string, error or `fmt.Stringer` value are cut to 4 KiB and each line to 32 KiB, below journald's `LineMax=`, so no continuation can start with a forged `<N>` priority; the
    configuration is never passed to a logger. Records 0072, 0081, 0095, 0096, 0100, 0110.
13. **Release pipeline and published artifacts** (`.github/workflows/release.yml`,
    `.github/workflows/base-image-digests.yml`, `.github/scripts/`, `deploy/backend/Dockerfile`,
    `.dockerignore`, `dotnet-tools.json`, the GitHub environment `release`; the smoke script
    `.github/scripts/smoke-test-backend.sh` runs on pull requests, reads no secret and pushes nothing): *Goal:* artifacts are
    published only from a GitHub release whose SemVer tag only the repository admin can create (tag ruleset)
    and whose commit the workflow checks is on `main`; the ancestry check runs in code the tagger controls,
    so the ruleset is the boundary. Every action is pinned by commit SHA and every base image by
    digest (`FROM ${BASE_<NAME>_IMAGE}@${BASE_<NAME>_DIGEST}` with build-argument defaults that the release
    build never overrides; the release workflow checks the form).
    Release binaries are built without restored CI caches (`setup-go` `cache: false`, plain
    `docker build --no-cache`, no cache backend) and only after `govulncheck` and the NuGet vulnerability check
    (`dotnet list package --vulnerable --include-transitive`) pass. The builder image's .NET version must equal the
    target framework (`check-builder-dotnet-version.sh`, record 0041). The registry token is
    readable only by the tag-triggered publish job, enters `docker login` only via stdin, and is limited to
    pushing `networlddev/vandox`. No `${{ }}` expression of any kind appears inside a `run:` script; every
    value goes through `env:`, and checkout does not persist the job token. The published image runs as a
    non-root user, a published version tag is never overwritten, and every published binary has a checksum
    in `SHA256SUMS`. The OIDC signing permission (`id-token: write`, `attestations: write`) is granted only
    to the `attest` job, which declares no environment, reads no secret and runs no repository code; every
    published binary and image digest gets a build provenance attestation and an SBOM attestation that the
    workflow verifies before the assets are attached to the GitHub release. The SBOM generator is a container image pinned by
    index digest (form-checked in `.github/scripts/generate-sbom.sh`), run without network, capabilities,
    token or writable access to `dist/`. Each of the scheduled check's two jobs (base image
    digests, SBOM generator pin) holds only `contents: read` and `issues: write`, runs its lookups (registries,
    syft's Git tags) without a token, writes only regex-checked image, tag and digest values into its own
    issue, and never writes to the repository. Records 0027, 0037, 0041.
    Vendored browser code (`src/Vandox.Backend/wwwroot/lib/uplot/`, uPlot 1.6.32) is copied by hand from the release
    tag, pinned by SHA-256 in `WebUiContentTests`, not loaded from a CDN and excluded from the code analysis, not
    from review (record 0098).
    The cloud sessions have no Docker daemon: a change to the Dockerfile, compose file, `.dockerignore` or the smoke
    script is verified by the CI job *Release build check* on the pull request head, not locally (the local
    substitutes are listed in `.squad/stack.md`, *Not checkable in a cloud session*).

## Guarantees

Deliberate behavior that must not change without the Product Manager. Each one is described in
`docs/ARCHITECTURE.md` and, where it was a real choice, has a decision record.

- **No data gaps unless explicitly recorded**: collection is gapless across backend downtime (spool and
  backfill); data that is nevertheless missing (agent stopped, spool full, collector timed out, sequence
  numbers missing) is recorded as a gap and treated as "unknown", never as "normal". `docs/ARCHITECTURE.md`
  section *Offline behavior and backfill*; records 0028, 0045.
- **A hanging collector or database never blocks the agent** (Go agent): every collector runs in its own goroutine
  under a deadline and is abandoned when the deadline passes (a blocked `/proc` or `/sys` read cannot be
  cancelled, only abandoned; sources that take a context also get it); a hung source (`/proc`, `/sys`,
  D-Bus, journald, MariaDB) loses only its own sample, recorded as a gap, while the other collectors, the
  spool and the sender keep running; while a collector is still stuck it is not started again.
  `docs/ARCHITECTURE.md` section *Components*; records 0029, 0110. Implemented by `internal/scheduler` (a runner goroutine and a worker goroutine per collector,
  a gap for every timeout, skip, panic, error and late wake-up).
- **Backfilled data never raises an alert by itself**: the backend classifies every record as live or
  backfilled from the data; alert rules run on live data only; backfill is stored and analyzed but never
  alerts. `docs/ARCHITECTURE.md` section *Offline behavior and backfill*; record 0022.

## Integration surface

What the Reviewer checks when the diff introduces or changes a thing of this kind: every place that must
change with it.

**A new or changed configuration option** touches:
- the configuration type and its loading: for the agent `internal/config` (`Agent`, `LoadAgent`, `AgentKeys`), for the backend `src/Vandox.Core/Configuration` (`BackendConfig` and its option classes with `ConfigKey` attributes, `BackendConfigLoader`)
- the commented example file `deploy/agent/agent.yaml` or `deploy/backend/vandoxd.yaml`: the example file sets the option explicitly (an optional one at its default; one without a default commented out with an example value, which the test checks): `TestLoadAgent_Example` and `BackendConfigLoaderLoadsRepositoryExample` load these files and fail when a key is missing, except `import.time_zone`, which has no default and must stay commented out (0049)
- the *Agent options* or *Backend options* table in `README.md` (key, default, description); a non-secret option has no environment variable (0049), a secret is added as **a new secret** below instead
- the tests that pin the configuration loading: `internal/config/agent_test.go` and `tests/Vandox.Core.Tests/BackendConfigLoaderTests.cs`

**A new secret** touches: its `Env*` constant (`internal/config` for the agent, `ConfigConstants` and
`BackendSecrets` for the backend), the known-variable list of each binary that reads it, the secrets table in
`README.md`

**A new or changed service / module** touches:
- its public interface and the package (Go, under `internal/`) or project (C#, under `src/`) that owns it
- where it is wired up: `cmd/vandox-agent`, or `src/Vandox.Backend` (`Hosting`, `Cli`, `Program.cs`)
- the test double used by the tests of its callers
- the component list in `docs/ARCHITECTURE.md`

**A new web UI page, component or static asset** touches:
- a page: the navigation in `src/Vandox.Backend/Components/Layout/MainLayout.razor`
- a file the browser loads: the *Static assets* list in `docs/areas/web-ui.md` and `ServeTests.ServeServesWebAssetsOnlyOnWebPort`
  (served on the web port, `404` on the ingest port)
- the rules of record 0099, checked by `WebUiContentTests` (no inline code, no external URL, no raw HTML)
- a brand file: the byte copy of `docs/assets/` in `wwwroot/brand/`

**A new log parser** touches:
- its type in `src/` (implementing `ILogParser`, with a type accepted by `ParserTypes.IsValid`)
- its registration where `ImportCommand` builds the `ParserRegistry` (`src/Vandox.Backend/Cli`), in priority order
- detection tests against the other registered parsers, so that no file is claimed by two of them
- the list of supported sources in `README.md` (*Import logs*)

**A new or changed wire format field or record kind** touches (record 0075):
- the Go model and encoder (`internal/model`, `internal/wire`) and the C# model and decoder
  (`src/Vandox.Core/Model`, `src/Vandox.Core/Wire`)
- the golden batch `testdata/wire/all-kinds.jsonl` (regenerate with `VANDOX_UPDATE_GOLDEN=1 go test ./internal/wire`)
  and both tests that read it (`internal/wire/golden_test.go`, `WireContractTests`)
- `docs/areas/wire-format.md` and, for a version change, the major/minor rules (0043)

**A new external API call or DTO** touches:
- the client and its types — the external shape must not leak past it
- the fake/stub in the tests

**A new or changed external source or destination** (`/proc`, `/sys`, systemd D-Bus, journald, the MariaDB
socket, the Telegram Bot API, the Tailscale network) touches:
- the small interface the code reads it through, and its fake in *Test doubles*
- the deadline that keeps it from blocking the agent (0029) and the gap it records on timeout (0028)
- the privilege it needs in `deploy/agent/` (0030) or the ACL and port binding (0010, 0017)
- the *Security areas* entry it falls under

**A change to a configuration file that a decision record constrains** — `.github/dependabot.yml`, a workflow
under `.github/workflows/`, `.golangci.yml`, `deploy/backend/Dockerfile` or `deploy/backend/docker-compose.yml` —
touches:
- the records that constrain it; records mostly name these files in prose ("the compose file", "the release
  workflow", "a weekly workflow"), so search for the topic, not the file name:
  `grep -l -i -E 'dependabot|workflow|dockerfile|compose|golangci|ci\.yml' docs/decisions/0*.md`. Among them: 0041 (base images
  pinned by digest through build arguments, no Dependabot `docker` entry, the weekly digest report in
  `base-image-digests.yml`), 0060 (the compose file, no Dependabot entry for it), 0037 (release workflow), 0047
  (`gocognit` and its exclusions in `.golangci.yml`), 0112 (`gosec` limited to G115 in `.golangci.yml`, no
  exclusions) and 0052 (image labels)
- a change that contradicts such a record is not merged as is: the record is edited (unreleased) or superseded
  (released) in the same pull request, or the change is dropped

## Test doubles

The hand-written fakes and stubs the tests reuse (no mocking library unless `docs/UNIT_TESTS.md` says
otherwise). Go rows are for the agent, C# rows for the backend:

Status of every row: planned (not implemented yet), except where the row says implemented. The first feature that introduces a surface adds its
double under this name and changes the status.

| Surface | Test double |
| ------- | ----------- |
| `/proc` | collectors read through an `io/fs.FS` (production `os.DirFS("/proc")`; symlink targets such as `/proc/<pid>/fd/*` through `io/fs.ReadLinkFS` (Go 1.25 and later; implemented by `os.DirFS` and `fstest.MapFS`, the blocking wrapper must implement `fs.ReadLinkFS` (`ReadLink` and `Lstat`) as well)); tests use fixtures under `testdata/proc/` (via `os.DirFS`; `testdata/proc/self/status` is a real capture read by the self-metrics, implemented for `self/status`) or `fstest.MapFS` (the variants of that file are made in memory), plus a **blocking filesystem wrapper** whose reads block until the test releases them (in `t.Cleanup`), for 0029, and a wrapper whose open or read answers a given error such as `syscall.EOPNOTSUPP` (implemented in `internal/sysstats`: fixtures `testdata/proc/{stat,loadavg,meminfo,vmstat,pressure,self/mountinfo}`) |
| `/sys` | same filesystem interface and blocking wrapper; fixtures under `testdata/sys/` (implemented: `testdata/sys/block`, `internal/sysstats`) |
| `statfs` and `adjtimex` | the collectors take function values (`Sources.StatFS`, `Sources.Clock`; production `StatFS` and `ReadClock`, both read-only system calls); tests use fake functions with scripted results and errors per path, and variants that block until the test releases them (in `t.Cleanup`), for 0029 (implemented; `internal/sysstats`) |
| systemd notification socket | a `unixgram` socket bound in `t.TempDir()` (or an abstract `@name`) that the test reads; it is local IPC, not network. The agent's watchdog loop is tested with a recording `notifier` (records state and time of every call, optional failure) (implemented; `internal/sdnotify`, `internal/agent`) |
| collectors and sink (scheduler) | scripted collector (per-run results, errors and panics), hanging collector (blocks ignoring its context, released in `t.Cleanup`), recording sink (records every `Put` with its time, optional blocking `Put`), recording `slog.Handler` (implemented; `internal/scheduler`) |
| systemd D-Bus | fake systemd reader (scripted unit states and errors, and a reader that blocks until its context is cancelled, for 0029) |
| journald | fake journal reader (scripted entries, cursors and errors, and a reader that blocks until its context is cancelled, for 0029) |
| MariaDB socket | fake MariaDB status source (status variables, process list, errors, and a source that hangs until its context is cancelled, for 0029) |
| database (C#) | `FakeImportStore` for `IImportStore` (in-memory import state and records, base lookup, scripted begin, find and write failures, `AfterBatch` hook; also records line hashes with scripted failures and emulates attempts and containment as the store does; `tests/Vandox.Import.Tests`); a scripted ping delegate for `PingChecker` (`tests/Vandox.Backend.Tests`); the SQLite store itself is tested against a real database file in a `TempDirectory` (implemented); the incident reconstruction is tested against the real store with `IncidentSamples` (records written through the store, `tests/Vandox.Backend.Tests`) and hand-written decorators around it (`RecordingRecordReader`, `RepeatingRecordReader`, `RecordingTemplateReader`, `RefusingIncidentStore`), a logger that keeps its messages (`ListLogger`) and a writer that cancels the run (`CancellingWriter`) (implemented); the forensics report tests add two decorators for `IIncidentStore` in `tests/Vandox.Backend.Tests`: `RecordingIncidentStore` (passes every call to another store and keeps a copy of every incident query, for the limit and start of each page) and `ScriptedIncidentStore` (answers the incident queries with scripted pages, optionally repeating the last one, and stops a runaway loop; for the page checks and the changed count between the two passes), next to `RecordingRecordReader` and `RepeatingRecordReader` for the record pages (implemented); the incidents view adds `FakeIncidentBrowser` (`IIncidentBrowser` with scripted results per method, copies of every call with its token, the highest number of calls in flight, a `Before` hook that can wait for a token or signal, and scripted failures) and `ScriptedRecordReader` (an `IRecordReader` over snapshots, connection snapshots and metric points that a test adds, answering a query as the store does with kind, source, metric name, range, keyset position, order and limit, so a page query that drops the source or the name returns the records of other series; it keeps a copy of every query with its token, counts the calls in flight and has a `Before` hook and a scripted failure), both in `tests/Vandox.Backend.Tests` (implemented); the report page and its download add `ReportSamples` (the hand-built summaries, outages and activities of the report tests: a rich sample that has every part of the report and a sparse one with the variants it cannot show, `WriteAsync` feeds them to any `IReportWriter`; the golden files `testdata/report/forensics-{en,de}.md` are its output as the unchanged writer of 91f6bde made it), `ReportPieces` (`FromMarkdown` and `FromHtml` read the headings, paragraphs, list items and table cells of a Markdown report or of the `article.report` element of a page, so that the two can be compared), `ReportPageHarness` (the report page with a `ReportService` over a `ScriptedRecordReader` and a `ScriptedIncidentStore`, or over a real store; fake clock, `ListLogger`, `RecordingNavigationManager`, static and interactive rendering, the pieces of the report, buttons and links by text and address), `GateProbeIncidentStore` (an `IIncidentStore` that counts reads, saves and the calls in flight, for the gate), `StartingResponseFeature` and `StartingResponseBody` (a response that reports `HasStarted` once a byte was written or the body was flushed, as Kestrel does, and refuses synchronous I/O as the server does, for the download handler under a `DefaultHttpContext`) and a scripted failure at a given query of `ScriptedIncidentStore` (`FailAtCall`, `Failure`) (implemented) |
| log parsers (C#) | `FakeParser` (scripted `Detect` and `Parse`) in `tests/Vandox.Core.Tests`, `LineParser` and `ProbeParser` (counts starts and completions, can stop reading early) in `tests/Vandox.Import.Tests`; `RecordingEmitter` (records, skips and warnings, validates every record like the importer except the payload size, optional failure and observer), `CountingEmitter` (validates like `RecordingEmitter`, so also except the payload size, keeps no record, counts per event, priority and warnings), `JournalExportBuilder` (text and binary fields as bytes), `MariaDbSamples` (records, journal entries, syslog lines and the feeds of the error log fixture for the MariaDB line tests), `PatternStream` (lazily generated input for the heap-bound tests), `LegacyMonitorSamples` (summary lines, rows, sockets and snapshots of the legacy monitoring log as text) and `SegmentStream` (byte segments repeated lazily, for the heap-bound tests of the legacy monitoring log parser), `SignatureSamples` (log line records, catalog texts and a detector run for the signature tests) and `DiscardingEventCollection` (counts the detected events and keeps none, for the heap-bound test) in `tests/Vandox.Core.Tests` (implemented) |
| files (C#) | `TempDirectory` helper per test project (a unique directory below the temp path, removed on dispose); `RepositoryFiles.Path` for fixtures such as `testdata/` and `deploy/` |
| Blazor components (C#) | `RecordingJsRuntime` (`IJSRuntime`: records identifier, arguments and token of every call in `RecordedJsCall` entries, hands out `RecordingJsObjectReference` instances for `import` (module) and `create` or `createStacked` (chart), and throws an exception scripted with `Fail(target, identifier, exception)`), `RecordingJsObjectReference` (`IJSObjectReference`: records its calls and its `DisposeAsync` in the call list of its runtime), `TestNavigationManager` (`NavigationManager` initialized with `http://localhost/`, for `NavLink` under `HtmlRenderer`; it records the navigations it is asked for in `Navigations` and does not navigate), `RecordingNavigationManager` (`NavigationManager` that starts at any address, records the targets of `NavigateTo` and applies them as the new address) and `FakeLogExplorer` (`ILogExplorer` with scripted results per method, copies of every query with its token, the highest number of calls in flight, a `Before` hook that can wait for a token or signal, and scripted failures; `LogPageHarness` wires it with the renderer, clock and logger for the page tests) (implemented), `IncidentPageHarness` (the same for the incident pages: a `FakeIncidentBrowser`, an `IncidentWindowReader` over a `ScriptedRecordReader`, clock, logger, recording script runtime and a fixed antiforgery token; static and interactive rendering, and a wait for a condition on the markup that the renderer signals after each render) (implemented) and `ComponentTestRenderer` (a `Renderer` subclass that renders a root component, runs `OnAfterRenderAsync`, re-renders it with new parameters, collects the exceptions the components hand to the renderer and waits for a condition after each render with `WaitUntilAsync`; no bUnit), all in `tests/Vandox.Backend.Tests`; static markup through `HtmlRenderer` as in `HomePageTests`; the web sources (`.razor`, scripts, style sheets) are checked by `WebSourceScanner` in `WebUiContentTests` (implemented); for the web login (all in `tests/Vandox.Backend.Tests`, implemented): `LoginTestClient` (an `HttpClient` for the web port of the running service, no automatic redirects and its own cookie jar that also sends `Secure` cookies over plain HTTP; reads the antiforgery token and `_handler` from the login form, posts the login form with optional extra headers, posts `/logout` with the token of the signed-in page, posts form and multipart bodies, copies cookies into a second client), `ScriptedPasswordVerifier` (`IPasswordVerifier`: accepts one password, counts calls, records overlapping calls and can block until released), `FixedAuthenticationStateProvider` (`AuthenticationStateProvider` returning a given principal), `FixedAntiforgeryStateProvider` (`AntiforgeryStateProvider` with one fixed token, for `<AntiforgeryToken />` under a plain renderer), `FixedAntiforgeryValidationFeature` (`IAntiforgeryValidationFeature` with a given result), `RecordingAuthenticationService` (`IAuthenticationService` that records sign-ins and sign-outs), `LoginServiceFixture` (a `LoginService` over a `ScriptedPasswordVerifier`, a `LoginThrottle`, a `FakeTimeProvider` and a `ListLogger`, with request contexts), `CountingTextReader` (a `TextReader` that counts the characters taken from it), `ReferenceVectors` (the argon2id vectors of `testdata/argon2/reference-argon2id.txt`); `BackendFixture` passes `VANDOX_WEB_PASSWORD_HASH` (the hash of `BackendFixture.Password` at the lowest accepted cost, a constant) when a caller gives no environment, can write the option `web.trusted_proxies`, starts the service with a given clock and reads the JSON log lines |
| Telegram Bot API | fake Telegram client (records sent messages, returns scripted updates; no network) |
| Tailscale network | none in code (0017: `vandoxd` embeds no Tailscale); the agent's sender is tested against a `net/http/httptest` server standing in for the ingest port; the ACL itself is deployment configuration, reviewed, not unit-tested |
| time | Go: injectable clock (spool age, live/backfill classification, deadlines); the scheduler and the watchdog loop read the `time` package and are tested in `testing/synctest` bubbles (0062; implemented); C#: `TimeProvider` with `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`) |

A double of an ASP.NET Core request or response stream behaves as Kestrel does: it refuses synchronous I/O, and
a response stream starts the response on any flush, not only when a byte is written (`StartingResponseBody` is
the reference). A
test double hid this once: it started the response only on a written byte, and only a Security delta review with
a Kestrel probe found the real behavior. Any other claim about server behavior that a double encodes is checked
once against real Kestrel.

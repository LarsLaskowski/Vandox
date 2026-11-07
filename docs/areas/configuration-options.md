# Configuration options

## Scope

Every option of the configuration files of the agent (`/etc/vandox/agent.yaml`) and the backend (`/etc/vandox/vandoxd.yaml`): its key,
default, accepted values and error text. The operator who writes the files and both binaries, which check every option at start-up,
rely on it. How the file is read and parsed and how secrets are supplied is in [Configuration and
secrets](configuration-and-secrets.md); what an option does at run time is in the area of the feature, linked at each option.

## Common rules

- **Errors.** Every option error reads `config: <file>:<line>: <key>: <reason>`, at the line of the named key, else of the element (of
  the key for a list itself), and never contains the value, a pattern's compile error or other text of the document. A null value is
  refused as `has no value`, a value of another YAML type with the decoder's reason for the type (such as `invalid value, want a
  string` or `invalid value, want a list`), before the option's own rule. The agent stops at start-up with exit code 1 on every
  option error, the backend with exit 1 ([Backend host](backend-host.md), *Command line and exit codes*).
- **Lists of mappings.** The strict decoder walks each element of `processes.programs`, `backups.processes`, `log_shipping.files` and
  `log_shipping.drop` like a section: an unknown or duplicate key (`unknown key`, `duplicate key`), an anchor, an alias or a custom tag
  inside an element, a null element (`has no value`) and an element that is not a mapping (`must be a section (a mapping of keys)`)
  are refused with the line of the element or key and the key path `<list>[<index>]` (and `.<key>`). A null element of any agent list
  is refused, never dropped.
- **Paths.** An absolute, clean path has no trailing slash and no empty, `.` or `..` element. The loader creates no directory.
- **Listen addresses.** A host and a port separated by a colon. The host is empty (all interfaces) or an IP literal (an IPv6 literal in
  brackets); a host name is rejected, because binding to a name is ambiguous. The port is one to five ASCII digits and 1 to 65535; a
  sign or a space is not accepted (`:+80` is rejected).
- **Sizes.** `spool.max_size`, `spool.min_free` and `ingest.min_free` are one to five ASCII digits without a leading zero (`0` alone is
  allowed), followed directly by `MiB` or `GiB`; the range is checked on the bytes. Any other text (`1024`, `1 GiB`, `1.5GiB`, `1GB`,
  `1gib`, `-1GiB`, `+1GiB`, `01GiB`, a value outside the range, an empty string) is refused. `0MiB` or `0GiB` switches a free-space
  check off. A key that is absent keeps its default.

## Agent

| Key | Default | Rule |
| --- | ------- | ---- |
| `agent_id` | none, required | 1 to 64 characters of `A-Z a-z 0-9 . _ -`, starting with a letter or digit (the rule of the [wire format](wire-format.md) for agent IDs) |
| `backend.url` | none, required | URL with scheme `http` or `https`; a host that is an IP address or a name of letters, digits, `.` and `-` (checked after percent-decoding); an optional port from 1 to 65535; no user info, query, fragment or path other than `/` |
| `backend.backfill_bandwidth` | `256KiB` | a whole number of one to five digits without a leading zero, then `KiB` or `MiB`, from `16KiB` to `1024MiB`; error text `must be a whole number of KiB or MiB from 16KiB to 1024MiB, such as 256KiB or 4MiB` |
| `spool.directory` | `/var/lib/vandox/spool` | absolute, clean path |
| `spool.max_size` | `2GiB` | a whole number of MiB or GiB from `64MiB` to `64GiB` |
| `spool.min_free` | `1GiB` | a whole number of MiB or GiB from `0MiB` to `1024GiB`; zero switches the check off |
| `log.level` | `info` | `debug`, `info`, `warn` or `error` |
| `filesystem.mount_points` | `["/"]` | a list of at most 16 absolute, clean paths of at most 1,024 bytes each, every path listed once |
| `processes.programs` | `[]` | a list of at most 64 rules, each a mapping of `program`, one of `command` and `cmdline_prefix`, and optionally `append_next_word` |
| `services.units` | `[sw-cp-server, sw-engine, psa, nginx, apache2, postfix, dovecot, mariadb, plesk-php*-fpm, php*-fpm, fail2ban]` | a list of at most 64 service unit names or patterns of 1 to 255 bytes |
| `mariadb.enabled` | `true` | `true` or `false` |
| `mariadb.socket` | `/run/mysqld/mysqld.sock` | absolute, clean path of 2 to 107 bytes |
| `backups.processes` | `[{command: pzstd}, {cmdline_prefix: "/opt/psa/admin/sbin/backup-archiver --pack "}, {cmdline_prefix: "/usr/lib/plesk-9.0/sw-tar --create "}]` | a list of at most 64 rules, each a mapping of exactly one of `command` and `cmdline_prefix` |
| `log_shipping.journal` | `true` | `true` or `false` |
| `log_shipping.min_priority` | `info` | `emerg`, `alert`, `crit`, `err`, `warning`, `notice`, `info` or `debug` |
| `log_shipping.files` | `[]` | a list of at most 16 mappings of `path` and `format` |
| `log_shipping.drop` | `[]` | a list of at most 64 rules, each a mapping of at least one of `log`, `program`, `priority` and `message` |

### Backend URL and backfill

- Plain `http` is allowed for `backend.url`, because the tailnet encrypts and authenticates the transport. Whether the host is a
  tailnet address is not checked; the network ACL is the boundary ([Network and security model](network-and-security-model.md)).
  `http` is meant only for the backend host's tailnet address (MagicDNS names do not resolve once Tailscale's DNS is off on the
  server), and the scheme must match the listener ([Agent sending](agent-sending.md)).
- `backend.backfill_bandwidth` is the bandwidth of the agent's backfill: the bytes a second of compressed request body it sends while
  it catches up on the spool after an outage ([Agent sending](agent-sending.md); the live stream is not limited). It is checked right
  after `backend.url`; the value is written as `256KiB`, not as a number of bytes, and a decimal point, a unit other than `KiB` or `MiB`,
  a space or a value out of range is refused with the text in the table.

### Spool

- `spool.directory` holds the spool's files (`segment-<number>.spool`, `spool-meta.json` and its temporary file; [Agent
  spool](agent-spool.md)) and the agent's state file `agent-state.json` (the last boot, the position in the journal, the open backup
  run and the positions of the `logs` collector, stored by the SHA-256 of each file's path, never the path, up to 64 KiB; [Agent](agent.md),
  *Agent state*). The agent never creates the directory: the installation does, writable by the agent's user. A directory that is
  missing, not a directory, a symbolic link (its last element) or not writable stops the agent at start-up with `agent cannot start`
  and exit code 1 (the spool's start errors, [Agent spool](agent-spool.md)). An existing directory that holds only `agent-state.json`
  becomes the spool's directory without migration.
- `spool.max_size` bounds the size of all of the spool's files on disk together; when a write would exceed it, the spool drops its
  oldest records with a recorded gap. `spool.min_free` is the space that must stay available to unprivileged users on the spool's file
  system after every write; when less would remain, the spool drops its oldest records the same way ([Agent spool](agent-spool.md)).
  Both follow *Sizes* (refused, for example: `32MiB` or `65GiB` for `spool.max_size`, `1025GiB` for `spool.min_free`);
  `spool.max_size` is checked before `spool.min_free`:

  | Key | Reason |
  | --- | ------ |
  | `spool.max_size` | `must be a whole number of MiB or GiB from 64MiB to 64GiB, such as 512MiB or 2GiB` |
  | `spool.min_free` | `must be a whole number of MiB or GiB from 0MiB to 1024GiB, such as 0MiB or 1GiB` |

### File systems

`filesystem.mount_points` names the mount points whose use the agent records ([Agent system metrics](agent-system-metrics.md)). The
list replaces the default `["/"]`; an empty list `[]` records none and the agent does not run the file system collector. At most 16
entries, each an absolute, clean path of 1 to 1,024 bytes, no two equal; an element that YAML would read as another type (`1`, `true`,
`2026-01-01`) is the text it was written as and fails the rule as not absolute. Anything else is refused with `filesystem.mount_points:
must be a list of at most 16 absolute, clean paths of at most 1024 bytes each (no trailing slash, no . or .. elements), each listed
once`. A null element (`~`, `null`, `!!null` or an empty `-`) is refused with `has no value` and the line of the element: the YAML
library would otherwise drop it silently, and an all-null list would turn into `[]` and switch the file system metrics off. A value
is recorded only when it is a mount point in the agent's own mount namespace; otherwise the agent logs that once and leaves it out.

### Process programs

`processes.programs` names the program a process belongs to ([Agent system metrics](agent-system-metrics.md), *Processes*). It is a
list of at most 64 rules in file order; the first rule that matches a process gives its program. Each rule is a mapping with
`program` (required: 1 to 128 characters of `A-Z a-z 0-9 . _ : / @ + -`, starting with a letter or digit), exactly one of `command`
(compared with the command of the process as a whole) and `cmdline_prefix` (compared literally with the start of the command line),
each 1 to 256 bytes without characters of the categories Cc, Cf, Zl or Zp, and `append_next_word` (true or false, default false, only
with `cmdline_prefix`), which appends the word after the prefix as `<program>/<word>`. A rule is a literal comparison; there is no
pattern language.

| Key | Reason |
| --- | ------ |
| `processes.programs` (more than 64 rules) | `must be a list of at most 64 rules` |
| `processes.programs[<i>].program` | `must be 1 to 128 characters of [A-Za-z0-9._:/@+-], starting with a letter or digit` |
| `processes.programs[<i>]` (neither or both of `command` and `cmdline_prefix`) | `must set exactly one of command and cmdline_prefix` |
| `processes.programs[<i>].command` or `.cmdline_prefix` (empty is "not set"; more than 256 bytes) | `must be 1 to 256 bytes` |
| `processes.programs[<i>].append_next_word` (without `cmdline_prefix`) | `needs cmdline_prefix` |
| `processes.programs[<i>]` (null element) | `has no value` |
| `processes.programs[<i>]` (element that is not a mapping) | `must be a section (a mapping of keys)` |

### Services

`services.units` names the services whose state the agent records ([Agent services and backup runs](agent-services.md)). The list
replaces the default (it is not extended); an empty list `[]` switches the `systemd` collector off, so the agent does not connect to the
system bus. An entry is a unit name or a pattern, and an entry without a unit type suffix means `<entry>.service`, as `systemctl` reads
it: the suffix after the last dot decides, so `php8.1-fpm` becomes `php8.1-fpm.service`. `*` and `?` are the only pattern characters;
they are matched by systemd against the names of the loaded units. An entry is 1 to 255 bytes long (counted after the implied
`.service`) of `A-Z a-z 0-9 : _ . @ - * ?`, does not start with `.` or `-`, holds at most one `@` and names a service: the suffixes
`.socket`, `.timer`, `.target`, `.mount`, `.automount`, `.swap`, `.path`, `.slice`, `.scope` and `.device` are refused, and so is a
template without an instance (`foo@.service`, `@foo`). Two entries that are equal once `.service` is added (`nginx` and
`nginx.service`) are refused. The loader stores every entry as its full name.

| Key | Reason |
| --- | ------ |
| `services.units` (more than 64 entries) | `must be a list of at most 64 unit names` |
| `services.units[<i>]` (empty; over 255 bytes with the implied `.service`; a character outside the set; starting with `.` or `-`; more than one `@`) | `must be a service unit name or pattern of 1 to 255 bytes of [A-Za-z0-9:_.@*?-], not starting with . or -` |
| `services.units[<i>]` (another unit type suffix) | `must name a service unit` |
| `services.units[<i>]` (`foo@.service`, `@foo`) | `must name an instance, not a template` |
| `services.units[<i>]` (equal to an earlier entry) | `listed twice` |
| `services.units` (null element) | `has no value` |
| `services.units`, `services.units[<i>]` (an element or the list of another YAML type) | the decoder's reason for the type, as for the other agent lists |

### MariaDB

`mariadb.enabled` and `mariadb.socket` configure the `mariadb` collector ([Agent MariaDB collector](agent-mariadb.md)). With
`mariadb.enabled: false` the collector is not scheduled and the agent never connects to the socket; a value that is not a boolean is
refused by the decoder with the key and the line. `mariadb.socket` is the path of MariaDB's Unix socket; the default is the socket of
Ubuntu's MariaDB package, which Plesk uses, and the option exists for a server that differs. It must be an absolute, clean path of 2
to 107 bytes, 107 being the limit of a Unix socket address; anything else, the empty value included, is refused with `mariadb.socket:
must be an absolute, clean path of at most 107 bytes`, a value with a control, format or line-separating character by the general
string rule ([Configuration and secrets](configuration-and-secrets.md), *Configuration file*). The collector itself never logs the path.

### Backup runs

`backups.processes` names the processes that mark a running backup ([Agent services and backup runs](agent-services.md)). The list
replaces the default (three rules: `command: pzstd` and the prefixes `/opt/psa/admin/sbin/backup-archiver --pack ` and
`/usr/lib/plesk-9.0/sw-tar --create `, the Plesk backup processes captured on a real server, #287; `--pack` and `--create` leave out a
restore); an empty list `[]` switches the `backups` collector off, so it writes no record and no log line, and a section without the
key or a null section keeps the default. It is a list of at most 64 rules in file order; a process is part of a backup when any rule
matches it. Each rule is a mapping with exactly one of `command` (compared with the command of the process as a whole) and
`cmdline_prefix` (compared literally with the start of the command line), each 1 to 256 bytes without characters of the categories
Cc, Cf, Zl or Zp; `*`, `?` and other characters are literal text. The keys `program` and `append_next_word` are unknown here.

| Key | Reason |
| --- | ------ |
| `backups.processes` (more than 64 rules) | `must be a list of at most 64 rules` |
| `backups.processes` (a value that is not a list) | `invalid value, want a list` |
| `backups.processes[<i>]` (neither or both of `command` and `cmdline_prefix`; empty is "not set") | `must set exactly one of command and cmdline_prefix` |
| `backups.processes[<i>].command` or `.cmdline_prefix` (more than 256 bytes) | `must be 1 to 256 bytes` |
| `backups.processes[<i>]` (null element) | `has no value` |
| `backups.processes[<i>]` (element that is not a mapping) | `must be a section (a mapping of keys)` |
| `backups.processes[<i>].<key>` (an unknown or duplicate key) | `unknown key`, `duplicate key` |

### Log shipping

`log_shipping` configures the `logs` collector ([Agent log shipping](agent-log-shipping.md)); a section without a key, or a null
section, keeps the defaults, which ship the journal at `info` and more severe and no file.

- `log_shipping.journal` switches the shipping of the journal (the system and the user journal files); `false` reads no journal. A
  value that is not a boolean is refused by the decoder with the key and the line.
- `log_shipping.min_priority`: a line with a known priority less severe than this is not shipped; a line without a priority (the
  lines of a traditional syslog file) is never dropped by it. The names are compared exactly (lower case). The filter decides before
  the agent classifies the line ([Agent log shipping](agent-log-shipping.md), *Classification*): a MariaDB entry begins with a header
  line, and the journal's MariaDB lines are assumed to carry priority 6 (the unit's `SyslogLevel=`, not checked on the server), so
  `notice` or stricter drops them and no `mariadb.*` event is made from the journal; a dropped header leaves its continuation lines to
  the open entry of the same host, program and process within 60 seconds, or else they are shipped as plain lines. A drop rule works
  the same way.
- `log_shipping.files` names the log files shipped besides the journal, in file order: each a mapping with `path`, an absolute, clean
  path of 2 to 1,024 bytes, listed once, and `format`, which is one of `syslog`, `plesk-panel`, `nginx-error`, `apache-error` and
  `sw-cp-server-error`, compared exactly (lower case). The last one is for Plesk's own web server (`/var/log/sw-cp-server/error_log`,
  nginx's line format) and is chosen by configuration only: the agent finds no file by content. The error log formats are the
  import's ([Agent log shipping](agent-log-shipping.md), *Error log lines*); the agent reads those files only when the operator has
  granted it read (`deploy/agent/README.md`). A path with U+0000 or another control or format character is refused by the general
  string rule. The path is opened at run time without following a link in any element. A file that holds what the journal ships
  (`/var/log/syslog` on a server whose rsyslog copies the journal) is shipped twice, once per source; the example file says so.
- `log_shipping.drop` holds rules for lines that are not shipped: each a mapping with at least one of `log` (`journal` or a path listed
  in `log_shipping.files`, compared exactly), `program` (compared whole and literally with the line's program), `priority` (a priority
  name: the line has that priority or a less severe one; a line without a priority never matches) and `message` (an RE2 regular
  expression in Go syntax, searched anywhere in the message unless anchored). An empty value is "not set". A line that meets every
  condition of a rule is not shipped. A pattern is at most 1,024 bytes and its compiled program at most 400 instructions, and the
  programs of all patterns together at most 400 (about a dozen ordinary patterns; a rule without `message` costs nothing): the count is
  that of the program an RE2 engine builds from the pattern (Perl syntax, simplified), taken before any expression is built, because
  matching costs about as much per line as the program is long (about 90 ms for a 16 KiB line at the bound on the measured host).

| Key | Reason |
| --- | ------ |
| `log_shipping.min_priority`, `log_shipping.drop[<i>].priority` | `must be one of emerg, alert, crit, err, warning, notice, info, debug` |
| `log_shipping.files` (more than 16 files) | `must be a list of at most 16 files` |
| `log_shipping.files[<i>].path` | `must be an absolute, clean path of at most 1024 bytes (no trailing slash, no . or .. elements)` |
| `log_shipping.files[<i>].path` (equal to an earlier path; at the later one) | `listed twice` |
| `log_shipping.files[<i>].format` | `must be one of syslog, plesk-panel, sw-cp-server-error, nginx-error, apache-error` |
| `log_shipping.drop` (more than 64 rules) | `must be a list of at most 64 rules` |
| `log_shipping.drop[<i>]` (no condition set) | `must set at least one of log, program, priority and message` |
| `log_shipping.drop[<i>].log` | `must be journal or a path listed in log_shipping.files` |
| `log_shipping.drop[<i>].message` (does not compile, over 1,024 bytes or over 400 instructions) | `must be an RE2 pattern of at most 1024 bytes and 400 compiled instructions` |
| `log_shipping.drop` (patterns over 400 instructions together) | `patterns must compile to at most 400 instructions together` |

## Backend

| Key | Default | Rule |
| --- | ------- | ---- |
| `web.listen` | `:8080` | `[host]:port` |
| `web.trusted_proxies` | empty | a comma-separated list of at most 16 IP addresses or networks |
| `ingest.listen` | `:8081` | `[host]:port`, a port different from `web.listen` |
| `ingest.agent_id` | none (optional) | the agent ID rule of the [wire format](wire-format.md): 1 to 64 characters of `A-Z a-z 0-9 . _ -`, starting with a letter or digit |
| `ingest.min_free` | `1GiB` | a whole number of MiB or GiB from `0MiB` to `1024GiB`, the rule of `spool.min_free`; zero switches the check off |
| `storage.directory` | `/data` | absolute, clean path |
| `log.level` | `info` | `debug`, `info`, `warn` or `error` |
| `import.plesk_panel_log` | `/var/log/plesk/panel.log` | absolute, clean path of a file in a directory |
| `import.sw_cp_server_error_log` | `/var/log/sw-cp-server/error_log` | absolute, clean path of a file in a directory |
| `import.nginx_error_log` | `/var/log/nginx/error.log` | absolute, clean path of a file in a directory |
| `import.apache_error_log` | `/var/log/apache2/error.log` | absolute, clean path of a file in a directory |
| `import.time_zone` | none (optional) | a time zone ID of the IANA time zone database, compared ordinally (`UTC`, `Etc/UTC`, `Europe/Berlin`; not `europe/berlin`, an offset or a Windows name) |
| `incidents.window_before` | `1h` | a duration of 1 minute to 24 hours |
| `incidents.window_after` | `30m` | a duration of 1 minute to 24 hours |

- `web.trusted_proxies` lists the reverse proxies whose `X-Forwarded-For` and `X-Forwarded-Proto` the web listener trusts ([Web
  login](web-login.md), *Reverse proxy trust*). Entries are separated by `,` and trimmed of spaces (U+0020 only); each is `address` or
  `address/prefix`. An IPv4 address is dotted decimal with four parts 0 to 255 without leading zeros; an IPv6 address has no zone and
  no brackets. The prefix is one to three digits without leading zero, 8 to 32 for IPv4 and 16 to 128 for IPv6; without a prefix the
  entry is a single address. Host bits below the prefix must be zero, so `172.18.0.1/16` is refused (write `172.18.0.0/16`). Refused:
  the unspecified address, an IPv4-mapped IPv6 address (write the IPv4 form), short or hexadecimal forms such as `127.1` or
  `0x7f.0.0.1`, host names, ports, an empty entry, and more than 16 entries. An empty value trusts no proxy. The error reads
  `web.trusted_proxies: must be a comma-separated list of at most 16 IP addresses or networks, such as 172.18.0.1 or 172.18.0.0/16`. It
  is an option, not a secret: an environment variable cannot set it.
- `ingest.agent_id` names the one agent whose batches the ingest endpoint accepts with the token ([Ingest
  endpoint](ingest-endpoint.md)); it is compared ordinally with the header's `agent_id`, so `Web-1` is not `web-1`. It has no default:
  while it or the secret `VANDOX_AGENT_TOKEN` is unset, the service runs without ingest. A value that breaks the rule (an empty string
  included) is refused with `ingest.agent_id: must be 1 to 64 characters of [A-Za-z0-9._-], starting with a letter or digit`.
- `ingest.min_free` is the free space of the file system of `storage.directory` below which ingest writes nothing and answers `503`
  ([Ingest endpoint](ingest-endpoint.md), *Free-space floor*). Its form is that of `spool.min_free` (*Sizes*: a size such as `512MiB`
  or `1GiB`; a bare number, a sign, a leading zero, `MB` or `GB` and more than `1024GiB` are refused); the error reads
  `ingest.min_free: must be a whole number of MiB or GiB from 0MiB to 1024GiB, such as 0MiB or 1GiB`. The web UI and the import do
  not check it.
- A duration (`incidents.window_before`, `incidents.window_after`; [Incidents](incidents.md)) is one to four ASCII digits followed by
  `m` (minutes) or `h` (hours), nothing before or after, and from 1 minute to 24 hours (`30m`, `1h`, `24h`, `1440m`). A sign, a space,
  a fraction, another digit script, a second unit (`1h30m`), a missing unit (`60`) and any other unit (`1d`, `1s`) are rejected. The
  error reads `incidents.window_before: must be a whole number of minutes or hours from 1m to 24h, such as 30m or 1h` (and the same
  with `incidents.window_after`).
- `import.time_zone` has no default: the value is `null` when the key is absent, also with an `import:` section that is null or only
  holds comments. A value that is no zone ID, and an empty value, is refused with the reason `must be a time zone of the IANA time
  zone database, such as UTC or Europe/Berlin`. While it is unset, `vandoxd import` fails a syslog file at its first line without a
  year, a MariaDB error log at its first entry, a Dovecot log file at its first Dovecot line, a Plesk or web server error log at its
  first line of the format and a legacy monitoring log at its first snapshot ([Log import](log-import.md)); the service itself does
  not use it.
- The four error log paths (`import.plesk_panel_log`, `import.sw_cp_server_error_log`, `import.nginx_error_log`,
  `import.apache_error_log`) name where the server writes these logs; `vandoxd import` compares them as text with the names of the
  imported files ([Plesk and web server error log parsers](log-parsers-web.md)) and never opens them. A value must be an absolute,
  clean path with at least two elements (a directory and a file name); otherwise, and when empty, it is refused with the reason `must
  be an absolute, clean path of a file in a directory (no trailing slash, no . or .. elements)`. The service itself does not use them.

## Related decisions

- [0049](../decisions/0049-strict-configuration-file-schema-and-errors.md) — why the file is strict and errors never echo values.
- [0105](../decisions/0105-forwarded-headers-only-from-trusted-proxies-failed-logins-locked-per-client-and-capped.md) — why
  `web.trusted_proxies` is a comma-separated string.
- [0113](../decisions/0113-process-collector-pss-from-smaps-rollup-top-processes-full-snapshot-under-memory-pressure.md) — why the process grouping rules are a list of mappings of literal comparisons that the strict decoder checks.
- [0117](../decisions/0117-systemd-collector-hand-written-d-bus-client-per-run-states-recorded-on-change.md) — why the units to track are a list of names and patterns, an entry without a suffix is a service, and other unit types are refused.
- [0118](../decisions/0118-mariadb-collector-hand-written-client-captured-statements-refusals-as-not-answering-with-error-code.md) — why the MariaDB collector, which these options switch and point to its socket, connects over the local socket with a client of its own, one connection per run.
- [0120](../decisions/0120-oom-kill-events-triggered-snapshots-boots-from-saved-state-and-backup-runs.md) — why backup runs are named by literal process rules with `pzstd` and Plesk's two creating processes as the defaults, and why the agent's state lives in `spool.directory` without a new option.
- [0121](../decisions/0121-log-shipping-journal-and-syslog-files-positions-per-seqnum-id-zstd-commit-when-kept.md) — why log shipping reads the journal, `syslog` files and the Plesk and web server error log files, why drop patterns are bounded by their compiled size, and why the state file stores a file's position by the hash of its path.
- [0123](../decisions/0123-agent-spool-segment-files-clock-bound-sequence-numbers-range-acknowledgements.md) — why `spool.max_size` is
  at most 64 GiB and the spool's files are uncompressed.
- [0125](../decisions/0125-sender-two-streams-shared-backoff-refused-records-given-up-with-a-gap.md) — why the backfill has its own,
  configurable bandwidth.
- [0126](../decisions/0126-ingest-endpoint-token-bound-to-one-agent-id-two-slots-through-the-commit-no-rate-cap.md) — why the
  backend binds the one token to `ingest.agent_id` and stops ingest below `ingest.min_free`.

## Not here

- The file itself, strict parsing, the general string rule, secrets and the order and form of load errors: [Configuration and
  secrets](configuration-and-secrets.md).
- What an option does at run time: the area linked at each option.

## Implementation

Agent: `internal/config` (Go; `LoadAgent`; `decode.go` refuses a null list element before decoding, `walkList` walks a list of mappings,
`checkMountPoints`, `checkProgramRules`, `checkServiceUnits`, `checkSocketPath`; drop patterns are parsed with `regexp/syntax`,
simplified and compiled to count their instructions). Backend: `Vandox.Core.Configuration` (C#; `BackendConfigLoader`, `ConfigDuration`
for the durations, `SourceTimeZone` for `import.time_zone`).

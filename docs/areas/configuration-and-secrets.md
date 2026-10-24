# Configuration and secrets

## Scope

How the agent (`vandox-agent`) and the backend (`vandoxd`) are configured: the strictly parsed configuration file
for every option, and the environment for secrets. The rules hold for both binaries, whatever language they are
written in; each binary implements them itself and a change to a rule changes both (see *Implementation*). What the
options and secrets do at run time is described in the area that owns the feature.

## Configuration file

- Each binary reads one YAML file: `/etc/vandox/agent.yaml` for the agent and `/etc/vandox/vandoxd.yaml` for the
  backend (mounted into the container). Each binary takes another path with `-config <path>`. The file is read once, at
  start-up (the agent's start-up is described in [Agent](agent.md), *Command line and start-up*).
- The file must be a regular file of at most 1 MiB. It is opened without blocking and its type is checked on the
  opened file, never only on the path before the open, so a FIFO or device named as the file, or swapped in while
  it is opened, is refused without being read and cannot block start-up. An empty file, a file with only comments, or a document that is just `---` or `~` means "defaults
  only" (for the agent, whose `agent_id` and `backend.url` have no default, that is an error naming the missing key).
- The file is **strict**. Only keys of the schema are accepted, and they are case-sensitive. These are errors:
  - an unknown key, a duplicate key, a key that is not a string;
  - more than one YAML document (a trailing `---` counts as a second one, even after an empty first document);
  - anchors, aliases and merge keys;
  - a tag outside the core schema (`!!str`, `!!int`, `!!bool`, `!!float`, `!!null`, `!!timestamp`, `!!map`,
    `!!seq`), judged on the tag the parser resolved for the node and not on the source text, so tag re-mappings,
    verbatim tags, `!!set` and `!!omap` are rejected; a `!!null` tag on a value other than `""`, `~`, `null`,
    `Null` or `NULL` is rejected as well;
  - a section with a non-mapping value; a leaf option or an element of a list with no value (the agent: a null element is refused, never dropped);
  - the backend also rejects nesting deeper than 64 levels.
- A section key with a null value keeps the defaults of that section.
- A string value must not contain characters of the Unicode categories Cc, Cf, Zl or Zp (control and format
  characters such as U+202E, line and paragraph separators), so a value that is logged later cannot break or
  disguise a log line.
- **Non-secret options exist only in the file.** There are no environment overrides, no command-line options
  for them (the file's path is the only thing a flag selects) and no merging of several files.

## Options

Agent:

| Key | Default | Rule |
| --- | ------- | ---- |
| `agent_id` | none, required | 1 to 64 characters of `A-Z a-z 0-9 . _ -`, starting with a letter or digit (the rule of the [wire format](wire-format.md) for agent IDs) |
| `backend.url` | none, required | URL with scheme `http` or `https`; a host that is an IP address or a name of letters, digits, `.` and `-` (checked after percent-decoding); an optional port from 1 to 65535; no user info, query, fragment or path other than `/` |
| `backend.backfill_bandwidth` | `256KiB` | a whole number of one to five digits without a leading zero, then `KiB` or `MiB`, from `16KiB` to `1024MiB`; error text `must be a whole number of KiB or MiB from 16KiB to 1024MiB, such as 256KiB or 4MiB` (see below) |
| `spool.directory` | `/var/lib/vandox/spool` | absolute, clean path (see below) |
| `spool.max_size` | `2GiB` | a whole number of MiB or GiB from `64MiB` to `64GiB` (see below) |
| `spool.min_free` | `1GiB` | a whole number of MiB or GiB from `0MiB` to `1024GiB`; zero switches the check off (see below) |
| `log.level` | `info` | `debug`, `info`, `warn` or `error` |
| `filesystem.mount_points` | `["/"]` | a list of at most 16 absolute, clean paths of at most 1,024 bytes each, every path listed once (see below) |
| `processes.programs` | `[]` | a list of at most 64 rules, each a mapping of `program`, one of `command` and `cmdline_prefix`, and optionally `append_next_word` (see below) |
| `services.units` | `[sw-cp-server, sw-engine, psa, nginx, apache2, postfix, dovecot, mariadb, plesk-php*-fpm, php*-fpm, fail2ban]` | a list of at most 64 service unit names or patterns of 1 to 255 bytes (see below) |
| `mariadb.enabled` | `true` | `true` or `false` (see below) |
| `mariadb.socket` | `/run/mysqld/mysqld.sock` | absolute, clean path of 2 to 107 bytes (see below) |
| `backups.processes` | `[{command: pzstd}]` | a list of at most 64 rules, each a mapping of exactly one of `command` and `cmdline_prefix` (see below) |
| `log_shipping.journal` | `true` | `true` or `false` (see below) |
| `log_shipping.min_priority` | `info` | `emerg`, `alert`, `crit`, `err`, `warning`, `notice`, `info` or `debug` (see below) |
| `log_shipping.files` | `[]` | a list of at most 16 mappings of `path` and `format` (see below) |
| `log_shipping.drop` | `[]` | a list of at most 64 rules, each a mapping of at least one of `log`, `program`, `priority` and `message` (see below) |

Backend:

| Key | Default | Rule |
| --- | ------- | ---- |
| `web.listen` | `:8080` | `[host]:port` |
| `web.trusted_proxies` | empty | a comma-separated list of at most 16 IP addresses or networks (see below) |
| `ingest.listen` | `:8081` | `[host]:port`, a port different from `web.listen` |
| `storage.directory` | `/data` | absolute, clean path |
| `log.level` | `info` | `debug`, `info`, `warn` or `error` |
| `import.plesk_panel_log` | `/var/log/plesk/panel.log` | absolute, clean path of a file in a directory |
| `import.sw_cp_server_error_log` | `/var/log/sw-cp-server/error_log` | absolute, clean path of a file in a directory |
| `import.nginx_error_log` | `/var/log/nginx/error.log` | absolute, clean path of a file in a directory |
| `import.apache_error_log` | `/var/log/apache2/error.log` | absolute, clean path of a file in a directory |
| `import.time_zone` | none (optional) | a time zone ID of the IANA time zone database, compared ordinally (`UTC`, `Etc/UTC`, `Europe/Berlin`; not `europe/berlin`, an offset or a Windows name) |
| `incidents.window_before` | `1h` | a duration of 1 minute to 24 hours (see below) |
| `incidents.window_after` | `30m` | a duration of 1 minute to 24 hours (see below) |

- A listen address is a host and a port separated by a colon. The host is empty (all interfaces) or an IP literal
  (an IPv6 literal in brackets); a host name is rejected, because binding to a name is ambiguous. The port is one to
  five ASCII digits and 1 to 65535; a sign or a space is not accepted (`:+80` is rejected).
- `web.trusted_proxies` lists the reverse proxies whose `X-Forwarded-For` and `X-Forwarded-Proto` the web listener trusts ([Web login](web-login.md),
  *Reverse proxy trust*). Entries are separated by `,` and trimmed of spaces (U+0020 only); each is `address` or `address/prefix`. An
  IPv4 address is dotted decimal with four parts 0 to 255 without leading zeros; an IPv6 address has no zone and no brackets. The prefix
  is one to three digits without leading zero, 8 to 32 for IPv4 and 16 to 128 for IPv6; without a prefix the entry is a single
  address. Host bits below the prefix must be zero, so `172.18.0.1/16` is refused (write `172.18.0.0/16`). Refused: the unspecified
  address, an IPv4-mapped IPv6 address (write the IPv4 form), short or hexadecimal forms such as `127.1` or `0x7f.0.0.1`, host names,
  ports, an empty entry, and more than 16 entries. An empty value trusts no proxy. The error reads `config: <file>:<line>:
  web.trusted_proxies: must be a comma-separated list of at most 16 IP addresses or networks, such as 172.18.0.1 or 172.18.0.0/16`
  and never contains the value. It is an option, not a secret: an environment variable cannot set it.
- A duration (`incidents.window_before`, `incidents.window_after`) is one to four ASCII digits followed by `m` (minutes) or `h` (hours),
  nothing before or after, and from 1 minute to 24 hours (`30m`, `1h`, `24h`, `1440m`). A sign, a space, a fraction, another digit
  script, a second unit (`1h30m`), a missing unit (`60`) and any other unit (`1d`, `1s`) are rejected. The error reads
  `config: <file>:<line>: incidents.window_before: must be a whole number of minutes or hours from 1m to 24h, such as 30m or 1h`
  (and the same with `incidents.window_after`); it never contains the value.
- `filesystem.mount_points` names the mount points whose use the agent records ([Agent](agent.md), *System metrics*). The list
  replaces the default `["/"]`; an empty list `[]` records none and the agent does not run the file system collector. At most 16
  entries, each an absolute, clean path (no trailing slash, no `.` or `..` element) of 1 to 1,024 bytes, no two equal; an element that
  YAML would read as another type (`1`, `true`, `2026-01-01`) is the text it was written as and fails the rule as not absolute.
  Anything else is refused with `config: <file>:<line>: filesystem.mount_points: must be a list of at most 16 absolute, clean paths of
  at most 1024 bytes each (no trailing slash, no . or .. elements), each listed once`, never with the value. A null element (`~`,
  `null`, `!!null` or an empty `-`) is refused with `has no value` and the line of the element: the YAML library would otherwise drop
  it silently and an all-null list would turn into `[]` and switch the file system metrics off. A value is recorded only when it is a
  mount point in the agent's own mount namespace; otherwise the agent logs that once and leaves it out.
- `processes.programs` names the program a process belongs to ([Agent](agent.md), *Processes*). It is a list of at most 64 rules in
  file order; the first rule that matches a process gives its program. Each rule is a mapping with `program` (required: 1 to 128
  characters of `A-Z a-z 0-9 . _ : / @ + -`, starting with a letter or digit), exactly one of `command` (compared with the command
  of the process as a whole) and `cmdline_prefix` (compared literally with the start of the command line), each 1 to 256 bytes
  without characters of the categories Cc, Cf, Zl or Zp, and `append_next_word` (true or false, default false, only with
  `cmdline_prefix`), which appends the word after the prefix as `<program>/<word>`. A rule is a literal comparison; there is no
  pattern language. The strict decoder walks the list like a section for each element: an unknown or duplicate key, an anchor, an
  alias or a custom tag inside an element, a null element and an element that is not a mapping are refused with the line of the
  element or key and the key path `processes.programs[<index>]` (and `.<key>`). The other errors read
  `config: <file>:<line>: <key>: <reason>`:

  | Key | Reason |
  | --- | ------ |
  | `processes.programs` (more than 64 rules) | `must be a list of at most 64 rules` |
  | `processes.programs[<i>].program` | `must be 1 to 128 characters of [A-Za-z0-9._:/@+-], starting with a letter or digit` |
  | `processes.programs[<i>]` (neither or both of `command` and `cmdline_prefix`) | `must set exactly one of command and cmdline_prefix` |
  | `processes.programs[<i>].command` or `.cmdline_prefix` (empty is "not set"; more than 256 bytes) | `must be 1 to 256 bytes` |
  | `processes.programs[<i>].append_next_word` (without `cmdline_prefix`) | `needs cmdline_prefix` |
  | `processes.programs[<i>]` (null element) | `has no value` |
  | `processes.programs[<i>]` (element that is not a mapping) | `must be a section (a mapping of keys)` |

  No error text contains the value of a rule.
- `services.units` names the services whose state the agent records ([Agent](agent.md), *Services*). The list replaces the default
  (it is not extended); an empty list `[]` switches the `systemd` collector off, so the agent does not connect to the system bus. An
  entry is a unit name or a pattern, and an entry without a unit type suffix means `<entry>.service`, as `systemctl` reads it: the suffix
  after the last dot decides, so `php8.1-fpm` becomes `php8.1-fpm.service`. `*` and `?` are the only pattern characters; they are matched
  by systemd against the names of the loaded units. An entry is 1 to 255 bytes long (counted after the implied `.service`) of
  `A-Z a-z 0-9 : _ . @ - * ?`, does not start with `.` or `-`, holds at most one `@` and names a service: the suffixes `.socket`, `.timer`,
  `.target`, `.mount`, `.automount`, `.swap`, `.path`, `.slice`, `.scope` and `.device` are refused, and so is a template without an
  instance (`foo@.service`, `@foo`). Two entries that are equal once `.service` is added (`nginx` and `nginx.service`) are refused. The
  loader stores every entry as its full name. The errors read `config: <file>:<line>: <key>: <reason>` with the line of the element
  (of the key for the list itself) and never contain the value:

  | Key | Reason |
  | --- | ------ |
  | `services.units` (more than 64 entries) | `must be a list of at most 64 unit names` |
  | `services.units[<i>]` (empty; over 255 bytes with the implied `.service`; a character outside the set; starting with `.` or `-`; more than one `@`) | `must be a service unit name or pattern of 1 to 255 bytes of [A-Za-z0-9:_.@*?-], not starting with . or -` |
  | `services.units[<i>]` (another unit type suffix) | `must name a service unit` |
  | `services.units[<i>]` (`foo@.service`, `@foo`) | `must name an instance, not a template` |
  | `services.units[<i>]` (equal to an earlier entry) | `listed twice` |
  | `services.units` (null element) | `has no value` |
  | `services.units`, `services.units[<i>]` (an element or the list of another YAML type) | the decoder's reason for the type, as for the other agent lists |

  The agent stops at start-up with exit code 1 on every one of them.
- `mariadb.enabled` and `mariadb.socket` configure the `mariadb` collector ([Agent](agent.md), *MariaDB*). With `mariadb.enabled: false`
  the collector is not scheduled and the agent never connects to the socket; a value that is not a boolean is refused by the decoder
  with the key and the line. `mariadb.socket` is the path of MariaDB's Unix socket; the default is the socket of Ubuntu's MariaDB
  package, which Plesk uses, and the option exists for a server that differs. It must be an absolute, clean path (no trailing slash, no
  `.` or `..` element) of 2 to 107 bytes, 107 being the limit of a Unix socket address; anything else, the empty value included, is
  refused with `config: <file>:<line>: mariadb.socket: must be an absolute, clean path of at most 107 bytes`, a value with a control,
  format or line-separating character by the general string rule above, and a null value as "has no value"; no error contains the
  value. The agent stops at start-up with exit code 1 on each of them. The collector itself never logs the path.
- `backups.processes` names the processes that mark a running backup ([Agent](agent.md), *Backup runs*). The list replaces the
  default (one rule, `command: pzstd`; Plesk's own backup processes are not in it until they are captured, #45); an empty list `[]`
  switches the `backups` collector off, so it writes no record and no log line, and a section without the key or a null section keeps
  the default. It is a list of at most 64 rules in file order; a process is part of a backup when any rule matches it. Each rule is a
  mapping with exactly one of `command` (compared with the command of the process as a whole) and `cmdline_prefix` (compared literally
  with the start of the command line), each 1 to 256 bytes without characters of the categories Cc, Cf, Zl or Zp; `*`, `?` and other
  characters are literal text. The strict decoder walks the list as for `processes.programs`, and the keys `program` and
  `append_next_word` are unknown here. The errors read `config: <file>:<line>: <key>: <reason>` and never contain the value:

  | Key | Reason |
  | --- | ------ |
  | `backups.processes` (more than 64 rules) | `must be a list of at most 64 rules` |
  | `backups.processes` (a value that is not a list) | `invalid value, want a list` |
  | `backups.processes[<i>]` (neither or both of `command` and `cmdline_prefix`; empty is "not set") | `must set exactly one of command and cmdline_prefix` |
  | `backups.processes[<i>].command` or `.cmdline_prefix` (more than 256 bytes) | `must be 1 to 256 bytes` |
  | `backups.processes[<i>]` (null element) | `has no value` |
  | `backups.processes[<i>]` (element that is not a mapping) | `must be a section (a mapping of keys)` |
  | `backups.processes[<i>].<key>` (an unknown or duplicate key) | `unknown key`, `duplicate key` |

  The agent stops at start-up with exit code 1 on every one of them.
- `log_shipping` configures the `logs` collector ([Agent](agent.md), *Log shipping*); a section without a key, or a null section, keeps
  the defaults, which ship the journal at `info` and more severe and no file.
  - `log_shipping.journal` switches the shipping of the journal (the system and the user journal files); `false` reads no journal. A
    value that is not a boolean is refused by the decoder with the key and the line.
  - `log_shipping.min_priority`: a line with a known priority less severe than this is not shipped; a line without a priority (the
    lines of a traditional syslog file) is never dropped by it. The names are compared exactly (lower case).
  - `log_shipping.files` names the log files shipped besides the journal, in file order: each a mapping with `path`, an absolute,
    clean path (no trailing slash, no empty, `.` or `..` element) of 2 to 1,024 bytes, listed once, and `format`, which is `syslog`
    (the only format). A path with U+0000 or another control or format character is refused by the general string rule above. The
    path is opened at run time without following a link in any element. A file that holds what the journal ships (`/var/log/syslog` on
    a server whose rsyslog copies the journal) is shipped twice, once per source; the example file says so.
  - `log_shipping.drop` holds rules for lines that are not shipped: each a mapping with at least one of `log` (`journal` or a path
    listed in `log_shipping.files`, compared exactly), `program` (compared whole and literally with the line's program), `priority` (a
    priority name: the line has that priority or a less severe one; a line without a priority never matches) and `message` (an RE2
    regular expression in Go syntax, searched anywhere in the message unless anchored). An empty value is "not set". A line that meets
    every condition of a rule is not shipped. A pattern is at most 1,024 bytes and its compiled program at most 400 instructions, and
    the programs of all patterns together at most 400 (about a dozen ordinary patterns; a rule without `message` costs nothing): the
    count is that of the program Go's `regexp` builds (Perl syntax, simplified), taken before any expression is built, because
    matching costs about as much per line as the program is long (about 90 ms for a 16 KiB line at the bound on the measured host).

  The strict decoder walks both lists as for `processes.programs` (a null element is `has no value`, an element that is not a mapping
  `must be a section (a mapping of keys)`, an unknown or duplicate key is refused). The other errors read
  `config: <file>:<line>: <key>: <reason>`, at the line of the named key, else of the element, and never contain the value or a
  pattern's compile error:

  | Key | Reason |
  | --- | ------ |
  | `log_shipping.min_priority`, `log_shipping.drop[<i>].priority` | `must be one of emerg, alert, crit, err, warning, notice, info, debug` |
  | `log_shipping.files` (more than 16 files) | `must be a list of at most 16 files` |
  | `log_shipping.files[<i>].path` | `must be an absolute, clean path of at most 1024 bytes (no trailing slash, no . or .. elements)` |
  | `log_shipping.files[<i>].path` (equal to an earlier path; at the later one) | `listed twice` |
  | `log_shipping.files[<i>].format` | `must be syslog` |
  | `log_shipping.drop` (more than 64 rules) | `must be a list of at most 64 rules` |
  | `log_shipping.drop[<i>]` (no condition set) | `must set at least one of log, program, priority and message` |
  | `log_shipping.drop[<i>].log` | `must be journal or a path listed in log_shipping.files` |
  | `log_shipping.drop[<i>].message` (does not compile, over 1,024 bytes or over 400 instructions) | `must be an RE2 pattern of at most 1024 bytes and 400 compiled instructions` |
  | `log_shipping.drop` (patterns over 400 instructions together) | `patterns must compile to at most 400 instructions together` |

  The agent stops at start-up with exit code 1 on every one of them.
- `spool.directory` holds the spool's files (`segment-<number>.spool`, `spool-meta.json` and its temporary file; [Agent](agent.md),
  *Spool and sending*) and the agent's state file `agent-state.json` (the last boot, the position in the journal, the open
  backup run and the positions of the `logs` collector, stored by the SHA-256 of each file's path, never the path, up to 64 KiB;
  [Agent](agent.md), *Agent state*). The agent never creates the directory: the installation does, writable by the agent's user. A
  directory that is missing, not a directory, a symbolic link (its last element) or not writable stops the agent at start-up with
  `agent cannot start` and exit code 1 (the spool's start errors, [Agent](agent.md), *Spool and sending*). An existing directory that
  holds only `agent-state.json` becomes the spool's directory without migration.
- `spool.max_size` bounds the size of all of the spool's files on disk together; when a write would exceed it, the spool drops its
  oldest records with a recorded gap. `spool.min_free` is the space that must stay available to unprivileged users on the spool's file
  system after every write; when less would remain, the spool drops its oldest records the same way, and `0MiB` or `0GiB` switches
  the check off ([Agent](agent.md), *Spool and sending*). Each value is one to five ASCII digits without a leading zero (`0` alone is
  allowed), followed directly by `MiB` or `GiB`; the range is checked on the bytes. A key that is absent keeps its default. Any other
  text (`1024`, `1 GiB`, `1.5GiB`, `1GB`, `1gib`, `-1GiB`, `+1GiB`, `01GiB`, a value outside the range such as `32MiB` or `65GiB` for
  `spool.max_size` and `1025GiB` for `spool.min_free`, an empty string) is refused with the key and the line, never the value:

  | Key | Reason |
  | --- | ------ |
  | `spool.max_size` | `must be a whole number of MiB or GiB from 64MiB to 64GiB, such as 512MiB or 2GiB` |
  | `spool.min_free` | `must be a whole number of MiB or GiB from 0MiB to 1024GiB, such as 0MiB or 1GiB` |

  A list and a null value are refused by the strict decoder before this rule, as for every string option (`invalid value, want a
  string`, `has no value`). `spool.max_size` is checked before `spool.min_free`. The agent stops at start-up with exit code 1 on each
  of them.
- A directory must be absolute and clean: no trailing slash and no empty, `.` or `..` element. The loader does not
  create it.
- Plain `http` is allowed for `backend.url`, because the tailnet encrypts and authenticates the transport. Whether
  the host is a tailnet address is not checked; the network ACL is the boundary (see the network area). `http` is meant only for
  the backend host's tailnet address or MagicDNS name, and the scheme must match the listener ([Agent](agent.md), *Connection*).
- `backend.backfill_bandwidth` is the bandwidth of the agent's backfill: the bytes a second of compressed request body it sends
  while it catches up on the spool after an outage ([Agent](agent.md), *Sending*; the live stream is not limited). It is checked right
  after `backend.url`; the value is written as `256KiB`, not as a number of bytes, and a decimal point, a unit other than `KiB` or `MiB`,
  a space or a value out of range is refused with the key, the line and the text above, never the value.
- A new option is added with its key, default, entry in the commented example file, row in the README table and
  test. The example files `deploy/agent/agent.yaml` and `deploy/backend/vandoxd.yaml` set every option explicitly
  (an optional one at its default; an option without a default commented out with an example value), and tests load
  them, so the examples cannot drift from the code.
- `import.time_zone` has no default: the value is `null` when the key is absent, also with an `import:` section that is
  null or only holds comments. A value that is no zone ID is refused with the key, the line and the reason "must be a
  time zone of the IANA time zone database, such as UTC or Europe/Berlin", never the value; an empty value is refused
  with the same error, a null value as "has no value" like every string option. While it is unset, `vandoxd import` fails a
  syslog file at its first line without a year, a MariaDB error log at its first entry, a Dovecot log file at its first Dovecot line, a Plesk or web server error log at its first line of the format and a legacy
  monitoring log at its first snapshot (see [Log import](log-import.md)); the service itself does not use it.
- The four error log paths (`import.plesk_panel_log`, `import.sw_cp_server_error_log`, `import.nginx_error_log`,
  `import.apache_error_log`) name where the server writes these logs; `vandoxd import` compares them as text with the
  names of the imported files (see [Log import](log-import.md), *Plesk and web server error logs*) and never opens
  them. A value must be absolute and clean like a directory and have at least two elements (a directory and a file
  name); otherwise it is refused with the key, the line and the reason "must be an absolute, clean path of a file in a
  directory (no trailing slash, no . or .. elements)", never the value; an empty value is refused with the same error,
  a null value as "has no value". The service itself does not use them.

## Secrets

| Variable | Used by | Required at load |
| -------- | ------- | ---------------- |
| `VANDOX_AGENT_TOKEN` | agent, backend | agent: yes; backend: no |
| `VANDOX_WEB_PASSWORD_HASH` | backend | no |
| `VANDOX_TELEGRAM_BOT_TOKEN` | backend | no |

- A secret is read only from its environment variable `VANDOX_<NAME>` or from the file named by `VANDOX_<NAME>_FILE`
  (Docker secrets, systemd credentials), never from the configuration file or the command line. No secret has a
  key in the configuration file; a key that looks like a secret gets a hint that secrets belong in the environment.
- Setting both the variable and its `_FILE` form is an error, also when one of them is empty. The agent never
  receives the web or Telegram secrets: they are unknown variables there.
- A `_FILE` path must be absolute and name a regular file (symbolic links are followed, so secret mounts work),
  checked on the opened file as for the configuration file. At most 4096 bytes plus a line ending are read; one trailing `\n` or `\r\n` is removed.
- A secret value has 1 to 4096 bytes of printable ASCII (`0x21` to `0x7E`). A BOM, spaces, other line endings and
  control characters are rejected instead of becoming part of the secret. A secret that needs other characters must
  be encoded, for example as base64. The agent token has at least 32 characters.
- On the backend an unset secret is not an error when the configuration loads; the feature that needs it decides:
  the web UI refuses to start without the password hash, the planned ingest endpoint will refuse without the token, and
  the planned Telegram notifier will stay off without its token (both tokens are read and validated, but unused in v0.1.0). This is what lets the backend run for log import without agents or Telegram.
- Every environment variable that starts with `VANDOX_` (in any letter case) must be known to the binary and may
  appear once; otherwise loading fails, so a typo cannot silently disable a feature.
- The web UI password hash must also be in the form of [Web login](web-login.md) (*Password*): an argon2id hash in the PHC form. The
  service checks that form at start-up, after the configuration is loaded; a hash that passes the secret rules above but not that form
  stops the service with exit 1 and one of the reasons listed there.
- A secret is never logged, shown in the web UI, written to the spool or put into an error text. A value that is
  compared with input (the ingest token an agent presents) is compared in constant time; the web UI password is
  checked through the comparison of its hash function.

## Limits and errors

- Loading stops at the **first** error. Order: the file and its structure and values in document and field order,
  then the environment and the secrets.
- An error names the file, the line (when known), the dotted key and a reason. It never contains the value, other
  text from the document, the content of a secret file, or the path in a `_FILE` variable; for a file-system problem
  only the bare reason (such as "no such file or directory" or "permission denied") is kept.
- A pasted secret must not reach a log:
  - an unknown key name is shown only if it has 1 to 31 characters of `A-Z a-z 0-9 _ -` (shorter than the
    shortest agent token); otherwise the error names the enclosing section and the line and says that the name is not
    shown;
  - an unknown `VANDOX_` variable is shown only if its name has 1 to 64 characters of `A-Z a-z 0-9 _`;
  - an error of the YAML parser is replaced by a fixed reason with the line number the parser gave (none when it
    gave none); the parser's message is never passed on, because it can quote the document.
- An unknown key whose name contains `token`, `password` or `secret` (any case) carries the hint that secrets are read
  only from the environment.
- A secret is held in a type that prints `[redacted]` in every string conversion, log and JSON output; only an
  explicit call reveals the value.

## Related decisions

- [0032](../decisions/0032-secrets-only-from-environment-or-docker-secrets.md) — why secrets stay out of the file and the command line.
- [0048](../decisions/0048-yaml-library-go-yaml-in-yaml-v3.md) — why the agent parses YAML with `go.yaml.in/yaml/v3` through a node tree.
- [0049](../decisions/0049-strict-configuration-file-schema-and-errors.md) — why the file is strict and errors never echo values.
- [0050](../decisions/0050-secret-sources-rules-and-redaction.md) — why `*_FILE`, strict value rules and unknown `VANDOX_` variables.
- [0078](../decisions/0078-configuration-and-secrets-in-the-backend-with-yamldotnet.md) — why the backend implements the same rules with YamlDotNet.
- [0104](../decisions/0104-argon2id-password-hash-konscious-strict-phc-form-hash-password-command.md) and [0105](../decisions/0105-forwarded-headers-only-from-trusted-proxies-failed-logins-locked-per-client-and-capped.md) — why the hash form is checked on top of the secret rules and why `web.trusted_proxies` is a comma-separated string.
- [0113](../decisions/0113-process-collector-pss-from-smaps-rollup-top-processes-full-snapshot-under-memory-pressure.md) — why the process grouping rules are a list of mappings of literal comparisons that the strict decoder checks.
- [0117](../decisions/0117-systemd-collector-hand-written-d-bus-client-per-run-states-recorded-on-change.md) — why the units to track are a list of names and patterns, an entry without a suffix is a service, and other unit types are refused.
- [0118](../decisions/0118-mariadb-collector-hand-written-client-captured-statements-refusals-as-not-answering-with-error-code.md) — why the MariaDB collector, which these options switch and point to its socket, connects over the local socket with a client of its own, one connection per run.
- [0120](../decisions/0120-oom-kill-events-triggered-snapshots-boots-from-saved-state-and-backup-runs.md) — why backup runs are named by literal process rules with `pzstd` as the only default, and why the agent's state lives in `spool.directory` without a new option.
- [0121](../decisions/0121-log-shipping-journal-and-syslog-files-positions-per-seqnum-id-zstd-commit-when-kept.md) — why log shipping reads the journal and `syslog` files only, why drop patterns are bounded by their compiled size, and why the state file stores a file's position by the hash of its path.
- [0123](../decisions/0123-agent-spool-segment-files-clock-bound-sequence-numbers-range-acknowledgements.md) — why `spool.max_size` is
  at most 64 GiB and the spool's files are uncompressed.
- [0125](../decisions/0125-sender-two-streams-shared-backoff-refused-records-given-up-with-a-gap.md) — why the backfill has its own,
  configurable bandwidth.

## Not here

- What an option does at run time: the area that owns the feature (ingest and backend host, storage, agent, import).
- The wire format of what the agent sends: [wire format](wire-format.md).

## Implementation

Agent: `internal/config` (Go; `LoadAgent`). Backend: `Vandox.Core.Configuration` (C#; `BackendConfigLoader`,
`SecretReader`). The checklist for a new option or secret is in `.squad/project.md` (*Integration surface*).

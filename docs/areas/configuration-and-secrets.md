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
  - a section with a non-mapping value; a leaf option with no value;
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
| `spool.directory` | `/var/lib/vandox/spool` | absolute, clean path |
| `log.level` | `info` | `debug`, `info`, `warn` or `error` |

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
- A directory must be absolute and clean: no trailing slash and no empty, `.` or `..` element. The loader does not
  create it.
- Plain `http` is allowed for `backend.url`, because the tailnet encrypts and authenticates the transport. Whether
  the host is a tailnet address is not checked; the network ACL is the boundary (see the network area).
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

## Not here

- What an option does at run time: the area that owns the feature (ingest and backend host, storage, agent, import).
- The wire format of what the agent sends: [wire format](wire-format.md).

## Implementation

Agent: `internal/config` (Go; `LoadAgent`). Backend: `Vandox.Core.Configuration` (C#; `BackendConfigLoader`,
`SecretReader`). The checklist for a new option or secret is in `.squad/project.md` (*Integration surface*).

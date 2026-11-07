# Configuration and secrets

## Scope

How the agent (`vandox-agent`) and the backend (`vandoxd`) are configured: the strictly parsed configuration file
for every option, and the environment for secrets, with their load order and error texts. The rules hold for both binaries, whatever
language they are written in; each binary implements them itself and a change to a rule changes both (see *Implementation*). The
operator and both binaries rely on it. Every option with its default and rule is in [Configuration options](configuration-options.md);
what the options and secrets do at run time is described in the area that owns the feature.

## Configuration file

- Each binary reads one YAML file: `/etc/vandox/agent.yaml` for the agent and `/etc/vandox/vandoxd.yaml` for the
  backend (mounted into the container). Each binary takes another path with `-config <path>`. The file is read once, at
  start-up (the agent's start-up is described in [Agent](agent.md), *Command line and start-up*; the backend's in [Backend
  host](backend-host.md)).
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
- Malformed or hostile content yields an error, never a crash, an unbounded allocation or a hang.
- Configuration values are logged only as structured attributes, never concatenated into a log message, and the configuration as a
  whole is never passed to a logger.
- **Non-secret options exist only in the file.** There are no environment overrides, no command-line options
  for them (the file's path is the only thing a flag selects) and no merging of several files.
- A new option is added with its key, default, entry in the commented example file, row in the README table and
  test. The example files `deploy/agent/agent.yaml` and `deploy/backend/vandoxd.yaml` set every option explicitly
  (an optional one at its default; an option without a default commented out with an example value), and tests load
  them, so the examples cannot drift from the code.

## Secrets

| Variable | Used by | Required at load |
| -------- | ------- | ---------------- |
| `VANDOX_AGENT_TOKEN` | agent, backend | agent: yes; backend: no |
| `VANDOX_WEB_PASSWORD_HASH` | backend | no |
| `VANDOX_TELEGRAM_BOT_TOKEN` | backend | no |

The command-signing key of the planned remote actions ([Notification and remote actions](notification-and-remote-actions.md)) will be
a secret under the same rules, held only on the backend host.

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
- **On a server installed with the `install.sh` of the release's install bundle the agent token is a systemd credential.** The script writes it to
  `/etc/vandox/agent-token` (root:root, mode 0600, one line with a newline) and the unit hands it to the agent with
  `LoadCredential=agent-token:/etc/vandox/agent-token`; the unit sets `VANDOX_AGENT_TOKEN_FILE` to the written-out path
  `/run/credentials/vandox-agent.service/agent-token` (never the `%d` specifier) and never `VANDOX_AGENT_TOKEN`, so the token is in no unit
  line, environment value or command line. The script reads the token from the terminal or one line of standard input, applies the rules
  above (32 to 4096 bytes of `0x21` to `0x7E`, checked before anything is written) and hands it to no child process; it is never an option
  ([Agent installation](agent-installation.md)). The script reads it with shell built-ins only (it is never exported, traced or seen by a
  child process) and writes the file through a temporary file that is renamed into place.
- On the backend an unset secret is not an error when the configuration loads; the feature that needs it decides:
  the web UI refuses to start without the password hash; without `VANDOX_AGENT_TOKEN` (or without `ingest.agent_id`) the service
  starts, logs `ingest disabled` once and answers every ingest request `503` ([Ingest endpoint](ingest-endpoint.md), *Enabled*); and
  the planned Telegram notifier will stay off without its token (read and validated, but unused). This is what lets the backend run
  for log import without agents or Telegram.
- Every environment variable that starts with `VANDOX_` (in any letter case) must be known to the binary and may
  appear once; otherwise loading fails, so a typo cannot silently disable a feature.
- The web UI password hash must also be in the form of [Web login](web-login.md) (*Password*): an argon2id hash in the PHC form. The
  service checks that form at start-up, after the configuration is loaded; a hash that passes the secret rules above but not that form
  stops the service with exit 1 and one of the reasons listed there.
- A secret is never logged, shown in the web UI, written to the spool or put into an error text. A value that is
  compared with input (the ingest token an agent presents; later the planned TOTP codes) is compared in constant time: the backend
  compares the SHA-256 digests of the presented and the configured token, so neither the length nor a matching prefix changes the
  steps ([Ingest endpoint](ingest-endpoint.md), *Token check*); the web UI password is checked through the comparison of its hash
  function ([Web login](web-login.md)).
- The running agent holds the token only in its secret form and never logs it; no spool file holds its bytes. The token leaves the
  agent's process only in the `Authorization: Bearer` header of the sender's requests to `backend.url`, and the sender's log lines and
  results carry no token, URL or host ([Agent sending](agent-sending.md)).

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
- [0104](../decisions/0104-argon2id-password-hash-konscious-strict-phc-form-hash-password-command.md) — why the hash form is checked on top of the secret rules.
- [0124](../decisions/0124-ingest-request-one-batch-per-post-acknowledged-by-status-alone.md) — why the agent token travels only as a
  bearer token in the ingest request.
- [0126](../decisions/0126-ingest-endpoint-token-bound-to-one-agent-id-two-slots-through-the-commit-no-rate-cap.md) — why the
  backend runs without ingest while the token or `ingest.agent_id` is unset.
- [0129](../decisions/0129-configuration-and-secret-files-opened-without-blocking.md) — why the configuration file and a `_FILE` secret file are opened without blocking and their type is checked on the opened file.
- [0131](../decisions/0131-agent-unit-and-install-scripts-credential-tailnet-allow-list-checksum-remove-and-purge.md) — why the agent token
  reaches the agent as a systemd credential and not as an environment value or a file the agent's group reads.

## Not here

- Every option, its default, rule and error text: [Configuration options](configuration-options.md).
- What an option or a secret does at run time: the area that owns the feature (ingest endpoint and backend host, storage, agent,
  import, web login).
- The wire format of what the agent sends: [wire format](wire-format.md).

## Implementation

Agent: `internal/config` (Go; `LoadAgent`, `decodeStrict`, `readFile`, `readSecret`, `checkEnviron`, the redacting type `Secret`; the
running agent's token in `config.Agent` (`Secrets`), tests `TestRun_DoesNotLogSecret`, `TestRun_SpoolHoldsNoSecret`,
`TestHTTPTransport_SendsTokenOnlyToBackend`, `TestSender_LogsHoldNoURLTokenOrBody`). Backend: `Vandox.Core.Configuration` (C#;
`BackendConfigLoader`, `StrictYamlDecoder`, `SecretReader`, `Secret`, `BackendSecrets`; the token comparison in `IngestToken` with
`CryptographicOperations.FixedTimeEquals`). Install script: `deploy/agent/install.sh` (`read_token`, `validate_token`, `write_token`,
`mv -T`; test `TestTokenPath_NeverReachesAChildProcess`). The checklist for a new option or secret is in `.squad/project.md`
(*Integration surface*).

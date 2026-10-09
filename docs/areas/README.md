# Areas

What the software does, area by area. Each area has one document here that states the behavior a user, an
operator or another component can rely on — formats, limits, error behavior, guarantees. The
[decision records](../decisions/README.md) explain *why* individual choices were made; the area documents say
*what holds today*. `ARCHITECTURE.md` shows how the parts fit together and links the areas instead of repeating them.

## Rules

- **One document per area**, `docs/areas/<slug>.md`, created from [`_template.md`](_template.md) and listed in the
  index below. The squad Lead keeps it in step with the code: a change in behavior updates the area document in the
  same pull request.
- **Normative and stable.** Write what must hold, not how one language or library does it: the test is whether
  the document would still be true if the component were rewritten in another language. No class, package or
  function names as a contract (mention them only as a pointer to where it is implemented).
- **One home per behavior.** A cross-cutting topic (security, offline behavior) lives in the area that owns it; the
  others link there.
- **Size.** About 1,000 to 4,000 words. Split a larger area; fold a document below about 500 words into a
  neighbor. Development and release process are not areas (see `CONTRIBUTING.md`).
- **A new area is a Lead decision** made in the plan (`plan.md`, *Areas*), with its name and scope in the index.
- Every decision record names its area in its `Area:` field (`—` for a record about the process). Area documents are
  not frozen by a release; their history is Git.

## Index

<!-- project:begin area-index -->
| Area | Scope | Not here |
| ---- | ----- | -------- |
| [Configuration and secrets](configuration-and-secrets.md) | The configuration file and the secrets of the agent and the backend: strict parsing, options, defaults, secret sources and error texts. | What an option does at run time (the area of the feature). |
| [Log import](log-import.md) | The `import` sub-command of `vandoxd`: accepted input, safe file access, repeatable import, parser contract, summary and exit codes. | Storage of records, alert suppression for imported data, the service entry point. |
| [Detection, alerts and reports](detection-and-reports.md) | Deterministic detection, live-only alerts, release staging, the nightly report and the service inventory. Planned. | Telegram delivery, storage, gap recording. |
| [Notification and remote actions](notification-and-remote-actions.md) | The Telegram bot (backend only, user allowlist, private chats) and signed remote actions from a local action list. Planned. | When alerts fire, secrets. |
| [Network and security model](network-and-security-model.md) | Tailscale and its ACL, exposure of the web UI, MariaDB access of the agent and the monitored server's resource budget. | Container and compose file, the agent's rights, secret handling. |
| [Agent](agent.md) | `vandox-agent` on the monitored server: outbound-only connection, direct `/proc` reads, isolated collectors, recorded gaps, spool and rights. Mostly planned. | The batch format, the configuration file, what the backend does with the data. |
| [Backend host](backend-host.md) | `vandoxd` as a service: command line and exit codes, labelled listeners, health probe, logging, shutdown, container image, compose file and exposure. | The configuration options, the import sub-command, storage, the ingest endpoint itself (not implemented). |
| [Storage](storage.md) | The backend's SQLite database: file and connections, schema and migrations, writing and deduplication, reading and log search, retention, write throughput. | The meaning of records (wire format), the import state machine (log import). |
| [Wire format](wire-format.md) | The batches the agent sends: stream layout, header, record kinds and fields, limits, versioning, batch validation, consumer duties. | Storage of records, spooling and sending (agent), the ingest endpoint. |
<!-- project:end area-index -->

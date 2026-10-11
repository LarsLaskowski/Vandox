# Spec: Mail log lines of Postfix and Dovecot become classified records

Status: Draft

Source: issue #18 "[Logs] Parser for mail.log (Postfix and Dovecot)" (dependency #15, the import framework, is merged).

## Problem / motivation

Release v0.1.0 is the forensics release. On the monitored server memory exhaustion ends with the OOM killer, and after
MariaDB, Plesk and mail go down. `mail.log` (and the same lines in `syslog` and in the systemd journal) shows what
happened to Postfix and Dovecot: processes that died (`fatal`, `panic`), warnings and errors, and lost connections to
the database. Today `vandoxd import` already reads `mail.log` as an rsyslog file (source type `syslog`, claimed by its
first line), keeps the program of every line (`postfix/smtpd`, `dovecot`), but recognizes nothing in it: a traditional
`mail.log` has no `<PRI>`, so every line has no priority, and a Postfix `fatal:` or a Dovecot `Panic:` is plain text.
Signature detection (#21, "Postfix/Dovecot fatal errors") and outage reconstruction (#23) need these classes as data.

## Behavior

- A `mail.log` (and its rotations, `mail.err`, Plesk's `maillog`, any rsyslog file) is imported as before by the syslog
  parser; the journal export as before by the journal parser. Neither the claim (by the first line, as for `auth.log`)
  nor the source type changes.
- Contents imported before this change keep their unclassified records (no release has imported mail logs yet).
- In the syslog and journal parsers, every line of **Postfix** (program `postfix/<daemon>`, also with an instance or
  service syslog name such as `postfix/submission/smtpd` or `postfix-out/smtp`) and of **Dovecot** (program `dovecot`) is
  classified:
  - its severity marker, written by the daemon where the daemon writes it (Postfix `warning: `, `error: `, `fatal: `,
    `panic: ` at the start of the message; Dovecot `Debug: `, `Warning: `, `Error: `, `Fatal: `, `Panic: ` at the
    start or right after the first prefix segment such as `imap-login: ` or `auth-worker(2290): `), sets the record's
    priority to the level the daemon itself gives that class in syslog (warning 4, error 3, fatal and panic 2, debug 7);
    neither daemon writes a marker for info lines to syslog, so an info line keeps the priority its source gave it;
  - `fatal` and `panic` lines get the event `postfix.fatal`, `postfix.panic`, `dovecot.fatal` or `dovecot.panic`;
  - a warning or error about a database connection that failed or was lost (Postfix's MySQL lookup table, Dovecot's
    MySQL driver, with the client error texts of libmysqlclient and MariaDB Connector/C) gets the event
    `postfix.database_unavailable` or `dovecot.database_unavailable`.
- The program, host, pid, time and message of a line stay exactly as read. Lines of other programs in a mail log (Plesk's
  `psa-pc-remote`, `spamd`, `opendkim`, ...) are stored as before without classification.
- The classification is per line: no lines are joined, none are dropped or added, and memory stays flat for any size of
  mail log.

## Acceptance criteria

- [ ] AC1: Postfix and Dovecot lines are attributed to their service: the record keeps the program as the source names it
  (Postfix `syslog_name/daemon`, Dovecot `dovecot`), and exactly the lines of these programs are classified.
- [ ] AC2: The severity classes fatal, panic, warning (and error, Dovecot's debug) are recognized from the
  daemon's marker at its anchored position and set the priority; fatal and panic set an event.
- [ ] AC3: Warnings and errors about a lost or failed database connection get a database event.
- [ ] AC4: A large mail log is parsed without memory growth (heap-bound test).
- [ ] AC5: The same line gets the same classification from a syslog file and from a journal export.

## Out of scope

- A parser type of its own for `mail.log` (record 0091 explains why the mail log stays with the syslog parser).
- Dovecot's own log file (`log_path` set to a file, format `Mmm dd HH:MM:SS <prefix><Type>: ...` without host): a
  follow-up issue (see *Out of scope / follow-ups* in `plan.md`).
- Lifecycle events (Postfix `daemon started`, `terminating on signal`, Dovecot `starting up`) and the OOM victims among
  mail processes (`process ... killed by signal 9`, `service(imap): child N killed with signal 9`): signature detection
  (#21) matches them; here they get the priority of their class only.
- PostgreSQL and other SQL drivers; Postfix with a syslog name that does not begin with `postfix`, Dovecot with another
  `instance_name` or with a `mail_log_prefix` of more than one segment.
- Parsing the message further (queue IDs, recipients, client addresses, Dovecot user names).

## Open questions

None for the Product Manager. The choice "no parser type of its own" is a Lead decision recorded in 0091.

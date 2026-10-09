# 0085: Year-less syslog times use the option import.time_zone, resolved with NodaTime's embedded zone database, and a year from the file's modification time

- **Status:** Proposed
- **Date:** 2026-10-09
- **Area:** Log import
- **Source:** Issue #16
- **Supersedes:** —

## Context

rsyslog's traditional file format, the default of the monitored Ubuntu 22.04 server (packaged configuration; not checked
on the server itself), writes `Mar  1 12:00:00`: no year, no zone, the server's local time. Issue #16 requires the right
year "using the file's rotation and modification time" and handled time zones. No log line names the zone. The backend
image's pinned runtime base (`mcr.microsoft.com/dotnet/aspnet`, `10.0-noble-chiseled`, linux/amd64) contains no
`zoneinfo` or `tzdata` file (its layers were listed on 2026-10-09), so .NET's `TimeZoneInfo` cannot resolve
`Europe/Berlin` in the container. The import is parsed again on resume and must give the same result
([0069](0069-log-import-idempotent-per-file-content-hash-with-resumable-batches.md)), so nothing may depend on the
clock of the run.

## Options considered

Where the zone comes from:

1. **Always UTC** — nothing to configure; wrong by one or two hours (and differently in summer and winter) on a server
   that logs in local time.
2. **A command-line flag of `vandoxd import`** — visible where it is used; but a fact about the server is typed again on
   every run, and a resume with another value shifts the rest of a file.
3. **A backend option `import.time_zone`, default `UTC`** (chosen) — set once next to the other options; the strict
   configuration rules (0049) validate it at start-up.
4. **Inferring the zone** (from a journal export of the same period, or a copied `/etc/localtime`) — no input to
   configure, but depends on what the operator happened to save and needs another binary format parser.

Where the zone rules come from:

5. **The host's `/usr/share/zoneinfo` through `TimeZoneInfo`** — no dependency; absent from the image, and results would
   differ between hosts.
6. **The `-extra` runtime image or copying `zoneinfo` into the image** — no new package; a larger image and a Docker
   change, and tests would still use the zone data of whatever host runs them.
7. **NodaTime with its embedded TZDB** (chosen) — one dependency (Apache-2.0, no dependencies of its own for `net8.0`);
   the same rules in tests, CI and the container, pinned by the package version, with explicit handling of skipped and
   repeated local times.

The year:

8. **The current year of the run** — not deterministic across runs.
9. **The file's modification time, else a `-YYYYMMDD` rotation date in the name** (chosen) — the latest year that puts the
   first line no more than a day after the file's last change, advancing at New Year; without either anchor the lines are
   skipped, never guessed.

## Decision

Options 3, 7 and 9: year-less times are read in the zone of `import.time_zone` (an IANA name from NodaTime's TZDB, default
`UTC`), the year comes from the modification time or the dateext rotation date, and daylight saving is resolved
deterministically in file order. The rules are in [Log import](../areas/log-import.md) (*System log parsers*) and the option in
[Configuration and secrets](../areas/configuration-and-secrets.md).

## Consequences

- A wrong option shifts every traditional time stamp by the zone's offset; the summary's time range shows it, and RFC
  3339 lines are unaffected.
- Changing the option between an interrupted import and its resume shifts the remaining lines of that file; the area
  document states it.
- A copy that lost the modification time (plain `cp`) more than a year after the log was written dates it a year late;
  the README asks for `cp -a` or `tar`.
- A file spanning more than a year of year-less lines is dated wrong for its older part.
- Zone rule updates arrive with NodaTime updates (Dependabot), not with the host.

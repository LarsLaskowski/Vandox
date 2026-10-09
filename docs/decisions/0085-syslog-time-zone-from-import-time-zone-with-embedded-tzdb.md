# 0085: Year-less syslog times need the option import.time_zone, without a default, resolved with NodaTime's embedded zone database, and take the year from the file's rotation date or modification time

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
clock of the run. Records do not reference their import file and a content imported completely is never imported again,
so times stored wrong cannot be corrected by a later run.

## Options considered

Where the zone comes from:

1. **Always UTC** — nothing to configure; wrong by one or two hours (and differently in summer and winter) on a server
   that logs in local time.
2. **A command-line flag of `vandoxd import`** — visible where it is used; but a fact about the server is typed again on
   every run, and a resume with another value shifts the rest of a file.
3. **A backend option `import.time_zone`, default `UTC`** — works without configuration on a server that logs in UTC;
   on any other server every year-less time is stored wrong without any sign, and the import cannot be redone.
4. **A backend option `import.time_zone` without a default** (chosen) — set once next to the other options and validated
   by the strict configuration rules (0049); while it is unset, a syslog file fails at its first year-less line with the
   fixed reason "import.time_zone is not set" (the lines before it are stored, the file stays incomplete), and a run
   after setting it resumes and completes the file. Files with RFC 3339 lines only and journal exports need no option.
5. **Inferring the zone** (from a journal export of the same period, or a copied `/etc/localtime`) — no input to
   configure, but depends on what the operator happened to save and needs another binary format parser.

Where the zone rules come from:

6. **The host's `/usr/share/zoneinfo` through `TimeZoneInfo`** — no dependency; absent from the image, and results would
   differ between hosts.
7. **The `-extra` runtime image or copying `zoneinfo` into the image** — no new package; a larger image and a Docker
   change, and tests would still use the zone data of whatever host runs them.
8. **NodaTime with its embedded TZDB** (chosen) — one dependency (Apache-2.0, no dependencies of its own for `net8.0`);
   the same rules in tests, CI and the container, pinned by the package version, with explicit handling of skipped and
   repeated local times.

The year:

9. **The current year of the run** — not deterministic across runs.
10. **The file's modification time, else a `-YYYYMMDD` rotation date in the name** — the modification time is always set
    by the importer (regular files and tar entries), so the name date would never be used, although the modification
    time is what a plain `cp` replaces and the name date survives it.
11. **A valid `-YYYYMMDD` rotation date in the name (the end of that local day), else the modification time** (chosen) —
    the latest year that puts the first line no more than a day after the anchor, advancing at New Year; without either
    anchor the lines are skipped, never guessed.

The repeated hour at the end of daylight saving time:

12. **The earlier offset unless it lies before the previous line** — one second of jitter moves the line, and every
    later line of that hour, into the second pass.
13. **The earlier offset unless it lies more than 10 minutes before the previous line** (chosen) — tolerates the small
    steps back that syslog files contain; a second pass whose first line comes within 10 minutes of the first pass's
    last line, with nothing logged in between, stays in the first pass, which no rule can tell apart without an offset.

## Decision

Options 4, 8, 11 and 13: year-less times are read in the zone of `import.time_zone` (an IANA name from NodaTime's TZDB,
without a default; unset, a file with year-less lines fails with "import.time_zone is not set"), the year comes from the
dateext rotation date or else the modification time, and daylight saving is resolved deterministically in file order
with a 10-minute tolerance in the repeated hour. The rules are in [Log import](../areas/log-import.md) (*System log parsers*) and the option in
[Configuration and secrets](../areas/configuration-and-secrets.md).

## Consequences

- The operator of a server that writes the traditional format (Ubuntu 22.04's packaged default) must set the option
  before its syslog files import; the summary names the reason and the run exits with 1 until then.
- A wrong option shifts every traditional time stamp by the zone's offset; the summary's time range shows it, and RFC
  3339 lines are unaffected.
- Changing the option between an interrupted import and its resume shifts the remaining lines of that file; the area
  document states it.
- A copy that lost the modification time (plain `cp`) more than a year after the log was written dates a file without a
  rotation date in its name (`syslog`, `syslog.1`) a year late; the README asks for `cp -a` or `tar`.
- A file spanning more than a year of year-less lines is dated wrong for its older part.
- Zone rule updates arrive with NodaTime updates (Dependabot), not with the host.

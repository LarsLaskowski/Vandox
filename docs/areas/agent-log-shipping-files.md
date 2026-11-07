# Agent log shipping from files

## Scope

How the `logs` collector of `vandox-agent` ships the files `log_shipping.files` names: how it opens them, cuts and parses their lines
(`syslog` and the error log formats of Plesk and the web servers), resolves local times, follows rotation and truncation, and where a
file starts. The collector, its filter, budgets, commit, gaps in general, logging and classification are in
[Agent log shipping](agent-log-shipping.md), and every rule there holds for these files too.

## Opening a file

- Each file of `log_shipping.files` has a `format`: `syslog` (the traditional or RFC 3339 form of rsyslog's files, *Syslog lines*) or
  one of the error log formats `plesk-panel` (the Plesk panel log, `/var/log/plesk/panel.log`), `nginx-error`
  (`/var/log/nginx/error.log`), `sw-cp-server-error` (Plesk's own web server, `/var/log/sw-cp-server/error_log`, which has nginx's line
  format and differs from `nginx-error` only in its source) and `apache-error` (`/var/log/apache2/error.log`), *Error log lines*. The
  file handling is the same for all five.
- Each file is opened through a walk from `/` that follows no link: every directory element with
  `openat(O_PATH|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC)` (so a directory that may only be searched works), the file with
  `openat(O_RDONLY|O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC)`, and `fstat` of the opened descriptor must show a regular file with exactly one
  link, so a symbolic link in any element and a hard link to another file are refused alike. Against an attacker who races the check,
  the refusal of a hard link relies on the distribution default `fs.protected_hardlinks=1` (Ubuntu's default), which stops a user from
  linking a file he neither owns nor can write. Files are opened for reading only.
- The reasons a file is not read are `missing` (no such file, an element that is no directory, or a path that is not absolute and
  clean), `link refused`, `not a regular file` (a FIFO or device is opened without blocking and closed at once), `permission denied`
  and `read failed` (any other error of the open, of `fstat` or of a read); they are logged as `log file not readable` (*Logging*). The open descriptor of a file is kept across runs.
- A read error on an open file, the kept current or renamed one, is a `read failed` too: the lines before the error are shipped and
  the position stays at the last whole line. A read error of the renamed file stops the reading of that configured file for the run,
  the current file included, so the order of its lines is kept; the next run tries again, at most as *Rotation* bounds it.

## Lines

- A file is read from its position through one 64 KiB buffer. A line ends at a line feed; its first 16,385 bytes are kept and the
  rest is read past without being kept. A `\r` directly before the line feed of a line that fit is removed. A line still longer than
  16,384 bytes is cut as the import's line reader cuts it: to its first 16,384 bytes, less up to three bytes so that no UTF-8 sequence
  is split, with `truncated` set. The cut comes before the header is parsed, so the header counts against the 16,384 bytes and the
  `message` equals the import's for the same line. A UTF-8 byte order mark at offset 0 is removed after the cut and an empty line is
  not shipped. The texts are cleaned and cut as for the journal ([Agent log shipping](agent-log-shipping.md), *Text of every line*).
- A last line without a line feed is **held**, at most 16,385 bytes in the collector's memory, until its line feed arrives; the saved
  offset is the start of that line, so after a restart it is read again from its start. A held line is shipped as it is when its
  file is truncated or when its renamed file is finished.
- A run that stops while it hands over the lines of a chunk (at its record or text budget, or on its time or deadline found at a
  shipped line) drops what it read after the last line it handed over, and the next run reads that again; a run that stops between
  two chunks (its time, looked at after every full 64 KiB, or its deadline) keeps the offset it read up to and the held start of the
  line, and the next run reads on from there. So a line of any length is read to its end over as many runs as it takes, at least one
  64 KiB chunk per run that reaches the file, and never holds back the lines after it.

## Syslog lines

The line rules are the import's syslog rules ([System and mail log parsers](log-parsers-system.md), *Syslog*): an optional `<PRI>`
(priority PRI modulo 8), a traditional or RFC 3339 time, `HOST` (1 to 255 bytes), a tag `PROGRAM[PID]:` or `PROGRAM:` (the program
1 to 128 bytes; a process ID above 2,147,483,647 gives pid 0) and the message after the colon and at most one space. `log` is the
configured path, `source` `syslog`, and the stages of *Classification* follow. Unlike the import, a line without a syslog header (an
RFC 5424 line, a continuation, other text) and a line whose header time does not exist (30 February, 31 November, 29 February of a
common year; hour 24 or minute or second 60 is no header time at all) or lies outside the storable range are **shipped raw**: the
whole line as the message, empty `host` and `program`, pid 0, no priority, and the capture time of the previous line read from the
file. The import skips such lines; live, a skipped line would be lost without a gap.

## Error log lines

- The grammar of a line, the level and the mapping to `priority` and `message` are the import's
  ([Plesk and web server error log parsers](log-parsers-web.md)): hand-written, linear, on bytes with ASCII byte comparisons only, with
  the import's limits (a process ID of at most 10 digits, a request ID and a module of at most 64 characters, a fraction of 1 to 6
  digits in the Plesk panel log, of exactly 6 in the Apache log, none in nginx's), and no date built before the range checks. `log` is
  the configured path, `source` the format's name, `host` and `program` are empty, `pid` is 0 and there is no `event` (these formats
  pass no stages).
- A line of the format whose time exists and lies in the storable range gets its priority (`debug` for Plesk `DEBUG`, nginx `debug`
  and Apache `trace1` to `trace8`, so `log_shipping.min_priority`, default `info`, drops those lines); a drop rule's `program` never
  matches them. Every other non-empty line (PHP output nginx copied from FastCGI's standard error, a stack trace of the panel log, an
  Apache start-up message, a line of another format, a line of the format whose time does not exist or lies outside the storable
  range) is **shipped raw** as for a `syslog` file, without a warning per line: the whole line as `message`, no priority, and the
  capture time of the line read from the file before it. Such a line is never an error.
- The time of a line carries its year; it is local time in the zone of `/etc/localtime`, resolved by the import's rules (month 1 to
  12, a day that exists, hour 0 to 23, minute and second 0 to 59, the year 1677 to 2262, the storable range checked on the whole
  second and again with the fraction), with the skipped hour shifted forward and the repeated hour resolved by the previous instant of
  the file's lines of the format, kept per configured path across runs and rotations (not across a restart). While `/etc/localtime`
  cannot be read, an error log file is not read either (positions kept, no gap).
- A client who can write a line feed into a logged request can forge a line of the format; it is shipped as such and matched by the
  signatures, as on import. The agent cannot read these files unless the operator grants it by hand
  ([Agent rights](agent-rights.md), `deploy/agent/README.md`).

**Where the agent differs from the import:**

- A raw line whose predecessor the agent did not read (the first line after a first start at the end of the file, of a file that
  appeared later, after a truncation or a lost rotation) takes the file's last time, where the import takes the file's modification
  time; the first raw line of the new file after a found rotation takes the renamed file's last time. Such a record differs from the
  import's in `captured_at` only.
- The first lines of a new file in the repeated hour can get another offset than on import, which starts each file afresh.
- The agent reads local times in the zone of `/etc/localtime`, the import in `import.time_zone`; the capture times differ by any
  difference between them, and by the panel log's own zone should it differ (unverified, record 0092).
- **E11, backfilled records.** A record whose capture time lies more than 10 minutes from its receive time is stored backfilled and
  never alerts ([Detection, alerts and reports](detection-and-reports.md), *Live data and backfill*; record 0130). That holds for a raw
  line that takes an hours-old predecessor's time (an Apache start-up message at dawn) and for every panel line should the panel log's
  zone differ from the host's.
- **E12, invalid UTF-8.** The agent replaces each invalid byte with U+FFFD, the import each maximal invalid sequence: for the bytes
  `e2 82 41` the agent's message holds two U+FFFD and the import's one (`ff fe` and `c0 af` give two on both, `f0 9f 98 41` three and
  one), so `message`, and near the 16,384-byte cut `truncated`, can differ. This holds for every file format, not the error logs alone.
- **E13.** When a rotation leaves a new file the agent cannot read, the rest of the renamed file waits with it under the standing
  `log file not readable` warning and is read once the path opens again; a restart in between loses the renamed file's descriptor, and
  a renamed file no longer found then becomes a gap (*Rotation while the agent was stopped*).
- A live record of one of these sources and an import of the same period are both stored
  ([Plesk and web server error log parsers](log-parsers-web.md), *Duplicates*).

## Time of a year-less line

It is local time in the zone of `/etc/localtime` (at most 64 KiB, read at every run that reads a file; an error never falls back to
UTC). The year is the year of the run's start in that zone, or the year before when the line would lie more than a day after the
run's start; a time in the hour skipped at the start of daylight saving time is shifted forward by the gap, and a time in the repeated
hour takes the earlier offset unless that lies more than ten minutes before the previous year-less line of the file. That previous
instant is kept per file across runs (not across a restart) as part of the reading state, so a run whose records were dropped never
moves it and the re-read resolves the repeated hour as the dropped run did. RFC 3339 times ignore the zone. While `/etc/localtime`
cannot be read or is no valid zone file, no configured file is read: their positions stay, no gap is recorded, and every run tries
again.

## Rotation

- At every run the configured path is opened again. When it names another file (device and inode) than the kept one, the file was
  rotated: the kept descriptor becomes the **renamed** file and the new file is read from its start. Every run reads the renamed file
  from its offset to its end first and ships those lines before the new file's.
- The renamed file is **finished** — its rest read, its held line shipped as it is, its descriptor closed — in the run in which the new
  file has data and the renamed file's size did not change since the previous run: the writer has moved (rsyslog reopens its files
  only at the `HUP` after all renames). An append to it after that is never read.
- A rotation while a renamed file is still read finishes that file first: the run reads the renamed file to its end and ships its held
  line; when the run stops before that (its budget, its time), the new file at the path is not taken in that run, and the next runs go
  on with the renamed file until it is finished, then with the newer ones.
- While the renamed file cannot be read (a read error), the run reads nothing else of that configured file, so its lines are never
  shipped after the newer file's, but only for a bound: at the **150th run in a row** (about 5 minutes at the 2-second interval) whose
  read of the renamed file fails without moving its offset, the rest of it is **given up**: its held line is shipped as it is, one gap
  runs from the renamed part's last time to the run's start, the warning `renamed log file given up` is logged (no path, error text or
  content), the renamed file is let go and the file at the path is read on in that run (a rotation that waited for the renamed file
  goes on with it). Every kept run whose read fails without moving the offset counts; a run whose read moves the offset (a whole line
  or a 64 KiB chunk, also one that then fails) or reads without an error starts the count anew, and a run that does not read the
  renamed file (the path or the time zone cannot be read, the run stopped before it) leaves it. The count belongs to the reading state
  of a run, so a dropped run's count is never kept and a dropped give-up run is repeated with one gap. It is not saved: a restart opens
  the renamed file anew by its identity and starts the count at 0.
- The **renamed part's last time** is set to the file's last time at the rotation and moved only by the renamed file's own lines that
  carry a parsed header (a raw line leaves it); it is not saved, so after a restart it is the saved file's last time, which can lie
  after lines of the renamed file the give-up loses. The renamed file's device, inode, offset and last seen size are saved with the
  file's position, so it survives a restart.

**Rotation while the agent was stopped.** After a restart the collector has no descriptor. A saved renamed file, and the saved file
when another file is now at the path, is looked for in the directory of the configured path, reached by the same link-free walk, among
`<name>.1` and the names `<name>-` plus eight digits (at most 65,536 directory names read, in chunks of 256; a directory that cannot be
listed gives `<name>.1` only). Each candidate is opened relative to the directory with `O_RDONLY|O_NOFOLLOW|O_NONBLOCK` and accepted
only when the **opened descriptor** is a regular file with one link and the saved device and inode, so no window lies between a check
and the open; the others are closed. A file found is read on as the renamed file. When the saved file is not found (deleted,
compressed, renamed otherwise), one gap runs from the file's last time to the capture time of the first line read from the new file
(else the run's start); when a saved renamed file is not found, one gap from the file's last time to the run's start. When the
directory cannot be reached (a link on its path, a missing or unreadable directory) the position stays and the file is not read.

## Truncation

The kept file was truncated or replaced in place when it is shorter than what was read of it or when the SHA-256 of its first bytes —
as many as were read, at most 256, kept with the position — changed, so a file that grew past the old position by the next run is
noticed as well. Its held line is then shipped as it is, one gap runs from the file's last time to the run's start, and reading
restarts at offset 0.

## A file's last time

- A file's last time (`last_time` of its position) is the capture time of the last line read from it, shipped or filtered (a raw line
  takes the previous line's); when the collector takes a position without reading a line, it is the start of that run. It starts the
  gaps of a truncation and of a lost rotation.
- **Its bound (every file format).** A line dated after the start of the run that reads it (a forged year such as `2262/04/11`, an RFC
  3339 `syslog` time in 2262, a clock stepped back) keeps its own capture time but leaves the file's last time at its previous value
  (an earlier line's time, else the start of the run that took the position), and a raw line after it takes that value; the run's
  latest time takes at most the run's start, and the gap closing a lost rotation ends at most at the run's start. So the gaps of a
  truncation, a lost rotation and a given-up span start at or before every line they lose, also after a run stopped on its budget right
  after the line, and end at or before the run's start, a given-up span's not before the start of the dropped run that read the line.
  A future time saved in the state file before this rule stays until a line dated before its run. The give-up of a renamed file starts
  at the renamed part's last time, not at the file's last time, which lines of the newer file can have moved meanwhile; it follows the
  same bound.

## First start and changed files

Without a saved position — a first start, no usable state, a file added to `log_shipping.files` — a file that exists starts at its
end, so nothing older is shipped (history is imported with `vandoxd import`); a file that is missing when it is first looked for is
read from its first line once it appears. Positions are matched to the configured files by the SHA-256 of the path; a file removed
from the configuration loses its position.

## Not read, no gap

No gap is recorded while a file cannot be read and its position stays, because its lines are still there: a read error of a renamed
file below the bound of 150 failing runs, a configured file that is missing, not readable, not a regular file or behind a link, a
directory of a lost rotation that cannot be reached, and an unknown time zone. Reading resumes at the position; a file that returns
with another inode is a rotation.

## Logging

The general rule of [Agent log shipping](agent-log-shipping.md) (*Logging*) holds: one line per change, fixed texts, never a path, a
field value or a message.

| Line | Attributes | When |
| ---- | ---------- | ---- |
| warning `log file not readable` | `file` (the index in `log_shipping.files`), `err`: `missing`, `not a regular file`, `link refused`, `permission denied` or `read failed` | the first run with that reason for the file; a different reason is logged again |
| info `log file readable again` | `file` | the first run that opens and reads the file without a problem after that |
| warning `renamed log file given up` | `file` | each run that gives a renamed file up after 150 failing runs (also a run that is dropped and repeated); logged before the `log file readable again` of the same run |
| warning `time zone not readable` | `err`: `localtime not readable` | the first run that cannot load the zone |
| info `time zone readable again` | — | the first run that loads it after that |

## Residuals

- The count of failing runs of a renamed file is not saved, so an agent restarted within every 150 runs never gives the file up, and
  the newer lines of its configured file stay held back with the warning `log file not readable` (`read failed`) standing.
- That rsyslog's logrotate configuration on the server renames before its single `HUP` is not checked by the installation (a manual
  step in `deploy/agent/README.md`); the renamed file is read until the writer has moved, whatever that window is.
- The times of a `syslog` file are those of the line as rsyslog writes it, reception time by default (not checked on the server).
- The agent keeps the previous instant of the repeated hour across rotations but not across a restart.

## Related decisions

- [0162](../decisions/0162-configured-log-files-are-opened-through-a-link-free-walk.md) — why configured files are opened only through a
  link-free walk.
- [0163](../decisions/0163-the-state-file-keys-a-configured-log-file-by-the-sha-256-of-its-path.md) — why their positions are stored by the
  hash of the path.
- [0164](../decisions/0164-error-logs-are-shipped-with-an-operator-named-format-and-a-go-port-of-the-imports-grammars.md) — why the error
  log grammars (#289) are ported from the import and pinned by golden records.
- [0166](../decisions/0166-a-log-line-dated-after-its-runs-start-never-moves-the-files-bookkeeping-times.md) — why a line dated after its
  run never moves a gap start.
- [0165](../decisions/0165-error-logs-the-agent-cannot-read-get-a-documented-grant-by-hand-not-a-new-right.md) — why the error log read
  grant is made by hand.
- [0169](../decisions/0169-a-renamed-log-file-whose-read-keeps-failing-is-given-up-after-150-kept-runs.md) — why a renamed file whose read
  keeps failing is given up after 150 kept runs.
- [0092](../decisions/0092-error-logs-by-content-or-configured-path-unreadable-lines-kept-raw.md) — why lines that do not parse are
  kept raw, and the unverified zone of the panel log.
- [0130](../decisions/0130-live-or-backfilled-is-decided-at-write-time.md) — why a
  record far from its receive time is stored backfilled (E11).

## Not here

- The collector, journal, filter, budgets, commit, gaps in general, logging and classification: [Agent log shipping](agent-log-shipping.md).
- The state file that saves the positions: [Agent](agent.md), *Agent state*.
- The `log_shipping.files` option and its error texts: [Configuration options](configuration-options.md).
- The read grant for the error logs and the rights of the collector: [Agent rights](agent-rights.md).
- The import's line rules: [System and mail log parsers](log-parsers-system.md), [Plesk and web server error log parsers](log-parsers-web.md).

## Implementation

`internal/logship` (configured files with the link-free open, rotation, truncation, positions), `internal/syslogline` (the syslog
line rules, the year-less time, `Clock.ResolveLocal` for a time with its year, `/etc/localtime`), `internal/errorlogline` (the line
grammars of the Plesk panel log and the nginx, `sw-cp-server` and Apache error logs; fixtures `testdata/logs/` with golden records the
C# import writes). E12 was measured with `procstats.CleanText` and `Utf8Text.Decode`. `O_PATH` is the constant `0x200000` of every
Linux port of Go but sparc64; only `linux/amd64` is built.

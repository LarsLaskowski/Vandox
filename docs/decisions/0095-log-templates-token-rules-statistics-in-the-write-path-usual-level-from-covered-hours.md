# 0095: Log templates: fixed token rules, statistics kept by the write path per source and program, the usual level from the hours the source logged

- **Status:** Accepted
- **Date:** 2026-10-12
- **Area:** Detection, alerts and reports
- **Source:** Issue #22, issue #209 (rebuild)
- **Supersedes:** —

## Context

Issue #22 asks that new or suddenly frequent error patterns become visible: log lines are normalized into templates
(numbers, IDs, IPs, e-mail addresses, paths and hex values replaced by placeholders); per template the first and last
time it was seen and a count per hour are stored; two queries answer which templates are new in a time range and which
exceed their usual hourly level; one million lines must be processed in reasonable time on the reference host, a
DS918+. Detection is deterministic ([0008](0008-deterministic-detection-and-alerting.md)). The signatures of #21
recognize known events on demand and store nothing
([0094](0094-log-signatures-embedded-yaml-linear-patterns-on-stored-lines.md)); templates are the opposite case: the
issue asks for stored statistics, and "first seen" must reach back further than the 90 days log lines are kept.

What the input is: the import writes up to 20,000 records per batch in one transaction, a batch with an import step
advances the file's counter in the same transaction, and a resent agent record is a no-op (storage, *Writing*). The same
kernel line is stored up to three times (`syslog`, `kern.log`, the journal), and a journal export may cover only the last
weeks while `syslog` covers months. A source that was not imported for some hours has no lines there: that is unknown,
not "zero" (guarantee *No data gaps unless explicitly recorded*, [0028](0028-data-gaps-are-always-recorded.md)).

Issue #209 adds the rebuild: a database upgraded from schema version 7 has template tables that cover only the lines stored after
the upgrade, and a change of the normalization rules leaves the stored statistics under the old templates. The issue asks for a
rebuild that reads the stored lines in batches, normalizes them and replaces the three template tables in bounded transactions, and
for a choice between a sub-command and a migration step. Two processes may open the database at once (`vandoxd import` next to the
service), every write transaction takes the writer lock with `BEGIN IMMEDIATE`, and another process waits at most the 5-second busy
timeout for it (storage, *Database file and connections*).

## Options considered

How a line becomes a template:

1. **Fixed token rules, hand-written, one forward pass** (chosen) — a message is split at white space and a fixed set of
   delimiters, each token is classified by an ordered list of rules (path, e-mail, IPv4, IPv6, hex with prefix, UUID,
   number, number with unit, parts of a token with a colon, any other token with a digit), and white space is
   collapsed. The same message always gives the same template, independent of what was seen before, so first-seen times
   stay stable across imports and releases of the same rules; the cost is linear in the message and needs no state.
   Names without a digit (host, user, database, unit names) stay literal, so two lines that differ only in such a name
   are two templates.
2. **A list of regular expressions on the non-backtracking engine, applied in order** — the pattern language of 0094;
   but every message passes once per expression, overlapping replacements depend on their order, and the parsers of
   security area 10 are hand-written for the same reasons (speed on the J3455, every accepted form enumerable).
3. **Online clustering (Drain and similar), which learns the variable positions from similar lines** — also catches
   variable words without digits; but it is stateful and order-dependent: the same line can map to different templates
   depending on the lines before it, and templates change as the clustering evolves, so "first seen" would not be a
   fact about the data. Excluded by 0008.

Where the statistics are kept:

4. **In the write path, in the transaction of the batch** (chosen) — every stored log line is counted exactly once and
   at the moment it becomes stored: a duplicate agent record is not stored and not counted, a failed or rolled-back
   batch counts nothing, and an interrupted import resumes without double counting, because counts commit with the
   records and the import step (the same reasoning as the FTS5 index, [0063](0063-storage-schema-records-table-typed-metric-and-log-tables-json-payloads.md)).
   The counts are aggregated per batch first, so a batch costs one statement per template and hour it touches, not one
   per line. The price: writing log lines gets slower (measured), and lines stored before the schema step are not
   counted.
5. **On demand from the stored lines, like the signatures** — nothing to keep in step; but every query scans the lines
   of the whole range again (one million lines per question), and first-seen ends with the 90-day retention of log lines.
6. **A separate catch-up pass over new lines with a stored cursor** — can be rebuilt after a rule change; but needs a
   trigger the service does not have yet, leaves counts stale between runs, and needs its own state and resume rules.

What a template is keyed by:

7. **Source, program and template text** (chosen) — copies of one line in `syslog`, `kern.log` and the journal are
   counted per source, so a journal export that starts later does not look like a sudden rise; the same text from two
   programs stays apart. The host is not part of the key: there is one monitored server, and many sources carry no host.
8. **Template text alone** — fewer rows; but copies add up and the counts jump wherever a further source begins.

What "usual level" means:

9. **The mean count per covered hour of a baseline window before the hour, times a factor, with a minimum count**
   (chosen) — an hour of the baseline counts (as zero when the template is absent) only when the template's source has
   at least one line in it; an hour without any line of the source is unknown and left out, and with too few covered
   hours no usual level is known and nothing is reported. Explainable from the stored counts and deterministic; the
   window, factor, minimum count and minimum covered hours are query parameters with documented defaults.
10. **Every hour of the window, zero when there is no count** — simpler; but an hour that was never imported counts
    as a quiet hour, which turns unknown into normal against the guarantee above, and makes every template of a fresh
    import look frequent.
11. **The median, or a seasonal baseline (same hour of the day or week)** — more robust against single bursts and
    daily rhythm; but needs the counts of every hour in the query, and weeks of data before it says anything. To be
    revisited with alerting.
12. **A fixed threshold per template** — does not adapt and needs configuration nobody can choose yet.

How the cost of the usual-level query is held:

13. **Covered hours counted once per source and hour, the cost stated and measured** (chosen) — the work grows with the
    template hours of the range that reach the minimum count, which neither the range nor the limit bounds, and text
    without digits that varies per line makes one template per line. The covered hours are computed per source and hour
    instead of per candidate, which needs the statement to materialize them (`AS MATERIALIZED`; SQLite would otherwise
    flatten the common table expressions and run the count per candidate); the baseline sum is still read once per
    candidate. The rest is measured at the default and the widest parameters
    and on such hostile text, and documented, because nothing calls the query yet.
14. **A cap on the candidates** — bounds the work; but a cap that cuts silently drops spikes and turns unknown into
    quiet, and a cap that refuses the query fails exactly when a flood happens, the moment its spikes matter. With no
    consumer yet the value would be a guess; to be revisited with the first caller.
15. **Make deadlines interrupt the statement** (`sqlite3_interrupt` or a progress handler bound to the cancellation
    token) — would make a deadline a real bound; but Microsoft.Data.Sqlite does neither, so this is a change of every
    reader query, the log search included, and of their error mapping: a follow-up of its own.

How the statistics are rebuilt from the stored lines (issue #209):

16. **A sub-command, `vandoxd templates`** (chosen) — run on purpose after an upgrade or a rule change, as often as needed, like
    `vandoxd incidents`; the service start is not delayed, and an interrupted run is visible (exit code 1) and is simply run again.
    The price: someone has to run it; the release notes of a rule change say so.
17. **A migration step** — runs by itself, once per database; but a migration step is one transaction by rule (all or nothing, the
    version written in the same transaction), so a rebuild of millions of lines would be one unbounded transaction, or a step whose
    batches commit before the version does; every opener (service start, import, incidents) would wait for it, the service's start
    and health check on the reference host for minutes; and a later rule change is not a schema change, so it would need a further
    step that changes no schema, once per rule change.
18. **Automatically at service start when a stored rules version differs** — no manual step; but needs a rules version kept in step
    with the normalizer by hand and a background job the service does not have (option 6), and nobody notices statistics that are
    incomplete while it runs or after it was stopped. To be revisited with the ingest service.

How the tables are replaced:

19. **In place, behind a high-water record ID** (chosen) — one transaction empties the three tables and reads the highest record
    ID; then batches of log lines with IDs up to that one are counted in transactions of their own, with the counting code of the
    write path. The write path keeps counting the lines it stores meanwhile, whose IDs are higher, and both only add to counts and
    widen first and last seen, so the order does not matter and every stored line is counted once when the run completes, provided
    no other rebuild counts meanwhile (option 22). The price: until a run completes, the statistics are incomplete (during the run
    and after an interrupted one), and the transaction that empties the tables grows with the statistics, not with a batch.
20. **Into shadow tables, swapped in at the end** — readers never see incomplete statistics, and an interrupted run leaves the old
    ones; but the swap is one transaction as large as the statistics plus a catch-up of the lines stored meanwhile, holding the
    writer lock (and another process past its busy timeout) for that long, and renamed tables cannot keep the index names and schema
    text of a migrated database, which is part of the contract.
21. **Resume an interrupted run from a stored cursor** — saves the work already done; but needs state in `meta` and a rule for a
    resume by a build with other rules, while a run from the start costs minutes. Every run starts from the beginning.

How two rebuilds at once are kept apart (the writer lock serializes transactions, not runs: a second run, started by an operator who
thinks the first one hangs, clears and counts again while the first one keeps adding the lines past its cursor on top):

22. **A run marker in `meta`, checked in every counting transaction** (chosen) — the transaction that empties the tables writes a
    random marker of its own; every batch and the final transaction first check, inside the transaction, that the marker is still
    theirs. A later run's clear deletes everything an earlier run counted and replaces the marker in one transaction, so the earlier
    run cannot count again and fails; the run that cleared last wins, and no line is counted twice. A marker left by an interrupted
    run records in the database that the statistics are incomplete and is simply overwritten by the next run. The price: one row and
    one read per batch, and a superseded run ends with exit code 1.
23. **No guard: a documented rule "never run two at once", or a lock held for the whole run** — the rule leaves both runs ending
    with exit code 0 and lines counted twice, which nothing shows afterwards; SQLite has no lock that outlives a transaction, and a
    lock file needs stale-lock handling after a crash, which a marker does not need.

## Decision

Options 1, 4, 7, 9 and 13: a hand-written normalizer turns every message into a template by fixed token rules; the
storage write path counts every stored log line under its source, program and template, first and last capture time
and per UTC hour, in the transaction of its batch; a template is new when it was first seen in the range and its source
has earlier lines, and an hour exceeds the usual level when its count reaches the minimum and is more than the factor
times the template's mean over the covered hours of the baseline window; the covered hours are computed (materialized) once per
source and hour, the baseline sum once per candidate, and the query's cost is stated, not capped. Options 16, 19 and 22: the sub-command `vandoxd templates` rebuilds the statistics in place,
in one transaction that empties the tables, writes the run's marker and fixes the high-water record ID, and then in bounded batches
of stored lines counted by the write path's code, each checking the marker first. The rules, defaults and limits are in
[Detection, alerts and reports](../areas/detection-and-reports.md) (*Log templates*), the tables and the rebuild in
[Storage](../areas/storage.md) and the sub-command in [Backend host](../areas/backend-host.md).

## Consequences

- The template statistics are history: they are kept when log lines expire (they fall under the hourly-rollup
  retention once retention exists), and code that deletes log lines does not change them.
- A change of the normalization rules changes the templates of new lines only, until `vandoxd templates` rebuilds the
  statistics from the stored lines; so does a database upgraded from schema version 7 (whose stored lines have no
  templates), where until then the queries treat hours before the upgrade as unknown, because the source has no counted
  lines there. The rebuild counts only lines that are still stored: once retention deletes lines (90 days), a rebuild
  loses the history of the expired ones, so it must stay a deliberate step, never a routine.
- During a rebuild and after an interrupted one the statistics are incomplete, and not in the way of the upgrade case:
  batches go by record ID, not by time, so an hour can hold a partial count that looks covered, and usual-level and
  new-template results are wrong, not unknown. The marker row in `meta` records that state in the database until a run
  completes; nothing reads it yet, so the first consumer that shows template results to a user must check it. Incidents
  rebuilt meanwhile carry the templates of that state, so `vandoxd incidents` is run again after the rebuild. The
  high-water ID holds because a new record gets an ID above every stored one; code that deletes the newest records (none
  does) would break it.
- Two rebuilds at once never double-count: the one that cleared last completes, the other fails as superseded.
- The transaction that empties the tables grows with the statistics: it holds the writer lock that long, so another
  process that waits longer than the busy timeout fails and is run again, and it writes the deleted pages to the WAL,
  which grows by about the size of the template tables until the next checkpoint.
- A rebuild batch holds at most as many keys as lines (source, program and template, each bounded by the model limits),
  and does no more work than the write path's largest batch; how long that holds the writer lock on the reference host
  is measured with the write path, not separately.
- A template's first-seen time is the earliest stored line: importing an older log later moves it earlier, so a
  template reported new may no longer be new afterwards.
- Hostile or random text without digits creates a template per line; the statistics grow at most by one row per stored
  line and one per template and hour, and a template text is cut at 1,024 UTF-8 bytes.
- Templates are external text: whoever shows them escapes them like log lines (security area 12); the normalizer
  removes control characters of category Cc, keeps format characters (Cf) and does not make the text safe for any
  output format.
- The usual-level query's work is not bounded by its range or limit, and a deadline does not interrupt a running
  statement; a caller that serves a user cannot rely on a deadline alone until deadlines interrupt statements
  (option 15).
- Nothing shows the queries yet; incident reconstruction (#23) and the web UI build on them. The criterion for one
  million lines on the reference host is in `docs/BENCHMARKS.md`.

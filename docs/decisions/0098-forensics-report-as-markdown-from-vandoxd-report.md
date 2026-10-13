# 0098: The forensics report is written as Markdown by `vandoxd report`, computed on demand in bounded passes; the web page follows separately

- **Status:** Accepted
- **Date:** 2026-10-13
- **Area:** Detection, alerts and reports
- **Source:** Issue #28
- **Supersedes:** —

## Context

Issue #28 asks for a summary across the whole imported period: the number and times of the outages with their victims and
hypotheses, recurring patterns (time of day, correlation with backups, boots and panel use, i.e. the memory of Plesk's
`sw-engine`), the largest permanent and transient memory consumers, "available in the web UI and exportable as Markdown". The
incidents exist since #23 (`vandoxd incidents`, [0096](0096-outage-incidents-rebuilt-from-stored-data-merged-by-overlapping-windows.md)).
The web UI has neither a login nor a layout yet (#24, #25 open), and the web UI must not be offered without its login
([0016](0016-web-ui-in-home-lan-with-login.md)). The Product Manager decided to split: this change delivers the analysis and the
Markdown export as a sub-command next to `vandoxd import` and `vandoxd incidents` on standard output; the web page becomes its own
issue that depends on #24 and #25. All times are UTC.

Any local process can forge kills, boots, backups and process names, so the number of incidents, snapshots and boots is not bounded
by anything trustworthy, and the victim commands and program names that end up in the report are external text
(security area 12). Standard output of a sub-command carries only its result ([0072](0072-vandoxd-import-sub-command-output-and-exit-codes.md)).

## Options considered

Where the report is delivered:

1. **A sub-command `vandoxd report` that writes Markdown to standard output** (chosen, the Product Manager's decision) — usable now,
   redirected into a file by the caller, no file written by `vandoxd`.
2. **The web page now** — what the issue names, but there is no login to put it behind (#24, #25).
3. **An option `-o <file>`** — writes a file whose path comes from the command line (security area 9) for what a shell
   redirection already does.

When the report is computed:

4. **On demand from the stored records and incidents, nothing stored** (chosen) — always matches the stored data; the cost is a
   full read per run, off any request path, like `vandoxd incidents`.
5. **Stored by `vandoxd incidents`** — one read less, but a second artifact to keep in step and a report that goes stale after an
   import.

How the outages are held while the report is written:

6. **Two passes over the stored incidents, one incident at a time** (chosen) — the first pass feeds the rules, the second lists each
   outage as it is read; memory does not grow with the number of incidents, which forged kills control. A change of the incidents
   between the passes is noted in the report.
7. **Collect all incidents, then write** — one pass, but memory grows with the number of incidents (up to 8 MiB of JSON each).
8. **List at most N outages** — bounded, but breaks "every outage is listed with its hypothesis".

How the series over the whole period are held:

9. **Bounded compact series** (chosen): one point per process snapshot (time, panel memory, backup present), the boot times, the runs
   of observed hours and the per-program totals, each with a fixed cap; over a cap the statements that need the series are unknown,
   never "no".
10. **A query per moment** (the snapshots and lines of each lookback read from the store) — no series in memory, but the base rate
    evaluates every observed hour, so the reads multiply by the number of hours.

How external text is written into Markdown:

11. **Only inside code spans** (chosen) — the text of a code span is never read as Markdown, HTML, an entity, a link or an
    autolink; control, format and other invisible characters are written as escapes, backslashes doubled, and the fence is
    longer than any backtick run of the text.
12. **Backslash before every ASCII punctuation character** — CommonMark reads such text literally, but extended autolinks of
    GitHub-flavoured Markdown (`www.`, `http://`) are recognized on the resulting text by some renderers.
13. **HTML entities** — depends on the renderer allowing HTML at all, and is unreadable as plain text.

## Decision

Options 1, 4, 6, 9 and 11: `vandoxd report` reads every stored log line, process snapshot and incident (the incidents twice, one at a
time), keeps only capped compact series, and writes the report as Markdown to standard output, with every piece of external text
inside a code span. The command, its exit codes and the report's content, formats and limits are in
[Detection, alerts and reports](../areas/detection-and-reports.md) (*Forensics report*) and [Backend host](../areas/backend-host.md)
(*Command line and exit codes*). The web page is a separate issue ("[Web] Forensics report page", depending on #24 and #25), on which
#29 depends.

## Consequences

- The report reflects the stored incidents; it is run after `vandoxd incidents`. Its time grows with all stored lines, snapshots and
  incidents.
- A run that fails or is interrupted after it started writing leaves an incomplete report on standard output and exits 1; the caller
  discards it.
- The web page renders the same analysis (the summary and the incidents read one at a time) and offers the same Markdown; the
  analysis lives outside the command so the page can call it.
- Shown as Markdown, a program name is monospaced and an invisible character is visible as its escape. Backslash escapes do not
  apply inside a code span, so a literal backslash sequence in a name (the six characters `\u202e`) is rendered with its backslash
  doubled (`\\u202e`) and stays distinguishable from the escape of a real U+202E (`\u202e`).
- Over a cap (1,000,000 snapshots, 10,000 boots, 100,000 runs of observed hours, 4,096 programs) the dependent statements are
  unknown or incomplete, as the report's notes say; to revisit if a real archive reaches one.

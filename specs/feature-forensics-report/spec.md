# Spec: Forensics report

Status: Approved by Lead

Source: issue #28 "[Logs] Forensics report"; Product Manager decisions of 2026-10-13 (`log.md`, step 2).

## Problem / motivation

Release v0.1.0 is the forensics release (record 0020): Vandox imports the server's past logs and explains the outages. Since #23,
`vandoxd incidents` turns every OOM kill into a stored incident with a timeline and a cause hypothesis, but there is no view across
the whole period: how many outages, when, which victims, whether they recur at a time of day or follow the nightly backup, a reboot or
panel use, and which programs hold the memory permanently or only at times. The operator needs that summary as a document.

## Behavior

- A new sub-command `vandoxd report [-config file] [-lang en|de]` writes the **forensics report** of everything stored as Markdown to
  standard output; the operator redirects it into a file. It reads, never changes, the stored data (log lines, process snapshots,
  incidents); `vandoxd incidents` is run before it.
- The report lists **every stored outage** (incident) with its number, first kill, window, kill count, victims and hypothesis (memory
  growth, largest consumers, backup running, boot, panel memory high, service events).
- It states whether outages **recur at a time of day** (24 UTC hours) and whether they **follow a backup, a boot or high panel
  memory** (Plesk `sw-engine`), each by a fixed rule that compares the share of outages with the base rate of the observed time over
  the whole period. Missing data makes a statement **unknown**, never "no".
- It names the **largest permanent and transient memory consumers** over the whole period.
- All times are **UTC**. The report is in **English** (default) or **German** (`-lang de`).
- External text (victim commands, program names) cannot inject Markdown or HTML into the report.
- The web page of the report is a separate issue ("[Web] Forensics report page", depending on #24 and #25); #29 depends on it.

The precise rules, formats and limits are in `docs/areas/detection-and-reports.md` (*Forensics report*) and
`docs/areas/backend-host.md` (*Command line and exit codes*).

## Acceptance criteria

- [ ] Every outage is listed with its hypothesis (issue #28).
- [ ] Recurring time-of-day and backup correlations are detected; boot and panel-memory correlations likewise; unknown data is
  reported as unknown (issue #28, PM decision 3).
- [ ] The report is exported as Markdown by `vandoxd report` on standard output (issue #28, PM decision 1).
- [ ] The largest permanent and transient memory consumers over the whole period are listed (issue #28).
- [ ] All times are UTC (PM decision 2); the report is available in English and German (PM decision 4).

The testable criteria with their test names are in `plan.md`.

## Out of scope

- The web page (follow-up issue "[Web] Forensics report page", depends on #24, #25; #29 depends on it).
- Local time zones in the report (`import.time_zone` is for reading imports only).
- A configuration option for the language, writing a file from `vandoxd`, a time range option.
- The agent's `kernel_event` and `gap` records (#53), backup detection beyond `pzstd` (#36), retention (#46).

## Open questions

None: the Product Manager decided the split, UTC, the rule frame and the two languages; the language selection is decided in record
0100.

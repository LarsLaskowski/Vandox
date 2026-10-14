# Spec: Incidents view

Status: Draft

Source: Issue #27 "[Web] Incidents view" (milestone v0.1.0). Depends on #23 (incident reconstruction), #24 (web UI
foundation), #25 (login) and #26 (logs view); all four are on `main`.

## Problem / motivation

Release v0.1.0 is the forensics release: `vandoxd incidents` already rebuilds every outage from the imported data and
stores it, and `vandoxd report` writes a Markdown summary, but the web UI shows no incident. The operator wants to
understand each reconstructed outage on one page: what was killed, when, what grew before it, what the memory, swap and
connections did in the window, and the log lines of any moment of it — and to keep a note of what they found.

## Behavior

### Incident list (`/incidents`)

- A new entry *Incidents* in the main navigation opens the list; it is marked on the list and on every incident page.
- The list shows the stored incidents **newest first** (by window start, then number), **20 per page**, with *Older
  incidents* to page on and *Newest incidents* to go back to the start. The page position is not part of the address.
- Each row shows the incident number (a link to its page), the time of the first kill (a link to the logs of that
  moment), the **duration** (from the first to the last kill), the number of kills, the **victim** (the command of the
  first kill, and how many further kills there were), a short **hypothesis** (the programs that grew most and whether a
  backup ran, or that this is unknown) and whether the incident has notes.
- Without incidents the page says how to create them (`vandoxd incidents` after an import).

### Incident page (`/incidents/<number>`)

- **Header:** number, window (start and end, UTC), first and last kill, duration, number of kills, when the incident was
  built and last changed.
- **Kills:** the kept kills with time, host, victim process ID and command, anonymous RSS and `oom_score_adj`, and the
  number of kills not kept.
- **Hypothesis:** the growth candidates with their growth, the largest consumers with their RSS, whether a backup ran,
  the last boot before the first kill, the number of service events — each "unknown" where the incident says so.
- **Timeline:** every stored timeline entry (events, OOM kills, boots, service events, backup first and last seen) in
  time order with its time, type, host and texts; OOM kills, boots and backup activity are marked visibly; the number of
  entries the reconstruction did not keep is shown.
- **Charts of the window**, read from the stored data of the window (not from the incident document):
  - *Memory per program* — a **stacked** chart of the RSS of the five programs with the highest peak in the window and
    of all other programs together, per process snapshot;
  - *Available memory and swap* — `mem_available_bytes` and `swap_used_bytes`;
  - *Connections* — the number of sockets of each connection snapshot.
- **Connections:** the stored connection peak (time, sockets, snapshots, the three processes with the most sockets).
- **Metrics and templates:** the stored metric extremes, new templates and template spikes.
- **Every time on the page is a link to the logs of that moment**: the logs view (`/logs`) with the range from 30 seconds
  before to 5 minutes after it. Exception (Lead decision at PR approval): the header's *built* and *last changed* times are
  times of the analysis, not moments of the monitored server, and are plain text.
- **Notes:** a free-text note per incident (up to 1,800 characters), saved with a *Save note* button; an empty note
  removes it, and leading line breaks are not kept. Notes survive every re-run of `vandoxd incidents`; when incidents are merged, the notes of the merged ones
  move to the incident that remains and are shown there, each with the number it was written for.

### Everything

- Every page needs the web login; saving a note needs the session and the antiforgery token of the page.
- Log text, program names, commands, hosts and templates are shown as text with invisible characters made visible, as in
  the logs view.
- Every read runs under a 10-second deadline and through the same limit of two concurrent reads as the logs view.

## Acceptance criteria

- [ ] AC-S1 (issue): the chart shows the memory per program within the incident window.
- [ ] AC-S2 (issue): clicking a time opens the logs of that moment.
- [ ] AC-S3 (issue): notes are stored — and survive a re-run of `vandoxd incidents` and a merge of incidents.
- [ ] AC-S4: the list shows time, victim, duration and hypothesis of each incident, newest first, in pages of 20.
- [ ] AC-S5: the incident page shows the timeline with OOM kills, boots and backup activity marked, the available memory
  and swap chart and the connection counts of the window.
- [ ] AC-S6: a note can only be saved by a signed-in user with a valid antiforgery token; external text is never rendered
  as markup.

The testable criteria with their test names are in [plan.md](plan.md).

## Out of scope

- Clicking a point **inside a chart** (the timeline, kill, metric and peak times are the clickable times); markers of
  the kills drawn into the charts.
- A note history, several notes per incident number, notes in `vandoxd report`.
- Editing or deleting incidents from the UI, running `vandoxd incidents` from the UI.
- Incidents of live data (#53), retention (#46).

## Open questions

None for the Product Manager. Two interpretations the Lead settled (record 0107): the list's *duration* is the time from
the first to the last kill (the window is shown separately, because its length is mostly the configured padding), and
"clicking a point in time" is satisfied by the times of the page, not by clicks inside a chart.

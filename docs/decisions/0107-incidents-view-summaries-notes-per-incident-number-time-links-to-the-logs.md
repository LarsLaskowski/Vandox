# 0107: The incidents view reads bounded summaries and the window's own records; notes are rows per incident number that follow merges, saved by a form post with the antiforgery token; every time links to the logs of that moment

- **Status:** Proposed
- **Date:** 2026-10-14
- **Area:** Web UI
- **Source:** Issue #27
- **Supersedes:** —

## Context

Issue #27 asks for a list of the reconstructed incidents (time, victim, duration, hypothesis), a page per incident with
its timeline, a stacked chart of the memory per program in the window together with available memory and swap, the
connection counts of the window, a jump from any time to the logs of that moment, and a free-text note per incident.

What exists: `vandoxd incidents` stores each incident as one JSON document of up to 8 MiB and keeps an incident's number
across re-runs by overlapping windows, but deletes the other incidents a rebuilt one overlaps
([0096](0096-outage-incidents-rebuilt-from-stored-data-merged-by-overlapping-windows.md)); the store's incident read
decodes every incident of a page at once and left the page size to this issue (#213). The document keeps only the extremes
of the metrics and the connection peak, not the series. The logs view takes its filters from the address and reads through
a gate of two concurrent calls so that two of the four reader connections stay free
([0106](0106-logs-view-address-as-the-view-bounded-search-pages-template-lines-by-normalization.md)). The web login protects
every form post with an antiforgery token and binds live connections to the session
([0103](0103-web-login-server-side-sessions-static-login-page-circuits-bound-to-session.md)); the web UI shows external text
only as escaped text and charts get fixed texts only ([0098](0098-web-ui-serves-only-its-own-static-web-assets.md),
[0099](0099-web-listener-security-headers-one-self-only-csp.md)). The note is the first thing the service itself writes.

## Options considered

Where notes live:

1. **A table of notes keyed by the incident number they were written for, pointing at the incident they belong to now, moved
   in the merge transaction, with a foreign key and no cascade** (chosen) — an update keeps the number, a merge moves the notes,
   no text is joined or cut, and a delete that forgets the move fails instead of losing notes.
2. **A field of the incident document** — every rebuild replaces the document.
3. **One note per incident, the notes of merged incidents appended** — exceeds the note bound or cuts the user's text.
4. **A cascading foreign key** — a merge would delete notes silently.

How a note is saved:

5. **A plain form post to its own endpoint with the antiforgery token, answered with a redirect** (chosen) — the pattern of
   the sign-out, under the login area's rule that every form post carries a token, independent of the live connection.
6. **A submit over the live connection** — errors on the page, but a write without a token, resting on the circuit's session
   binding alone.
7. **A statically rendered form handled by the framework** — the pages render interactively, so the framework would not
   handle the post.

What the list reads:

8. **Summaries: the incident columns and a projection of each document decoded row by row, a fixed page of 20, newest first
   by window start and number** (chosen) — one document's bytes at a time; settles #213 without loading whole incidents.
9. **The existing incident read with a small limit** — every incident of the page decoded whole, up to 8 MiB each.
10. **Summary columns in the schema** — stored incidents would need a backfill or a rebuild.
11. **The database's JSON functions** — a second JSON reader next to the one that writes and reads the document.

Chart data and its labels:

12. **The window's own process snapshots and the two metric series of one source (the source of the window's first process
    snapshot, else of its first metric), capped at 300 snapshots and 1,000 points per series, the five programs with the
    highest peak plus the rest as one series, stacked; program names escaped and capped as chart labels** (chosen) — the
    document has no series; uPlot writes labels and title as text (read in the vendored source), so an escaped name carries no
    markup and its direction controls are visible.
13. **Fixed labels with a legend table beside the chart** — keeps "fixed texts only", but the legend needs color swatches
    without `style` attributes and leaves the chart.
14. **Evenly thinned snapshots instead of the first 300** — needs a count first and thins the minutes before a kill.

Reader slots: **one gate of two slots shared by the logs and incidents views** (chosen) over a gate of its own for the
incidents view, which would leave one of four readers for `/healthz` and everything else.

Time links: **30 seconds before to 5 minutes after the moment, in the logs view's address format** (chosen) — the logs page
lists oldest first, so the moment is at the top of the first page; ±1 minute puts it on the second page of a busy log.

Duration in the list: **from the first to the last kill** (chosen) — the window's length is mostly the configured padding and
the same for every incident with one kill; the window is shown on the incident page.

## Decision

Options 1, 5, 8 and 12, the shared gate, the 30-second/5-minute time link and the kill-to-kill duration: `/incidents` lists
summaries 20 per page, `/incidents/<number>` shows one decoded incident and charts read from the window's records, every time
links to `logs` with the range around it, and notes are rows of `incident_notes` saved through `POST /incidents/<number>/notes`
with the antiforgery token and moved to the surviving incident when `vandoxd incidents` merges. The behavior is in
[Web UI](../areas/web-ui.md) (*Incidents view*), the table and its rules in [Storage](../areas/storage.md).

## Consequences

- The service writes to the store for the first time (one short transaction per note). Behind a long write of another process
  (the clear transaction of `vandoxd templates`) a note save can hit the busy timeout and fails with an error page; nothing is
  half-written.
- An incident with notes cannot be deleted without moving its notes; retention (#46) has to move or keep them.
- Notes are bounded per row (1,800 characters), and there is at most one row per incident number ever given, so their size
  grows with incidents, not with requests; the incident page shows at most 64 notes of an incident.
- Chart labels may now carry external text, escaped and capped; titles and the other labels stay fixed texts.
- A chart covers at most 300 snapshots and says where it stops; a window with data of several sources is charted for one.
- A time inside a chart is not clickable, and kills are not drawn into the charts; the times of the page are the links.
- The list's duration is 0:00:00 for an incident with one kill; it says how long the kills went on, not how long the service
  was down, which the stored data does not tell.

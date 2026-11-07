# Incidents view

## Scope

The pages of the [web UI](web-ui.md) that show the stored incidents: the list (`/incidents`), the incident page
(`/incidents/<number>`) with the charts of its window and its notes, and the note endpoint (`POST /incidents/<number>/notes`), the first
write of the web UI. The user relies on it to look at an outage and to write down what was found. The incidents themselves are built
by `vandoxd incidents` ([Incidents](incidents.md)); the store reads and the note save are in
[Storage reading and log exploration](storage-reading.md) and [Storage writing](storage-writing.md).

## Behavior

### List page (`/incidents`)

- The page lists the stored incidents **newest first** (by window start, then number), **20 per page**: the number (a link to its page),
  the time of the first kill (a link to the logs of that moment, below), the duration, the kills, the victim, the cause and the backup
  state, and a marker for an incident that has a note.
- The duration runs from the first to the last kill (`H:MM:SS`, hours not bounded, `0:00:00` for one kill; the window's length is
  mostly the configured padding and is shown on the incident page). The victim is the command of the first kill, followed by `and
  further kills` when there are more. The cause is the programs that grew most in the window (`+<n> MiB` each, rounded half away from
  zero), `none` when the growth is known and no program grew, and `unknown` when it could not be told; the backup is `running`, `not
  running` or `unknown`.
- Prerendered it shows its title and `Loading…` and queries nothing ([Web UI](web-ui.md), *Pages and render mode*).
- A full page offers *Older incidents* (the next 20, after the last row), a later page offers *Newest incidents* (the first page again).
  An older page without a row shows `No older incidents.`; an empty store shows `No incidents are stored. Run vandoxd incidents after an
  import.` The position of the page is not in the address.
- The page reads summaries, not whole incidents: the columns of an incident and a few fields of its document (first victim, last kill,
  hypothesis), decoded row by row, so a page of 20 holds one document's bytes at a time
  ([Storage reading and log exploration](storage-reading.md), *Incidents*).

### Incident page (`/incidents/<number>`)

- Header: the window, the first and the last kill (each a link to the logs of that moment), the duration from the first to the last kill,
  the number of kills, and when the incident was built and last changed (UTC).
- Then the kills kept in the document (time, host, victim, process ID, anonymous memory, OOM score adjustment; at most 16, the page says
  how many more exist), the hypothesis (programs that grew, largest consumers, backup state and programs, the last boot, the number of
  process snapshots and service events), the timeline in stored order with the number of events the document left out, the connection
  peak (time, sockets, the number of connection snapshots in the window, the commands with the most sockets), the metric extremes (value,
  source, time) and the new templates and template spikes. A growth the incident could not tell is shown as `unknown`.
- A timeline row of the type `oom_kill`, `boot`, `backup_first_seen` or `backup_last_seen` carries the class `mark-oom`, `mark-boot` or
  `mark-backup` from this fixed map; no other type gets a class, whatever its text, and it is shown as escaped text.
- **Every time links to the logs of that moment**: the link is `logs` with `from` and `to` of the logs view's address
  ([Logs view](web-ui-logs-view.md), *Address*): `from` is the time floored to the whole second minus 30 seconds, `to` that second plus
  5 minutes, both clipped to the first and last whole second of the storable range, so the moment is at the top of the first page of the
  logs view. It is not a click inside a chart. Times link on the kills, the first and last kill, the timeline entries, the last boot, the
  metric extremes, the connection peak and the templates, and are shown as `yyyy-MM-dd HH:mm:ss` UTC.
- A number that no incident has (also one that is not positive, which reads nothing) shows `This incident does not exist. It may have been
  merged into another one by vandoxd incidents.` and no note form.
- Every link of the view is one of the fixed paths `incidents/<number>`, `incidents/<number>/notes` and `logs` plus the query of a
  validated address or a number.

### Charts of the window

Read from the records of `[start, end)` after the incident is shown, each from its own source:

- *Memory per program (MiB, stacked)*: one point per process snapshot of the source of the earliest process snapshot in the window; the
  five programs with the highest peak, ties by name, and `Other programs`; a program absent from a snapshot counts 0; at most 4,096
  programs are told apart, the rest are summed into `Other programs`.
- *Available memory and swap (MiB)*: `mem_available_bytes` and `swap_used_bytes` read from the source of the incident's stored extreme of
  that metric, at most 1,000 points each, at the union of their times with a gap where a series has no point or a value that is not
  finite; a metric without a stored extreme has no series.
- *Connections*: the sum of the state counts of each connection snapshot of the source of the earliest one in the window.
- A chart is cut after 300 snapshots and the page says `The chart shows the first 300 snapshots, up to <time> UTC.` A window without any of
  them shows `No snapshot or metric was stored in this window.` A chart takes its source per query, so live agent data, whose sources are
  collector names, charts as well as a legacy import with one source for all kinds.
- Chart labels are program names, escaped as external text and capped at 64 characters, so their direction controls are visible escapes;
  titles and the other series labels are fixed texts.

### External text

The external text of a stored incident (kill hosts and victims, timeline types, hosts and texts, programs, commands, metric names and
sources, templates) is shown only through the external-text display of the [web UI](web-ui.md) (*External text*): timeline texts capped
at 512 characters, every other field at 2,048. The page writes no raw HTML and no `style` attribute.

### Notes

- The page shows one form for the incident's own note and one for each note that was moved to it from an incident merged into it
  (`Written for incident #<n>`; such a heading holds only numbers), at most 64 (`Further notes of merged incidents exist and are not
  shown.` beyond).
- A form is a plain `POST` to `incidents/<number>/notes` with the antiforgery token, the number the note was written for as a hidden field
  and a text area of at most 1,800 characters; `Save note` stores it, an empty text deletes the note.
- A note is user text: it is shown only as the content of its text area, encoded, so a `</textarea>` in it cannot end the element.

### The note endpoint (`POST /incidents/<number>/notes`)

- It needs a session (anonymous: `401`) and a valid antiforgery token ([Web login](web-login.md), *Cross-site requests*). The checks run in
  this order: the antiforgery validation result (missing or invalid: `400`, the form is not read); the number of the route (not positive:
  `404`); the form: exactly one `note` and one `written_for` **from the form only**, never from the query string or the route (a field
  missing or given twice, also in another letter case, is refused with `400`).
- The text has its line ends converted to line feeds and **leading line feeds removed** (a text area drops the first line feed of its
  content, so a re-saved text stays equal), and a text of only white space is empty. More than 1,800 UTF-16 characters, or any U+0000, is
  refused with `400`; `written_for` is 1 to 18 ASCII digits above 0. The 1,800 characters count after decoding and line-end conversion;
  the worst case a browser sends under `maxlength` is 1,800 characters of three bytes written as `%XX`, 16,200 bytes, which stays under
  the 16 KiB form value limit of the service.
- *Saved* and *deleted* are answered with `303` and `Location: /incidents/<number>` built from the parsed number, *not found* (an unknown
  incident, or a note that does not belong to it) with `404`, a note the store itself refuses with `400`, a store error with `500`, all
  without a body. A refused post stores nothing.
- **The log never holds a note text**: a failed save is logged with the exception only (`incidents view: saving a note failed`), never
  the text, a form value or the number from the request; a refused post is answered with a bare status and no log line of its content.
- The save does not take the read gate; it is one immediate transaction under the write lock ([Storage writing](storage-writing.md),
  *Incident notes*).

### Calls and deadline

- The incident and its notes are read in one action, the data of the window in a second one after it; each action runs under the
  10-second deadline of the logs view ([Logs view](web-ui-logs-view.md), *Calls and deadline*) and a new action cancels the running one.
- A list or incident read that runs into the deadline shows `Reading the incidents took longer than 10 seconds and was stopped.`, a
  window read `Reading the data of the window took longer than 10 seconds and was stopped.` (the rest of the page stays). A store error
  shows `The incidents could not be read. The service log has the details.` or `The data of the window could not be read. The service log
  has the details.` and is logged (`incidents view: reading failed`).
- The incident and window reads take the shared read gate ([Web UI](web-ui.md), *Store reads of the views*), once per store call, so a
  window read of many snapshots takes it once per snapshot.

## Limits and errors

- **Memory per circuit.** The list holds 20 summaries. The incident page decodes one incident (up to 8 MiB of JSON, for the moment of the
  read) and keeps only capped text: up to 1,000 timeline entries with texts of at most 512 characters and every other field at most
  2,048, at most 64 notes of 1,800 characters, and charts of at most 300 points of six series, two series of 1,000 points and 300 points;
  while the window is read it holds one snapshot at a time and at most 4,096 programs times 300 snapshots of values (**about 10 MB**).
  These amounts are **per circuit**, not across the service: the gate is held per store call, so many open incident pages can each build
  at once, as with the 5 MiB of a logs page. The number of circuits is not bounded ([Web UI](web-ui.md), *Limits and errors*).

## Related decisions

- [0107](../decisions/0107-incidents-view-summaries-notes-per-incident-number-time-links-to-the-logs.md) — why the incidents view reads summaries and the window's own records, saves notes by a form post and links every time to the logs.

## Not here

- The general web UI (render mode, gate, external-text display, headers, circuits): [Web UI](web-ui.md); the other views:
  [Logs view](web-ui-logs-view.md), [Report page](web-ui-report-page.md).
- How incidents are built and merged: [Incidents](incidents.md).
- How summaries and notes are read and saved: [Storage reading and log exploration](storage-reading.md),
  [Storage writing](storage-writing.md).
- Sessions and the antiforgery token: [Web login](web-login.md).

## Implementation

`Vandox.Backend`: `Components/Pages/IncidentsPage` and `IncidentPage`, `Components/Incidents/IncidentNoteForm`; `Incidents/`
(`IncidentsView`, `IncidentRowView`, `IncidentDetailView`, `IncidentWindowReader`, `ProgramMemorySeries`, `GatedIncidentBrowser`,
`GatedRecordReader`, `IncidentNoteText`, `IncidentNoteEndpoint` with `RequiredAntiforgery` and no `AllowAnonymous`, logging
`IncidentNoteSaveFailed`); chart labels through `LogText.Escape(name, 64)`.

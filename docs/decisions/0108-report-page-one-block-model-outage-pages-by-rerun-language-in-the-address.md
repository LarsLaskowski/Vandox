# 0108: The report page renders the blocks the Markdown is written from, lists the outages in pages of 50 by a new run, takes the language from the address, and runs one report at a time under a deadline of 2 minutes; the Markdown is a GET download

- **Status:** Proposed
- **Date:** 2026-10-15
- **Area:** Web UI
- **Source:** Issue #231
- **Supersedes:** —

## Context

Issue #231 asks for the forensics report in the web UI behind the login: the same content as `vandoxd report`, in English or
German per user choice, with a download of the same Markdown, external text escaped by Razor, a deadline on the run because it
reads every stored line, and the incidents read through `ForensicsReporter` one at a time
([0100](0100-forensics-report-as-markdown-from-vandoxd-report.md), [0102](0102-report-language-from-a-flag-english-default-text-tables-in-code.md)).

What exists: `ForensicsReporter` streams the report into `MarkdownReportWriter` in three parts (summary, one section per outage,
notes); its memory does not grow with the number of incidents, which forged kill lines control, but a run keeps up to about 60 MB of
capped series across the run. The web UI renders no raw HTML (no `MarkupString`, [0099](0099-web-listener-security-headers-one-self-only-csp.md)),
shows external text only through its escaping component, takes the filters of the logs view from the address
([0106](0106-logs-view-address-as-the-view-bounded-search-pages-template-lines-by-normalization.md)), reads through a gate of two
concurrent store calls and stops every page action after 10 seconds ([0107](0107-incidents-view-summaries-notes-per-incident-number-time-links-to-the-logs.md)).
The number of live connections (circuits) is not bounded, and the container has 512 MB. A run of the command takes minutes on a large
database (README, *Write the forensics report*).

## Options considered

How the page shows the same content:

1. **One block model** (chosen): the report's parts are composed once as headings, paragraphs, list items and tables whose text is
   plain text or external text; the Markdown writer serializes the blocks (external text as code spans) and the page renders the same
   blocks (external text through the escaping component). The same content holds by construction; a test compares both.
2. **Razor markup that mirrors the Markdown writer with shared format helpers** — two layouts of one report drift apart, and only a
   test notices.
3. **Markdown rendered to HTML by a library (Markdig)** — a new dependency, and its HTML reaches the page only as raw markup, which
   0099 forbids.
4. **The Markdown text in a `pre` element** — the same bytes and safe, but tables and lists are not rendered.

How the outages are held by the page:

5. **Pages of 50 outages; each page is a new run that keeps only the outages of that page** (chosen) — the circuit holds a bounded
   document, every outage is reachable, and the store is read one incident at a time as by the command; a further page costs a whole run,
   which only an archive with more than 50 outages needs.
6. **All outages in the circuit** — memory per circuit grows with the number of incidents, which forged kills control.
7. **The sealed analysis kept per circuit and the outages read page by page from the store** — up to about 60 MB per circuit, with no
   bound on circuits.
8. **The first N outages on the page, all of them only in the download** — breaks "the same content"; 0100 rejected a cut list for the
   same reason.

How the language is chosen:

9. **The address parameter `lang` (`en` by default, or `de`)** (chosen) — the language is part of the view, bookmarkable and without
   state, like the filters of the logs view.
10. **A preference in a cookie or in the browser's local storage** — a new cookie, or script that reads the storage before the run.
11. **A preference in the server-side session** — new state in a security area, lost with the session.

How the Markdown is downloaded:

12. **A `GET` endpoint behind the session that streams the writer's output** (chosen) — the bytes of the command, no copy of the
    report in memory; a failure after the first byte aborts the response so that no truncated file looks complete.
13. **A form post with the antiforgery token** — keeps another site from starting a run, but the page itself, which another site can
    link to as well, runs the report on opening; the run changes nothing, and the run limit below bounds the cost either way.
14. **The Markdown built in the circuit and handed to the browser as a blob** — needs script and `blob:` URLs, which the policy does
    not allow.

How long and how many runs:

15. **One run at a time across the service, under a deadline of 2 minutes that includes the wait for the run** (chosen) — the 60 MB
    of a run exist once, the store calls of a run take the gate of the web views one call at a time, and a run that would take longer
    is stopped; the page and the download share the limit.
16. **The 10 seconds of the other views** — stops the report on any real archive.
17. **A background run whose result is kept for every viewer** — no deadline on a request, but 60 MB kept and a result that goes stale
    after `vandoxd incidents`.

How the download writes to the response (the server refuses synchronous I/O):

18. **A `StreamWriter` over the response body without `AutoFlush`, disposed with `await using`, and the Markdown writer flushing each
    part with `FlushAsync`** (chosen) — in the test against the running service (Kestrel) every download answered `500` while the writer
    was created with `AutoFlush = true`: setting that property flushes at once and synchronously, before anything is written, and the
    response body refuses synchronous I/O. Without it the writer only writes and flushes asynchronously, and its asynchronous disposal
    flushes asynchronously too (measured in a scratch program: a stream that throws on `Write` and `Flush` sees neither). Each part of the
    report still reaches the client when it is written, because the Markdown writer flushes after each part.
19. **`AllowSynchronousIO` on the server** — allows blocking writes for every request instead of keeping the writer asynchronous.
20. **The writer flushed but never disposed** — an undisposed writer the analyzers report, kept alive only by the response.
21. **A write-only wrapper of the response body whose synchronous `Flush()` does nothing** (with `AutoFlush = true`) — a new type and its
    tests only to absorb the synchronous flush that the `AutoFlush` setter makes; removing the setter removes the cause.

## Decision

Options 1, 5, 9, 12, 15 and 18. The page `/report` and the download `/report/markdown` are described in [Web UI](../areas/web-ui.md)
(*Report page*), the report itself in [Detection, alerts and reports](../areas/detection-and-reports.md) (*Forensics report*).

## Consequences

- The Markdown writer is rewritten on the block model; a golden file of the output before the change pins that its bytes do not
  change.
- External text is escaped by the rule of each medium: code spans with `\n` and doubled backslashes in the Markdown, the web UI's
  visible escapes in the page; the words, numbers and times are identical.
- An archive whose run takes longer than 2 minutes cannot be shown or downloaded in the web UI; `vandoxd report` remains for it. The
  time of a run on a large database is measured once and written into the area document.
- A second user, or the download next to an open page, waits for the running report within its own deadline.
- A failure before the first byte of the download removes the file headers and sets only the status; it never clears the response, so
  the security headers and `Cache-Control: no-store` stay.
- The incident store of the web refuses to save with a faulted task, so callers that await it see the refusal as from any store.
- Another site can make a signed-in browser open the page or the download and so start a run; it changes nothing and is bounded by
  the run limit and the deadline.

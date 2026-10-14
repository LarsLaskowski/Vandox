# 0103: The logs view takes every filter from the address, searches in bounded pages from the index or by a time-ordered scan, and finds a template's lines by normalizing candidates

- **Status:** Proposed
- **Date:** 2026-10-13
- **Area:** Web UI
- **Source:** Issue #26
- **Supersedes:** —

## Context

v0.1.0 is the forensics release ([0020](0020-analysis-before-alerting-forensics-release.md)): the imported logs must be searchable and
explorable in the web UI, with filters for source, severity, service and time range, context lines around a hit, a template view with
first seen and hourly counts that leads to a template's lines, every filter in the URL, and a search over several million lines that
answers in under two seconds on the backend host (a Celeron J3455 NAS, 3 to 4 times slower than the development host in
`docs/BENCHMARKS.md`; about 5 times for the search work of this view in its first run there).

The store's search ([0066](0066-log-search-takes-literal-terms-only.md)) joins every FTS5 match with the line and record tables and
sorts by capture time before the limit, so its cost grows with the number of stored lines that contain the words, not with the range
or the limit; 0066 left narrowing by time to the logs view. Imported lines are not stored in time order: an older log imported later
gets higher record IDs. No table links a stored line to its template; a template is the key (source, program, normalized text)
([0095](0095-log-templates-token-rules-statistics-in-the-write-path-usual-level-from-covered-hours.md)), and the write path and its
throughput criterion ([0083](0083-storage-write-criterion-is-the-median-of-five-batches-on-the-data-volume.md)) carry the
deduplication of the ingest security area. Log text is external and may be forged; the web UI escapes HTML and sends one self-only
policy ([0098](0098-web-ui-serves-only-its-own-static-web-assets.md),
[0099](0099-web-listener-security-headers-one-self-only-csp.md)), but direction-changing characters (U+202E) survive HTML escaping. Every
page is prerendered and then runs in the Interactive Server mode
([0081](0081-backend-host-two-listeners-json-logs-blazor-interactive-server.md)), so a query in the page's start-up runs twice. The login
(#25) is not built yet.

## Options considered

How a search stays within two seconds:

1. **Index first only, bounded by the deadline** — fast for rare words; a word in a quarter of the lines costs a lookup and a sort per
   match on every page. Misses the criterion for the most common searches.
2. **Time-ordered scan only** — every request is bounded, but a rare word in a large range needs many continuation steps where the
   index answers at once.
3. **Per request: from the index when the words are selective, otherwise a bounded time-ordered scan** — the choice reads only the
   index (a capped read of the matching row IDs, which index mode then reads by record ID, so the index is walked once), both modes are bounded (the cap, the number of examined lines), and a request that hits
   the scan bound returns how far it got.
4. **Order by record ID, which FTS5 delivers natively and stops at the limit** — fast, but pages would not be in time order for imported
   data.
5. **A schema change that puts time into the full-text index or adds a time-clustered index of matches** — FTS5 cannot filter a range
   on an unindexed column; a second index doubles the write cost the write criterion is measured on.

How to list a template's lines:

6. **A template ID column on every stored line, set by the write path and the rebuild** — exact and indexable, but changes the write
   path and its criterion, leaves existing lines without a template until a rebuild rewrites every row, and a frequent template still
   needs option 3.
7. **Candidates narrowed by the template's literal words, each normalized and compared with the template** — no schema or write-path
   change; the normalizer is the definition of a template's lines, so the result is exact; a word is used only when every line of the
   template must contain it, so no line is lost.
8. **A text search for the template's words alone** — returns lines of other templates with the same words.

The view:

9. **The template number in the address**, or source, program and template text — the text survives a rebuild only when the rules did
   not change, and a rule change is the reason for a rebuild; the number is short and exact, and a stale one is reported.
10. **Context on demand for one hit**, for every hit (two more queries and up to ten lines per hit), or a separate context address (not
    asked for).
11. **Query only once the page is interactive**, query while prerendering and carry the result into the circuit (up to 1.6 MB of
    external text in the page), or no prerender for these pages (every page is prerendered).
12. **Invisible and direction-changing characters as visible escapes**, isolation of the direction only (U+202E still reverses the rest
    of its line), or removing them (loses evidence).
13. **The escaped text of a field capped, with a visible cut marker**, or every escape written out — a 16 KiB message of control
    characters escapes to about 98,000 characters, roughly 192 KiB per field and 20 MB for one page with its context, while the number
    of circuits is not bounded before the login.

Reader slots and the scan:

14. **At most two explorer calls at once across the service, one per page**, no limit (four tabs hold the four reader connections the
    health check also needs, so it fails for up to the deadline, again and again), or a separate reader pool for the view (changes
    the store's reader bound for one consumer).
15. **The scan tests each examined line against the full-text match of the user's words on its own, and examines fewer lines the more
    word parts the text has**, or collects the matches of the chunk's record-ID range into a set — imported lines are not in ID order,
    so a time-ordered chunk can span almost every ID and the set grows with the matches, not with the scan bound. The per-line test
    opens one index iterator per word part (a term such as an IP address or a path has several), so its cost grows with the parts;
    dividing the bound by them keeps the work of a request about constant, and a fixed bound would let a text of many common parts in
    a range without them run into the deadline. A template's words are left out of the scan's test: its source and program are
    filters and the normalization decides exactly, so they would only make the scan of a template's lines shorter.

Reading the matches and the template list (after the first benchmark run):

16. **The probe keeps the matching IDs (at most the cap) and index mode reads the lines by those IDs**, or the probe only counts and
    index mode asks the index again (the intersection of several common words is walked twice, about half of the slowest case), or an
    empty page when the count is 0 (halves only an empty intersection; a one-line intersection still walks twice).
17. **The template list sums the template hours per template in key order, then joins the templates**, or sums the hours of the range
    through the index on the hour (two lookups per template hour; about 0.75 s for 374,020 template hours, against 0.03 s), or reads
    the all-time count for the whole storable range (does not help a 30-day range). The chosen statement reads the whole table
    whatever the range is, which is never more than the other one costs for the whole range.

Bounding the index check (after the first run on the reference host):

18. **The probe under a time budget of about 0.7 s, enforced by the progress handler; when it runs out, the request uses scan mode and
    the page says so** — the handler does run while FTS5 reads its index pages (measured with the project's SQLite 3.53.3: at most
    29 ms apart, an interrupted probe stops within that gap), so every page answers in about two seconds whatever the words and the
    size of the store; the cost is that such a search may give no final empty answer until the range is narrowed (the Product
    Manager's choice). Rejected: the probe in record-ID slices (measured: slices do not make a phrase of common parts cheaper, 58 s
    in all for a 200-part term); a second thread calling `sqlite3_interrupt` (checked at the same points, no earlier stop); a separate
    connection for the probe (breaks the reader bound of option 14); keeping the final answer and capping the word parts instead
    (the probe still grows with the stored lines, several seconds on the reference host); a smaller word limit (refuses ordinary
    searches, an IP address alone has four parts); a full-text index without positions (cannot answer terms of several parts) or a
    tokenizer that keeps `.` and `/` inside words (changes what a word matches, needs a rebuild).

## Decision

Options 3, 7, 9, 10, 11, 12, 13, 14, 15, 16, 17 and 18: the logs view reads every filter from its address and queries only when interactive, each query under
a deadline; the store answers a page from the full-text index when the words match fewer rows than a cap and otherwise by scanning a
bounded number of lines in time order, testing each examined line on its own, and falls back to the scan when the index check does not end within its time budget, and finds a template's lines by normalizing the candidates its literal words select. External text is
escaped and capped per field with a visible cut marker; the view runs at most two store calls at once across the service, so the
health check keeps a reader. The rules
— address parameters, limits, modes, texts and the display of external text — are in [Web UI](../areas/web-ui.md) (*Logs view*) and
[Storage](../areas/storage.md) (*Log exploration*).

## Consequences

- Every request does bounded work; a common word over a large range is answered page by page, and a request that ends at the scan
  bound says up to which time it searched and offers to continue. There is no total hit count. The page position is not in the
  address: a shared address opens the first page.
- The full-text work of several common words with a rare or empty intersection grows with the number of word parts (tokens, not
  terms: the 16-term rule does not bound them) times the size of their index entries; it runs under the deadline and the probe's
  time budget, both stopped through the progress handler at its next call. A search whose index check runs out of the budget is
  answered page by page with *Continue*, every page paying the budget again, and may give no final empty answer until the range is
  narrowed; on a loaded host more searches take this path, the lines found do not change.
- The template list counts whole hours: a range that starts or ends inside an hour counts that hour completely.
- A line made mostly of invisible characters is shown cut, with the number of characters not shown.
- Two tabs with slow searches make a third wait for a slot (within its deadline); the health check is not affected.
- Search operators stay unavailable (0066); results are oldest first within the range.
- The literal-term search of 0066 stays as it is for its existing callers; the view uses the new explorer interface.
- A template link breaks when `vandoxd templates` rebuilds the statistics (numbers change); the page says so.
- The criterion is measured with `tools/Vandox.StorageBenchmark --search-lines` on the reference host (`docs/BENCHMARKS.md`). The
  benchmark program asserts nothing and is excluded from the coverage measure with `[ExcludeFromCodeCoverage]`, as the template benchmark
  already is; the selectivity cap and the scan bound may be tuned from the measurement without a new decision.
- Until the login (#25) exists, whoever reaches the web port can read every stored log line through the view; the compose file publishes
  the web port on loopback only ([0060](0060-compose-file-port-bindings-volumes-and-memory-limit.md)), and v0.1.0 is not released
  without the login. Forged lines are shown like real ones. Until then the number of circuits is not bounded either (a page holds
  up to about 5 MiB of capped text in the worst case, the container 512 MB), and the host has no Host-header allow-list, so through
  DNS rebinding a website the operator's browser visits can read the logs when a reverse proxy forwards unknown Host headers or the
  web port is reachable from the LAN; the login's reverse-proxy trust (#25) has to include a Host allow-list. Nothing from `main` is
  published before a release tag.
- A program-only filter over a large range scans; skipping hours without lines of the program through the template statistics is left
  to a measurement that needs it.

# Log search and exploration

## Scope

The literal-term log search and the bounded log exploration that serve the logs view ([Logs view](web-ui-logs-view.md)): rules,
index and scan modes, pages, template lines, sources and hours. Every read runs on a reader connection and is interrupted by a cancelled
token or a passed deadline ([Storage](storage.md), *Database file and connections*).

## Behavior

### Log search

A search takes literal terms only; the text is never passed to FTS5 as a query.

- The text must be valid UTF-8 of at most 1,024 bytes, without a control or format character other than white space. It is split at
  white space into 1 to 16 terms, and every term needs at least one letter or decimal digit.
- Each term is quoted as an FTS5 string (a `"` inside doubles) and the terms are joined with a space, so all terms must match and no
  FTS5 operator, prefix (`*`), column filter or `NEAR` has any effect. The expression is bound as a parameter, never concatenated into
  SQL.
- Matching is case-insensitive and ignores diacritics; a term with punctuation (`anon-rss`) matches the tokens in order as a phrase.
  `ß` is not folded to `ss`. A term that consists only of numbers that are not decimal digits (`½`, `Ⅻ`) or of private-use characters is
  refused although the tokenizer would index it.
- A violated rule is an error that names the rule and never contains the search text.
- The cost of a search is not bounded by the time range or the limit: SQLite runs the `MATCH` over the whole retention first and filters
  by time and source afterwards. The bound is time: a cancelled token or a passed deadline interrupts the running search, so every caller
  that serves a user must pass a deadline. The logs view does not use this search: it searches through the log exploration below, which
  bounds the work of every request.

### Log exploration

The logs view ([Logs view](web-ui-logs-view.md)) reads through the log exploration; the log search above keeps its rules for its other
callers. Every operation is interrupted by a cancelled token or a passed deadline, with no partial result. A missing string is an argument
error; a broken rule is a store error whose message is `store: invalid query: ` and the rule, and never contains the search text.

**Finding lines.** A request has a text, a source, a program, a highest priority, a template number, a time range `[from, to)`, an
optional position *after* (capture time and record ID), a page size and a scan bound.

- Every filter given must hold: all words of the text (the rules of *Log search*), the source (exact), the program (exact, ordinal), the
  priority at most the given one (a line without a priority is then not returned), the capture time in the range. An empty text applies
  the other filters only. The lines come **oldest first, by capture time, then record ID**: the order of the log, not of the record IDs,
  because imported lines are not stored in time order.
- A page holds at most the page size of lines. Its *next* position is the position (capture time, record ID) of the page's last line when
  the page is full, and a request with *after* set to it continues with the next line, without a gap or a repeat, also across lines with
  the same capture time. *Next* is empty at the end of the range.
- **Two strategies, chosen per request from the full-text index alone.** First the record IDs of the matching rows are read from the
  index up to a cap of **12,000**, without looking at the lines, under the index check budget below. None: the page is empty and nothing
  more is read. Fewer than the cap: **index mode**, which reads the lines with exactly those record IDs (a lookup by primary key; the
  index is not read again), applies the filters and sorts them (at most the cap, by construction). The cap or more, or no text: **scan
  mode**, which walks the log lines of the range in capture-time order from the position (through the index on kind, source and time with
  a source, or the index on kind and time without one) and keeps the lines that pass the filters and, with a text, are in the full-text
  match.
- In scan mode the match is tested for each examined line on its own and nothing is collected over the range, so scan mode holds at most
  the page and its work grows with the examined lines, not with the matches: every term of the user's text that is **one word part** has
  its own `EXISTS` on the full-text index (a single token stops at its seek), and the terms of **several parts** (an IP address, a path,
  `a.b`) are tested in the line index below. The whole text is never tested in one `MATCH` per line, because `rowid = …` does not bound the
  full-text work to one line.
- The lines found, followed through *next* until it is empty, do not depend on the strategy; only scan mode can end a page early.
- **The scan bound:** scan mode examines at most the scan bound of log lines (1 to 100,000; the logs view uses **8,000**), counted whether
  or not they match, and for a text of several **word parts** at most the bound divided by the parts (at least 1): the bound for no text or
  a text of one part, bound / `n` for `n` parts. The parts are the runs of letters, numbers and private-use characters that the tokenizer
  splits the user's text into (`10.0.0.1` has 4, `postfix/smtpd connect` 3). The scan tests only the **user's text**: the words of a
  template narrow the index check only, since the normalization decides which lines are the template's.
- When the scan bound ends the request before the page is full, the page says that the scan bound was reached and *next* is the position
  of the last examined line, also when the page is empty: a request with *after* set to it searches on, so continuing until *next* is
  empty returns every matching line exactly once. A full page ends the scan at its last line (the bound is then not reported as reached).
- **The line index.** The examined lines that pass every other test are collected in chunks of at most **64 lines** (and at most 262,144
  characters of messages, whichever comes first), inserted into a private in-memory SQLite database (`:memory:`) with one FTS5 table of
  the same tokenizer as `log_fts` (`unicode61 remove_diacritics 2`), matched against the terms of several parts (the same quoted
  expression as for the stored index) and emptied again; a line is kept when it matches. The same library, tokenizer and expression decide
  on the same stored message, so scan mode and index mode return the same lines. The in-memory database is opened only for a text with
  such terms, once per call, reads no database file and takes no reader connection (so the reader bound of the web views holds), is
  interrupted by the call's cancellation like a reader statement, and is disposed on every path. Messages are bound parameters only. Its
  work is bounded by the text of one chunk: at most 64 messages of 16,384 bytes (1 MiB; 256 KiB of characters ends a chunk earlier) and
  their index in memory, with at most two calls at once.
- **The page clock.** A scan ends after the examined line (or the matched chunk) at which **1.5 s** have passed since the start of the
  call, the index check's budget included, measured with the store's clock; at least one line is always examined. It then reports the
  scan bound as reached, with *next* at the last examined line, as at the line bound, so a continuation always advances. The line bound
  stays the deterministic upper bound; the clock only ends a page earlier, and the lines found by following *next* to the end do not
  depend on it. A call that passes no page budget (used by tests) has no clock.
- **The index check budget.** The index check stops when the call is cancelled (the deadline: the call then ends as cancelled, as
  everywhere) or when the time since the check started reaches **0.7 s**, measured with the store's clock. A check stopped by the budget
  discards its IDs, and the request runs in scan mode with the bound above; the page says that the index check was stopped. A budget of
  zero or less skips the check the same way. A check that ends within the budget works as described (no IDs: an empty page; fewer than the
  cap: index mode; the cap: scan mode, the index check not reported as stopped). Any other database error of the check is a store error.
  The cancellation check of the call is in place again after the index check on every path. The budget is wall-clock time of the host, so
  on a loaded host more searches go the stepwise way; which lines are found, followed to the end through *next*, does not depend on it.
  Every page of such a search pays the budget again, because the store does not remember the mode between requests, and the search may
  have no final "no lines" answer until the range is narrowed.
- **Template lines:** with a template number the lines are those of the template's source and program whose message normalizes (the rules
  of [Log templates](log-templates.md)) to the template's text, exactly. The words of the template that contain no placeholder narrow the
  candidates: they are added to the text as search terms (at most 16 terms and 1,024 bytes in all, the user's words first; a template
  longer than 1,020 bytes loses its last word, which a cut may have split; a template without such a word uses scan mode). Every
  candidate's message is normalized and kept only when it equals the template's text; in scan mode a rejected line counts as examined. A
  source or program filter that differs from the template's gives no lines. An unknown number is `store: invalid query: unknown template`.
- **Rules**, each refused with `store: invalid query: ` and: `from and to are required`, `from and to must be within the storable range`,
  `from must be before to`, `limit must be between 1 and 1000`, `scan limit must be between 1 and 100000`, `source is longer than 1024
  bytes`, `program is longer than 1024 bytes`, `priority must be between 0 and 7`, `template ID must not be negative`, `after must be
  within the storable range`, `after id must be positive`, and the rules of *Log search* for a non-empty text.

**Context.** A line is returned with up to the requested number of lines before and after it (0 to 20 each) with the same source, log
name and host, in capture-time order, whatever their other fields; lines of other logs of the source are skipped. Each side examines at most **2,000** lines
of the source and returns fewer when that bound or the data ends. Errors: `record ID must be positive`, `context lines must be between 0 and
20`, `unknown log line` (an ID that is not a stored log line).

**Sources, templates, hours.**

- **Sources:** the distinct sources of stored log lines in ordinal order, at most 64 (one index seek per source).
- **One template** by number, or none (`template ID must be positive`).
- **Templates:** the templates with at least one line in the hours that start in the range, optionally of one source and one program, each
  with its number of lines in those hours, most lines first, then lowest number, at most a limit of 1 to 1,000 (`limit must be between 1
  and 1000`; the window and length rules as above). The count is kept per UTC hour, so a range start or end inside an hour counts that
  **whole hour**. The hours are summed per template in key order (the range conditions are on the hour column without using its index),
  then joined to the templates, so the statement reads the whole table of template hours whatever the range is: its cost grows with the
  stored template hours (about 0.03 s for 374,000 template hours), not with the range.
- **Source hours:** the start of every hour in the range in which a source has lines, ascending (`source is required`; limit 1 to 10,000,
  `limit must be between 1 and 10000`). **Template hours:** a template's hourly counts, as the hourly counts of
  [Log templates](log-templates.md). Both come from the template statistics, so lines stored before them (a database upgraded from schema
  7) have none until `vandoxd templates` has run.

**Storage layout.** No table, column or index exists for the exploration alone; scan mode relies on the record indexes serving the order
of the scan ([Storage](storage.md), *Schema*).

## Limits and errors

- **Cost of the exploration.** Index mode reads at most the cap of matches. Scan mode reads at most the divided bound of lines and does one
  index lookup per word part of the user's text for each. The index check is bounded by its budget plus one gap between two cancellation
  checks (31 ms at most in the measurements). A scan page ends by the line bound or the page clock, whichever comes first: its time is the
  clock (1.5 s) plus the examined line or chunk that was running when it ran out, and the read of at most 100 lines.
- **The residual:** one `MATCH` inside the line index runs to its end, so a chunk of crafted messages (long runs of one repeated word and a
  phrase of many parts of it, 64 lines or 256 KiB of characters) can keep one chunk busy for about 0.3 s on the development host and about
  1.5 s on the reference host (an estimate of the security review, not a measurement), and a page for up to about 3 s; the deadline of the
  caller still ends the call. A stored message is bounded (16,384 bytes) but not trusted: any local process can write such lines.
- The per-line tests are cheap otherwise (`docs/BENCHMARKS.md`, *Log search*). A search without a text and with a program or source that
  few lines have examines up to the scan bound of lines per request and says how far it got.
- The exact error texts are stated with each operation above.

## Related decisions

- [0066](../decisions/0066-log-search-takes-literal-terms-only.md) — why log search takes literal terms only.
- [0097](../decisions/0097-reader-statements-interrupted-by-a-progress-handler-bound-to-the-token.md) — why a cancelled token or deadline interrupts a reader statement.
- [0106](../decisions/0106-logs-view-address-as-the-view-bounded-search-pages-template-lines-by-normalization.md) — why the logs view's search chooses between the index and a bounded scan and finds a template's lines by normalizing candidates.

## Not here

- Other store reads: [Storage reading](storage-reading.md).
- The database file, connections, interruption of reader statements and schema: [Storage](storage.md).
- The template queries and the normalization: [Log templates](log-templates.md).
- The page that shows the search, its address and its deadline: [Logs view](web-ui-logs-view.md).

## Implementation

`Vandox.Storage`: `SqliteStore` implements `ILogSearcher` with `FtsQuery` and `ILogExplorer`. The exploration is `LogFinder`
(`SelectiveMatches`, `ProbeBudget`, `PageBudget`; the page reports `ScanLimitReached`, `IndexCheckStopped` and `Next`), `LogFinderQueries`,
`LineIndex`, `LogCriteria`, `LogFilterRules`, `LogExplorerQueries` (`LogContextAsync`, `LogSourcesAsync`, `TemplateAsync`,
`TemplatesAsync`, `SourceHoursAsync`, `TemplateHoursAsync`) and `TemplateTerms`. A broken rule is an `InvalidQueryException`, a
cancellation an `OperationCanceledException`, a missing string an `ArgumentNullException`.

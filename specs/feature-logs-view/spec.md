# Spec: Logs view

Status: Approved by Lead

Source: Issue #26 (milestone v0.1.0 – Forensics). Depends on #24 (web UI foundation, PR #229) and #22 (log templates and hourly
frequency, PR #211), both merged.

## Problem / motivation

v0.1.0 is the forensics release: Vandox imports the existing logs of the production server and explains past outages
(memory exhaustion on the 2 GB server, the OOM killer ends MariaDB, Plesk and mail go down). The imported lines, their
full-text index and the template statistics exist in the database, but nothing shows them: the web UI has only the overview
page. The operator needs to search and explore every imported line in the browser, see the lines around a hit, and see which
message shapes (templates) appeared when and how often.

## Behavior

The web UI gets two pages under one navigation entry *Logs*:

1. **Search** (`/logs`): a form with a text field, a source selection, a program (service) field, a severity selection and a
   time range (start and end, UTC). Submitting it changes the address of the page; the page always shows the result of its
   address, so a view is bookmarked or shared by copying the address, and back and forward in the browser move between
   searches.
   - The text is a set of literal words that must all occur (the existing search rules: no operators, case and diacritics
     ignored). Without text the page lists the lines that match the other filters.
   - Severity means "at least as severe as": choosing `warning` shows `emerg` to `warning`. Lines without a severity are not shown
     while a severity is chosen.
   - The source is one of the sources that hold log lines (the importer's parser types such as `journal`, `syslog`,
     `mariadb`); the program is matched exactly (for example `postfix/smtpd`, `mariadbd`).
   - Results are listed oldest first within the range, 100 per page, with time (UTC, milliseconds), source, host, program and
     process ID, severity and the message. *Next page* and *First page* move through them; the page position is not part of the
     address, so an opened or shared address shows the first page and browser back returns to the previous search, not to the
     previous page. Without a range the whole stored data is searched.
   - A search over a very large range with a common word does not run until it has seen every line: one request examines a
     bounded number of lines, and when that bound ends the request before the page is full, the page says up to which time it
     has searched and offers *Continue*. The same happens when the words cannot be looked up in the index within about 0.7
     seconds (for example many common words that never occur in the same line, or an IP address whose parts are all common): the page
     then also says "These words are too common to look up at once, so the search goes through the time range step by step. Narrow the
     time range to get a complete answer." Such a search may give no final "no lines" answer until the range is narrowed (Product
     Manager's decision, option A of the reference-host run 1).
   - Each hit can be expanded to show up to 5 lines before and after it from the same log (same source, log file name and
     host), whatever the filters; one hit is expanded at a time.
2. **Templates** (`/logs/templates`): the templates of a time range (optionally of one source and one program) with their
   text, source, program, first seen, last seen, total number of lines and number of lines in the range (counted in whole
   hours: a start or end inside an hour counts that whole hour), the most frequent first. Selecting a template shows its hourly counts as a chart (an hour in which the source logged but the template did not
   is 0; an hour in which the source logged nothing is a gap, unknown) and a link *Show lines* that opens the search page with
   that template as a filter, which lists exactly the lines that have this template.

Common to both:

- Every filter is in the address. An address with an invalid or repeated filter shows an error that names the parameter and
  runs no query; unknown parameters are ignored.
- Log text (messages, templates, programs, hosts, sources) is shown as text, never as markup; invisible and direction-changing
  characters are shown as visible escapes such as `\u202e`, so a forged line cannot rearrange or hide what is displayed. A field
  whose escaped text would grow beyond a fixed size (a message made mostly of invisible characters) is cut with a visible marker
  that says how many characters are not shown.
- A query that takes longer than 10 seconds is stopped with a message that asks to narrow the range or add filters. A rule the
  store refuses (for example more than 16 words) is shown with its rule, never with the text.
- Nothing is queried before the page is interactive; the prerendered page shows the filters and "Searching…".

## Acceptance criteria

- [ ] AC-A: Full-text search with filters for source, severity, service (program) and time range, with pages and *Continue*.
- [ ] AC-B: The result list shows up to 5 context lines before and after a chosen hit.
- [ ] AC-C: The template view shows templates with first seen, last seen, counts and the hourly counts of a chosen template, and
  switches from a template to exactly its lines.
- [ ] AC-D: Every filter of both pages is in the address; opening the address reproduces the view (shareable, bookmarkable).
- [ ] AC-E: A search over 5,000,000 stored lines answers its first page in under two seconds on the reference host
  (Synology DS918+, SSD volume) for the measured cases in `docs/BENCHMARKS.md`.
- [ ] AC-F: External text is displayed escaped (markup as text, invisible and bidirectional characters as visible escapes).

## Out of scope

- Search operators (`OR`, `NOT`, prefixes, phrases across words): record 0066 stands.
- A total hit count, sorting newest first, a host filter, a text filter on templates, export of results.
- The incident and report pages (#27 and the report's page), the login (#25), retention (#46).
- Keeping links to templates valid across `vandoxd templates` rebuilds (template numbers change; documented).

## Open questions

None. The user-visible choices the request leaves open (oldest first, 100 per page, 5 context lines on demand, severity as "at
least as severe", whole data without a range, UTC times, template links by number) are the Lead's, recorded in
[0103](../../docs/decisions/0103-logs-view-address-as-the-view-bounded-search-pages-template-lines-by-normalization.md); the
Product Manager may override them at review.

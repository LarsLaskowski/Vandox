# 0066: Log search takes literal terms only; every term is quoted for FTS5, operators and prefixes are not offered yet

- **Status:** Proposed
- **Date:** 2026-10-06
- **Source:** Issue #14
- **Supersedes:** —

## Context

Issue #14 requires full-text search for logs with FTS5 (0007). The search text will come from the logs view
(#26), i.e. from a user's browser, and FTS5 parses the right-hand side of `MATCH` as its own query language:
barewords, `"strings"` with `""` as escaped quote, `*` prefix queries, `^` initial-token queries, `+`
concatenation, `NEAR(...)`, the upper-case operators `AND`, `OR`, `NOT`, parentheses and column filters
(`col:`, `{a b}:`, `-col:`). A malformed expression is an SQL error, a NUL byte ends the expression early
("unterminated string"), and some expressions (many prefixes, `OR` chains) are expensive. *Security areas* 10
in `.squad/project.md`: external input yields an error, never a crash, an unbounded allocation or a hang.

## Options considered

1. **Pass the text to `MATCH` unchanged** — full FTS5 syntax for users; every syntax error becomes a database
   error, and prefix and `OR` queries add cost on top of the plain lookups.
2. **Own query language** (e.g. `-word`, `"phrase"`) translated to FTS5 — useful, but a user-facing design
   that belongs to #26, which has not been planned.
3. **Literal terms** — the text is split at white space and each term is quoted as an FTS5 string, so every
   FTS5 operator character is literal; all terms must match (implicit `AND`).

## Decision

Option 3. `store.SearchLogs` takes `LogSearch.Text` and builds the `MATCH` expression with `ftsQuery`:

- The text must be valid UTF-8, at most 1024 bytes, and contain no character of category Cc or Cf other
  than white space (this rejects NUL; tab and newline separate terms); it is split with `strings.Fields`;
  there must be 1 to 16 terms; a term without any letter or digit is refused (it would match nothing).
- Each term becomes `"` + the term with every `"` doubled + `"`; terms are joined with a space.
- The expression is bound as a parameter, never concatenated into SQL. Errors name the rule that failed and
  wrap `store.ErrInvalidQuery`; they never contain the search text.
- Matching is case-insensitive and ignores diacritics (`unicode61 remove_diacritics 2`, 0063); a term
  containing punctuation (`anon-rss`) matches the tokens in order as a phrase; `*` inside a term is not a
  prefix query.

## Consequences

- No input can produce an FTS5 syntax error or invoke an operator, and the expression has at most 16
  phrases of together at most 1024 bytes.
- The cost of a search is **not** bounded by the time range or the result limit: SQLite runs the `MATCH`
  first (`SCAN log_fts VIRTUAL TABLE INDEX 0:M1`), looks up every matching line, filters by time and source
  afterwards and sorts the remainder (`USE TEMP B-TREE FOR ORDER BY`). The cost grows with the number of
  stored lines that contain the terms over the whole retention (90 days of logs); measured on 2026-10-06 on a
  2.1 GHz Xeon, a common word in 300,000 lines and a 60-second window took about 77 ms. The bound is time:
  `SearchLogs` honors its context, and a cancelled or expired context interrupts the running query (driver
  behavior, tested). Every caller that serves a user (the logs view, #26; the query API, #47) must pass a
  context with a deadline. A plan that narrows by time first (e.g. FTS5 `rowid` ranges, which needs record
  IDs ordered by capture time — not the case for imports and backfills) is left to #26 if measurements
  require it.
- Users cannot search by prefix, with `OR` or `NOT`, or by column. #26 may add an own syntax or allow
  operators; that is a new decision that supersedes this one.
- `ß` is not folded to `ss` by the tokenizer (`grosse` does not find `Größe`).

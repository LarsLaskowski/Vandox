# 0066: Log search takes literal terms only; every term is quoted for FTS5, operators and prefixes are not offered yet

- **Status:** Accepted
- **Date:** 2026-10-06
- **Area:** Storage
- **Source:** Issue #14
- **Supersedes:** —

## Context

Full-text search for logs uses FTS5 (0007). The search text comes from a user's browser, and FTS5 parses the right-hand side of `MATCH`
as its own query language (operators, prefixes, column filters, `NEAR`). A malformed expression is an SQL error, a NUL byte ends it
early, and some expressions are expensive. External input must yield an error, never a crash, an unbounded allocation or a hang
(*Security areas* 10 in `.squad/project.md`).

## Options considered

1. **Pass the text to `MATCH` unchanged** — full syntax for users; every syntax error becomes a database error and prefix and `OR` queries add cost.
2. **Own query language** translated to FTS5 — useful, but a user-facing design that belongs to the logs view, which is not planned.
3. **Literal terms** (chosen) — split at white space, each term quoted, all terms must match.

Within option 3, a term is refused when it has no letter or decimal digit (simple, refuses every punctuation-only term), not when the
tokenizer would not index it (would also accept `½`, but nobody needs it and the simpler rule was already reviewed).

## Decision

Option 3. The exact rules and limits are in the [Storage](../areas/storage.md) area (*Log search*).

## Consequences

- No input can produce an FTS5 syntax error or invoke an operator, and the expression stays small.
- The cost of a search is not bounded by the time range or the result limit, because SQLite runs the `MATCH` over the whole retention first (a common word in 300,000 lines and a 60-second window took about 77 ms). The bound is time: the search honors its cancellation token, and every caller that serves a user must pass a deadline. Narrowing by time first is left to the logs view if measurements require it.
- Users cannot search by prefix, with `OR` or `NOT`, or by column; offering them is a new decision that supersedes this one.
- `ß` is not folded to `ss`, and numbers that are not decimal digits (`½`, `Ⅻ`) alone are refused; both are known limitations kept for a simple rule.

# 0070: Log parsers implement one interface in internal/logparse; an explicit registry picks the parser by confidence

- **Status:** Proposed
- **Date:** 2026-10-06
- **Source:** Issue #15
- **Supersedes:** —

## Context

Issue #15 asks to "detect the source type per file (by path/name and content sniffing) and hand it to the
matching parser". The parsers are separate issues: #16 (journal export, syslog, kern.log), #17 (MariaDB
error log), #18 (mail.log), #19 (Plesk and web server error logs, "unknown formats kept as raw lines"), #20
(legacy `top`/`lsof` log, which yields metrics and snapshots, not log lines). #16 requires its parsers to
live in `internal/` "so the agent can reuse them for log shipping" (#37). #16 needs the file's name and
modification time to give year-less syslog timestamps a year. Every parser reads lines and must keep memory
flat (#15, #18). Log parsing is a security area (`.squad/project.md`, 10).

## Options considered

Where the interface lives:

1. **In the importer (`cmd/vandoxd/internal/importer`)** — next to its only caller today; but parsers in
   `internal/` could not implement it without importing backend code, which Go's `internal` rule forbids
   (0061).
2. **In a shared package `internal/logparse`** — parsers in `internal/` and the importer both depend on it;
   the agent can use the same parsers later.

How parsers are registered:

- a. **Global registry filled by `init()` functions** — adding a parser is one file; but registration order
  (the tie-breaker) depends on file names and imports, tests share global state, and a parser is active just
  by being linked.
- b. **Explicit list passed to `NewRegistry`** — `vandoxd` names its parsers in one function in priority
  order; tests build their own registries.

How the parser is chosen:

- i. **The first parser whose `Detect` returns true** — simple; but a generic parser (e.g. #19's raw-line
  fallback) placed early would shadow specific ones, and order alone expresses priority.
- ii. **A confidence level per parser, highest wins, ties by registration order** — a content signature
  beats a name match; generic parsers can claim files weakly.
- iii. **Ambiguity is an error** — avoids a silent wrong choice, but makes overlapping name patterns (e.g.
  `syslog` and `kern.log` both sniffed as syslog lines) a hard failure.

## Decision

Option 2 with b and ii.

- `internal/logparse` declares `Parser` (`Type() string`, `Detect(f File, head []byte) Confidence`,
  `Parse(ctx, f File, r io.Reader, out Emitter) error`), `File` (`Name`: slash path relative to the import
  root or inside the archive, cleaned, `.gz` removed after decompression — a label only; `ModTime` in UTC),
  `Emitter` (`Record(model.Record) error`, `Skip(line int64, reason string)`), `Confidence`
  (`NoMatch` < `MatchName` < `MatchContent`) and `SniffBytes` = 4096, the length of the head `Detect` sees.
- `NewRegistry(parsers ...Parser)` refuses nil parsers, duplicate types and types outside
  `^[a-z][a-z0-9._-]{0,63}$` (`CheckType`, also used by the store for `import_files.source_type`).
  `Registry.Detect` returns the parser with the highest confidence, on a tie the earliest registered, or
  nil with `NoMatch`. There is no global registry; `vandoxd` lists its parsers in `importParsers()`
  (`cmd/vandoxd/import.go`), empty until #16.
- Contract of `Parse`, documented on the interface: deterministic for the same content and `File` (resume,
  0069); records of origin `import` with UTC capture times; memory bounded independently of the input size;
  honor the context; return the emitter's error; `Skip` reasons are fixed texts without input content.
  The importer validates every record with `store.CheckImportRecord` and counts refused ones as skipped, so a
  parser bug cannot make a whole batch fail.
- `logparse.LineReader` is the shared bounded line reader: lines without `\n`/`\r\n`, cut to `MaxLineBytes`
  (= `model.MaxTextBytes`, 16 KiB) at a UTF-8 boundary with a `truncated` flag, the rest of the line
  discarded, so a file without newlines cannot grow memory. Parsers are expected to use it.
- `internal/logparse/logparsetest.Parser` is a scripted parser for tests (`DetectFunc`, `ParseFunc`, recorded
  calls) with the helpers `HeadPrefix` and `Lines`; it is production code for the coverage gate with its own
  test, like `storetest` (0067).

## Consequences

- #16–#20 each add a type in `internal/` and one line in `importParsers`; the importer does not change.
  `.squad/project.md` lists this under *Integration surface*.
- The interface works on streams of whole files. The agent's live shipping (#37) can reuse the line-level
  logic but will need its own entry point for a journal cursor; that is #37's decision.
- A parser that is not deterministic breaks resuming; parser tests should cover determinism.
- Until #16 is merged, `vandoxd import` lists every file as not recognized.
- Choosing among equal confidences by registration order means the order in `importParsers` is part of the
  behavior; a test pins it once there are parsers.

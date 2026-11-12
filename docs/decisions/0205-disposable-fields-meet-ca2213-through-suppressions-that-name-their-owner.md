# 0205: Disposable fields meet CA2213 through suppressions that name their owner

- **Status:** Accepted
- **Date:** 2026-11-12
- **Area:** —
- **Source:** Issue #429
- **Supersedes:** —

## Context

The template's shared .NET rule baseline ([0203](0203-two-language-toolchain-on-the-templates-shared-dotnet-rules.md))
sets CA2213 (*Disposable fields should be disposed*) at `warning`, which the build treats as an error. CA2213 reports a
disposable field that the declaring type's `Dispose` never disposes, also when the field only holds an object its caller
owns. Six fields are reported: five hold an object the caller owns — the wrapped stream of `FilterStream`
(Import), `GzipMemberReader` (wire decoder; `BatchDecoder.OpenAsync` documents that it leaves its input open) and the test
stream `HookedStream`, the standard-error writer of `JsonLineLoggerProvider` (shared by the bootstrap and the service
logger factory), and the current `ActionRun` of `ActionSequence` (disposed by the caller of `BeginAsync`). The sixth, the
`SemaphoreSlim` gate of `ChartInterop`, is the type's own; it creates its wait handle only when `AvailableWaitHandle` is
read, which no code does, and `DisposeAsync` hands the gate to a render that is still waiting, whose `finally` then
releases it.

## Options considered

1. **Dispose every reported field** — rejected: it closes standard error under a second logger, closes a stream the
   decoder promises to leave open, disposes a run while its action still uses its token, and makes the release of a
   waiting chart render throw `ObjectDisposedException`.
2. **Remove the fields' disposable types (a delegate instead of the stream or writer, a hand-made async lock)** —
   rejected: it rewrites working wrappers and the chart's locking without a change in behavior.
3. **Switch CA2213 off, or exclude the types, in `.editorconfig`** — rejected: the rules and their severities are set
   only in the template's blocks (`.squad/stack.md`, *Analyzer gate*).
4. **A `#pragma warning disable CA2213 // <who owns it>` around each reported field declaration** — chosen, in the
   form record [0204](0204-sql-command-text-meets-ca2100-through-justified-suppressions-at-the-command-helpers.md) set
   for CA2100.

## Decision

A disposable field the type does not own is declared between `#pragma warning disable CA2213` and `#pragma warning
restore CA2213`, with a comment that names its owner; a field the type owns is disposed in its `Dispose`, except the
`ChartInterop` gate, whose suppression names why it stays undisposed.

## Consequences

- A new disposable field still fails the build unless it is disposed or its declaration carries the suppression, so
  every ownership choice is visible at the field.
- The analyzer no longer checks the six suppressed fields: a change that makes one of the types the owner of its field
  removes the suppression and disposes it.
- Revisit when the template's baseline changes CA2213.

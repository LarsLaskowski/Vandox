# 0201: The output writes of `vandoxd` commands opt out of cancellation explicitly

- **Status:** Accepted
- **Date:** 2026-11-11
- **Area:** Backend host
- **Source:** Product Manager request: move the backend to .NET 11 (feature dotnet-11-upgrade)
- **Supersedes:** —

## Context

.NET 11 gives `TextWriter.WriteAsync(string)` and `WriteLineAsync(string)` overloads with a `CancellationToken`; the rules CA2016
and S8949 now report every such call in a method that has a token but does not pass it (`BackendApp`, `HashPasswordCommand`,
`HealthCheckCommand`, `IncidentsCommand`, `TemplatesCommand`). Several of these writes run after the token is cancelled on
purpose: the health check prints its failure when the probe was cancelled, a command prints its result after its work is
done. A write given the cancelled token would throw `OperationCanceledException` instead of printing, and inside a `catch`
it would escape as an unhandled exception.

## Options considered

1. **Pass `CancellationToken.None`** (chosen) — what the rules accept as an explicit opt-out; every command prints what it
   prints today.
2. **Pass the command's token** — a cancelled command would lose its last line or end with an unhandled exception, against
   the backend host's command contract.
3. **Suppress the two rules on the commands** — hides them for every later call.

## Decision

Each output write of a command passes `CancellationToken.None`; cancellation is observed by the work, never by the write of its
result or of its failure. The commands' output and exit codes are in [backend-host](../areas/backend-host.md).

## Consequences

- The overloads with a token write through `WriteAsync(ReadOnlyMemory<char>, …)`; a hand-written `TextWriter` (the test
  double `LockedTextWriter`) overrides the span writes so a line stays one write under its lock.
